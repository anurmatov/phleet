using System.Text.Json;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Shared;

namespace Fleet.Orchestrator.Tests.Services;

/// <summary>
/// What a provision decides for a gemini agent, through the real
/// <see cref="ContainerProvisioningService"/>. The provider runs Google's Antigravity CLI with a
/// Gemini API key from its environment: the agent holds the key as the <c>GEMINI_API_KEY</c> Env
/// Ref, no credential file is mounted, the CLI's home (<c>/root/.gemini</c>) is a host directory
/// that survives a reprovision, and the CLI sends MCP headers like the other two.
/// </summary>
/// <remarks>
/// Without the key the agent would start and fail every turn, so a provision refuses it by name
/// (<c>gemini_api_key_missing</c>) before a file is written, a reprovision before the old container
/// is touched, and a preview reports it. All names, hosts and values are synthetic.
/// </remarks>
public sealed class GeminiProvisioningTests
{
    private const string KeyRef = "GEMINI_API_KEY";
    private const string KeyValue = "synthetic-gemini-key-9d27c4";
    private const string Fault = "gemini_api_key_missing";
    private const string Container = "fleet-agent1";
    private const string HomeBind = "./workspaces/fleet-agent1/gemini:/root/.gemini";

    private static readonly Dictionary<string, string?> ForwarderOn = new() { ["SecretProxy:Enabled"] = "true" };

    private static Agent NewAgent(string provider = "gemini", bool withKey = true) => new()
    {
        Name = "agent1",
        DisplayName = "agent1",
        Role = "developer",
        Model = provider == "codex" ? "gpt-5.5" : provider == "gemini" ? "gemini-4-argon-medium" : "claude-sonnet-4-6",
        Provider = provider,
        ContainerName = Container,
        MemoryLimitMb = 512,
        EnvRefs = withKey ? [new() { EnvKeyName = KeyRef }] : [],
        McpEndpoints = [new() { McpName = "fleet-telegram", Url = "http://fleet-telegram:3800", TransportType = "http" }],
    };

    /// <param name="envLine">The key's line in <c>.env</c>; null leaves the key out of the file.</param>
    private static async Task<ProvisioningHarness> StartAsync(
        Agent agent, string? envLine = KeyRef + "=" + KeyValue, IReadOnlyDictionary<string, string?>? config = null,
        SecretBinding? binding = null)
    {
        var harness = ProvisioningHarness.Create(config);
        if (envLine is not null)
            File.AppendAllText(harness.EnvFile, envLine + "\n");
        // Whatever an earlier setup left on the host: nothing mounts it any more.
        foreach (var file in new[] { ".claude-credentials.json", ".codex-credentials.json", ".gemini-credentials.json" })
            File.WriteAllText(Path.Combine(harness.BaseDir, file), "{}");
        var ca = Path.Combine(harness.BaseDir, SecretProxyState.CaDirectory);
        Directory.CreateDirectory(ca);
        File.WriteAllText(Path.Combine(ca, SecretProxyState.CaCertificateFile), "synthetic certificate\n");
        await harness.SeedAsync(db =>
        {
            if (binding is not null) db.SecretBindings.Add(binding);
            db.Agents.Add(agent);
        });
        return harness;
    }

    private static List<string> EnvOf(string createBody) =>
        [.. JsonDocument.Parse(createBody).RootElement.GetProperty("Env").EnumerateArray().Select(e => e.GetString()!)];

    private static List<string> BindsOf(string createBody) =>
        [.. JsonDocument.Parse(createBody).RootElement.GetProperty("HostConfig").GetProperty("Binds").EnumerateArray().Select(e => e.GetString()!)];

    private static string LogsOf(ProvisioningHarness harness) => string.Join("\n", harness.Logs.Entries.Select(e => e.Message));

    /// <summary>The old container, and the files its provision generated, as a reprovision finds them.</summary>
    private static string LeaveARunningContainer(ProvisioningHarness harness)
    {
        harness.ContainerExists = true;
        var generated = harness.GeneratedDir(Container);
        Directory.CreateDirectory(generated);
        var sentinel = Path.Combine(generated, "appsettings.json");
        File.WriteAllText(sentinel, "the running container's file");
        return sentinel;
    }

    private static void AssertTheOldContainerWasLeftAlone(ProvisioningHarness harness, string sentinel)
    {
        Assert.Empty(harness.CreateBodies);
        Assert.DoesNotContain(harness.DockerRequests,
            r => r.StartsWith("DELETE ", StringComparison.Ordinal) || r.EndsWith("/stop", StringComparison.Ordinal));
        Assert.Equal("the running container's file", File.ReadAllText(sentinel));
        Assert.Single(Directory.GetFiles(harness.GeneratedDir(Container), "*", SearchOption.AllDirectories));
    }

