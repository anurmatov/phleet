using System.Text.Json;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.Extensions.Logging;

namespace Fleet.Orchestrator.Tests.Services;

/// <summary>
/// #382 provisioning: codex local mode adds <c>Agent.CodexOssBaseUrl</c> and
/// <c>Agent.ContextWindow</c> to the generated appsettings and nothing to the container env; P1
/// stops a codex local model with no reachable server before any container exists; claude local
/// and legacy codex output stay equal to <c>main</c>'s.
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
}
