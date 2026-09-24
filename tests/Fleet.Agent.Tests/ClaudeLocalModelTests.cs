using Fleet.Shared;

namespace Fleet.Agent.Tests;

/// <summary>
/// <see cref="ClaudeLocalModel"/> (#340 D1/D2): the one local-mode predicate, validation rules
/// V1–V7, the canonical base URL, and the claude child's environment.
/// </summary>
public class ClaudeLocalModelTests
{
    private const string Url = "http://host.docker.internal:11434";
    private const string Tag = "qwen3.8:27b-agent";

    private static string? Fault(string? baseUrl, string? model = Tag, string? effort = null, string provider = "claude") =>
        ClaudeLocalModel.DescribeConfigFault(provider, baseUrl, model, effort);

    // ── IsEnabled ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("claude", Url, true)]
    [InlineData("claude", " ", true)]   // whitespace-only is on, and a fault — never quietly off
    [InlineData("claude", "", false)]
    [InlineData("claude", null, false)]
    [InlineData("codex", Url, false)]
    [InlineData("gemini", Url, false)]
    [InlineData("Claude", Url, false)]
    [InlineData(null, Url, false)]
    public void IsEnabled_TruthTable(string? provider, string? baseUrl, bool expected) =>
        Assert.Equal(expected, ClaudeLocalModel.IsEnabled(provider, baseUrl));

    // ── Off: nothing to validate ─────────────────────────────────────────────

    [Theory]
    [InlineData("claude", "claude-opus-5-5", "high")]
    [InlineData("codex", "ollama/gpt-oss:20b", "low")]
    [InlineData("gemini", "gemini-2.5-pro", null)]
    public void NoBaseUrl_NoFault_WhateverTheRestOfTheConfig(string provider, string model, string? effort)
    {
        Assert.Null(ClaudeLocalModel.DescribeConfigFault(provider, null, model, effort));
        Assert.Null(ClaudeLocalModel.DescribeConfigFault(provider, "", model, effort));
    }

    [Fact]
    public void ValidLocalConfig_NoFault() => Assert.Null(Fault(Url));

    // ── V1 ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("codex")]
    [InlineData("gemini")]
    [InlineData("Claude")]
    public void V1_BaseUrlOnNonClaudeProvider_Rejected(string provider) =>
        Assert.Contains("applies only to provider claude", Fault(Url, provider: provider));

    // ── V2: the exact accept/reject table, with canonical form ───────────────

    [Theory]
    [InlineData("http://host.docker.internal:11434", "http://host.docker.internal:11434")]
    [InlineData("http://host.docker.internal:11434/", "http://host.docker.internal:11434")]
    [InlineData("HTTP://Host.Docker.Internal:11434", "http://host.docker.internal:11434")]
    [InlineData("https://inference.example:443/", "https://inference.example")]
    public void V2_Accepted_AndCanonicalized(string input, string canonical)
    {
        Assert.Null(Fault(input));
        Assert.Equal(canonical, ClaudeLocalModel.CanonicalizeBaseUrl(input));
    }

    [Theory]
    [InlineData("http://host.docker.internal:11434/v1", "has a path")]
    [InlineData("http://host.docker.internal:11434/v1/", "has a path")]
    [InlineData("http://host.docker.internal:11434/api", "has a path")]
    [InlineData("http://host.docker.internal:11434//", "has a path")]
    [InlineData("http://host.docker.internal:11434/?a=1", "has a query")]
    [InlineData("http://user:pw@host.docker.internal:11434", "has credentials")]
    [InlineData("ftp://host.docker.internal:11434", "must use http or https")]
    [InlineData(" http://host.docker.internal:11434", "has surrounding whitespace")]
    public void V2_Rejected(string input, string reason)
    {
        var fault = Fault(input);

        Assert.NotNull(fault);
        Assert.Contains(reason, fault);
        // Claude Code appends the suffix itself; the text says so.
        Assert.Contains("/v1/messages", fault);
    }

    [Theory]
    [InlineData("http://@host.docker.internal:11434", "has credentials")]  // empty userinfo still leaves '@'
    [InlineData("http://host.docker.internal:11434/#x", "has a fragment")]
    [InlineData("http://host.docker.internal:11434/?", "has a query")]
    [InlineData("http://host.docker.internal:11434 ", "has surrounding whitespace")]
    [InlineData("   ", "has surrounding whitespace")]
    [InlineData("host.docker.internal:11434", "must use http or https")]
    [InlineData("not a url", "is not an absolute URI")]
    public void V2_Rejected_EdgeCases(string input, string reason) =>
        Assert.Contains(reason, Fault(input));

