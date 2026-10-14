using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Helpers;
using Fleet.Shared;

namespace Fleet.Orchestrator.Tests.Helpers;

/// <summary>
/// #382: every branch of the A1 alias rule in <see cref="AgentPatchHelpers.ResolveLocalBaseUrl"/>,
/// and <see cref="AgentPatchHelpers.FinalizeLocalModel"/> for codex local mode — the two helpers
/// both write paths (REST and MCP) call. #386: PV1, and pi's PI1–PI4 with the context window.
/// A REST value of the wrong JSON type gets a 400 text naming its path.
/// </summary>
public class AgentPatchHelpersLocalModelTests
{
    private const string Url = "http://inference-host:11434";

    private static (bool Sent, string? Value, string? Error) Resolve(string? value, string? alias) =>
        AgentPatchHelpers.ResolveLocalBaseUrl(value, alias, "localBaseUrl", "anthropicBaseUrl");

    [Theory]
    [InlineData(null, null, false, null)]          // neither sent
    [InlineData(Url, null, true, Url)]             // one sent: it wins (validated later, at finalize)
    [InlineData(null, Url, true, Url)]
    [InlineData("", null, true, null)]             // "" clears
    [InlineData(null, "", true, null)]
    [InlineData("", "", true, null)]               // both "" clears
    [InlineData("http://Inference-Host:11434/", Url, true, Url)] // equal after canonicalization
    public void A1_Resolves(string? value, string? alias, bool sent, string? expected)
    {
        var (isSent, resolved, error) = Resolve(value, alias);

        Assert.Null(error);
        Assert.Equal(sent, isSent);
        Assert.Equal(expected, resolved);
    }

    [Theory]
    [InlineData("", Url)]
    [InlineData(Url, "")]
    [InlineData(Url, "http://other-host:11434")]
    public void A1_Conflict_NamesBothFields(string value, string alias)
    {
        var (_, resolved, error) = Resolve(value, alias);

        Assert.Null(resolved);
        Assert.Contains("localBaseUrl", error);
        Assert.Contains("anthropicBaseUrl", error);
    }

    [Theory]
    [InlineData(Url + "/v1", Url, "localBaseUrl:")]
    [InlineData(Url, Url + "/v1", "anthropicBaseUrl:")]
    public void A1_BothSet_AFaultNamesItsField(string value, string alias, string prefix)
    {
        var (_, _, error) = Resolve(value, alias);

        Assert.StartsWith(prefix, error);
        Assert.Contains("Fleet adds", error);
    }

    private static Agent Codex(string model, string? url) => new()
    {
        Name = "agent1", DisplayName = "agent1", Role = "test", Model = model, Provider = "codex",
        MemoryLimitMb = 1024, ContainerName = "agent1", LocalBaseUrl = url,
    };

    [Theory]
    [InlineData("ollama/qwen3.8:27b")]
    [InlineData("LMStudio/qwen3.8:27b")]
    [InlineData("ollama/hf.co/org/model:q4")]
    public void Finalize_CodexLocal_IsStoredCanonical(string model)
    {
        var agent = Codex(model, "HTTP://Inference-Host:11434/");

        Assert.Null(AgentPatchHelpers.FinalizeLocalModel(agent));
        Assert.Equal(Url, agent.LocalBaseUrl);
        Assert.Equal(model, agent.Model); // never rewritten
    }

    [Theory]
    [InlineData("qwen3.8:27b")]
    [InlineData("zai/glm-5.3")]
    [InlineData("gpt-5")]
    [InlineData("ollama/bad tag")]
    public void Finalize_CodexLocal_RejectsAModelThatIsNotALocalTag(string model)
    {
        var fault = AgentPatchHelpers.FinalizeLocalModel(Codex(model, Url));

        Assert.NotNull(fault);
        Assert.True(fault.Contains("ollama/<tag>") || fault.Contains("local model tag"), fault);
    }

    [Fact]
    public void Finalize_CodexCloud_IsUntouched()
    {
        var agent = Codex("gpt-5", null);

        Assert.Null(AgentPatchHelpers.FinalizeLocalModel(agent));
        Assert.Null(agent.LocalBaseUrl);
    }

    // ── #386: provider pi and PV1 ────────────────────────────────────────────────────────────────

