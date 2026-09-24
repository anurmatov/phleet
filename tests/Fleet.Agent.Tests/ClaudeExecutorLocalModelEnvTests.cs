using Fleet.Agent.Configuration;
using Fleet.Agent.Services;

namespace Fleet.Agent.Tests;

/// <summary>
/// #340 D2/D4: the claude child's environment. Local mode sets the D2 pairs and removes the OAuth
/// token; off, the start info's environment is never materialised, so the child inherits as before.
/// </summary>
public class ClaudeExecutorLocalModelEnvTests
{
    private static AgentOptions Options(string provider, string? baseUrl) => new()
    {
        Name = "fleet-agent1",
        Role = "generic-role",
        WorkDir = "/workspace",
        Provider = provider,
        Model = "qwen3.8:27b-agent",
        AnthropicBaseUrl = baseUrl,
    };

    [Fact]
    public void LocalMode_SetsTheThirteenPairs_AndRemovesTheOAuthToken()
    {
        var env = new Dictionary<string, string?>
        {
            ["CLAUDE_CODE_OAUTH_TOKEN"] = "must-not-survive",
            ["ANTHROPIC_API_KEY"] = "must-be-blanked",
            ["PATH"] = "/usr/bin",
        };

        var applied = ClaudeExecutor.ConfigureLocalModelEnvironment(
            () => env, Options("claude", "http://inference-host:11434"));

        Assert.True(applied);
        Assert.False(env.ContainsKey("CLAUDE_CODE_OAUTH_TOKEN"));
        Assert.Equal("/usr/bin", env["PATH"]);
        Assert.Equal("http://inference-host:11434", env["ANTHROPIC_BASE_URL"]);
        Assert.Equal("", env["ANTHROPIC_API_KEY"]);
        Assert.Equal("phleet-local-no-auth", env["ANTHROPIC_AUTH_TOKEN"]);
        Assert.Equal("0", env["CLAUDE_CODE_ATTRIBUTION_HEADER"]);
        Assert.Equal("off", env["CLAUDE_CODE_TOTAL_TOKENS_REMINDER"]);
        Assert.Equal("1", env["DISABLE_ERROR_REPORTING"]);
        Assert.Equal("1", env["DISABLE_FEEDBACK_COMMAND"]);
        Assert.Equal("1", env["CLAUDE_CODE_DISABLE_FEEDBACK_SURVEY"]);
        Assert.Equal("0", env["CLAUDE_CODE_AUTO_MODE_SERVER"]);
        Assert.Equal("qwen3.8:27b-agent", env["ANTHROPIC_DEFAULT_OPUS_MODEL"]);
        Assert.Equal("qwen3.8:27b-agent", env["ANTHROPIC_DEFAULT_SONNET_MODEL"]);
        Assert.Equal("qwen3.8:27b-agent", env["ANTHROPIC_DEFAULT_HAIKU_MODEL"]);
        Assert.Equal("qwen3.8:27b-agent", env["CLAUDE_CODE_SUBAGENT_MODEL"]);
        Assert.Equal(14, env.Count);   // the 13 pairs plus PATH
    }

    [Theory]
    [InlineData("claude", null)]
    [InlineData("claude", "")]
    [InlineData("codex", "http://inference-host:11434")]
    [InlineData("gemini", "http://inference-host:11434")]
    public void Off_NeverTouchesTheEnvironment(string provider, string? baseUrl)
    {
        var sentinel = new Dictionary<string, string?> { ["CLAUDE_CODE_OAUTH_TOKEN"] = "kept" };
        var accessed = false;

        var applied = ClaudeExecutor.ConfigureLocalModelEnvironment(
            () => { accessed = true; return sentinel; }, Options(provider, baseUrl));

        Assert.False(applied);
        Assert.False(accessed);
        Assert.Equal("kept", Assert.Single(sentinel).Value);
    }
}