    [Fact]
    public void V2_LongerThan500Characters_Rejected() =>
        Assert.Contains("longer than 500", Fault("http://" + new string('a', 490) + ".example"));

    [Fact]
    public void V2_FaultNeverEchoesTheValue()
    {
        var fault = Fault("http://someone:hunter2@host.docker.internal:11434");

        Assert.DoesNotContain("hunter2", fault);
        Assert.DoesNotContain("someone", fault);
    }

    // ── V3 ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("http://localhost:11434")]
    [InlineData("http://LocalHost:11434")]
    [InlineData("http://localhost.:11434")]
    [InlineData("http://127.0.0.1:11434")]
    [InlineData("http://127.1.2.3:11434")]
    [InlineData("http://127.1:11434")]
    [InlineData("http://[::1]:11434")]
    [InlineData("http://[::ffff:127.0.0.1]:11434")]
    [InlineData("http://0.0.0.0:11434")]
    [InlineData("http://[::]:11434")]
    public void V3_ContainerLocalHost_Rejected(string input) =>
        Assert.Contains("inside an agent container is the container", Fault(input));

    [Theory]
    [InlineData("http://192.0.2.10:11434")]
    [InlineData("http://inference-host:11434")]
    [InlineData("http://localhost-inference:11434")]
    public void V3_OtherHosts_Accepted(string input) => Assert.Null(Fault(input));

