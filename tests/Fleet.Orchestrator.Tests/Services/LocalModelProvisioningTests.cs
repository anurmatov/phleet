using System.Text.Json;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.Extensions.Logging;

namespace Fleet.Orchestrator.Tests.Services;

/// <summary>
/// #382 provisioning: codex local mode adds <c>Agent.CodexOssBaseUrl</c> and
/// <c>Agent.ContextWindow</c> to the generated appsettings and nothing to the container env; P1
/// stops a codex local model with no reachable server before any container exists; claude local
/// and legacy codex output stay equal to <c>main</c>'s. #386: a pi agent gets <c>Agent.PiBaseUrl</c>,
/// <c>Agent.ContextWindow</c> and the auto-grants but no credential (A4); PI1–PI5 and PV1 stop
/// provisioning, and a reprovision, before Docker is called (A5b, A30).
/// </summary>
/// <remarks>
/// The <c>Fixtures/LocalModelMain</c> files were produced by these same agents through
/// <see cref="ContainerProvisioningService"/> at <c>main</c> 9766f51, before #382.
/// </remarks>
public class LocalModelProvisioningTests
{
    private const string Url = "http://inference-host:11434";
    private const string OssEnvRef = "CODEX_OSS_BASE_URL";

    private static Agent ClaudeLocal() => new()
    {
        Name = "agent-local", DisplayName = "Agent Local", Role = "tester",
        Model = "qwen3.8:27b", Provider = "claude", LocalBaseUrl = Url,
        ContextWindow = 131_072, ContainerName = "fleet-agent-local", MemoryLimitMb = 512,
    };

    private static Agent CodexLegacy() => new()
    {
        Name = "agent-legacy", DisplayName = "Agent Legacy", Role = "tester",
        Model = "ollama/qwen3.8:27b", Provider = "codex",
        ContainerName = "fleet-agent-legacy", MemoryLimitMb = 512,
        EnvRefs = [new AgentEnvRef { EnvKeyName = OssEnvRef }],
    };

    private static Agent CodexLocal(int? window = 131_072, bool withEnvRef = false) => new()
    {
        Name = "agent-codex", DisplayName = "Agent Codex", Role = "tester",
        Model = "ollama/qwen3.8:27b", Provider = "codex", LocalBaseUrl = Url,
        ContextWindow = window, ContainerName = "fleet-agent-codex", MemoryLimitMb = 512,
        EnvRefs = withEnvRef ? [new AgentEnvRef { EnvKeyName = OssEnvRef }] : [],
    };

