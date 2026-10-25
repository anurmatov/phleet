using System.Net;
using System.Security.Cryptography;
using System.Text;
using Fleet.Orchestrator.Services;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Openshell.Extension.V1;
using Openshell.Middleware.V1;

namespace Fleet.Orchestrator.Tests.Services;

/// <summary>
/// Connected source regressions for the concrete preflight (#479): qualification records are the
/// ACTUAL generated MiddlewareManifest/ValidateConfig wire messages bound to the selected hook
/// registration and the approved plan's middleware config inside a digest-sealed record set, the
/// live policy flows only through the authorized capabilities reader (which has no production
/// implementation), and every path that depends on the unshipped upstream contracts stays
/// fail-closed with zero engine mutations. Fixtures are serialized generated messages, not
/// handwritten shapes.
/// </summary>
public sealed class OpenShellPreparationPreflightTests : IDisposable
{
    private const string Plugin = "fleetbootstrap";
    private readonly string _root = Directory.CreateTempSubdirectory("ospreflight").FullName;
    private readonly Engine _engine = new();

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    [Fact]
    public async Task VerifyAsync_AcceptsFullyQualifiedPlan()
    {
        Qualified();
        await _preflight().VerifyAsync(Selection(), default);
        Assert.Empty(_engine.Paths); // qualification reads no engine surface at all
    }

    [Fact]
    public async Task VerifyAsync_AcceptsDistinctIngressAndReceiverPorts()
    {
        // Compiler-valid plan (ingress :17443 == its own endpoint port) behind a fronting ingress
        // while the backend receiver listens elsewhere: ports need not be equal.
        Qualified();
        var receiver = new OpenShellBootstrapReceiverOptions
        { Enabled = true, Port = 19443, IngressDnsName = "ingress.example.invalid" };
        await new OpenShellPreparationPreflight(Http(_engine), _root, Plugin, receiver, Reader(Policy())).VerifyAsync(Selection(), default);
    }

    [Fact]
    public async Task VerifyAsync_RefusesRewrittenPlanHash()
    {
        Qualified();
        await AssertRefusal("bootstrap_preflight_plan_changed", Selection() with { PlanHash = new string('c', 64) });
    }

    [Theory]
    [InlineData("", Plugin)]
    [InlineData("/qualification", "")]
    [InlineData("relative/root", Plugin)]
    public async Task VerifyAsync_RefusesUnconfiguredQualification(string root, string plugin)
    {
        Qualified();
        var ex = await Assert.ThrowsAnyAsync<OpenShellStateConflictException>(() =>
            _preflight(root, plugin).VerifyAsync(Selection(), default));
        Assert.Contains("bootstrap_preflight_unconfigured", ex.Message);
    }

    [Fact]
    public async Task VerifyAsync_RefusesMissingQualificationArtifacts()
    {
        Qualified();
        File.Delete(Path.Combine(_root, "hook"));
        await AssertRefusal("bootstrap_artifact_missing");
        File.WriteAllBytes(Path.Combine(_root, "hook"), HookBytes);
        File.Delete(Path.Combine(_root, "validate-request.json"));
        await AssertRefusal("bootstrap_artifact_missing");
    }

    [Fact]
    public async Task VerifyAsync_RefusesHookDigestMismatch()
    {
        Qualified();
        File.WriteAllText(Path.Combine(_root, "hook"), "tampered");
        await AssertRefusal("bootstrap_hook_artifact_unqualified");
    }

    [Fact]
    public async Task VerifyAsync_DetectsStaleRecordSubstitution()
    {
        // A refusal reply swapped in without resealing: the digest seal catches it first.
        Qualified();
        File.WriteAllText(Path.Combine(_root, "validate-config.json"), Json(new ValidateConfigResponse { Valid = false, Reason = "nope" }));
        await AssertRefusal("bootstrap_qualification_record_unqualified");
    }

    [Fact]
    public async Task VerifyAsync_RefusesManifestWithoutRequiredBinding()
    {
        Qualified();
        var manifest = Manifest();
        manifest.Bindings.Clear();
        manifest.Bindings.Add(new MiddlewareBinding { Operation = SupervisorMiddlewareOperation.WebsocketMessage,
            Phase = SupervisorMiddlewarePhase.PreCredentials, MaxPayloadBytes = 4096 });
        WriteRecord("describe.json", manifest); Seal();
        await AssertRefusal("bootstrap_hook_describe_unqualified");
    }