    private const string PiUrl = "http://inference-host:8000";

    private static Agent Pi(string model = "qwen3.8-flash-next", string? url = PiUrl, int? window = 131_072, string? effort = null) => new()
    {
        Name = "agent1", DisplayName = "agent1", Role = "test", Model = model, Provider = "pi",
        MemoryLimitMb = 1024, ContainerName = "agent1", LocalBaseUrl = url, ContextWindow = window, Effort = effort,
    };

    [Theory]
    [InlineData(null)]
    [InlineData("off")]
    [InlineData("on")]
    public void Finalize_Pi_IsStoredCanonical_ModelAndEffortNeverRewritten(string? effort)
    {
        var agent = Pi(url: "HTTP://Inference-Host:8000/", effort: effort);

        Assert.Null(AgentPatchHelpers.FinalizeLocalModel(agent));
        Assert.Equal((PiUrl, "qwen3.8-flash-next", effort), (agent.LocalBaseUrl, agent.Model, agent.Effort));
    }

    public static TheoryData<Agent, string> PiFaults => new()
    {
        { Pi(url: null), PiLocalModel.Pi1Text },
        { Pi(url: ""), PiLocalModel.Pi1Text },
        { Pi(model: "qwen 3.8"), PiLocalModel.Pi2Text },
        { Pi(model: "ollama/qwen3.8-flash-next"), PiLocalModel.Pi2Text },
        { Pi(model: "lmstudio/qwen3.8-flash-next"), PiLocalModel.Pi2Text },
        { Pi(model: "zai/glm-5.3"), PiLocalModel.Pi2Text },
        { Pi(window: null), PiLocalModel.Pi3Text },
        { Pi(window: 65_535), PiLocalModel.Pi3Text },
        { Pi(effort: "xhigh"), PiLocalModel.Pi4Text },
        { Pi(effort: "none"), PiLocalModel.Pi4Text },
    };

    [Theory]
    [MemberData(nameof(PiFaults))]
    public void Finalize_Pi_RefusesWithTheRuleText_AndChangesNothing(Agent agent, string expected)
    {
        var before = (agent.LocalBaseUrl, agent.Model, agent.Effort, agent.ContextWindow);

        Assert.Equal(expected, AgentPatchHelpers.FinalizeLocalModel(agent));
        Assert.Equal(before, (agent.LocalBaseUrl, agent.Model, agent.Effort, agent.ContextWindow));
    }

    [Theory]
    [InlineData("foo")]
    [InlineData("Codex")]
    public void Finalize_PV1_RefusesAnUnknownProvider(string provider)
    {
        var agent = Codex("gpt-5", null);
        agent.Provider = provider;

        Assert.Equal($"Unknown provider '{provider}'; use claude, codex, gemini or pi.", AgentPatchHelpers.FinalizeLocalModel(agent));
    }

    [Fact]
    public void Finalize_AnEmptyProvider_StaysClaude()
    {
        var agent = Codex("claude-opus-5-5", null);
        agent.Provider = "";

        Assert.Null(AgentPatchHelpers.FinalizeLocalModel(agent));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(4_096)]
    public void Finalize_CodexLocal_IgnoresTheContextWindow_AsBefore(int? window)
    {
        // PI3 is pi's rule: a codex local agent below 65536, or with none, is still accepted.
        var agent = Codex("ollama/qwen3.8:27b", Url);
        agent.ContextWindow = window;

        Assert.Null(AgentPatchHelpers.FinalizeLocalModel(agent));
    }

    // ── A body of the wrong JSON type ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("\"true\"")]
    [InlineData("1")]
    [InlineData("[]")]
    public void DescribeBodyFault_ANonBoolean_NamesItsPath(string value)
    {
        var ex = Assert.ThrowsAny<System.Text.Json.JsonException>(() =>
            System.Text.Json.JsonSerializer.Deserialize<Fleet.Orchestrator.Endpoints.AgentConfigUpdateRequest>(
                $$"""{"mountDockerSock":{{value}}}""",
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));

        Assert.Equal(
            "Invalid request body at '$.mountDockerSock': malformed JSON or a value of the wrong type (booleans take true or false).",
            AgentPatchHelpers.DescribeBodyFault(ex));
    }
}
