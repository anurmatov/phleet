using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Helpers;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Tests.Helpers;

/// <summary>
/// #340 D1 point 1 on the <c>PUT /api/agents/{name}/config</c> path. The handler maps
/// <c>AnthropicBaseUrl</c> (<c>""</c> clears), applies the rest of the request, then calls
/// <see cref="AgentPatchHelpers.FinalizeClaudeLocalModel"/> and returns 400 without saving on a
/// fault. These tests run that sequence against a store and read back through a fresh context.
/// </summary>
/// <remarks>
/// The handler itself is inline in <c>Program.cs</c> with RabbitMQ- and Docker-bound dependencies,
/// so it is not driven over HTTP here; the shared helper and the save-or-not decision are.
/// </remarks>
public class AgentPatchHelpersClaudeLocalModelTests
{
    private const string LocalTag = "qwen3.8:27b-agent";

    private readonly DbContextOptions<OrchestratorDbContext> _options =
        new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase($"put-claude-local-{Guid.NewGuid():N}")
            .Options;

    private void Seed(string? baseUrl = null, string model = "claude-sonnet-5")
    {
        using var db = new OrchestratorDbContext(_options);
        db.Agents.Add(new Agent
        {
            Name = "agent1", DisplayName = "agent1", Role = "test", Model = model, Provider = "claude",
            MemoryLimitMb = 1024, ContainerName = "agent1", AnthropicBaseUrl = baseUrl,
        });
        db.SaveChanges();
    }

    /// <summary>The PUT handler's sequence for these fields. Returns the 400 error, or null.</summary>
    private string? Put(string? anthropicBaseUrl, string? model = null, string? effort = null, string? provider = null)
    {
        using var db = new OrchestratorDbContext(_options);
        var agent = db.Agents.Single(a => a.Name == "agent1");

        if (model is not null) agent.Model = model;
        if (effort is not null) agent.Effort = effort == "" ? null : effort;
        if (provider is not null) agent.Provider = provider;
        if (anthropicBaseUrl is not null) agent.AnthropicBaseUrl = anthropicBaseUrl == "" ? null : anthropicBaseUrl;

        if (AgentPatchHelpers.FinalizeClaudeLocalModel(agent) is { } fault)
            return fault;

        db.SaveChanges();
        return null;
    }

    private Agent Stored()
    {
        using var db = new OrchestratorDbContext(_options);
        return db.Agents.AsNoTracking().Single(a => a.Name == "agent1");
    }

    [Fact]
    public void TrailingSlash_IsStoredCanonical()
    {
        Seed();

        Assert.Null(Put("http://host.docker.internal:11434/", model: LocalTag));

        Assert.Equal("http://host.docker.internal:11434", Stored().AnthropicBaseUrl);
    }

    [Theory]
    [InlineData("http://host.docker.internal:11434/v1", LocalTag, "", null, "has a path")]
    [InlineData("http://host.docker.internal:11434/?a=1", LocalTag, "", null, "has a query")]
    [InlineData("ftp://host.docker.internal:11434", LocalTag, "", null, "must use http or https")]
    [InlineData("http://[::1]:11434", LocalTag, "", null, "is the container")]
    [InlineData("http://host.docker.internal:11434", "qwen;id", "", null, "not allowed in a CLI argument")]
    [InlineData("http://host.docker.internal:11434", "haiku", "", null, "is a Claude model id")]
    [InlineData("http://host.docker.internal:11434", "zai/glm-5.3", "", null, "selects the codex path")]
    [InlineData("http://host.docker.internal:11434", LocalTag, "high", null, "must be empty (model default, sent as xhigh), off, low, medium or xhigh")]
    [InlineData("http://host.docker.internal:11434", LocalTag, "max", null, "must be empty (model default, sent as xhigh), off, low, medium or xhigh")]
    [InlineData("http://host.docker.internal:11434", LocalTag, "", "gemini", "applies only to provider claude")]
    public void Rejected_RowUnchanged(string baseUrl, string model, string effort, string? provider, string expected)
    {
        Seed();

        var fault = Put(baseUrl, model, effort, provider);

        Assert.NotNull(fault);
        Assert.Contains(expected, fault);
        var stored = Stored();
        Assert.Null(stored.AnthropicBaseUrl);
        Assert.Equal("claude-sonnet-5", stored.Model);
        Assert.Equal("claude", stored.Provider);
        Assert.Null(stored.Effort);
    }

    [Fact]
    public void V8_CloudClaudeOff_Rejected()
    {
        Seed();

        var fault = Put(null, model: "claude-sonnet-5", effort: "off");

        Assert.Equal(
            "Effort 'off' applies only to local Claude models; clear it or choose low, medium, high, xhigh or max.",
            fault);
        Assert.Null(Stored().Effort);
    }

    [Fact]
    public void V8_ClearingTheBaseUrl_WithOffStillSet_Rejected()
    {
        Seed("http://host.docker.internal:11434", LocalTag);
        // the stored effort would be off; simulate the flip-to-cloud patch
        var fault = Put("", model: "claude-sonnet-5", effort: "off");

        Assert.Contains("applies only to local Claude models", fault);
    }

    [Fact]
    public void LocalOff_IsAccepted()
    {
        Seed("http://host.docker.internal:11434", LocalTag);

        var fault = Put("http://host.docker.internal:11434", LocalTag, "off");

        Assert.Null(fault);
        Assert.Equal("off", Stored().Effort);
    }

    [Fact]
    public void EmptyString_Clears()
    {
        Seed("http://host.docker.internal:11434", LocalTag);

        Assert.Null(Put("", model: "claude-sonnet-5"));

        Assert.Null(Stored().AnthropicBaseUrl);
    }

    [Fact]
    public void Omitted_KeepsTheStoredValue()
    {
        Seed("http://host.docker.internal:11434", LocalTag);

        Assert.Null(Put(anthropicBaseUrl: null));

        Assert.Equal("http://host.docker.internal:11434", Stored().AnthropicBaseUrl);
    }
}
