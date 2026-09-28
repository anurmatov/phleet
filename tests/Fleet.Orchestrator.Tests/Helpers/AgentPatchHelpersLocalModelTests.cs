using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Helpers;

namespace Fleet.Orchestrator.Tests.Helpers;

/// <summary>
/// #382: every branch of the A1 alias rule in <see cref="AgentPatchHelpers.ResolveLocalBaseUrl"/>,
/// and <see cref="AgentPatchHelpers.FinalizeLocalModel"/> for codex local mode — the two helpers
/// both write paths (REST and MCP) call.
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
}