    // ── The names ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheEnvRefAndTheFault_AreNamedOnce()
    {
        Assert.Equal(KeyRef, ProvisioningFaults.GeminiApiKeyEnvRef);
        Assert.Equal(Fault, ProvisioningFaults.GeminiApiKeyMissing);
        Assert.Equal(HomeBind, ContainerSpecBuilder.GeminiHomeBind(Container));
        Assert.Equal("/root/.gemini", ContainerSpecBuilder.GeminiHome);
    }

    // ── What the container is created with ───────────────────────────────────────────────────────

    [Fact]
    public async Task AGeminiAgent_IsCreatedWithItsKey_ItsCliHomeAsAHostDirectory_AndNoCredentialFile()
    {
        await using var harness = await StartAsync(NewAgent());

        var preview = await harness.Service.PreviewAsync("agent1");
        var result = await harness.Service.ProvisionAsync("agent1");

        Assert.True(result.Success, result.Message);
        var body = harness.CreateBodies.Single();
        // The key, like any other Env Ref's value.
        Assert.Contains($"{KeyRef}={KeyValue}", EnvOf(body));
        // The CLI's home, below the agent's own workspace directory on the host.
        Assert.Contains(HomeBind, preview.Desired.Binds);
        Assert.Contains($"{harness.BaseDir}/workspaces/{Container}/gemini:/root/.gemini", BindsOf(body));
        Assert.Single(BindsOf(body), b => b.Contains("/root/.gemini", StringComparison.Ordinal));
        // The Google sign-in file is on the host and is mounted nowhere.
        Assert.True(File.Exists(Path.Combine(harness.BaseDir, ".gemini-credentials.json")));
        Assert.DoesNotContain(".gemini-credentials.json", body);
        Assert.DoesNotContain("oauth_creds", body);
        Assert.DoesNotContain(preview.Desired.Binds, b => b.Contains("credentials", StringComparison.Ordinal));
        Assert.DoesNotContain(preview.Diffs, d => d.StartsWith(Fault, StringComparison.Ordinal));
        // The value is in the container's environment and nowhere an operator reads.
        Assert.DoesNotContain(KeyValue, result.Message + LogsOf(harness));
        foreach (var file in Directory.EnumerateFiles(harness.GeneratedDir(Container), "*", SearchOption.AllDirectories))
            Assert.DoesNotContain(KeyValue, File.ReadAllText(file));
    }

