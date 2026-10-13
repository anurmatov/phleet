using Fleet.Agent.Services;
using Fleet.Shared;

namespace Fleet.Agent.Tests;

/// <summary>
/// <see cref="LocalModel"/> (#382, #386): the URL rules the local harnesses share (L1, L2), the
/// codex local model rules (C1, C2), the pi rules (PI1–PI4), the canonical origin, and the codex
/// base URL derived from it.
/// </summary>
public class LocalModelTests
{
    private const string Url = "http://inference-host:11434";
    private const string CodexModel = "ollama/qwen3.8:27b";
    private const string ClaudeTag = "qwen3.8:27b";

    private static string? Fault(string? provider, string? baseUrl, string? model = CodexModel, string? effort = null) =>
        LocalModel.DescribeConfigFault(provider, baseUrl, model, effort);

    // ── Off: nothing to validate ─────────────────────────────────────────────

    [Theory]
    [InlineData("codex", null)]
    [InlineData("codex", "")]
    [InlineData("gemini", null)]
    [InlineData("claude", "")]
    public void NoBaseUrl_NoFault(string provider, string? baseUrl) =>
        Assert.Null(Fault(provider, baseUrl, "gpt-5"));

    [Fact]
    public void ValidCodexLocalConfig_NoFault() => Assert.Null(Fault("codex", Url));

    [Fact]
    public void ValidClaudeLocalConfig_NoFault() => Assert.Null(Fault("claude", Url, ClaudeTag));

    // The claude-only effort vocabulary (V7) does not reach codex, which forwards none verbatim.
    [Theory]
    [InlineData("none")]
    [InlineData("minimal")]
    [InlineData("high")]
    public void CodexLocal_AnyCodexEffort_NoFault(string effort) =>
        Assert.Null(Fault("codex", Url, CodexModel, effort));

    // ── L1 ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("gemini")]
    [InlineData("Codex")]
    [InlineData("Pi")]
    [InlineData(null)]
    public void L1_BaseUrlOnAnotherProvider_Rejected(string? provider) =>
        Assert.Equal(
            "Local model runs on claude, codex or pi; clear the local server URL first.",
            Fault(provider, Url, "gemini-2.5-pro"));

    [Fact]
    public void L1_TheTextIsTheSharedConstant() =>
        Assert.Equal("Local model runs on claude, codex or pi; clear the local server URL first.", LocalModel.L1Text);

    // ── PI1–PI4 (#386): a pi agent is always local and checked in full ───────

    private const string PiUrl = "http://inference-host:8000";
    private const string PiTag = "qwen3.8-flash-next";

    private static string? PiFault(string? baseUrl, string? model = PiTag, string? effort = null, int? contextWindow = 131072) =>
        LocalModel.DescribeConfigFault("pi", baseUrl, model, effort, contextWindow);

    [Theory]
    [InlineData(null)]
    [InlineData("on")]
    [InlineData("off")]
    [InlineData("")]
    public void Pi_ValidConfig_NoFault(string? effort)
    {
        Assert.Null(PiFault(PiUrl, effort: effort));
        Assert.True(LocalModel.IsEnabled("pi", PiUrl));
        Assert.True(LocalModel.IsPiEnabled("pi", PiUrl));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void PI1_NoUrl_Rejected(string? baseUrl) =>
        Assert.Equal("Pi runs only on a local server; set the local server URL.", PiFault(baseUrl));

    [Theory]
    [InlineData("http://inference-host:8000/v1", "has a path")]
    [InlineData("http://localhost:8000", "is the container")]
    public void PI_L2_StillAppliesToTheUrl(string baseUrl, string reason) =>
        Assert.Contains(reason, PiFault(baseUrl));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("qwen 3.8")]
    [InlineData("ollama/qwen3.8")]
    [InlineData("lmstudio/qwen3.8")]
    [InlineData("LMSTUDIO/qwen3.8")]
    [InlineData("zai/glm-5.3")]
    public void PI2_NotABareTag_Rejected(string? model) =>
        Assert.Equal(
            "Pi takes the bare model tag the server serves (no ollama/ or lmstudio/ prefix).",
            PiFault(PiUrl, model));

    [Theory]
    [InlineData("hf.co/org/model:q4")]
    [InlineData("qwen3.8:27b")]
    public void PI2_ABareTagWithAnUnreservedSlashOrColon_Accepted(string model) =>
        Assert.Null(PiFault(PiUrl, model));

