using Fleet.Orchestrator.Data;
using Microsoft.Extensions.Logging;

namespace Fleet.Orchestrator.Tests.Services;

/// <summary>
/// #367: provisioning puts <c>CLAUDE_CODE_MAX_CONTEXT_TOKENS</c> in the container env only for a
/// claude agent in local mode with <c>ContextWindow</c> set. Read from the real desired spec
/// (<c>PreviewAsync</c>: DB row → container env), so the DB mapping is exercised too.
/// </summary>
public class ContextWindowProvisioningTests
{
    private const string LocalUrl = "http://inference-host:11434";
    private const string EnvName = "CLAUDE_CODE_MAX_CONTEXT_TOKENS";

    private static Agent NewAgent(string provider, string? baseUrl, int? window) => new()
    {
        Name = "agent-ctx",
        DisplayName = "Agent Ctx",
        Role = "developer",
        Model = baseUrl is null ? "claude-sonnet-4-6" : "qwen3.8:27b-agent",
        Provider = provider,
        AnthropicBaseUrl = baseUrl,
        ContextWindow = window,
        ContainerName = "fleet-agent-ctx",
        MemoryLimitMb = 512,
    };

    private static async Task<List<string>> DesiredEnvAsync(ProvisioningHarness harness, Agent agent)
    {
        await harness.SeedAsync(db => db.Agents.Add(agent));
        var preview = await harness.Service.PreviewAsync(agent.Name);
        return preview.Desired.Env;
    }

    [Fact]
    public async Task LocalAgent_WithAValue_GetsTheEnvWithThatValue()
    {
        await using var harness = ProvisioningHarness.Create();

        var env = await DesiredEnvAsync(harness, NewAgent("claude", LocalUrl, 131_072));

        Assert.Contains($"{EnvName}=131072", env);
        Assert.Single(env, e => e.StartsWith(EnvName + "=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LocalAgent_WithoutAValue_GetsNoEnv()
    {
        await using var harness = ProvisioningHarness.Create();

        var env = await DesiredEnvAsync(harness, NewAgent("claude", LocalUrl, null));

        Assert.DoesNotContain(env, e => e.StartsWith(EnvName + "=", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("gemini")]
    public async Task NonLocalAgent_WithAValue_GetsNoEnv(string provider)
    {
        await using var harness = ProvisioningHarness.Create();

        var env = await DesiredEnvAsync(harness, NewAgent(provider, null, 131_072));

        Assert.DoesNotContain(env, e => e.StartsWith(EnvName + "=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CloudAgent_WithAValue_ProvisionsAndLogsThatItWasIgnored()
    {
        await using var harness = ProvisioningHarness.Create();
        await harness.SeedAsync(db => db.Agents.Add(NewAgent("claude", null, 131_072)));

        var result = await harness.Service.ProvisionAsync("agent-ctx");

        Assert.True(result.Success, result.Message);
        Assert.Single(harness.Logs.Entries, e =>
            e.Level == LogLevel.Information && e.Message.Contains("ContextWindow 131072 ignored for 'agent-ctx'"));
    }

    [Fact]
    public async Task LocalAgent_WithAValue_LogsNothingAboutIgnoring()
    {
        await using var harness = ProvisioningHarness.Create();
        await harness.SeedAsync(db => db.Agents.Add(NewAgent("claude", LocalUrl, 65_536)));

        var result = await harness.Service.ProvisionAsync("agent-ctx");

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain(harness.Logs.Entries, e => e.Message.Contains("ignored for"));
    }
}