    /// <summary>The CLI sends headers, so the agent is given its identity and told it may use servers that carry one.</summary>
    [Fact]
    public async Task AGeminiAgent_GetsTheSameMcpConfiguration_AsAClaudeAgentWithTheSameRows()
    {
        var files = new Dictionary<string, string>();
        foreach (var provider in new[] { "claude", "gemini" })
        {
            await using var harness = await StartAsync(NewAgent(provider));
            var result = await harness.Service.ProvisionAsync("agent1");
            Assert.True(result.Success, result.Message);
            files[provider] = File.ReadAllText(Path.Combine(harness.GeneratedDir(Container), ".mcp.json"));

            var appsettings = JsonDocument.Parse(File.ReadAllText(Path.Combine(harness.GeneratedDir(Container), "appsettings.json")));
            Assert.True(appsettings.RootElement.GetProperty("Agent").GetProperty("McpHeaderSupport").GetBoolean());
        }

        Assert.Equal(files["claude"], files["gemini"]);
        var servers = JsonDocument.Parse(files["gemini"]).RootElement.GetProperty("mcpServers");
        foreach (var server in new[] { "fleet-telegram", "fleet-memory" })
            Assert.StartsWith("Bearer fa1.agent.agent1.", servers.GetProperty(server).GetProperty("headers").GetProperty("Authorization").GetString());
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    public async Task AnyOtherAgent_NeedsNoGeminiKey_AndGetsNoGeminiHome(string provider)
    {
        await using var harness = await StartAsync(NewAgent(provider, withKey: false), envLine: null);

        var preview = await harness.Service.PreviewAsync("agent1");
        var result = await harness.Service.ProvisionAsync("agent1");

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain("/root/.gemini", harness.CreateBodies.Single());
        Assert.DoesNotContain(preview.Diffs, d => d.StartsWith(Fault, StringComparison.Ordinal));
        Assert.Null(ProvisioningFaults.DescribeGeminiApiKeyFault(NewAgent(provider, withKey: false), new Dictionary<string, string>(), []));
    }

    // ── Without the key ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WithoutTheEnvRef_AProvisionIsRefused_BeforeAnyFileOrContainer()
    {
        // The value in .env is not enough: the agent must hold the Env Ref.
        await using var harness = await StartAsync(NewAgent(withKey: false));

        var result = await harness.Service.ProvisionAsync("agent1");

        Assert.False(result.Success);
        Assert.StartsWith(Fault + ": ", result.Message);
        Assert.Contains(KeyRef, result.Message);
        Assert.DoesNotContain(KeyValue, result.Message + LogsOf(harness));
        Assert.Empty(harness.CreateBodies);
        Assert.DoesNotContain(harness.DockerRequests, r => !r.StartsWith("GET ", StringComparison.Ordinal));
        Assert.False(Directory.Exists(harness.GeneratedDir(Container)));
    }

    [Fact]
    public async Task WithoutTheEnvRef_AReprovisionIsRefused_AndTheOldContainerAndItsFilesAreLeftAlone()
    {
        await using var harness = await StartAsync(NewAgent(withKey: false));
        var sentinel = LeaveARunningContainer(harness);

        var result = await harness.Service.ReprovisionAsync("agent1", session: AgentSessionFile.Fresh);

        Assert.False(result.Success);
        Assert.StartsWith(Fault + ": ", result.Message);
        AssertTheOldContainerWasLeftAlone(harness, sentinel);
    }

    [Fact]
    public async Task WithoutTheEnvRef_APreviewReportsTheFault_BesideItsDiff()
    {
        await using var harness = await StartAsync(NewAgent(withKey: false));

        var preview = await harness.Service.PreviewAsync("agent1");

        var fault = Assert.Single(preview.Diffs, d => d.StartsWith(Fault, StringComparison.Ordinal));
        Assert.Contains(KeyRef, fault);
        Assert.DoesNotContain(KeyValue, string.Join("\n", preview.Diffs));
        // A preview changes nothing.
        Assert.DoesNotContain(harness.DockerRequests, r => !r.StartsWith("GET ", StringComparison.Ordinal));
        Assert.False(Directory.Exists(harness.GeneratedDir(Container)));
    }

    /// <summary>
    /// An Env Ref whose key <c>.env</c> does not hold would reach the container as the literal
    /// <c>&lt;secret&gt;</c>, and a blank one as nothing: neither is a key, so both refuse like a
    /// missing Env Ref, as a codex local agent's server URL does.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("GEMINI_API_KEY=")]
    [InlineData("GEMINI_API_KEY=   ")]
    [InlineData("GEMINI_API_KEY=\"\"")]
    [InlineData("#GEMINI_API_KEY=synthetic-commented-out")]
    public async Task AnEnvRefWithNoValueInEnv_IsRefusedTheSameWay_OnEveryPath(string? envLine)
    {
        await using var harness = await StartAsync(NewAgent(), envLine);

        var preview = await harness.Service.PreviewAsync("agent1");
        var provision = await harness.Service.ProvisionAsync("agent1");

        Assert.Single(preview.Diffs, d => d.StartsWith(Fault, StringComparison.Ordinal));
        Assert.False(provision.Success);
        Assert.StartsWith(Fault + ": ", provision.Message);
        Assert.Contains(".env", provision.Message);
        Assert.Empty(harness.CreateBodies);
        Assert.False(Directory.Exists(harness.GeneratedDir(Container)));

        var sentinel = LeaveARunningContainer(harness);
        var reprovision = await harness.Service.ReprovisionAsync("agent1");

        Assert.False(reprovision.Success);
        Assert.StartsWith(Fault + ": ", reprovision.Message);
        AssertTheOldContainerWasLeftAlone(harness, sentinel);
    }

    [Fact]
    public async Task OnceTheKeyIsAttached_TheSameAgentProvisions()
    {
        await using var harness = await StartAsync(NewAgent(withKey: false));
        Assert.False((await harness.Service.ProvisionAsync("agent1")).Success);

        await harness.MutateAsync(db =>
        {
            db.AgentEnvRefs.Add(new AgentEnvRef { AgentId = db.Agents.Single().Id, EnvKeyName = KeyRef });
            return Task.CompletedTask;
        });
        var result = await harness.Service.ProvisionAsync("agent1");

        Assert.True(result.Success, result.Message);
        Assert.Contains($"{KeyRef}={KeyValue}", EnvOf(harness.CreateBodies.Single()));
    }

    // ── A bound key ──────────────────────────────────────────────────────────────────────────────

    private static SecretBinding Binding(string? exposeAs = null) => new()
    {
        EnvKeyName = KeyRef,
        Hosts = "generativelanguage.googleapis.com",
        HeaderName = "X-Goog-Api-Key",
        ExposeAs = exposeAs,
    };

    [Fact]
    public async Task ABoundKey_Counts_AndReachesTheAgentAsAPlaceholderOnly()
    {
        await using var harness = await StartAsync(NewAgent(), config: ForwarderOn, binding: Binding());

        var result = await harness.Service.ProvisionAsync("agent1");

        Assert.True(result.Success, result.Message);
        var body = harness.CreateBodies.Single();
        var delivered = Assert.Single(EnvOf(body), e => e.StartsWith(KeyRef + "=", StringComparison.Ordinal))[(KeyRef.Length + 1)..];
        Assert.True(SecretPlaceholders.IsPlaceholder(delivered));
        Assert.DoesNotContain(KeyValue, body);
        var hosts = JsonDocument.Parse(body).RootElement.GetProperty("HostConfig").GetProperty("ExtraHosts")
            .EnumerateArray().Select(e => e.GetString()!);
        Assert.Contains(hosts, h => h.StartsWith("generativelanguage.googleapis.com:", StringComparison.Ordinal));
    }

    /// <summary>The agent's CLI reads one variable. A placeholder under any other name is no key to it.</summary>
    [Theory]
    [InlineData("OTHER_NAME", false)]
    [InlineData("OTHER_NAME,GEMINI_API_KEY", true)]
    public async Task ABoundKey_MustBeExposedUnderItsOwnName(string exposeAs, bool provisions)
    {
        await using var harness = await StartAsync(NewAgent(), config: ForwarderOn, binding: Binding(exposeAs));

        var preview = await harness.Service.PreviewAsync("agent1");
        var result = await harness.Service.ProvisionAsync("agent1");

        Assert.Equal(provisions, result.Success);
        Assert.Equal(provisions ? 0 : 1, preview.Diffs.Count(d => d.StartsWith(Fault, StringComparison.Ordinal)));
        if (provisions) return;
        Assert.StartsWith(Fault + ": ", result.Message);
        Assert.Empty(harness.CreateBodies);
        Assert.False(Directory.Exists(harness.GeneratedDir(Container)));
    }

    /// <summary>A bound key with the forwarder off is the secret rule's refusal, as for any bound key.</summary>
    [Fact]
    public async Task ABoundKey_WithTheForwarderOff_IsRefusedByTheSecretRule_NotDeliveredInClear()
    {
        await using var harness = await StartAsync(NewAgent(), binding: Binding());

        var result = await harness.Service.ProvisionAsync("agent1");

        Assert.False(result.Success);
        Assert.StartsWith("secret_proxy_disabled", result.Message);
        Assert.Empty(harness.CreateBodies);
    }

    // ── The CLI home across a reprovision ────────────────────────────────────────────────────────

    /// <summary>
    /// The home is the agent's: a reprovision, with or without a new session, mounts the same host
    /// directory again and removes nothing in it. A new session is the session file alone, as for
    /// the other providers; the CLI's own conversation store is not the orchestrator's to delete.
    /// </summary>
    [Theory]
    [InlineData("auto")]
    [InlineData("fresh")]
    public async Task AReprovision_MountsTheSameHome_AndRemovesNothingInIt(string session)
    {
        await using var harness = await StartAsync(NewAgent());
        harness.ContainerExists = true;
        var home = Path.Combine(harness.BaseDir, "workspaces", Container, "gemini");
        var conversation = Path.Combine(home, "antigravity-cli", "conversations", "synthetic-conversation.json");
        Directory.CreateDirectory(Path.GetDirectoryName(conversation)!);
        File.WriteAllText(conversation, "kept");
        var sessionFile = Path.Combine(harness.BaseDir, "workspaces", Container, ".fleet", "session.json");
        Directory.CreateDirectory(Path.GetDirectoryName(sessionFile)!);
        File.WriteAllText(sessionFile, "{}");

        var result = await harness.Service.ReprovisionAsync("agent1", session: session);

        Assert.True(result.Success, result.Message);
        Assert.Contains($"{home}:/root/.gemini", BindsOf(harness.CreateBodies.Single()));
        Assert.Equal("kept", File.ReadAllText(conversation));
        Assert.Equal(session == "auto", File.Exists(sessionFile));
    }
}