    [Fact]
    public async Task VerifyAsync_RefusesManifestWithoutAudienceOrProtocol()
    {
        Qualified();
        var manifest = Manifest();
        manifest.ExpectedAudience = "";
        WriteRecord("describe.json", manifest); Seal();
        await AssertRefusal("bootstrap_hook_describe_unqualified");
        Qualified();
        manifest = Manifest();
        manifest.Extension.ProtocolVersion = new ProtocolVersion { Major = 2, Minor = 0 };
        WriteRecord("describe.json", manifest); Seal();
        await AssertRefusal("bootstrap_hook_describe_unqualified");
    }

    [Fact]
    public async Task VerifyAsync_RefusesValidateRequestForAnotherHook()
    {
        // Fully self-consistent record set — generated schema, valid response — for ANOTHER hook.
        Qualified();
        WriteRecord("validate-request.json", new ValidateConfigRequest
        { MiddlewareName = "otherhook", Config = PlanConfig() });
        Seal();
        await AssertRefusal("bootstrap_hook_validate_request_unqualified");
    }

    [Fact]
    public async Task VerifyAsync_RefusesValidateRequestForAnotherConfig()
    {
        // The request config must be the approved plan's own middleware config, not a peer.
        Qualified();
        WriteRecord("validate-request.json", new ValidateConfigRequest
        { MiddlewareName = Plugin, Config = new Struct { Fields = { ["service"] = Value.ForString("other") } } });
        Seal();
        await AssertRefusal("bootstrap_hook_validate_request_unqualified");
    }

    [Fact]
    public async Task VerifyAsync_RefusesValidateConfigRefusal()
    {
        Qualified();
        WriteRecord("validate-config.json", new ValidateConfigResponse { Valid = false, Reason = "config_unsupported" });
        Seal();
        await AssertRefusal("bootstrap_hook_validate_config_unqualified");
    }

    [Fact]
    public async Task VerifyAsync_RefusesNonGeneratedRecordSchema()
    {
        // The previous round's handwritten shape is not the generated wire schema.
        Qualified();
        File.WriteAllText(Path.Combine(_root, "describe.json"),
            """{"plugin":"fleetbootstrap","scope":"local","hook_sha256":"aa","config_sha256":"bb"}""");
        Seal(); // sealed so the digest cannot mask the schema failure being under test
        await AssertRefusal("bootstrap_qualification_record_malformed");
    }

    [Theory]
    [InlineData("restart")]
    [InlineData("root")]
    [InlineData("workdir")]
    [InlineData("command")]
    [InlineData("image")]
    public async Task VerifyAsync_RefusesStaticallyDriftedPlan(string fault)
    {
        Qualified();
        var plan = OpenShellPolicyCompilerTests.Plan();
        var spec = plan.ReadSpec();
        switch (fault)
        {
            case "restart": spec.RestartPolicy = Openshell.V1.SandboxRestartPolicy.Always; break;
            case "root": spec.Policy.Process.RunAsUser = "0"; break;
            case "workdir": spec.Policy.Filesystem.IncludeWorkdir = true; break;
            case "command": spec.Command[2] = "--OpenShell:Bootstrap=false"; break;
            case "image": spec.Template.Image = "example.invalid/agent@sha256:" + new string('d', 64); break;
        }
        var drifted = plan with { SpecBase64 = Convert.ToBase64String(spec.ToByteArray()) };
        await AssertRefusal("bootstrap_static_qualification_changed", Selection(drifted));
    }

    [Theory]
    [InlineData("http")]
    [InlineData("path")]
    [InlineData("query")]
    public async Task VerifyAsync_RefusesMalformedIngressIndependently(string fault)
    {
        Qualified();
        var plan = OpenShellPolicyCompilerTests.Plan();
        var ingress = fault switch
        {
            "http" => "http://ingress.example.invalid:17443/identity/ready",
            "path" => "https://ingress.example.invalid:17443/identity/other",
            _ => "https://ingress.example.invalid:17443/identity/ready?retry=1",
        };
        await AssertRefusal("bootstrap_ingress_unqualified", Selection(plan with { BootstrapIngress = ingress }));
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("collision")]
    [InlineData("dns")]
    public async Task VerifyAsync_RefusesReceiverBindingIndependently(string fault)
    {
        Qualified();
        var receiver = fault switch
        {
            "disabled" => new OpenShellBootstrapReceiverOptions { Port = 17443, IngressDnsName = "ingress.example.invalid" },
            // Backend receiver alone is disqualifying: the API port may never host the receiver.
            "collision" => new OpenShellBootstrapReceiverOptions { Enabled = true, Port = 3600, IngressDnsName = "ingress.example.invalid" },
            _ => new OpenShellBootstrapReceiverOptions { Enabled = true, Port = 17443, IngressDnsName = "elsewhere.example.invalid" },
        };
        var ex = await Assert.ThrowsAnyAsync<OpenShellStateConflictException>(() =>
            new OpenShellPreparationPreflight(Http(_engine), _root, Plugin, receiver, Reader(Policy())).VerifyAsync(Selection(), default));
        Assert.Contains("bootstrap_receiver_binding_unqualified", ex.Message);
    }

