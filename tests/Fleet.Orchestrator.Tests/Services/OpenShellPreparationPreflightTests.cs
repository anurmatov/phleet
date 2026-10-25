using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fleet.Orchestrator.Services;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;

namespace Fleet.Orchestrator.Tests.Services;

/// <summary>
/// Connected source regressions for the concrete preflight (#479): qualification records are bound
/// to the exact pinned binary, config and approved plan, the live policy flows only through the
/// authorized capabilities reader (which has no production implementation), and every path that
/// depends on the unshipped upstream contracts stays fail-closed with zero engine mutations.
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
    public async Task VerifyAsync_RefusesRewrittenPlanHash()
    {
        Qualified();
        var selection = Selection() with { PlanHash = new string('c', 64) };
        await AssertRefusal("bootstrap_preflight_plan_changed", selection);
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
        File.WriteAllBytes(Path.Combine(_root, "hook"), Encoding.UTF8.GetBytes("pinned-hook-bytes"));
        File.Delete(Path.Combine(_root, "config.json"));
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
    public async Task VerifyAsync_RefusesDescribeBoundToAnotherHook()
    {
        // Internally valid Describe for a DIFFERENT binary: self-consistency proves nothing.
        Qualified();
        var other = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("another-hook")));
        Write("describe.json", Describe(other, ConfigDigest()));
        await AssertRefusal("bootstrap_hook_describe_unqualified");
    }

    [Fact]
    public async Task VerifyAsync_RefusesValidateConfigBoundToAnotherConfig()
    {
        // Replace the qualified config and re-bind Describe to it; the ValidateConfig record is
        // still a perfectly valid reply for the OTHER config — and must be refused.
        Qualified();
        File.WriteAllText(Path.Combine(_root, "config.json"), Policy() + " # rotated policy");
        Write("describe.json", Describe(HookDigest(), ConfigDigest()));
        await AssertRefusal("bootstrap_hook_validate_config_unqualified");
    }

    [Fact]
    public async Task VerifyAsync_RefusesValidateConfigBoundToAnotherPlan()
    {
        Qualified();
        Write("validate-config.json", Validate(HookDigest(), ConfigDigest(), new string('f', 64)));
        await AssertRefusal("bootstrap_hook_validate_config_unqualified");
    }

    [Fact]
    public async Task VerifyAsync_RefusesValidateConfigRefusal()
    {
        Qualified();
        Write("validate-config.json", new JsonObject { ["accepted"] = false, ["plugin"] = Plugin,
            ["hook_sha256"] = HookDigest(), ["config_sha256"] = ConfigDigest(), ["plan_hash"] = Selection().PlanHash });
        await AssertRefusal("bootstrap_hook_validate_config_unqualified");
    }

    [Fact]
    public async Task VerifyAsync_RefusesMalformedQualificationRecord()
    {
        Qualified();
        Write("describe.json", JsonNode.Parse("""{"accepted":true}""")!.AsObject());
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
        Qualified(drifted);
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
        var drifted = plan with { BootstrapIngress = ingress };
        Qualified(drifted);
        await AssertRefusal("bootstrap_ingress_unqualified", Selection(drifted));
    }

    [Fact]
    public async Task VerifyAsync_AcceptsValidDifferentReceiverPort()
    {
        var plan = OpenShellPolicyCompilerTests.Plan() with { BootstrapIngress = "https://ingress.example.invalid:19443/identity/ready" };
        Qualified(plan);
        var receiver = new OpenShellBootstrapReceiverOptions
        { Enabled = true, Port = 19443, IngressDnsName = "ingress.example.invalid" };
        await new OpenShellPreparationPreflight(Http(_engine), _root, Plugin, receiver, Reader(Policy())).VerifyAsync(Selection(plan), default);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("collision")]
    [InlineData("port")]
    [InlineData("dns")]
    public async Task VerifyAsync_RefusesReceiverBindingIndependently(string fault)
    {
        Qualified();
        var plan = OpenShellPolicyCompilerTests.Plan();
        var receiver = fault switch
        {
            "disabled" => new OpenShellBootstrapReceiverOptions { Port = 17443, IngressDnsName = "ingress.example.invalid" },
            // Backend receiver alone is disqualifying: the API port may never host the receiver.
            "collision" => new OpenShellBootstrapReceiverOptions { Enabled = true, Port = 3600, IngressDnsName = "ingress.example.invalid" },
            "port" => new OpenShellBootstrapReceiverOptions { Enabled = true, Port = 18000, IngressDnsName = "ingress.example.invalid" },
            _ => new OpenShellBootstrapReceiverOptions { Enabled = true, Port = 17443, IngressDnsName = "elsewhere.example.invalid" },
        };
        OpenShellPreparationScope selection;
        if (fault == "collision")
        {
            var collided = plan with { BootstrapIngress = "https://ingress.example.invalid:3600/identity/ready" };
            Qualified(collided);
            selection = Selection(collided);
        }
        else selection = Selection();
        var ex = await Assert.ThrowsAnyAsync<OpenShellStateConflictException>(() =>
            new OpenShellPreparationPreflight(Http(_engine), _root, Plugin, receiver, Reader(Policy())).VerifyAsync(selection, default));
        Assert.Contains("bootstrap_receiver_binding_unqualified", ex.Message);
    }

    [Fact]
    public async Task VerifyAsync_RefusesWithoutAuthorizedCapabilitiesReader()
    {
        // The missing-reader dependency is retained deliberately: no production reader exists
        // until the reviewed upstream ComputeDriver.GetCapabilities contract ships.
        Qualified();
        await Assert.ThrowsAnyAsync<OpenShellStateConflictException>(() =>
            new OpenShellPreparationPreflight(Http(_engine), _root, Plugin, Receiver(), null).VerifyAsync(Selection(), default));
    }

    [Fact]
    public async Task VerifyAsync_RefusesAbsentCapabilityPolicy()
    {
        Qualified();
        var ex = await Assert.ThrowsAnyAsync<OpenShellStateConflictException>(() =>
            new OpenShellPreparationPreflight(Http(_engine), _root, Plugin, Receiver(), Reader(null)).VerifyAsync(Selection(), default));
        Assert.Contains("bootstrap_current_policy_unavailable", ex.Message);
    }

    [Fact]
    public async Task VerifyAsync_RefusesLivePolicyDrift()
    {
        Qualified();
        var ex = await Assert.ThrowsAnyAsync<OpenShellStateConflictException>(() =>
            new OpenShellPreparationPreflight(Http(_engine), _root, Plugin, Receiver(),
                Reader(Policy().Replace("\"enabled\":true", "\"enabled\":false"))).VerifyAsync(Selection(), default));
        Assert.Contains("bootstrap_resource_admission_unqualified", ex.Message);
    }

    [Fact]
    public async Task VerifyAsync_RefusesLivePolicyNotMatchingQualifiedConfig()
    {
        // Records re-bound to the rotated config, live policy still the plan's ack: the live
        // policy and the qualification config must be the same document, byte for byte.
        Qualified();
        File.WriteAllText(Path.Combine(_root, "config.json"), Policy() + " # rotated policy");
        Write("describe.json", Describe(HookDigest(), ConfigDigest()));
        Write("validate-config.json", Validate(HookDigest(), ConfigDigest(), Selection().PlanHash));
        var ex = await Assert.ThrowsAnyAsync<OpenShellStateConflictException>(() =>
            new OpenShellPreparationPreflight(Http(_engine), _root, Plugin, Receiver(), Reader(Policy())).VerifyAsync(Selection(), default));
        Assert.Contains("bootstrap_current_policy_unqualified", ex.Message);
    }

    [Fact]
    public async Task VerifyAsync_PropagatesReaderFailureAsRefusalNotUnneeded()
    {
        Qualified();
        var reader = new ThrowingReader();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new OpenShellPreparationPreflight(Http(_engine), _root, Plugin, Receiver(), reader).VerifyAsync(Selection(), default));
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
        services.AddSingleton<IOpenShellRuntimeEvidenceReader, Reader2>();
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

    // ---- helpers ----

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

    private sealed class Reader2 : IOpenShellRuntimeEvidenceReader
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

    private static string Policy() => OpenShellPolicyCompilerTests.Admission().PolicyAcknowledgement;

    private static OpenShellPreparationScope Selection(OpenShellLaunchPlan? plan = null)
    {
        var p = plan ?? OpenShellPolicyCompilerTests.Plan();
        return new OpenShellPreparationScope(1, "start", null, p.Hash, DateTime.UtcNow.AddSeconds(100), p);
    }

    private string HookDigest() => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(_root, "hook"))));
    private string ConfigDigest() => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(_root, "config.json"))));

    private static JsonObject Describe(string hookSha, string configSha) => new()
    { ["plugin"] = Plugin, ["scope"] = "local", ["hook_sha256"] = hookSha, ["config_sha256"] = configSha };

    private static JsonObject Validate(string hookSha, string configSha, string planHash) => new()
    { ["accepted"] = true, ["plugin"] = Plugin, ["hook_sha256"] = hookSha, ["config_sha256"] = configSha, ["plan_hash"] = planHash };

    /// <summary>Writes the exact bound qualification-root contract the preflight expects.</summary>
    private void Qualified(OpenShellLaunchPlan? plan = null)
    {
        File.WriteAllBytes(Path.Combine(_root, "hook"), Encoding.UTF8.GetBytes("pinned-hook-bytes"));
        File.WriteAllText(Path.Combine(_root, "hook-sha256"), HookDigest());
        File.WriteAllText(Path.Combine(_root, "config.json"), Policy());
        Write("describe.json", Describe(HookDigest(), ConfigDigest()));
        Write("validate-config.json", Validate(HookDigest(), ConfigDigest(), (plan ?? OpenShellPolicyCompilerTests.Plan()).Hash));
    }

    private void Write(string name, JsonNode node) => File.WriteAllText(Path.Combine(_root, name), node.ToJsonString());

    private async Task AssertRefusal(string code, OpenShellPreparationScope? selection = null)
    {
        var ex = await Assert.ThrowsAnyAsync<OpenShellStateConflictException>(() =>
            _preflight().VerifyAsync(selection ?? Selection(), default));
        Assert.Contains(code, ex.Message);
    }
}
