using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Fleet.Orchestrator.Services;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;

namespace Fleet.Orchestrator.Tests.Services;

/// <summary>
/// Connected source regressions for the concrete preflight (#479): every independently
/// implementable qualification refuses on drift, and the paths that depend on the unshipped
/// upstream ownership contract stay fail-closed with zero engine mutations.
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
        Assert.Contains("/v1.41/plugins/" + Plugin + "/json", _engine.Paths); // current policy read live
    }

    [Fact]
    public async Task VerifyAsync_RefusesRewrittenPlanHash()
    {
        Qualified();
        var selection = Selection() with { PlanHash = new string('c', 64) };
        await AssertRefusal("bootstrap_preflight_plan_changed", selection);
        Assert.Empty(_engine.Paths); // nothing is read before the binding check
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
        Assert.Empty(_engine.Paths);
    }

    [Fact]
    public async Task VerifyAsync_RefusesMissingHookArtifacts()
    {
        Qualified();
        File.Delete(Path.Combine(_root, "hook"));
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
    public async Task VerifyAsync_RefusesHookDescribeDrift()
    {
        Qualified();
        Write("describe.json", new JsonObject { ["plugin"] = "other", ["scope"] = "local" });
        await AssertRefusal("bootstrap_hook_describe_unqualified");
    }

    [Fact]
    public async Task VerifyAsync_RefusesMalformedQualificationRecord()
    {
        Qualified();
        Write("validate-config.json", new JsonObject { ["accepted"] = true, ["extra"] = 1 });
        await AssertRefusal("bootstrap_qualification_record_malformed");
    }

    [Fact]
    public async Task VerifyAsync_RefusesValidateConfigRefusal()
    {
        Qualified();
        Write("validate-config.json", new JsonObject { ["accepted"] = false });
        await AssertRefusal("bootstrap_hook_validate_config_refused");
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
        Assert.Empty(_engine.Paths);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("foreign")]
    public async Task VerifyAsync_RefusesPluginIdentityChange(string fault)
    {
        Qualified();
        _engine.Respond = _ => Json(fault == "foreign"
            ? PluginJson("other:latest", true, PolicySetting())
            : PluginJson(Plugin + ":latest", false, PolicySetting()));
        await AssertRefusal("bootstrap_plugin_identity_changed");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task VerifyAsync_RefusesMissingOrAmbiguousCurrentPolicy(int policies)
    {
        Qualified();
        var settings = new List<string>();
        for (var i = 0; i < policies; i++) settings.Add("resource_admission_policy=" + Drifted());
        if (policies == 0) settings.Add("something_else=1");
        _engine.Respond = _ => Json(PluginJson(Plugin + ":latest", true, settings.ToArray()));
        await AssertRefusal("bootstrap_current_policy_unavailable");
    }

    [Fact]
    public async Task VerifyAsync_RefusesCurrentPolicyDrift()
    {
        Qualified();
        _engine.Respond = _ => Json(PluginJson(Plugin + ":latest", true,
            "resource_admission_policy=" + Drifted()));
        await AssertRefusal("bootstrap_resource_admission_unqualified");
    }

    [Fact]
    public async Task VerifyAsync_RefusesEngineReadFailureAsUnknownNotUnneeded()
    {
        Qualified();
        _engine.Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        await AssertRefusal("bootstrap_current_policy_unavailable");
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("port")]
    [InlineData("dns")]
    public async Task VerifyAsync_RefusesReceiverBindingMismatch(string fault)
    {
        Qualified();
        var receiver = Receiver();
        receiver = fault switch
        {
            "disabled" => new OpenShellBootstrapReceiverOptions { Port = 17443, IngressDnsName = "ingress.example.invalid" },
            "port" => new OpenShellBootstrapReceiverOptions { Enabled = true, Port = 18000, IngressDnsName = "ingress.example.invalid" },
            _ => new OpenShellBootstrapReceiverOptions { Enabled = true, Port = 17443, IngressDnsName = "elsewhere.example.invalid" },
        };
        var ex = await Assert.ThrowsAnyAsync<OpenShellStateConflictException>(() =>
            new OpenShellPreparationPreflight(Http(_engine), _root, Plugin, receiver).VerifyAsync(Selection(), default));
        Assert.Contains("bootstrap_receiver_binding_unqualified", ex.Message);
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
        services.AddSingleton<IOpenShellRuntimeEvidenceReader, Reader>();
        services.AddSingleton<IOpenShellBootstrapProbe, Probe>();
        services.AddSingleton<IOpenShellArtifactCleanup, Cleanup>();
        // Real preflight, qualification root deliberately absent: refuse, never simulate.
        services.AddSingleton<IOpenShellPreparationPreflight>(new OpenShellPreparationPreflight(
            Http(_engine), Path.Combine(_root, "not-installed"), Plugin, Receiver()));
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

    private sealed class Reader : IOpenShellRuntimeEvidenceReader
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
        => new(Http(_engine), root ?? _root, plugin ?? Plugin, Receiver());

    private static OpenShellBootstrapReceiverOptions Receiver() => new()
    { Enabled = true, Port = 17443, IngressDnsName = "ingress.example.invalid" };

    private static OpenShellPreparationScope Selection(OpenShellLaunchPlan? plan = null)
    {
        var p = plan ?? OpenShellPolicyCompilerTests.Plan();
        return new OpenShellPreparationScope(1, "start", null, p.Hash, DateTime.UtcNow.AddSeconds(100), p);
    }

    /// <summary>Writes the exact qualification-root contract the preflight expects.</summary>
    private void Qualified()
    {
        var hook = Encoding.UTF8.GetBytes("pinned-hook-bytes");
        File.WriteAllBytes(Path.Combine(_root, "hook"), hook);
        File.WriteAllText(Path.Combine(_root, "hook-sha256"), Convert.ToHexStringLower(SHA256.HashData(hook)));
        Write("describe.json", new JsonObject { ["plugin"] = Plugin, ["scope"] = "local" });
        Write("validate-config.json", new JsonObject { ["accepted"] = true });
        _engine.Respond = _ => Json(PluginJson(Plugin + ":latest", true, PolicySetting()));
    }

    private string PolicySetting() => "resource_admission_policy=" + OpenShellPolicyCompilerTests.Admission().PolicyAcknowledgement;

    // Real drift: the live policy must not admit the workspace anymore.
    private static string Drifted() => OpenShellPolicyCompilerTests.Admission().PolicyAcknowledgement
        .Replace("\"enabled\":true", "\"enabled\":false");

    private void Write(string name, JsonNode node) => File.WriteAllText(Path.Combine(_root, name), node.ToJsonString());

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string PluginJson(string name, bool enabled, params string[] settings)
    {
        var json = new JsonObject { ["Name"] = name, ["Enabled"] = enabled,
            ["Settings"] = new JsonArray(settings.Select(s => JsonValue.Create(s)).ToArray<JsonNode?>()) };
        return json.ToJsonString();
    }

    private async Task AssertRefusal(string code, OpenShellPreparationScope? selection = null)
    {
        var ex = await Assert.ThrowsAnyAsync<OpenShellStateConflictException>(() =>
            _preflight().VerifyAsync(selection ?? Selection(), default));
        Assert.Contains(code, ex.Message);
    }
}