    [Fact]
    public async Task VerifyAsync_RefusesWithoutAuthorizedCapabilitiesReader()
    {
        // The missing-reader dependency is retained deliberately: no production reader exists
        // until the reviewed upstream ComputeDriver.GetCapabilities contract ships.
        Qualified();
        var ex = await Assert.ThrowsAnyAsync<OpenShellStateConflictException>(() =>
            new OpenShellPreparationPreflight(Http(_engine), _root, Plugin, Receiver(), null).VerifyAsync(Selection(), default));
        Assert.Contains("bootstrap_current_policy_unavailable", ex.Message);
    }

    [Fact]
    public async Task VerifyAsync_RefusesAbsentCapabilityPolicy()
    {
        Qualified();
        await AssertRefusal("bootstrap_current_policy_unavailable", reader: Reader(null));
    }

    [Fact]
    public async Task VerifyAsync_RefusesLivePolicyDrift()
    {
        Qualified();
        await AssertRefusal("bootstrap_resource_admission_unqualified",
            reader: Reader(Policy().Replace("\"enabled\":true", "\"enabled\":false")));
    }

    [Fact]
    public async Task VerifyAsync_RefusesLivePolicyNotMatchingQualifiedConfig()
    {
        // config.json rotated (sealed consistently); live policy is still the old document.
        Qualified();
        File.WriteAllText(Path.Combine(_root, "config.json"), Policy() + " # rotated policy");
        Seal();
        await AssertRefusal("bootstrap_current_policy_unqualified", reader: Reader(Policy()));
    }