    [Theory]
    [InlineData(null)]
    [InlineData(65535)]
    [InlineData(4096)]
    public void PI3_ContextUnsetOrBelow65536_Rejected(int? contextWindow) =>
        Assert.Equal(
            "Pi needs the server's context size, at least 65536; set the context window to the server's --ctx.",
            PiFault(PiUrl, contextWindow: contextWindow));

    [Fact]
    public void PI3_Exactly65536_Accepted() => Assert.Null(PiFault(PiUrl, contextWindow: 65536));

    [Theory]
    [InlineData("xhigh")]
    [InlineData("medium")]
    [InlineData("ON")]
    public void PI4_EffortNotOffOrOn_Rejected(string effort) =>
        Assert.Equal("Pi effort is off or on.", PiFault(PiUrl, effort: effort));

    // ── L2 ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("http://inference-host:11434/v1", "has a path")]
    [InlineData("http://inference-host:11434/v1/", "has a path")]
    [InlineData("http://inference-host:11434/api", "has a path")]
    [InlineData("http://inference-host:11434/?a=1", "has a query")]
    [InlineData("http://inference-host:11434/#x", "has a fragment")]
    [InlineData("http://user:pw@inference-host:11434", "has credentials")]
    [InlineData(" http://inference-host:11434", "has surrounding whitespace")]
    public void L2_Rejected_ForBothHarnesses(string input, string reason)
    {
        foreach (var (provider, model) in new[] { ("codex", CodexModel), ("claude", ClaudeTag) })
        {
            var fault = Fault(provider, input, model);

            Assert.NotNull(fault);
            Assert.Contains("Local server URL", fault);
            Assert.Contains(reason, fault);
            // The operator gives an origin; the text says which path each harness gets.
            Assert.Contains("Fleet adds /v1/messages for Claude and /v1 for Codex", fault);
        }
    }

    [Theory]
    [InlineData("http://localhost:11434")]
    [InlineData("http://127.0.0.1:11434")]
    [InlineData("http://[::1]:11434")]
    public void L2_ContainerLocalHost_Rejected_ForBothHarnesses(string input)
    {
        foreach (var (provider, model) in new[] { ("codex", CodexModel), ("claude", ClaudeTag) })
        {
            var fault = Fault(provider, input, model);

            Assert.NotNull(fault);
            Assert.Contains("Local server URL", fault);
            Assert.Contains("inside an agent container is the container", fault);
        }
    }

    [Fact]
    public void L2_FaultNeverEchoesTheValue()
    {
        var fault = Fault("codex", "http://someone:hunter2@inference-host:11434");

        Assert.NotNull(fault);
        Assert.DoesNotContain("hunter2", fault);
        Assert.DoesNotContain("someone", fault);
    }

    // ── C1 ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("qwen3.8:27b")]   // bare tag: the claude form
    [InlineData("zai/glm-5.3")]   // hosted
    [InlineData("gpt-5")]         // cloud
    [InlineData("owl/t-lite")]
    [InlineData("ollama/")]
    [InlineData("")]
    [InlineData(null)]
    public void C1_CodexLocalModelWithoutALocalPrefix_Rejected(string? model) =>
        Assert.Equal("Codex local model must be ollama/<tag> or lmstudio/<tag>.", Fault("codex", Url, model));

    [Theory]
    [InlineData("ollama/qwen3.8:27b")]
    [InlineData("lmstudio/qwen3.8:27b")]
    [InlineData("OLLAMA/qwen3.8:27b")]
    [InlineData("ollama/qwen3.8-flash-next:125b-a6b-q4_K_M")]
    [InlineData("ollama/hf.co/org/model:q4")]
    public void C1_C2_LocalPrefixAndSafeTag_Accepted(string model) =>
        Assert.Null(Fault("codex", Url, model));

    // ── C2 ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("ollama/qwen 27b")]
    [InlineData("ollama/-qwen")]
    [InlineData("ollama/:qwen")]
    [InlineData("ollama/qwen;rm")]
    [InlineData("lmstudio/qwen$(id)")]
    public void C2_UnsafeTagAfterThePrefix_Rejected(string model) =>
        Assert.Contains("not allowed in a CLI argument", Fault("codex", Url, model));

    [Fact]
    public void C2_TagLongerThan100_Rejected() =>
        Assert.Contains("not allowed in a CLI argument", Fault("codex", Url, "ollama/" + new string('q', 101)));

    // ── Canonical origin and the codex base URL ──────────────────────────────

