using Fleet.Agent.Configuration;
using Fleet.Agent.Services;

namespace Fleet.Agent.Tests;

/// <summary>
/// #340 D2 and #460 AC-U5: the claude child's environment, applied from the harness plan. Local mode
/// removes the OAuth token and the thinking overrides, sets the D2 pairs, then the mode defaults; cloud
/// adds only <c>CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1</c> and keeps everything it inherits.
/// </summary>
public class ClaudeExecutorLocalModelEnvTests
{
    // BuildArgs writes {WorkDir}/system-prompt.md. A fixed path such as /workspace would fail on a
    // runner without it — and inside an agent container would overwrite that agent's live prompt.
    private static readonly string WorkDir = CreateWorkDir();

    private static string CreateWorkDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "claude-local-args-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static bool Apply(IDictionary<string, string?> env, AgentOptions options, string? contextWindow = null) =>
        ClaudeExecutor.ApplyHarnessEnvironment(env, options,
            new AgentHarness(options, name => name == Fleet.Shared.ContextWindow.EnvVar ? contextWindow : null,
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance).Plan!);

    private static AgentOptions Options(string provider, string? baseUrl) => new()
    {
        Name = "fleet-agent1",
        Role = "generic-role",
        WorkDir = WorkDir,
        Provider = provider,
        Model = "qwen3.8:27b-agent",
        AnthropicBaseUrl = baseUrl,
    };

    [Fact]
    public void LocalMode_SetsTheThirteenPairsAndTheDefault_AndRemovesTheOAuthToken()
    {
        var env = new Dictionary<string, string?>
        {
            ["CLAUDE_CODE_OAUTH_TOKEN"] = "must-not-survive",
            ["ANTHROPIC_API_KEY"] = "must-be-blanked",
            ["PATH"] = "/usr/bin",
        };

        var applied = Apply(env, Options("claude", "http://inference-host:11434"));

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
        Assert.Equal("1", env["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"]);
        // The 13 pairs, the #460 default and PATH; no MAX_MCP_OUTPUT_TOKENS without a window.
        Assert.Equal(15, env.Count);
    }