    private static string Fixture(string agent, string file) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "LocalModelMain", agent, file));

    private static void AppendEnv(ProvisioningHarness h, string line) =>
        File.AppendAllText(Path.Combine(h.BaseDir, ".env"), line + "\n");

    private static async Task<(string Appsettings, List<string> Env)> ProvisionAsync(ProvisioningHarness h, Agent agent)
    {
        await h.SeedAsync(db => db.Agents.Add(agent));
        var preview = await h.Service.PreviewAsync(agent.Name);
        var result = await h.Service.ProvisionAsync(agent.Name);
        Assert.True(result.Success, result.Message);
        var appsettings = await File.ReadAllTextAsync(Path.Combine(h.GeneratedDir(agent.ContainerName), "appsettings.json"));
        return (appsettings, preview.Desired.Env);
    }

    // ── Acceptance 4 and the resolvable legacy row of 13: byte-equal to main ─────────────────────

    [Theory]
    [InlineData("claude-local")]
    [InlineData("codex-legacy")]
    public async Task ClaudeLocalAndLegacyCodex_OutputEqualsMain(string which)
    {
        await using var h = ProvisioningHarness.Create();
        AppendEnv(h, $"{OssEnvRef}={Url}/v1");

        var (appsettings, env) = await ProvisionAsync(h, which == "claude-local" ? ClaudeLocal() : CodexLegacy());

        Assert.Equal(Fixture(which, "appsettings.json"), appsettings);
        Assert.Equal(Fixture(which, "env.txt").Split('\n', StringSplitOptions.RemoveEmptyEntries), env);
    }

    // ── Acceptance 5: codex local generation ─────────────────────────────────────────────────────

    [Fact]
    public async Task CodexLocal_GetsTheCodexKeysAndNoEnv_WithoutAnEnvRef()
    {
        await using var h = ProvisioningHarness.Create();

        var (appsettings, env) = await ProvisionAsync(h, CodexLocal());

        var agent = JsonDocument.Parse(appsettings).RootElement.GetProperty("Agent");
        Assert.Equal($"{Url}/v1", agent.GetProperty("CodexOssBaseUrl").GetString());
        Assert.Equal(131_072, agent.GetProperty("ContextWindow").GetInt32());
        Assert.Equal(JsonValueKind.Null, agent.GetProperty("AnthropicBaseUrl").ValueKind);
        Assert.DoesNotContain(env, e => e.StartsWith(OssEnvRef + "=", StringComparison.Ordinal));
        Assert.DoesNotContain(env, e => e.StartsWith("CLAUDE_CODE_MAX_CONTEXT_TOKENS=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CodexLocal_WithoutAWindow_GetsTheUrlOnly()
    {
        await using var h = ProvisioningHarness.Create();

        var (appsettings, _) = await ProvisionAsync(h, CodexLocal(window: null));

        var agent = JsonDocument.Parse(appsettings).RootElement.GetProperty("Agent");
        Assert.Equal($"{Url}/v1", agent.GetProperty("CodexOssBaseUrl").GetString());
        Assert.False(agent.TryGetProperty("ContextWindow", out _));
    }

    [Fact]
    public void CodexLocal_WithACloudModel_IsRefusedAtGeneration()
    {
        var agent = CodexLocal();
        agent.Model = "gpt-5";

        var ex = Assert.Throws<InvalidOperationException>(
            () => ContainerProvisioningService.GenerateAppsettingsJson(agent, ""));

        Assert.Contains("must be ollama/<tag> or lmstudio/<tag>", ex.Message);
    }

    // ── Observability ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CodexLocal_LogsTheLocalModel_AndWarnsWhenTheEnvRefIsSuperseded()
    {
        await using var h = ProvisioningHarness.Create();
        AppendEnv(h, $"{OssEnvRef}=http://other-host:11434/v1");

        await ProvisionAsync(h, CodexLocal(withEnvRef: true));

        Assert.Single(h.Logs.Entries, e => e.Level == LogLevel.Information && e.Message ==
            $"Local model for 'agent-codex': harness codex, server {Url}, context window 131072");
        Assert.Single(h.Logs.Entries, e => e.Level == LogLevel.Warning
            && e.Message.Contains($"{OssEnvRef} Env Ref superseded by the local server URL"));
    }

    // ── Acceptance 13: P1 ────────────────────────────────────────────────────────────────────────

    public static TheoryData<string> P1Cases => ["no env ref", "blank value", "missing .env"];

    [Theory]
    [MemberData(nameof(P1Cases))]
    public async Task P1_UnreachableServer_FailsBeforeAnyContainerIsCreated(string when)
    {
        await using var h = when == "missing .env"
            ? ProvisioningHarness.Create(new Dictionary<string, string?>
                { ["Provisioning:EnvFilePath"] = Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.env") })
            : ProvisioningHarness.Create();
        if (when == "blank value") AppendEnv(h, $"{OssEnvRef}=");
        var agent = CodexLegacy();
        if (when == "no env ref") agent.EnvRefs = [];
        await h.SeedAsync(db => db.Agents.Add(agent));

        var result = await h.Service.ProvisionAsync(agent.Name);

        Assert.False(result.Success);
        Assert.Contains("has no server URL", result.Message);
        // Only the existing-container inspect reached Docker: nothing was created.
        Assert.Equal(1, h.DockerRequestCount);
        Assert.False(Directory.Exists(h.GeneratedDir(agent.ContainerName)));
    }

    [Theory]
    [MemberData(nameof(P1Cases))]
    public async Task P1_OnReprovision_FailsBeforeTheOldContainerIsRemoved(string when)
    {
        await using var h = when == "missing .env"
            ? ProvisioningHarness.Create(new Dictionary<string, string?>
                { ["Provisioning:EnvFilePath"] = Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.env") })
            : ProvisioningHarness.Create();
        if (when == "blank value") AppendEnv(h, $"{OssEnvRef}=");
        var agent = CodexLegacy();
        if (when == "no env ref") agent.EnvRefs = [];
        await h.SeedAsync(db => db.Agents.Add(agent));

        var result = await h.Service.ReprovisionAsync(agent.Name);

        Assert.False(result.Success);
        Assert.Contains("has no server URL", result.Message);
        Assert.Equal(0, h.DockerRequestCount);
    }

    [Fact]
    public void P1_DoesNotApplyToCodexLocalWithAUrl_OrToCloudCodex()
    {
        var env = new Dictionary<string, string>();

        Assert.Null(ContainerProvisioningService.DescribeCodexServerUrlFault(CodexLocal(), env));

        var cloud = CodexLegacy();
        cloud.Model = "gpt-5";
        cloud.EnvRefs = [];
        Assert.Null(ContainerProvisioningService.DescribeCodexServerUrlFault(cloud, env));
    }

    // ── #386 provider pi ─────────────────────────────────────────────────────────────────────────

    private const string PiUrl = "http://inference-host:8000";
    private const string PiTag = "qwen3.8-flash-next";

    private static Agent Pi(string name = "agent-pi", string? url = PiUrl, int? window = 131_072) => new()
    {
        Name = name, DisplayName = "Agent Pi", Role = "tester",
        Model = PiTag, Provider = "pi", LocalBaseUrl = url, Effort = "off",
        ContextWindow = window, ContainerName = $"fleet-{name}", MemoryLimitMb = 512,
    };

    private static void WriteHostCredentialFiles(ProvisioningHarness h)
    {
        foreach (var file in new[] { ".claude-credentials.json", ".codex-credentials.json", ".gemini-credentials.json" })
            File.WriteAllText(Path.Combine(h.BaseDir, file), "{}");
    }

    private static AgentCredentialMount CredentialMount(ProvisioningHarness h, string mountPath)
    {
        var path = Path.Combine(h.BaseDir, "credentials", $"{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{}");
        return new AgentCredentialMount
        {
            MountPath = mountPath,
            CredentialFile = new CredentialFile { Name = Path.GetFileName(path), FileName = "c.json", FilePath = path },
        };
    }

    private static List<string> CreatedBinds(ProvisioningHarness h) =>
        JsonDocument.Parse(Assert.Single(h.CreateBodies)).RootElement
            .GetProperty("HostConfig").GetProperty("Binds").EnumerateArray().Select(b => b.GetString()!).ToList();

    private static List<string> CreatedEnv(ProvisioningHarness h) =>
        JsonDocument.Parse(Assert.Single(h.CreateBodies)).RootElement
            .GetProperty("Env").EnumerateArray().Select(e => e.GetString()!).ToList();

    private static List<string> AllowedTools(string appsettings) =>
        JsonDocument.Parse(appsettings).RootElement.GetProperty("Agent").GetProperty("AllowedTools")
            .EnumerateArray().Select(t => t.GetString()!).ToList();

    /// <summary>Nothing reached Docker: no inspect, no create, no bind, and no file was generated.</summary>
    private static void AssertNothingProvisioned(ProvisioningHarness h, Agent agent)
    {
        Assert.Empty(h.DockerRequests);
        Assert.Empty(h.CreateBodies);
        Assert.False(Directory.Exists(h.GeneratedDir(agent.ContainerName)));
    }

    [Fact]
    public async Task A4_Pi_GetsPiBaseUrlAndContextWindow_TheAutoGrants_AndNoCredentialOrClaudeContextEnv()
    {
        await using var h = ProvisioningHarness.Create();
        WriteHostCredentialFiles(h);
        var agent = Pi();
        agent.Tools.Add(new AgentTool { ToolName = "mcp__fleet-memory__memory_search", IsEnabled = true });

        var (appsettings, previewEnv) = await ProvisionAsync(h, agent);

        var node = JsonDocument.Parse(appsettings).RootElement.GetProperty("Agent");
        Assert.Equal(PiUrl, node.GetProperty("PiBaseUrl").GetString());
        Assert.Equal(131_072, node.GetProperty("ContextWindow").GetInt32());
        Assert.Equal("pi", node.GetProperty("Provider").GetString());
        Assert.Equal(PiTag, node.GetProperty("Model").GetString());
        Assert.Equal(JsonValueKind.Null, node.GetProperty("AnthropicBaseUrl").ValueKind);
        Assert.False(node.TryGetProperty("CodexOssBaseUrl", out _));

        Assert.Equal(
            ["mcp__fleet-memory__memory_get", "mcp__fleet-memory__memory_search", "mcp__fleet-temporal__notify_cto"],
            AllowedTools(appsettings));

        var binds = CreatedBinds(h);
        Assert.DoesNotContain(binds, b => b.Contains("-credentials.json", StringComparison.Ordinal));
        Assert.DoesNotContain(binds, b => b.Contains("/root/.claude-host", StringComparison.Ordinal));
        Assert.DoesNotContain(binds, b => b.Contains("/root/.codex-host", StringComparison.Ordinal));
        Assert.DoesNotContain(binds, b => b.Contains("/root/.gemini", StringComparison.Ordinal));

        foreach (var env in new[] { previewEnv, CreatedEnv(h) })
        {
            Assert.DoesNotContain(env, e => e.StartsWith("CLAUDE_CODE_MAX_CONTEXT_TOKENS=", StringComparison.Ordinal));
            Assert.DoesNotContain(env, e => e.StartsWith("ANTHROPIC_", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A4_APiAgentNamedAsTheCtoAgent_GetsMemoryGetButNoNotifyCto()
    {
        await using var h = ProvisioningHarness.Create();

        var (appsettings, _) = await ProvisionAsync(h, Pi(ProvisioningHarness.CtoAgent));

        var tools = AllowedTools(appsettings);
        Assert.Contains("mcp__fleet-memory__memory_get", tools);
        Assert.DoesNotContain("mcp__fleet-temporal__notify_cto", tools);
    }

    [Fact]
    public async Task Pi_EmitsTheCanonicalOrigin()
    {
        await using var h = ProvisioningHarness.Create();

        var (appsettings, _) = await ProvisionAsync(h, Pi(url: "HTTP://Inference-Host:8000/"));

        Assert.Equal(PiUrl, JsonDocument.Parse(appsettings).RootElement.GetProperty("Agent").GetProperty("PiBaseUrl").GetString());
    }

    [Fact]
    public async Task Pi_LogsThePiLine_AndNotTheGenericLocalModelLine()
    {
        await using var h = ProvisioningHarness.Create();

        await ProvisionAsync(h, Pi());

        Assert.Single(h.Logs.Entries, e => e.Level == LogLevel.Information && e.Message ==
            $"Pi agent 'agent-pi': server {PiUrl}, model {PiTag}, context 131072");
        Assert.DoesNotContain(h.Logs.Entries, e => e.Message.StartsWith("Local model for 'agent-pi'", StringComparison.Ordinal));
    }

    // ── A5b: PI5 ─────────────────────────────────────────────────────────────────────────────────

    public static TheoryData<string> Pi5MountPaths =>
    [
        "/root/.claude-host/.credentials.json",
        "/root/.codex-host/auth.json",
        "/root/.gemini/oauth_creds.json",
        "/root/.pi/agent/auth.json",
        // Normalised the way Docker mounts them (#386 review): none of these slips past PI5.
        "/root//.claude-host/.credentials.json",
        "/root/./.codex-host/auth.json",
        "/workspace/../root/.pi/agent/auth.json",
    ];

    [Theory]
    [MemberData(nameof(Pi5MountPaths))]
    public async Task A5b_Pi5_ACredentialMount_FailsProvisioningNamingThePath_BeforeDocker(string mountPath)
    {
        await using var h = ProvisioningHarness.Create();
        var agent = Pi();
        agent.CredentialMounts.Add(CredentialMount(h, mountPath));
        await h.SeedAsync(db => db.Agents.Add(agent));

        var result = await h.Service.ProvisionAsync(agent.Name);

        Assert.False(result.Success);
        Assert.Equal(Fleet.Shared.PiLocalModel.Pi5Text(mountPath), result.Message);
        AssertNothingProvisioned(h, agent);
    }

    [Theory]
    [MemberData(nameof(Pi5MountPaths))]
    public async Task A5b_Pi5_OnReprovision_FailsBeforeTheOldContainerIsRemoved(string mountPath)
    {
        await using var h = ProvisioningHarness.Create();
        var agent = Pi();
        agent.CredentialMounts.Add(CredentialMount(h, mountPath));
        await h.SeedAsync(db => db.Agents.Add(agent));

        var result = await h.Service.ReprovisionAsync(agent.Name);

        Assert.False(result.Success);
        Assert.Equal(Fleet.Shared.PiLocalModel.Pi5Text(mountPath), result.Message);
        // DeprovisionAsync would have inspected the container first: nothing reached Docker.
        AssertNothingProvisioned(h, agent);
    }

    [Fact]
    public async Task A5b_AMountOutsideTheProviderDirectories_IsAcceptedAndMounted()
    {
        await using var h = ProvisioningHarness.Create();
        var agent = Pi();
        var mount = CredentialMount(h, "/workspace/creds.json");
        agent.CredentialMounts.Add(mount);

        await ProvisionAsync(h, agent);

        Assert.Contains($"{mount.CredentialFile.FilePath}:/workspace/creds.json:ro", CreatedBinds(h));
    }

    // ── PI1–PI4 at provisioning (#386 §9) ────────────────────────────────────────────────────────

    public static TheoryData<string, string> PiConfigFaults => new()
    {
        { "no url", Fleet.Shared.PiLocalModel.Pi1Text },
        { "window unset", Fleet.Shared.PiLocalModel.Pi3Text },
        { "window 65535", Fleet.Shared.PiLocalModel.Pi3Text },
        { "ollama model", Fleet.Shared.PiLocalModel.Pi2Text },
        { "effort xhigh", Fleet.Shared.PiLocalModel.Pi4Text },
    };

    private static Agent FaultyPi(string fault)
    {
        var agent = Pi();
        switch (fault)
        {
            case "no url": agent.LocalBaseUrl = null; break;
            case "window unset": agent.ContextWindow = null; break;
            case "window 65535": agent.ContextWindow = 65_535; break;
            case "ollama model": agent.Model = "ollama/" + PiTag; break;
            case "effort xhigh": agent.Effort = "xhigh"; break;
        }
        return agent;
    }

    [Theory]
    [MemberData(nameof(PiConfigFaults))]
    public async Task PiConfigFault_FailsProvisioning_BeforeDocker(string fault, string expected)
    {
        await using var h = ProvisioningHarness.Create();
        var agent = FaultyPi(fault);
        await h.SeedAsync(db => db.Agents.Add(agent));

        var result = await h.Service.ProvisionAsync(agent.Name);

        Assert.False(result.Success);
        Assert.Equal(expected, result.Message);
        AssertNothingProvisioned(h, agent);
    }

    [Theory]
    [MemberData(nameof(PiConfigFaults))]
    public async Task PiConfigFault_OnReprovision_FailsBeforeTheOldContainerIsRemoved(string fault, string expected)
    {
        await using var h = ProvisioningHarness.Create();
        var agent = FaultyPi(fault);
        await h.SeedAsync(db => db.Agents.Add(agent));

        var result = await h.Service.ReprovisionAsync(agent.Name);

        Assert.False(result.Success);
        Assert.Equal(expected, result.Message);
        AssertNothingProvisioned(h, agent);
    }

    // ── A30: PV1 at provisioning ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("foo")]
    [InlineData("Claude")]
    [InlineData("PI")]
    public async Task A30_AnUnknownProvider_FailsProvisioning_WithNoCreateAndNoBind(string provider)
    {
        await using var h = ProvisioningHarness.Create();
        WriteHostCredentialFiles(h);
        var agent = ClaudeLocal();
        agent.Provider = provider;
        agent.LocalBaseUrl = null;
        await h.SeedAsync(db => db.Agents.Add(agent));

        var provision = await h.Service.ProvisionAsync(agent.Name);
        var reprovision = await h.Service.ReprovisionAsync(agent.Name);

        var expected = $"Unknown provider '{provider}'; use claude, codex, gemini or pi.";
        Assert.Equal((false, expected), (provision.Success, provision.Message));
        Assert.Equal((false, expected), (reprovision.Success, reprovision.Message));
        AssertNothingProvisioned(h, agent);
    }
}