    // ── V4 ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("qwen3.8:27b-agent")]
    [InlineData("library/qwen3:8b")]
    [InlineData("gpt-oss_20b")]
    public void V4_SafeTag_Accepted(string model) => Assert.Null(Fault(Url, model));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("qwen 27b")]
    [InlineData("qwen;rm")]
    [InlineData("-qwen")]
    [InlineData(":qwen")]
    [InlineData("qwen\"x")]
    [InlineData("qwen$(id)")]
    public void V4_UnsafeTag_Rejected(string? model) =>
        Assert.Contains("not allowed in a CLI argument", Fault(Url, model));

    [Fact]
    public void V4_TagLongerThan100_Rejected() =>
        Assert.Contains("not allowed in a CLI argument", Fault(Url, new string('q', 101)));

    // ── V5 (case-insensitive, aliases and claude-*) ─────────────────────────

    [Theory]
    [InlineData("claude-opus-5-5")]
    [InlineData("Claude-Sonnet-5")]
    [InlineData("opus")]
    [InlineData("SONNET")]
    [InlineData("haiku")]
    public void V5_ClaudeModelId_Rejected(string model) =>
        Assert.Contains("is a Claude model id", Fault(Url, model));

    [Theory]
    [InlineData("opus-local")]
    [InlineData("claudette:7b")]
    public void V5_TagThatOnlyResemblesClaude_Accepted(string model) => Assert.Null(Fault(Url, model));

    // ── V6 ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("ollama/qwen3:8b")]
    [InlineData("LMStudio/qwen3")]
    [InlineData("zai/glm-5.3")]
    public void V6_CodexPathPrefix_Rejected(string model) =>
        Assert.Contains("selects the codex path", Fault(Url, model));

    // ── V7 (#349: local thinking vocabulary) ────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("off")]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("xhigh")]
    public void V7_AcceptsTheLocalVocabulary(string? effort) =>
        Assert.Null(Fault(Url, effort: effort));

    [Theory]
    [InlineData("high")]
    [InlineData("max")]
    [InlineData("none")]
    [InlineData("minimal")]
    [InlineData("XHIGH")]
    [InlineData("off ")]
    [InlineData(" low")]
    [InlineData("OFF")]
    public void V7_RejectsEverythingElse_Exactly(string effort) =>
        Assert.Equal(
            "Effort on a local Claude model must be empty (model default, sent as xhigh), off, low, medium or xhigh.",
            Fault(Url, effort: effort));

    // ── V8 (#349: off is local-only) ────────────────────────────────────────

    [Fact]
    public void V8_CloudClaudeOff_RejectedExactly() =>
        Assert.Equal(
            "Effort 'off' applies only to local Claude models; clear it or choose low, medium, high, xhigh or max.",
            ClaudeLocalModel.DescribeLocalOnlyEffortFault("claude", null, "off"));

    [Theory]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    [InlineData("xhigh")]
    [InlineData("max")]
    [InlineData(null)]
    [InlineData("")]
    public void V8_CloudClaudeOtherEfforts_Accepted(string? effort) =>
        Assert.Null(ClaudeLocalModel.DescribeLocalOnlyEffortFault("claude", null, effort));

    [Fact]
    public void V8_LocalOff_AndNonClaudeOff_Accepted()
    {
        Assert.Null(ClaudeLocalModel.DescribeLocalOnlyEffortFault("claude", Url, "off"));
        Assert.Null(ClaudeLocalModel.DescribeLocalOnlyEffortFault("codex", null, "off"));
    }

    // ── EffortArgument (#349) ────────────────────────────────────────────────

    [Theory]
    [InlineData(null, "xhigh")]
    [InlineData("", "xhigh")]
    [InlineData("low", "low")]
    [InlineData("medium", "medium")]
    [InlineData("xhigh", "xhigh")]
    public void EffortArgument_Table(string? effort, string? expected) =>
        Assert.Equal(expected, ClaudeLocalModel.EffortArgument(effort));

    [Fact]
    public void EffortArgument_OffMeansNoFlag() => Assert.Null(ClaudeLocalModel.EffortArgument("off"));

    // ── D2: the child environment ────────────────────────────────────────────

    [Fact]
    public void BuildEnvironment_IsExactlyTheThirteenPairsInOrder()
    {
        var env = ClaudeLocalModel.BuildEnvironment(Url, Tag, "xhigh");

        Assert.Equal(
            new KeyValuePair<string, string>[]
            {
                new("ANTHROPIC_BASE_URL", Url),
                new("ANTHROPIC_API_KEY", ""),
                new("ANTHROPIC_AUTH_TOKEN", "phleet-local-no-auth"),
                new("CLAUDE_CODE_ATTRIBUTION_HEADER", "0"),
                new("CLAUDE_CODE_TOTAL_TOKENS_REMINDER", "off"),
                new("DISABLE_ERROR_REPORTING", "1"),
                new("DISABLE_FEEDBACK_COMMAND", "1"),
                new("CLAUDE_CODE_DISABLE_FEEDBACK_SURVEY", "1"),
                new("CLAUDE_CODE_AUTO_MODE_SERVER", "0"),
                new("ANTHROPIC_DEFAULT_OPUS_MODEL", Tag),
                new("ANTHROPIC_DEFAULT_SONNET_MODEL", Tag),
                new("ANTHROPIC_DEFAULT_HAIKU_MODEL", Tag),
                new("CLAUDE_CODE_SUBAGENT_MODEL", Tag),
            },
            env);
    }

    [Fact]
    public void BuildEnvironment_OffAppendsTheExtraBodyConstantLast()
    {
        var env = ClaudeLocalModel.BuildEnvironment(Url, Tag, "off");
        Assert.Equal(14, env.Count);
        Assert.Equal(
            new KeyValuePair<string, string>(
                "CLAUDE_CODE_EXTRA_BODY", """{"thinking":{"type":"disabled"}}"""),
            env[^1]);
        // The thirteen inherited entries keep their order and are untouched.
        Assert.Equal("http://host.docker.internal:11434", env[0].Value);
        Assert.Equal(Tag, env[^2].Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("xhigh")]
    public void BuildEnvironment_NonOff_IsByteIdenticalToToday(string? effort)
    {
        var env = ClaudeLocalModel.BuildEnvironment(Url, Tag, effort);
        Assert.Equal(13, env.Count);
        Assert.DoesNotContain(env, e => e.Key == "CLAUDE_CODE_EXTRA_BODY");
    }

    [Fact]
    public void RemovedEnvVars_IsTheOAuthTokenPlusTheFiveThinkingOverrides() =>
        Assert.Equal(
        [
            "CLAUDE_CODE_OAUTH_TOKEN",
            "CLAUDE_CODE_EFFORT_LEVEL",
            "CLAUDE_CODE_EXTRA_BODY",
            "MAX_THINKING_TOKENS",
            "CLAUDE_CODE_DISABLE_THINKING",
            "CLAUDE_CODE_DISABLE_ADAPTIVE_THINKING",
        ], ClaudeLocalModel.RemovedEnvVars);
}