    [Fact]
    public void LocalMode_WithAContextWindow_AddsMaxMcpOutputTokens()
    {
        var env = new Dictionary<string, string?>();

        Assert.True(Apply(env, Options("claude", "http://inference-host:11434"), contextWindow: "131072"));
        Assert.Equal("8192", env["MAX_MCP_OUTPUT_TOKENS"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Cloud_AddsOnlyTheNonessentialTrafficDefault(string? baseUrl)
    {
        var env = new Dictionary<string, string?> { ["CLAUDE_CODE_OAUTH_TOKEN"] = "kept" };

        var applied = Apply(env, Options("claude", baseUrl), contextWindow: "131072");

        Assert.False(applied);
        Assert.Equal(
            new Dictionary<string, string?> { ["CLAUDE_CODE_OAUTH_TOKEN"] = "kept", ["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1" },
            env);
    }

    // ── #349: thinking env control ───────────────────────────────────────────

    [Fact]
    public void LocalMode_RemovesTheInheritedThinkingOverrides()
    {
        var env = new Dictionary<string, string?>
        {
            ["CLAUDE_CODE_OAUTH_TOKEN"] = "must-not-survive",
            ["CLAUDE_CODE_EFFORT_LEVEL"] = "low",                       // overrides --effort
            ["CLAUDE_CODE_EXTRA_BODY"] = """{"evil":"operator-input"}""",
            ["MAX_THINKING_TOKENS"] = "0",
            ["CLAUDE_CODE_DISABLE_THINKING"] = "1",
            ["CLAUDE_CODE_DISABLE_ADAPTIVE_THINKING"] = "1",
            ["PATH"] = "/usr/bin",
        };

        var applied = Apply(env, Options("claude", "http://inference-host:11434"));

        Assert.True(applied);
        Assert.False(env.ContainsKey("CLAUDE_CODE_OAUTH_TOKEN"));
        Assert.False(env.ContainsKey("CLAUDE_CODE_EFFORT_LEVEL"));
        // Effort is null here, so nothing sets it back: the inherited value is gone, not replaced.
        Assert.False(env.ContainsKey("CLAUDE_CODE_EXTRA_BODY"));
        Assert.False(env.ContainsKey("MAX_THINKING_TOKENS"));
        Assert.False(env.ContainsKey("CLAUDE_CODE_DISABLE_THINKING"));
        Assert.False(env.ContainsKey("CLAUDE_CODE_DISABLE_ADAPTIVE_THINKING"));
        Assert.Equal("/usr/bin", env["PATH"]);
    }

    [Fact]
    public void LocalModeOff_ReplacesAnInheritedExtraBodyWithTheConstant()
    {
        var env = new Dictionary<string, string?> { ["CLAUDE_CODE_EXTRA_BODY"] = """{"evil":"operator-input"}""" };
        var options = Options("claude", "http://inference-host:11434");
        options.Effort = "off";

        Assert.True(Apply(env, options));
        Assert.Equal("""{"thinking":{"type":"disabled"}}""", env["CLAUDE_CODE_EXTRA_BODY"]);
    }

    [Fact]
    public void LocalModeOff_SetsTheFixedExtraBodyConstant()
    {
        var env = new Dictionary<string, string?> { ["PATH"] = "/usr/bin" };
        var options = Options("claude", "http://inference-host:11434");
        options.Effort = "off";

        var applied = Apply(env, options);

        Assert.True(applied);
        Assert.Equal("""{"thinking":{"type":"disabled"}}""", env["CLAUDE_CODE_EXTRA_BODY"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("xhigh")]
    public void LocalModeNonOff_DoesNotSetExtraBody(string? effort)
    {
        var env = new Dictionary<string, string?> { ["PATH"] = "/usr/bin" };
        var options = Options("claude", "http://inference-host:11434");
        options.Effort = effort;

        Assert.True(Apply(env, options));
        Assert.False(env.ContainsKey("CLAUDE_CODE_EXTRA_BODY"));
    }

    // ── #349: the effort argument ────────────────────────────────────────────

    [Theory]
    [InlineData(null, true, "xhigh")]
    [InlineData("", true, "xhigh")]
    [InlineData("off", true, null)]
    [InlineData("low", true, "low")]
    [InlineData("medium", true, "medium")]
    [InlineData("xhigh", true, "xhigh")]
    public void BuildArgs_LocalMode_UsesTheLocalMapping(string? effort, bool local, string? expectedArg)
    {
        var options = Options("claude", local ? "http://inference-host:11434" : null);
        options.Effort = effort;
        var args = BuildArgsForTest(options);

        var hasEffort = args.Contains("--effort ");
        Assert.Equal(expectedArg is not null, hasEffort);
        if (expectedArg is not null)
            Assert.Contains($"--effort {expectedArg} ", args);
    }

    /// <summary>
    /// AC 5: cloud args, byte for byte, as <c>main</c> produced them before #349 — captured by
    /// running main's <c>BuildArgs</c> with these options; <c>{WORKDIR}</c> stands for the work dir —
    /// followed by the #460 harness arguments.
    /// </summary>
    private const string CloudPrefix =
        "-p --input-format stream-json --output-format stream-json --verbose --allowedTools \"Read,Write,Edit,Bash,Glob,Grep\" "
      + "--model claude-model-x --max-turns 50 --permission-mode acceptEdits ";

    // #460: the harness plan's arguments follow, last. No --tools: a cloud agent keeps every built-in.
    private const string CloudSuffix = "--append-system-prompt-file \"{WORKDIR}/system-prompt.md\" "
      + "--exclude-dynamic-system-prompt-sections --strict-mcp-config";

    [Theory]
    [InlineData(null, CloudPrefix + CloudSuffix)]
    [InlineData("low", CloudPrefix + "--effort low " + CloudSuffix)]
    [InlineData("medium", CloudPrefix + "--effort medium " + CloudSuffix)]
    [InlineData("high", CloudPrefix + "--effort high " + CloudSuffix)]
    [InlineData("xhigh", CloudPrefix + "--effort xhigh " + CloudSuffix)]
    [InlineData("max", CloudPrefix + "--effort max " + CloudSuffix)]
    public void BuildArgs_CloudMode_IsMainThenTheHarnessArgs(string? effort, string expectedFromMain)
    {
        var options = Options("claude", null);
        options.Model = "claude-model-x";
        options.Effort = effort;

        Assert.Equal(expectedFromMain, BuildArgsForTest(options).Replace(WorkDir, "{WORKDIR}"));
    }

    /// <summary>The real BuildArgs output, via the internal test hook — not a mirrored copy.</summary>
    private static string BuildArgsForTest(AgentOptions options) =>
        new ClaudeExecutor(
            Microsoft.Extensions.Options.Options.Create(options),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ClaudeExecutor>.Instance,
            new Fleet.Agent.Services.PromptBuilder(
                Microsoft.Extensions.Options.Options.Create(options),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<Fleet.Agent.Services.PromptBuilder>.Instance))
            .BuildArgsForTests();
}
