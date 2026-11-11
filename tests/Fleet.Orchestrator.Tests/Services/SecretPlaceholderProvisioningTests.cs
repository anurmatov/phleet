using System.Text.Json;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Fleet.Orchestrator.Tests.Services;

/// <summary>
/// A bound secret through the real <see cref="ContainerProvisioningService.ProvisionAsync"/>: what the
/// container is created with, what lands on disk, and what the forwarder is told. All values are
/// synthetic.
/// </summary>
public class SecretPlaceholderProvisioningTests
{
    private const string BoundKey = "EXAMPLE_API_KEY";
    private const string BoundValue = "synthetic-bound-value-6b1f0c";
    private const string PlainKey = "PLAIN_SETTING";
    private const string PlainValue = "synthetic-plain-value-93ad";
    private const string Host = "api.example.test";

    private static readonly Dictionary<string, string?> Enabled = new() { ["SecretProxy:Enabled"] = "true" };

    private static Agent NewAgent(string name = "agent1", params string[] envRefs) => new()
    {
        Name = name,
        DisplayName = name,
        Role = "developer",
        Model = "claude-sonnet-4-6",
        Provider = "claude",
        ContainerName = "fleet-" + name,
        MemoryLimitMb = 512,
        EnvRefs = [.. envRefs.Select(k => new AgentEnvRef { EnvKeyName = k })],
    };

    private static SecretBinding Binding(string key = BoundKey, string hosts = Host) =>
        new() { EnvKeyName = key, Hosts = hosts };

    private static async Task<ProvisioningHarness> StartAsync(
        bool enabled = true, bool withCa = true, Action<OrchestratorDbContext>? seed = null)
    {
        var harness = ProvisioningHarness.Create(enabled ? Enabled : null);
        File.AppendAllText(harness.EnvFile, $"{BoundKey}={BoundValue}\n{PlainKey}={PlainValue}\n");
        if (withCa)
        {
            var ca = Path.Combine(harness.BaseDir, SecretProxyState.CaDirectory);
            Directory.CreateDirectory(ca);
            File.WriteAllText(Path.Combine(ca, SecretProxyState.CaCertificateFile), "synthetic certificate\n");
        }
        await harness.SeedAsync(db =>
        {
            if (seed is not null) seed(db);
            else
            {
                db.SecretBindings.Add(Binding());
                db.Agents.Add(NewAgent("agent1", BoundKey, PlainKey));
            }
        });
        return harness;
    }

    private static List<string> Env(string createBody) =>
        [.. JsonDocument.Parse(createBody).RootElement.GetProperty("Env").EnumerateArray().Select(e => e.GetString()!)];

    private static string ValueOf(string createBody, string name) =>
        Env(createBody).Single(e => e.StartsWith(name + "=", StringComparison.Ordinal))[(name.Length + 1)..];

    private static SecretProxyState State(ProvisioningHarness harness) =>
        SecretProxyState.TryParse(File.ReadAllText(harness.SecretState.StatePath!), out _)!;