    [Fact]
    public async Task VerifyAsync_PropagatesReaderFailureAsRefusalNotUnneeded()
    {
        Qualified();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new OpenShellPreparationPreflight(Http(_engine), _root, Plugin, Receiver(), new ThrowingReader()).VerifyAsync(Selection(), default));
    }

    [Fact]
    public async Task VerifyLegacyOwnership_FailsClosedWithZeroEngineCalls()
    {
        Qualified();
        // Blocked on the reviewed upstream read-only ownership contract; no local substitute.
        var ex = await Assert.ThrowsAnyAsync<OpenShellStateConflictException>(() =>
            _preflight().VerifyLegacyOwnershipAsync(Selection(), default));
        Assert.Contains("legacy_ownership_inventory_unqualified", ex.Message);
        Assert.Empty(_engine.Paths);
    }

    [Theory]
    [InlineData("legacy1")]
    [InlineData("")]
    [InlineData("ABCDEF")]
    public async Task StopLegacyExact_RefusesAnythingButAnExactId(string id)
    {
        await Assert.ThrowsAnyAsync<OpenShellStateConflictException>(() =>
            _preflight().StopLegacyExactAsync(id, default));
        Assert.Empty(_engine.Paths); // never a name-based or partial-id stop
    }

    [Theory]
    [InlineData(HttpStatusCode.NoContent, true)]
    [InlineData(HttpStatusCode.NotModified, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.Conflict, false)]
    public async Task StopLegacyExact_RequestsOnlyTheExactIdAndAcceptsOnlyStoppedStates(HttpStatusCode status, bool allowed)
    {
        var id = new string('e', 64);
        _engine.Respond = _ => new HttpResponseMessage(status);
        var task = _preflight().StopLegacyExactAsync(id, default);
        if (!allowed) await Assert.ThrowsAnyAsync<OpenShellStateConflictException>(() => task);
        else await task;
        Assert.Equal($"/v1.41/containers/{id}/stop", Assert.Single(_engine.Paths));
    }

    [Fact]
    public async Task Coordinator_WithConcretePreflight_RefusesBeforeDbOrCreate()
    {
        var plan = OpenShellPolicyCompilerTests.Plan();
        var selection = new OpenShellPreparationScope(1, "start", null, plan.Hash, DateTime.UtcNow.AddSeconds(100), plan);
        var gateway = new Gateway();
        var services = new ServiceCollection();
        services.AddSingleton<IOpenShellGatewayClient>(gateway);
        services.AddSingleton<IOpenShellRuntimeEvidenceReader, EvidenceNone>();
        services.AddSingleton<IOpenShellBootstrapProbe, Probe>();
        services.AddSingleton<IOpenShellArtifactCleanup, Cleanup>();
        // Real preflight, qualification root deliberately absent, no authorized reader:
        // refuse, never simulate.
        services.AddSingleton<IOpenShellPreparationPreflight>(new OpenShellPreparationPreflight(
            Http(_engine), Path.Combine(_root, "not-installed"), Plugin, Receiver(), null));
        var dbCalls = 0;
        var driver = new OpenShellProvisioningService(
            () => { dbCalls++; throw new Exception("DB must not be reached"); },
            new FixedAuthority(selection), services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
        var result = await driver.TryPrepareAsync("agent1", "start", false, default);
        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Equal("preparation_refused_before_launch", result.Message);
        Assert.Equal(409, result.StatusCode); // OpenShellStateConflictException maps to a conflict refusal
        Assert.Equal(1, gateway.VerifyInputsCalls); // the chain is connected up to the refusal
        Assert.Equal(0, gateway.CreateCalls);
        Assert.Equal(0, dbCalls);
        Assert.Empty(_engine.Paths);
    }

    // ---- fixtures: generated messages, serialized with proto3 JSON ----

    private static readonly byte[] HookBytes = Encoding.UTF8.GetBytes("pinned-hook-bytes");

    private static MiddlewareManifest Manifest() => new()
    {
        Name = "example-hook", ExpectedAudience = "orchestrator:/identity/ready",
        Extension = new PeerMetadata
        {
            ProtocolVersion = new ProtocolVersion { Major = 1, Minor = 0 },
            ImplementationName = "example-hook", ImplementationVersion = "1",
            SupportedCapabilities = { "openshell.supervisor-middleware.contract" },
        },
        Bindings = { new MiddlewareBinding { Operation = SupervisorMiddlewareOperation.HttpRequest,
            Phase = SupervisorMiddlewarePhase.PreCredentials, MaxPayloadBytes = 1_048_576,
            RequestTimeout = Duration.FromTimeSpan(TimeSpan.FromSeconds(2)) } },
    };

    private static Struct PlanConfig() => OpenShellPolicyCompilerTests.Plan().ReadSpec()
        .Policy.NetworkMiddlewares.Values.Single().Config;

    private static string Policy() => OpenShellPolicyCompilerTests.Admission().PolicyAcknowledgement;

    private static string Json(IMessage message) => JsonFormatter.Default.Format(message);

    /// <summary>Writes the exact bound qualification-root contract the preflight expects.</summary>
    private void Qualified()
    {
        File.WriteAllBytes(Path.Combine(_root, "hook"), HookBytes);
        File.WriteAllText(Path.Combine(_root, "hook-sha256"),
            Convert.ToHexStringLower(SHA256.HashData(HookBytes)));
        File.WriteAllText(Path.Combine(_root, "describe.json"), Json(Manifest()));
        File.WriteAllText(Path.Combine(_root, "validate-request.json"),
            Json(new ValidateConfigRequest { MiddlewareName = Plugin, Config = PlanConfig() }));
        File.WriteAllText(Path.Combine(_root, "validate-config.json"), Json(new ValidateConfigResponse { Valid = true }));
        File.WriteAllText(Path.Combine(_root, "config.json"), Policy());
        Seal();
    }

    private void WriteRecord(string name, IMessage message)
        => File.WriteAllText(Path.Combine(_root, name), Json(message));

    /// <summary>Seals the record set exactly as the operator custody procedure requires.</summary>
    private void Seal()
    {
        using var sha = SHA256.Create();
        foreach (var name in new[] { "config.json", "describe.json", "validate-config.json", "validate-request.json" })
        {
            var bytes = File.ReadAllBytes(Path.Combine(_root, name));
            var header = Encoding.UTF8.GetBytes($"{name}\0{bytes.Length}\0");
            sha.TransformBlock(header, 0, header.Length, null, 0);
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }
        sha.TransformFinalBlock([], 0, 0);
        File.WriteAllText(Path.Combine(_root, "records-sha256"), Convert.ToHexStringLower(sha.Hash!));
    }

    // ---- peers and helpers ----

    private sealed class FixedAuthority(OpenShellPreparationScope selection) : IOpenShellPreparationAuthority
    {
        public Task<OpenShellPreparationScope?> SelectAsync(string agentName, string intent, CancellationToken ct)
            => Task.FromResult<OpenShellPreparationScope?>(selection);
    }

    private sealed class Gateway : IOpenShellGatewayClient
    {
        public int VerifyInputsCalls, CreateCalls;
        public Task VerifyBootstrapInputsAsync(OpenShellLaunchPlan plan, CancellationToken ct = default)
        { VerifyInputsCalls++; return Task.CompletedTask; }
        public Task<OpenShellSandboxSnapshot> CreateAsync(OpenShellLaunchPlan plan, Guid launchId, Guid epoch,
            Guid requestId, DateTime bootstrapDeadline, CancellationToken ct = default)
        { CreateCalls++; throw new NotSupportedException("create_unqualified"); }
        public Task<IReadOnlyList<OpenShellSandboxSnapshot>> InventoryAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<OpenShellSandboxSnapshot?> ReadExactAsync(string sandboxId, CancellationToken ct = default)
            => Task.FromResult<OpenShellSandboxSnapshot?>(null);
        public Task<OpenShellSandboxSnapshot> StopExactAsync(OpenShellSandboxSnapshot target, Guid requestId,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpenShellDeleteAcknowledgement> DeleteExactAsync(OpenShellSandboxSnapshot target,
            Guid requestId, bool allowMissing, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class EvidenceNone : IOpenShellRuntimeEvidenceReader
    {
        public Task<OpenShellRuntimeEvidence> ReadAsync(OpenShellRuntimeTarget target, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class Probe : IOpenShellBootstrapProbe
    {
        public Task VerifyAsync(Guid launchId, Guid epoch, OpenShellSandboxSnapshot target,
            IReadOnlyList<OpenShellSupervisorProof> proofs, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Cleanup : IOpenShellArtifactCleanup
    {
        public Task RetireAsync(OpenShellArtifactCleanupTarget target, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class PolicyReader(string? policy) : IOpenShellCurrentPolicyReader
    {
        public Task<string?> ReadCurrentPolicyAsync(CancellationToken ct) => Task.FromResult(policy);
    }

    private sealed class ThrowingReader : IOpenShellCurrentPolicyReader
    {
        public Task<string?> ReadCurrentPolicyAsync(CancellationToken ct)
            => throw new InvalidOperationException("capabilities_transport_unavailable");
    }

    private sealed class Engine : System.Net.Http.HttpMessageHandler
    {
        public List<string> Paths = [];
        public Func<HttpRequestMessage, HttpResponseMessage> Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(Respond(request));
        }
    }

    private static HttpClient Http(Engine engine) => new(engine) { BaseAddress = new Uri("http://localhost") };

    private OpenShellPreparationPreflight _preflight(string? root = null, string? plugin = null)
        => new(Http(_engine), root ?? _root, plugin ?? Plugin, Receiver(), Reader(Policy()));

    private static OpenShellBootstrapReceiverOptions Receiver() => new()
    { Enabled = true, Port = 17443, IngressDnsName = "ingress.example.invalid" };

    private static IOpenShellCurrentPolicyReader Reader(string? policy) => new PolicyReader(policy);

    private static OpenShellPreparationScope Selection(OpenShellLaunchPlan? plan = null)
    {
        var p = plan ?? OpenShellPolicyCompilerTests.Plan();
        return new OpenShellPreparationScope(1, "start", null, p.Hash, DateTime.UtcNow.AddSeconds(100), p);
    }

    private async Task AssertRefusal(string code, OpenShellPreparationScope? selection = null,
        IOpenShellCurrentPolicyReader? reader = null)
    {
        var ex = await Assert.ThrowsAnyAsync<OpenShellStateConflictException>(() =>
            new OpenShellPreparationPreflight(Http(_engine), _root, Plugin, Receiver(),
                reader ?? Reader(Policy())).VerifyAsync(selection ?? Selection(), default));
        Assert.Contains(code, ex.Message);
    }
}
