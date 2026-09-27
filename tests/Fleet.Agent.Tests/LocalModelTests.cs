using Fleet.Agent.Services;
using Fleet.Shared;

namespace Fleet.Agent.Tests;

/// <summary>
/// <see cref="LocalModel"/> (#382): the URL rules both harnesses share (L1, L2), the codex local
/// model rules (C1, C2), the canonical origin, and the codex base URL derived from it.
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
    [InlineData(null)]
    public void L1_BaseUrlOnAnotherProvider_Rejected(string? provider) =>
        Assert.Equal(
            "Local model runs on claude or codex; clear the local server URL first.",
            Fault(provider, Url, "gemini-2.5-pro"));

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
}