    private static async Task<int> AgentIdAsync(ProvisioningHarness harness, string name)
    {
        await using var scope = harness.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Agents.SingleAsync(a => a.Name == name)).Id;
    }

    // ── Leaks ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BoundSecret_ReachesTheContainerOnlyAsAPlaceholder_AndAPlainRefIsUnchanged()
    {
        await using var harness = await StartAsync();

        var result = await harness.Service.ProvisionAsync("agent1");

        Assert.True(result.Success, result.Message);
        var body = Assert.Single(harness.CreateBodies);
        Assert.DoesNotContain(BoundValue, body);
        var placeholder = ValueOf(body, BoundKey);
        Assert.True(SecretPlaceholders.IsPlaceholder(placeholder));
        Assert.Equal(PlainValue, ValueOf(body, PlainKey));
    }

    [Fact]
    public async Task BoundSecret_IsInNoGeneratedFile_NoLogLine_AndTheDatabaseKeepsOnlyAHash()
    {
        await using var harness = await StartAsync();

        var result = await harness.Service.ProvisionAsync("agent1");

        Assert.True(result.Success, result.Message);
        var placeholder = ValueOf(harness.CreateBodies.Single(), BoundKey);

        // Everything provisioning wrote for the agent: appsettings, .mcp.json, instructions, contexts.
        var workspace = Path.Combine(harness.BaseDir, "workspaces");
        var files = Directory.EnumerateFiles(workspace, "*", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain(BoundValue, text);
            Assert.DoesNotContain(placeholder, text);
        }

        var logs = string.Join("\n", harness.Logs.Entries.Select(e => e.Message));
        Assert.DoesNotContain(BoundValue, logs);
        Assert.DoesNotContain(placeholder, logs);
        Assert.DoesNotContain(BoundValue, result.Message);
        Assert.DoesNotContain(placeholder, result.Message);

        await using var scope = harness.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var reference = await db.AgentEnvRefs.SingleAsync(r => r.EnvKeyName == BoundKey);
        Assert.Equal(SecretPlaceholders.Hash(placeholder), reference.PlaceholderSha256);
        Assert.Null((await db.AgentEnvRefs.SingleAsync(r => r.EnvKeyName == PlainKey)).PlaceholderSha256);
    }

    [Fact]
    public async Task ForwarderState_HoldsTheValueAndTheGrantHash_NeverThePlaceholder_AndIsPrivate()
    {
        await using var harness = await StartAsync();

        await harness.Service.ProvisionAsync("agent1");

        var placeholder = ValueOf(harness.CreateBodies.Single(), BoundKey);
        var path = harness.SecretState.StatePath!;
        var raw = File.ReadAllText(path);
        Assert.DoesNotContain(placeholder, raw);
        Assert.DoesNotContain(PlainValue, raw);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.GetDirectoryName(path)!));
        }

        var state = State(harness);
        var binding = Assert.Single(state.Bindings);
        Assert.Equal((BoundKey, BoundValue, "Authorization"), (binding.Key, binding.Value, binding.Header));
        Assert.Equal([Host], binding.Hosts);
        var grant = Assert.Single(state.Grants);
        Assert.Equal(("agent1", BoundKey, SecretPlaceholders.Hash(placeholder)), (grant.Agent, grant.Key, grant.PlaceholderSha256));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public async Task Preview_ShowsAMarker_MintsNothing_AndWritesNoState()
    {
        await using var harness = await StartAsync();

        var preview = await harness.Service.PreviewAsync("agent1");

        Assert.Contains($"{BoundKey}={SecretPlaceholders.PreviewValue}", preview.Desired.Env);
        Assert.DoesNotContain(preview.Desired.Env, e => e.Contains(BoundValue, StringComparison.Ordinal));
        Assert.Contains($"{Host}:{SecretProxyNetwork.DefaultAddress}", preview.Desired.ExtraHosts);
        Assert.Contains(SecretProxyNetwork.NetworkName, preview.Desired.Networks);
        Assert.False(File.Exists(harness.SecretState.StatePath!));
        await using var scope = harness.Services.CreateAsyncScope();
        Assert.All(await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().AgentEnvRefs.ToListAsync(),
            r => Assert.Null(r.PlaceholderSha256));
    }

    // ── Steering ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GrantedContainer_IsSteeredToTheForwarder_TrustsItsCa_AndJoinsTheNetworkAtItsFixedAddress()
    {
        await using var harness = await StartAsync();

        var result = await harness.Service.ProvisionAsync("agent1");

        Assert.True(result.Success, result.Message);
        using var body = JsonDocument.Parse(harness.CreateBodies.Single());
        var hostConfig = body.RootElement.GetProperty("HostConfig");
        Assert.Equal([$"{Host}:{SecretProxyNetwork.DefaultAddress}"],
            hostConfig.GetProperty("ExtraHosts").EnumerateArray().Select(h => h.GetString()));
        Assert.Contains(hostConfig.GetProperty("Binds").EnumerateArray().Select(b => b.GetString()),
            b => b == $"{harness.BaseDir}/{SecretProxyState.CaDirectory}/{SecretProxyState.CaCertificateFile}:{SecretProxyState.AgentCaPath}:ro");
        var env = Env(harness.CreateBodies.Single());
        Assert.Contains($"NODE_EXTRA_CA_CERTS={SecretProxyState.AgentCaPath}", env);
        Assert.Contains("REQUESTS_CA_BUNDLE=/etc/ssl/certs/ca-certificates.crt", env);
        Assert.Contains("SSL_CERT_FILE=/etc/ssl/certs/ca-certificates.crt", env);

        var id = await AgentIdAsync(harness, "agent1");
        Assert.Null(SecretProxyNetwork.TryParse(SecretProxyNetwork.DefaultSubnet, SecretProxyNetwork.DefaultAddress, out var network));
        var expected = network!.AgentAddress(id)!.ToString();
        var (name, connect) = Assert.Single(harness.NetworkConnects);
        Assert.Equal(SecretProxyNetwork.NetworkName, name);
        Assert.Equal(expected, JsonDocument.Parse(connect).RootElement
            .GetProperty("EndpointConfig").GetProperty("IPAMConfig").GetProperty("IPv4Address").GetString());
        Assert.Equal(expected, State(harness).Grants.Single().SourceIp);

        // The network is joined before the container starts.
        var requests = harness.DockerRequests.ToList();
        Assert.True(requests.FindIndex(r => r.Contains("/connect", StringComparison.Ordinal))
            < requests.FindIndex(r => r.EndsWith("/start", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AgentWithoutABoundSecret_IsCreatedExactlyAsBefore(bool enabled)
    {
        // The same agent, with and without the forwarder enabled: byte-identical Docker input.
        await using var baseline = await StartAsync(enabled: false, seed: db => db.Agents.Add(NewAgent("agent1", PlainKey)));
        await using var harness = await StartAsync(enabled, seed: db =>
        {
            db.SecretBindings.Add(Binding());
            db.Agents.Add(NewAgent("agent1", PlainKey));
        });

        await baseline.Service.ProvisionAsync("agent1");
        var result = await harness.Service.ProvisionAsync("agent1");

        Assert.True(result.Success, result.Message);
        Assert.Equal(
            baseline.CreateBodies.Single().Replace(baseline.BaseDir, "<base>"),
            harness.CreateBodies.Single().Replace(harness.BaseDir, "<base>"));
        Assert.Empty(harness.NetworkConnects);
        Assert.DoesNotContain("ExtraHosts", harness.CreateBodies.Single());
    }

    // ── Refusals: never fall back to the value ────────────────────────────────

    [Fact]
    public async Task ForwarderDisabled_RefusesTheProvision_BeforeDocker_AndNeverDeliversTheValue()
    {
        await using var harness = await StartAsync(enabled: false);

        var result = await harness.Service.ProvisionAsync("agent1");

        Assert.False(result.Success);
        Assert.StartsWith("secret_proxy_disabled", result.Message);
        Assert.Contains(BoundKey, result.Message);
        Assert.DoesNotContain(BoundValue, result.Message);
        Assert.Empty(harness.CreateBodies);
    }

    [Fact]
    public async Task ForwarderDisabled_PreviewStillNeverShowsTheValue()
    {
        await using var harness = await StartAsync(enabled: false);

        var preview = await harness.Service.PreviewAsync("agent1");

        Assert.DoesNotContain(preview.Desired.Env, e => e.StartsWith(BoundKey + "=", StringComparison.Ordinal));
        Assert.Contains(preview.Diffs, d => d.StartsWith("secret_proxy_disabled", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("SecretProxy:Enabled", "yes", "secret_proxy_setting_invalid")]
    [InlineData("SecretProxy:Subnet", "10.213.96.0/30", "secret_proxy_subnet_invalid")]
    [InlineData("SecretProxy:Address", "10.213.96.200", "secret_proxy_address_invalid")]
    public async Task MisconfiguredForwarder_RefusesTheProvision_WithTheSettingFault(string key, string value, string fault)
    {
        var config = new Dictionary<string, string?>(Enabled) { [key] = value };
        await using var harness = ProvisioningHarness.Create(config);
        File.AppendAllText(harness.EnvFile, $"{BoundKey}={BoundValue}\n");
        await harness.SeedAsync(db =>
        {
            db.SecretBindings.Add(Binding());
            db.Agents.Add(NewAgent("agent1", BoundKey));
        });

        var result = await harness.Service.ProvisionAsync("agent1");

        Assert.False(result.Success);
        Assert.StartsWith(fault, result.Message);
        Assert.Empty(harness.CreateBodies);
    }

    // The same spellings scripts/lib/comms-profiles.sh (fleet_secrets_enabled) accepts and refuses:
    // setup and upgrade start the forwarder for exactly the values the orchestrator enables it for.
    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("True")]
    [InlineData(" true ")]
    [InlineData("\"true\"")]
    [InlineData("'TRUE'")]
    public void EnabledSetting_AcceptsTrueInAnyLetterCase_WithBlanksAndOnePairOfQuotes(string value)
    {
        var settings = SecretProxySettings.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["SecretProxy:Enabled"] = value }).Build());

        Assert.True(settings.Enabled);
        Assert.Null(settings.Fault);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("false")]
    [InlineData("FALSE")]
    [InlineData("\"false\"")]
    public void EnabledSetting_IsOff_ForFalseOrNothing_WithNoFault(string? value)
    {
        var settings = SecretProxySettings.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["SecretProxy:Enabled"] = value }).Build());

        Assert.False(settings.Enabled);
        Assert.Null(settings.Fault);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("on")]
    [InlineData("tru")]
    [InlineData("\"true")]
    [InlineData("true # note")]
    [InlineData("truefalse")]
    public void EnabledSetting_IsAFault_ForAnythingElse_NeverSilentlyOff(string value)
    {
        var settings = SecretProxySettings.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["SecretProxy:Enabled"] = value }).Build());

        Assert.False(settings.Enabled);
        Assert.StartsWith("secret_proxy_setting_invalid", settings.Fault);
    }

    [Fact]
    public async Task MissingCa_RefusesTheProvision_BeforeDocker()
    {
        await using var harness = await StartAsync(withCa: false);

        var result = await harness.Service.ProvisionAsync("agent1");

        Assert.False(result.Success);
        Assert.StartsWith("secret_proxy_ca_missing", result.Message);
        Assert.Empty(harness.CreateBodies);
    }

    [Fact]
    public async Task NetworkAttachFailure_RemovesTheContainer_NeverStartsIt_AndLeavesNoGrant()
    {
        await using var harness = await StartAsync();
        harness.FailNetworkConnect = true;

        var result = await harness.Service.ProvisionAsync("agent1");

        Assert.False(result.Success);
        Assert.StartsWith("secret_proxy_network_unavailable", result.Message);
        Assert.Contains(harness.DockerRequests, r => r.StartsWith("DELETE ", StringComparison.Ordinal));
        Assert.DoesNotContain(harness.DockerRequests, r => r.EndsWith("/start", StringComparison.Ordinal));
        Assert.Empty(State(harness).Grants);
        await using var scope = harness.Services.CreateAsyncScope();
        Assert.All(await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().AgentEnvRefs.ToListAsync(),
            r => Assert.Null(r.PlaceholderSha256));
    }

    [Fact]
    public async Task TwoBoundSecretsExposedUnderOneName_RefuseTheProvision()
    {
        await using var harness = await StartAsync(seed: db =>
        {
            db.SecretBindings.Add(new SecretBinding { EnvKeyName = "FIRST_KEY", Hosts = Host, ExposeAs = "SHARED_NAME" });
            db.SecretBindings.Add(new SecretBinding { EnvKeyName = "SECOND_KEY", Hosts = "other.example.test", ExposeAs = "SHARED_NAME" });
            db.Agents.Add(NewAgent("agent1", "FIRST_KEY", "SECOND_KEY"));
        });

        var result = await harness.Service.ProvisionAsync("agent1");

        Assert.False(result.Success);
        Assert.StartsWith("secret_expose_conflict", result.Message);
        Assert.Empty(harness.CreateBodies);
    }

    // ── A reprovision that would be refused leaves the old container running ──

    private static void AssertOldContainerUntouched(ProvisioningHarness harness)
    {
        Assert.DoesNotContain(harness.DockerRequests, r => r.StartsWith("DELETE ", StringComparison.Ordinal));
        Assert.DoesNotContain(harness.DockerRequests, r => r.EndsWith("/stop", StringComparison.Ordinal));
        Assert.Empty(harness.CreateBodies);
    }

    [Fact]
    public async Task Reprovision_WithTheForwarderDisabled_RefusesBeforeTheOldContainerIsRemoved()
    {
        await using var harness = await StartAsync(enabled: false);
        harness.ContainerExists = true;

        var result = await harness.Service.ReprovisionAsync("agent1");

        Assert.False(result.Success);
        Assert.StartsWith("secret_proxy_disabled", result.Message);
        AssertOldContainerUntouched(harness);
    }

    [Theory]
    [InlineData("SecretProxy:Enabled", "yes", "secret_proxy_setting_invalid")]
    [InlineData("SecretProxy:Subnet", "10.213.96.0/30", "secret_proxy_subnet_invalid")]
    [InlineData("SecretProxy:Address", "10.213.96.200", "secret_proxy_address_invalid")]
    public async Task Reprovision_WithAMisconfiguredForwarder_RefusesBeforeTheOldContainerIsRemoved(string key, string value, string fault)
    {
        await using var harness = ProvisioningHarness.Create(new Dictionary<string, string?>(Enabled) { [key] = value });
        File.AppendAllText(harness.EnvFile, $"{BoundKey}={BoundValue}\n");
        await harness.SeedAsync(db =>
        {
            db.SecretBindings.Add(Binding());
            db.Agents.Add(NewAgent("agent1", BoundKey));
        });
        harness.ContainerExists = true;

        var result = await harness.Service.ReprovisionAsync("agent1");

        Assert.False(result.Success);
        Assert.StartsWith(fault, result.Message);
        AssertOldContainerUntouched(harness);
    }

    [Fact]
    public async Task Reprovision_WithoutTheCa_RefusesBeforeTheOldContainerIsRemoved()
    {
        await using var harness = await StartAsync(withCa: false);
        harness.ContainerExists = true;

        var result = await harness.Service.ReprovisionAsync("agent1");

        Assert.False(result.Success);
        Assert.StartsWith("secret_proxy_ca_missing", result.Message);
        AssertOldContainerUntouched(harness);
    }

    [Fact]
    public async Task Reprovision_WithConflictingExposures_RefusesBeforeTheOldContainerIsRemoved()
    {
        await using var harness = await StartAsync(seed: db =>
        {
            db.SecretBindings.Add(new SecretBinding { EnvKeyName = "FIRST_KEY", Hosts = Host, ExposeAs = "SHARED_NAME" });
            db.SecretBindings.Add(new SecretBinding { EnvKeyName = "SECOND_KEY", Hosts = "other.example.test", ExposeAs = "SHARED_NAME" });
            db.Agents.Add(NewAgent("agent1", "FIRST_KEY", "SECOND_KEY"));
        });
        harness.ContainerExists = true;

        var result = await harness.Service.ReprovisionAsync("agent1");

        Assert.False(result.Success);
        Assert.StartsWith("secret_expose_conflict", result.Message);
        AssertOldContainerUntouched(harness);
    }

    [Fact]
    public async Task Reprovision_WhenTheStateCannotBeWritten_RefusesBeforeTheOldContainerIsRemoved_AndKeepsItsGrant()
    {
        // The running container holds a working placeholder. A swap would have to revoke it and
        // publish a new one; neither can be published, so nothing is touched, not even the state.
        await using var harness = await StartAsync();
        await harness.Service.ProvisionAsync("agent1");
        var grant = State(harness).Grants.Single();
        var requestsBefore = harness.DockerRequests.Count;
        harness.ContainerExists = true;
        ForceWriteFailure(harness);

        var result = await harness.Service.ReprovisionAsync("agent1");

        Assert.False(result.Success);
        Assert.StartsWith("secret_proxy_state_write_failed", result.Message);
        Assert.Contains("left running", result.Message);
        Assert.DoesNotContain(harness.DockerRequests.Skip(requestsBefore), r => r.StartsWith("DELETE ", StringComparison.Ordinal));
        Assert.DoesNotContain(harness.DockerRequests.Skip(requestsBefore), r => r.EndsWith("/stop", StringComparison.Ordinal));
        Assert.Single(harness.CreateBodies);
        Assert.Equal(grant, State(harness).Grants.Single());
    }

    [Fact]
    public async Task Reprovision_WhenAnAgentLosesItsLastBoundSecret_StillChecksThatTheRevocationCanBePublished()
    {
        await using var harness = await StartAsync();
        await harness.Service.ProvisionAsync("agent1");
        await harness.MutateAsync(async db =>
            db.AgentEnvRefs.Remove(await db.AgentEnvRefs.SingleAsync(r => r.EnvKeyName == PlainKey)));
        // The bound reference stays in the database with its hash, but its binding is deleted: the
        // next provision delivers nothing through the forwarder and must revoke the old placeholder.
        await harness.MutateAsync(async db => db.SecretBindings.Remove(await db.SecretBindings.SingleAsync()));
        var requestsBefore = harness.DockerRequests.Count;
        harness.ContainerExists = true;
        ForceWriteFailure(harness);

        var result = await harness.Service.ReprovisionAsync("agent1");

        Assert.False(result.Success);
        Assert.StartsWith("secret_proxy_state_write_failed", result.Message);
        Assert.DoesNotContain(harness.DockerRequests.Skip(requestsBefore), r => r.StartsWith("DELETE ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reprovision_WithNothingWrong_ReplacesTheContainer_AndItsPlaceholder()
    {
        await using var harness = await StartAsync();
        await harness.Service.ProvisionAsync("agent1");
        var first = ValueOf(harness.CreateBodies[0], BoundKey);
        harness.ContainerExists = true;

        var result = await harness.Service.ReprovisionAsync("agent1");

        Assert.True(result.Success, result.Message);
        Assert.Contains(harness.DockerRequests, r => r.StartsWith("DELETE ", StringComparison.Ordinal));
        var second = ValueOf(harness.CreateBodies[1], BoundKey);
        Assert.NotEqual(first, second);
        Assert.Equal(SecretPlaceholders.Hash(second), State(harness).Grants.Single().PlaceholderSha256);
    }

    [Fact]
    public async Task Reprovision_OfAnAgentWithoutBoundSecrets_DoesNotTouchTheForwarderState()
    {
        await using var harness = await StartAsync(seed: db =>
        {
            db.SecretBindings.Add(Binding());
            db.Agents.Add(NewAgent("agent1", PlainKey));
        });
        harness.ContainerExists = true;
        ForceWriteFailure(harness);

        var result = await harness.Service.ReprovisionAsync("agent1");

        Assert.True(result.Success, result.Message);
        Assert.False(File.Exists(harness.SecretState.StatePath!));
    }

    // ── Revocation ────────────────────────────────────────────────────────────

    [Fact]
    public async Task EveryProvision_MintsANewPlaceholder_AndTheForwarderForgetsTheOldOne()
    {
        await using var harness = await StartAsync();

        await harness.Service.ProvisionAsync("agent1");
        var first = ValueOf(harness.CreateBodies[0], BoundKey);
        await harness.Service.ProvisionAsync("agent1");
        var second = ValueOf(harness.CreateBodies[1], BoundKey);

        Assert.NotEqual(first, second);
        Assert.Equal(SecretPlaceholders.Hash(second), State(harness).Grants.Single().PlaceholderSha256);
    }

    [Fact]
    public async Task Deprovision_RemovesTheGrant()
    {
        await using var harness = await StartAsync();
        await harness.Service.ProvisionAsync("agent1");
        Assert.Single(State(harness).Grants);
        harness.ContainerExists = true;

        var result = await harness.Service.DeprovisionAsync("agent1");

        Assert.True(result.Success, result.Message);
        Assert.Empty(State(harness).Grants);
        Assert.Single(State(harness).Bindings);
    }

    [Fact]
    public async Task RemovingTheEnvRef_ThenRewritingTheState_RemovesTheGrant()
    {
        await using var harness = await StartAsync();
        await harness.Service.ProvisionAsync("agent1");

        await harness.MutateAsync(async db =>
            db.AgentEnvRefs.Remove(await db.AgentEnvRefs.SingleAsync(r => r.EnvKeyName == BoundKey)));
        Assert.Null(await harness.SecretState.WriteAsync());

        Assert.Empty(State(harness).Grants);
    }

    [Fact]
    public async Task RotatedValueInEnv_ReachesTheStateOnTheNextWrite_WithTheSameGrant()
    {
        await using var harness = await StartAsync();
        await harness.Service.ProvisionAsync("agent1");
        var grant = State(harness).Grants.Single();

        File.WriteAllText(harness.EnvFile, $"{BoundKey}=synthetic-rotated-0e77\n");
        Assert.Null(await harness.SecretState.WriteAsync());

        Assert.Equal("synthetic-rotated-0e77", State(harness).Bindings.Single().Value);
        Assert.Equal(grant, State(harness).Grants.Single());
    }

    [Theory]
    [InlineData("")]
    [InlineData("EXAMPLE_API_KEY=\n")]
    [InlineData("EXAMPLE_API_KEY=<secret>\n")]
    public async Task ValueMissingFromEnv_IsWrittenAsNoValue_SoTheForwarderRefuses(string env)
    {
        await using var harness = await StartAsync();
        await harness.Service.ProvisionAsync("agent1");

        File.WriteAllText(harness.EnvFile, env);
        Assert.Null(await harness.SecretState.WriteAsync());

        Assert.Null(State(harness).Bindings.Single().Value);
    }

    [Fact]
    public async Task EveryWrite_RefreshesTheTimestamp_EvenWhenNothingElseChanged()
    {
        // The forwarder refuses a state that is too old, so an unchanged state is still rewritten.
        await using var harness = await StartAsync();
        await harness.Service.ProvisionAsync("agent1");
        var first = State(harness);

        await Task.Delay(20);
        Assert.Null(await harness.SecretState.WriteAsync(failClosed: false));
        var second = State(harness);

        Assert.True(second.GeneratedAtUtc > first.GeneratedAtUtc);
        Assert.True(DateTimeOffset.UtcNow - second.GeneratedAtUtc < TimeSpan.FromSeconds(SecretProxyState.HeartbeatSeconds));
        Assert.Equal(first.Bindings.Single(), second.Bindings.Single() with { Hosts = first.Bindings.Single().Hosts });
        Assert.Equal(first.Grants, second.Grants);
    }

    // ── A state write that fails is never a success ───────────────────────────

    private static void ForceWriteFailure(ProvisioningHarness harness, bool deleteFailsToo = false)
    {
        harness.SecretState.ReplaceFile = (_, _) => throw new IOException("synthetic: no space left on device");
        if (deleteFailsToo)
            harness.SecretState.DeleteFile = _ => throw new UnauthorizedAccessException("synthetic: read-only");
    }

    [Fact]
    public async Task Deprovision_WhenTheStateCannotBeWritten_FailsAndWithdrawsTheStateThatStillListedTheGrant()
    {
        await using var harness = await StartAsync();
        await harness.Service.ProvisionAsync("agent1");
        var path = harness.SecretState.StatePath!;
        Assert.Single(State(harness).Grants);
        harness.ContainerExists = true;
        ForceWriteFailure(harness);

        var result = await harness.Service.DeprovisionAsync("agent1");

        // Not "stopped and removed": the revocation did not reach the forwarder.
        Assert.False(result.Success);
        Assert.Contains("secret_proxy_state_write_failed", result.Message);
        Assert.Contains("not revoked", result.Message);
        // The old file authorised the grant. It is gone, so the forwarder refuses everything.
        Assert.False(File.Exists(path));
        Assert.Contains(harness.Logs.Entries, e => e.Message.Contains("withdrawn after a failed write", StringComparison.Ordinal));

        // The database did record the revocation, and the next good write publishes it.
        harness.SecretState.ReplaceFile = (target, content) => File.WriteAllText(target, content);
        Assert.Null(await harness.SecretState.WriteAsync());
        Assert.Empty(State(harness).Grants);
    }

    [Fact]
    public async Task Provision_WhenTheStateCannotBeWritten_FailsBeforeDocker_AndLeavesNoStateBehind()
    {
        await using var harness = await StartAsync();
        ForceWriteFailure(harness);

        var result = await harness.Service.ProvisionAsync("agent1");

        Assert.False(result.Success);
        Assert.Contains("secret_proxy_state_write_failed", result.Message);
        Assert.Empty(harness.CreateBodies);
        Assert.False(File.Exists(harness.SecretState.StatePath!));
    }

    [Fact]
    public async Task WhenTheStateCanBeNeitherWrittenNorWithdrawn_TheCallStillFails_AndTheOldFileIsLeftToTheAgeBound()
    {
        await using var harness = await StartAsync();
        await harness.Service.ProvisionAsync("agent1");
        var before = File.ReadAllText(harness.SecretState.StatePath!);
        harness.ContainerExists = true;
        ForceWriteFailure(harness, deleteFailsToo: true);

        var result = await harness.Service.DeprovisionAsync("agent1");

        Assert.False(result.Success);
        Assert.Contains("secret_proxy_state_write_failed", result.Message);
        // Nothing on disk could be changed. The file keeps its old timestamp, which is what the
        // forwarder's age bound acts on (Fleet.SecretProxy.Tests: StateOlderThanTheAgeBound_IsRefusedWhole).
        Assert.Equal(before, File.ReadAllText(harness.SecretState.StatePath!));
        Assert.Contains(harness.Logs.Entries, e => e.Message.Contains("neither written nor withdrawn", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BackgroundRefresh_ThatFails_ReportsTheFault_ButDoesNotWithdrawTheState()
    {
        // The refresh has no change of its own to enforce. A state it cannot refresh expires at the forwarder.
        await using var harness = await StartAsync();
        await harness.Service.ProvisionAsync("agent1");
        var before = File.ReadAllText(harness.SecretState.StatePath!);
        ForceWriteFailure(harness);

        var fault = await harness.SecretState.WriteAsync(failClosed: false);

        Assert.Equal("secret_proxy_state_write_failed", fault);
        Assert.Equal(before, File.ReadAllText(harness.SecretState.StatePath!));
    }

    [Fact]
    public async Task DisabledWriter_EmptiesAStateLeftByAnEnabledRun_AndCreatesNoneOtherwise()
    {
        await using var enabled = await StartAsync();
        await enabled.Service.ProvisionAsync("agent1");
        var path = enabled.SecretState.StatePath!;
        Assert.Single(State(enabled).Grants);

        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Provisioning:BaseDir"] = enabled.BaseDir }).Build();
        var disabled = new SecretProxyStateWriter(
            enabled.Services.GetRequiredService<IServiceScopeFactory>(), config, SecretProxySettings.Disabled,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SecretProxyStateWriter>.Instance);
        Assert.Null(await disabled.WriteAsync());

        var emptied = SecretProxyState.TryParse(File.ReadAllText(path), out var fault);
        Assert.Null(fault);
        Assert.Empty(emptied!.Bindings);
        Assert.Empty(emptied.Grants);

        await using var never = await StartAsync(enabled: false, seed: db => db.Agents.Add(NewAgent("agent1", PlainKey)));
        await never.Service.ProvisionAsync("agent1");
        Assert.False(File.Exists(never.SecretState.StatePath!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledWriter_NeverReportsAFault_EvenWhenALeftoverStateCannotBeEmptied(bool deleteFailsToo)
    {
        // With the feature off, a broken state directory must not fail ordinary saves.
        await using var enabled = await StartAsync();
        await enabled.Service.ProvisionAsync("agent1");
        var path = enabled.SecretState.StatePath!;
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Provisioning:BaseDir"] = enabled.BaseDir }).Build();
        var disabled = new SecretProxyStateWriter(
            enabled.Services.GetRequiredService<IServiceScopeFactory>(), config, SecretProxySettings.Disabled,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SecretProxyStateWriter>.Instance)
        {
            ReplaceFile = (_, _) => throw new IOException("synthetic"),
        };
        if (deleteFailsToo)
            disabled.DeleteFile = _ => throw new UnauthorizedAccessException("synthetic");

        Assert.Null(await disabled.WriteAsync());

        // Emptying failed, so the leftover was removed instead; where even that failed it stays, and
        // a forwarder still running refuses it once it is too old.
        Assert.Equal(deleteFailsToo, File.Exists(path));
    }

    // ── GitHub App ────────────────────────────────────────────────────────────

    private const string Pem = "c3ludGhldGljLXBlbS1iYXNlNjQ=";

    private static SecretBinding GitHubBinding() => new()
    {
        EnvKeyName = "GITHUB_APP_PEM",
        Kind = SecretBindingRules.KindGitHubApp,
        Hosts = "api.github.com,github.com",
        ExposeAs = "GH_TOKEN,GITHUB_TOKEN",
        AppIdEnvKey = "GITHUB_APP_ID",
    };

    [Fact]
    public async Task UnboundGitHubAppKey_IsStillInjectedIntoEveryAgent()
    {
        await using var harness = await StartAsync(enabled: false, seed: db => db.Agents.Add(NewAgent("agent1", "GITHUB_APP_ID")));
        File.AppendAllText(harness.EnvFile, $"GITHUB_APP_PEM={Pem}\nGITHUB_APP_ID=424242\n");

        await harness.Service.ProvisionAsync("agent1");

        Assert.Equal(Pem, ValueOf(harness.CreateBodies.Single(), "GITHUB_APP_PEM"));
    }

    [Fact]
    public async Task BoundGitHubAppKey_ReachesNoAgent_AndAGrantedAgentGetsOnePlaceholderUnderTheTokenNames()
    {
        await using var harness = await StartAsync(seed: db =>
        {
            db.SecretBindings.Add(GitHubBinding());
            db.Agents.Add(NewAgent("granted", "GITHUB_APP_PEM", "GITHUB_APP_ID"));
            db.Agents.Add(NewAgent("other", "GITHUB_APP_ID"));
        });
        File.AppendAllText(harness.EnvFile, $"GITHUB_APP_PEM={Pem}\nGITHUB_APP_ID=424242\n");

        Assert.True((await harness.Service.ProvisionAsync("granted")).Success);
        Assert.True((await harness.Service.ProvisionAsync("other")).Success);

        var granted = harness.CreateBodies[0];
        var other = harness.CreateBodies[1];
        Assert.DoesNotContain(Pem, granted);
        Assert.DoesNotContain(Pem, other);
        Assert.DoesNotContain(Env(granted), e => e.StartsWith("GITHUB_APP_PEM=", StringComparison.Ordinal));
        Assert.DoesNotContain(Env(other), e => e.StartsWith("GITHUB_APP_PEM=", StringComparison.Ordinal));
        var token = ValueOf(granted, "GH_TOKEN");
        Assert.True(SecretPlaceholders.IsPlaceholder(token));
        Assert.Equal(token, ValueOf(granted, "GITHUB_TOKEN"));
        Assert.DoesNotContain(Env(other), e => e.StartsWith("GH_TOKEN=", StringComparison.Ordinal));
        // The App id is not a secret and stays a plain reference.
        Assert.Equal("424242", ValueOf(granted, "GITHUB_APP_ID"));

        var binding = State(harness).Bindings.Single();
        Assert.Equal((SecretBindingRules.KindGitHubApp, Pem, "424242"), (binding.Kind, binding.Value, binding.AppId));
        Assert.Equal("granted", State(harness).Grants.Single().Agent);
    }

    // ── SSH keys: used through a socket, never delivered ───────────────────────

    private const string SshKeyName = "SSH_KEY_EXAMPLE";
    private const string SshKeyValue = "c3ludGhldGljLXNzaC1rZXktdGV4dC1mNGIyYTE=";
    private const string SshDestination = "deploy@build.example.test";
    private const string SshHostKeyLine =
        "build.example.test ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIGZha2VmYWtlZmFrZWZha2VmYWtlZmFrZWZha2VmYWtlZmFr";

    private static SecretBinding SshBinding() => new()
    {
        EnvKeyName = SshKeyName,
        Kind = SecretBindingRules.KindSshKey,
        Hosts = SshDestination,
        SshKnownHosts = SshHostKeyLine,
    };

    private static async Task<ProvisioningHarness> StartWithSshAsync(string agentName = "agent1", params string[] extraRefs)
    {
        var harness = await StartAsync(seed: db =>
        {
            db.SecretBindings.Add(Binding());
            db.SecretBindings.Add(SshBinding());
            db.Agents.Add(NewAgent(agentName, [SshKeyName, .. extraRefs]));
        });
        File.AppendAllText(harness.EnvFile, $"{SshKeyName}={SshKeyValue}\n");
        return harness;
    }

    [Fact]
    public async Task BoundSshKey_IsNeverInTheContainer_WhichGetsItsOwnSocketDirectoryInstead()
    {
        await using var harness = await StartWithSshAsync();

        var result = await harness.Service.ProvisionAsync("agent1");

        Assert.True(result.Success, result.Message);
        var body = harness.CreateBodies.Single();
        Assert.DoesNotContain(SshKeyValue, body);
        var env = Env(body);
        Assert.DoesNotContain(env, e => e.StartsWith(SshKeyName + "=", StringComparison.Ordinal));
        Assert.Contains("SSH_AUTH_SOCK=/run/fleet-secrets/ssh/agent.sock", env);
        // The placeholder of an SSH grant is recorded, not handed out.
        Assert.DoesNotContain(env, e => SecretPlaceholders.FindAll(e).Count > 0);

        using var json = JsonDocument.Parse(body);
        var hostConfig = json.RootElement.GetProperty("HostConfig");
        Assert.Contains(hostConfig.GetProperty("Binds").EnumerateArray().Select(b => b.GetString()),
            b => b == $"{harness.BaseDir}/secret-proxy/ssh/agent1:/run/fleet-secrets/ssh:ro");
        // The agent reaches the SSH server itself: nothing is steered to the forwarder for it.
        Assert.True(!hostConfig.TryGetProperty("ExtraHosts", out var extraHosts)
            || extraHosts.ValueKind == JsonValueKind.Null || extraHosts.GetArrayLength() == 0);
        Assert.True(Directory.Exists(Path.Combine(harness.BaseDir, "secret-proxy", "ssh", "agent1")));
    }

    [Fact]
    public async Task BoundSshKey_ReachesTheForwarderState_WithItsDestinationsHostKeysAndGrant()
    {
        await using var harness = await StartWithSshAsync();

        Assert.True((await harness.Service.ProvisionAsync("agent1")).Success);

        var state = State(harness);
        var binding = state.Bindings.Single(b => b.Key == SshKeyName);
        Assert.Equal(SecretBindingRules.KindSshKey, binding.Kind);
        Assert.Equal([SshDestination], binding.Hosts);
        Assert.Equal([SshHostKeyLine], binding.KnownHosts);
        Assert.Equal(SshKeyValue, binding.Value);
        var grant = Assert.Single(state.Grants);
        Assert.Equal(("agent1", SshKeyName), (grant.Agent, grant.Key));
    }

    [Fact]
    public async Task SshKeyAndHeaderSecretOnOneAgent_EachGoItsOwnWay()
    {
        await using var harness = await StartWithSshAsync("agent1", BoundKey);

        Assert.True((await harness.Service.ProvisionAsync("agent1")).Success);

        var body = harness.CreateBodies.Single();
        Assert.True(SecretPlaceholders.IsPlaceholder(ValueOf(body, BoundKey)));
        Assert.Contains("SSH_AUTH_SOCK=/run/fleet-secrets/ssh/agent.sock", Env(body));
        using var json = JsonDocument.Parse(body);
        Assert.Equal([$"{Host}:{SecretProxyNetwork.DefaultAddress}"],
            json.RootElement.GetProperty("HostConfig").GetProperty("ExtraHosts").EnumerateArray().Select(h => h.GetString()));
        Assert.Equal(2, State(harness).Grants.Count);
    }

    [Fact]
    public async Task StateWithoutAnSshKey_HasNoHostKeyMember_SoAnOlderForwarderStillReadsIt()
    {
        await using var harness = await StartAsync();

        Assert.True((await harness.Service.ProvisionAsync("agent1")).Success);

        Assert.DoesNotContain("knownHosts", File.ReadAllText(harness.SecretState.StatePath!));
    }

    [Fact]
    public async Task SshPreview_ShowsTheSocket_AndNeverTheKey()
    {
        await using var harness = await StartWithSshAsync();

        var preview = await harness.Service.PreviewAsync("agent1");

        Assert.Contains("SSH_AUTH_SOCK=/run/fleet-secrets/ssh/agent.sock", preview.Desired.Env);
        Assert.DoesNotContain(preview.Desired.Env, e => e.Contains(SshKeyValue, StringComparison.Ordinal));
        Assert.Contains(preview.Desired.Binds, b => b.EndsWith("/secret-proxy/ssh/agent1:/run/fleet-secrets/ssh:ro", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AgentWhoseNameCannotBeASocketDirectory_IsRefusedBeforeDocker()
    {
        var longName = new string('a', 49);
        await using var harness = await StartWithSshAsync(longName);

        var result = await harness.Service.ProvisionAsync(longName);

        Assert.False(result.Success);
        Assert.StartsWith("secret_ssh_agent_name_unsupported", result.Message);
        Assert.Empty(harness.CreateBodies);
    }

    [Fact]
    public async Task SshKeyNextToAPlainSocketVariable_IsRefusedBeforeDocker()
    {
        await using var harness = await StartWithSshAsync("agent1", "SSH_AUTH_SOCK");
        File.AppendAllText(harness.EnvFile, "SSH_AUTH_SOCK=/tmp/synthetic.sock\n");

        var result = await harness.Service.ProvisionAsync("agent1");

        Assert.False(result.Success);
        Assert.StartsWith("secret_expose_conflict", result.Message);
        Assert.Empty(harness.CreateBodies);
    }

    [Fact]
    public async Task Deprovision_RemovesTheSshGrant_SoTheForwarderClosesTheSocket()
    {
        await using var harness = await StartWithSshAsync();
        Assert.True((await harness.Service.ProvisionAsync("agent1")).Success);
        Assert.Single(State(harness).Grants);
        harness.ContainerExists = true;

        var result = await harness.Service.DeprovisionAsync("agent1");

        Assert.True(result.Success, result.Message);

        Assert.Empty(State(harness).Grants);
    }

    [Theory]
    [InlineData(new[] { "GH_TOKEN" }, null, "secret_binding_expose_invalid")]
    [InlineData(null, "GITHUB_APP_ID", "secret_binding_app_id_unexpected")]
    public void SshBinding_HasNoExposedNameAndNoAppId(string[]? exposeAs, string? appIdKey, string fault)
    {
        Assert.StartsWith(fault, SecretGrantPlanner.DescribeBindingFault(
            SshKeyName, SecretBindingRules.KindSshKey, [SshDestination], "Authorization", exposeAs, appIdKey, [SshHostKeyLine]));
        Assert.Null(SecretGrantPlanner.DescribeBindingFault(
            SshKeyName, SecretBindingRules.KindSshKey, [SshDestination], "Authorization", null, null, [SshHostKeyLine]));
    }
}