    [Theory]
    [InlineData("http://Inference-Host:11434/", "http://inference-host:11434")]
    [InlineData("HTTP://inference-host:11434", "http://inference-host:11434")]
    [InlineData("http://inference-host:11434", "http://inference-host:11434")]
    [InlineData("http://inference-host:80/", "http://inference-host")]
    [InlineData("https://inference-host:443", "https://inference-host")]
    public void CanonicalizeBaseUrl_LowercasesAndDropsDefaultPortAndTrailingSlash(string input, string canonical)
    {
        Assert.Null(LocalModel.DescribeBaseUrlFault(input));
        Assert.Equal(canonical, LocalModel.CanonicalizeBaseUrl(input));
    }

    [Fact]
    public void CodexOssBaseUrl_IsTheCanonicalOriginPlusV1()
    {
        Assert.Equal("http://inference-host:11434/v1", LocalModel.CodexOssBaseUrl(Url));
        Assert.Equal(
            "http://inference-host:11434/v1",
            LocalModel.CodexOssBaseUrl(LocalModel.CanonicalizeBaseUrl("http://Inference-Host:11434/")));
    }

    // ── CodexLocalModelProviders.Split: the executor's routing, shared ───────

    [Theory]
    [InlineData("ollama/gpt-oss:20b", "ollama", "gpt-oss:20b")]
    [InlineData("OLLAMA/gpt-oss:20b", "ollama", "gpt-oss:20b")]
    [InlineData("lmstudio/qwen3-coder", "lmstudio", "qwen3-coder")]
    [InlineData("gpt-5", null, "gpt-5")]
    [InlineData("owl/t-lite", null, "owl/t-lite")]
    [InlineData("/gpt-oss:20b", null, "/gpt-oss:20b")]
    [InlineData("ollama/", null, "ollama/")]
    [InlineData("OLLAMA/x", "ollama", "x")]
    [InlineData("zai/glm-5.3", null, "zai/glm-5.3")]
    [InlineData("ollama/hf.co/org/model:q4", "ollama", "hf.co/org/model:q4")]
    public void Split_AgreesWithTheExecutor(string configured, string? expectedProvider, string expectedModel)
    {
        Assert.Equal((expectedProvider, expectedModel), CodexLocalModelProviders.Split(configured));
        Assert.Equal((expectedProvider, expectedModel), CodexExecutor.SplitLocalModel(configured));
    }

    // ── #386: PI5 path normalisation and the pi-only effort values ─────────────────────────────

    [Theory]
    [InlineData("/root/.claude-host/.credentials.json")]
    [InlineData("/root//.claude-host/.credentials.json")]
    [InlineData("/root/./.codex-host/auth.json")]
    [InlineData("/workspace/../root/.pi/agent/auth.json")]
    [InlineData("//root/.gemini//oauth_creds.json")]
    public void Pi5_ANormalisedCredentialMountPath_IsRefused_NamingThePathAsGiven(string mountPath) =>
        Assert.Equal(PiLocalModel.Pi5Text(mountPath), PiLocalModel.DescribeCredentialMountFault(mountPath));

    [Theory]
    [InlineData("/workspace/creds.json")]
    [InlineData("/root/claude/.credentials.json")]
    [InlineData("/root/.claude/../../workspace/creds.json")]
    public void Pi5_AnUnrelatedMountPath_IsAccepted(string mountPath) =>
        Assert.Null(PiLocalModel.DescribeCredentialMountFault(mountPath));

    [Theory]
    [InlineData("claude", "on", "Effort 'on' applies only to provider pi; clear it or choose an effort for claude.")]
    [InlineData("", "on", "Effort 'on' applies only to provider pi; clear it or choose an effort for claude.")]
    [InlineData("codex", "on", "Effort 'on' applies only to provider pi; clear it or choose an effort for codex.")]
    [InlineData("gemini", "on", "Effort 'on' applies only to provider pi; clear it or choose an effort for gemini.")]
    [InlineData("codex", "off", "Effort 'off' applies only to pi and local claude models; clear it or choose an effort for codex.")]
    [InlineData("gemini", "off", "Effort 'off' applies only to pi and local claude models; clear it or choose an effort for gemini.")]
    public void PiOnlyEffort_IsRefusedOnEveryOtherProvider(string provider, string effort, string expected) =>
        Assert.Equal(expected, PiLocalModel.DescribePiOnlyEffortFault(provider, effort));

    [Theory]
    [InlineData("pi", "on")]
    [InlineData("pi", "off")]
    [InlineData("claude", "off")] // claude local's off; claude cloud's off is V8
    [InlineData("claude", "xhigh")]
    [InlineData("codex", "none")]
    [InlineData("gemini", null)]
    public void PiOnlyEffort_LeavesEveryValidValueAlone(string provider, string? effort) =>
        Assert.Null(PiLocalModel.DescribePiOnlyEffortFault(provider, effort));
}
