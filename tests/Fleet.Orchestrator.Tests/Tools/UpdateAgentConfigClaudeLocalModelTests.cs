using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleet.Orchestrator.Tests.Tools;

/// <summary>
/// #340 D1 point 1 through <c>update_agent_config</c>: the resulting state is validated before
/// SaveChanges, a valid base URL is stored canonical, and a rejected request persists nothing.
/// </summary>
/// <remarks>
/// Every scope gets its own context over one in-memory store, and every assertion reads through a
/// fresh one — so "unchanged" means unchanged in the store, not in a tracked entity.
/// </remarks>
public class UpdateAgentConfigClaudeLocalModelTests
{
    private const string LocalTag = "qwen3.8:27b-agent";

    private readonly DbContextOptions<OrchestratorDbContext> _options =
        new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase($"claude-local-{Guid.NewGuid():N}")
            .Options;

    private sealed class NoOpAclChangeNotifier : IAclChangeNotifier
    {
        public Task PublishAclChangedAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private UpdateAgentConfigTool Tool()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => new OrchestratorDbContext(_options));
        return new UpdateAgentConfigTool(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), new NoOpAclChangeNotifier());
    }

    private void Seed(string provider = "claude", string model = "claude-sonnet-5",
        string? baseUrl = null, string? effort = null)
    {
        using var db = new OrchestratorDbContext(_options);
        db.Agents.Add(new Agent
        {
            Name = "agent1", DisplayName = "agent1", Role = "test", Model = model, Provider = provider,
            MemoryLimitMb = 1024, ContainerName = "agent1", AnthropicBaseUrl = baseUrl, Effort = effort,
        });
        db.SaveChanges();
    }

    private Agent Stored()
    {
        using var db = new OrchestratorDbContext(_options);
        return db.Agents.AsNoTracking().Single(a => a.Name == "agent1");
    }

    [Fact]
    public async Task Enable_TrailingSlash_IsStoredCanonical()
    {
        Seed();

        var result = await Tool().UpdateAgentConfigAsync("agent1",
            anthropic_base_url: "http://host.docker.internal:11434/", model: LocalTag, effort: "");

        Assert.DoesNotContain("Invalid", result);
        Assert.Contains("anthropic_base_url: (none) → http://host.docker.internal:11434", result);
        var stored = Stored();
        Assert.Equal("http://host.docker.internal:11434", stored.AnthropicBaseUrl);
        Assert.Equal(LocalTag, stored.Model);
    }

    [Fact]
    public async Task Enable_MixedCase_IsStoredCanonical()
    {
        Seed();

        await Tool().UpdateAgentConfigAsync("agent1",
            anthropic_base_url: "HTTP://Host.Docker.Internal:11434", model: LocalTag);

        Assert.Equal("http://host.docker.internal:11434", Stored().AnthropicBaseUrl);
    }

    [Theory]
    // V2
    [InlineData("http://host.docker.internal:11434/v1", LocalTag, "has a path")]
    [InlineData("http://user:pw@host.docker.internal:11434", LocalTag, "has credentials")]
    [InlineData("   ", LocalTag, "has surrounding whitespace")]
    // V3
    [InlineData("http://localhost:11434", LocalTag, "is the container")]
    // V4–V6
    [InlineData("http://host.docker.internal:11434", "qwen 27b", "not allowed in a CLI argument")]
    [InlineData("http://host.docker.internal:11434", "claude-sonnet-5", "is a Claude model id")]
    [InlineData("http://host.docker.internal:11434", "ollama/qwen3:8b", "selects the codex path")]
    public async Task Rejected_RowUnchanged(string baseUrl, string model, string expected)
    {
        Seed();

        var result = await Tool().UpdateAgentConfigAsync("agent1", anthropic_base_url: baseUrl, model: model);

        Assert.StartsWith("Invalid Claude local model configuration", result);
        Assert.Contains(expected, result);
        var stored = Stored();
        Assert.Null(stored.AnthropicBaseUrl);
        Assert.Equal("claude-sonnet-5", stored.Model);
    }

    [Fact]
    public async Task V1_ProviderChangedWithoutClearing_RejectedRowUnchanged()
    {
        Seed(model: LocalTag, baseUrl: "http://host.docker.internal:11434");

        var result = await Tool().UpdateAgentConfigAsync("agent1", provider: "codex", model: "gpt-5");

        Assert.Contains("applies only to provider claude", result);
        var stored = Stored();
        Assert.Equal("claude", stored.Provider);
        Assert.Equal("http://host.docker.internal:11434", stored.AnthropicBaseUrl);
    }

    [Fact]
    public async Task V1_ProviderChangedAndClearedInOneRequest_Accepted()
    {
        Seed(model: LocalTag, baseUrl: "http://host.docker.internal:11434");

        var result = await Tool().UpdateAgentConfigAsync("agent1",
            provider: "codex", model: "gpt-5", anthropic_base_url: "");

        Assert.DoesNotContain("Invalid", result);
        var stored = Stored();
        Assert.Equal("codex", stored.Provider);
        Assert.Null(stored.AnthropicBaseUrl);
    }

    [Fact]
    public async Task V7_EffortOutsideTheLocalVocabulary_RejectedRowUnchanged()
    {
        Seed(model: LocalTag, baseUrl: "http://host.docker.internal:11434");

        var result = await Tool().UpdateAgentConfigAsync("agent1", effort: "high");

        Assert.Contains("must be empty (model default, sent as xhigh), off, low, medium or xhigh", result);
        Assert.Null(Stored().Effort);
    }

    [Fact]
    public async Task V7_MaxIsAlsoRejected_OnALocalAgent()
    {
        Seed(model: LocalTag, baseUrl: "http://host.docker.internal:11434");

        var result = await Tool().UpdateAgentConfigAsync("agent1", effort: "max");

        Assert.Contains("must be empty (model default, sent as xhigh), off, low, medium or xhigh", result);
        Assert.Null(Stored().Effort);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("xhigh")]
    [InlineData("")]
    public async Task V7_TheLocalVocabulary_Saves(string effort)
    {
        Seed(model: LocalTag, baseUrl: "http://host.docker.internal:11434");

        var result = await Tool().UpdateAgentConfigAsync("agent1", effort: effort);

        // "" is a no-change when the stored value is already null.
        if (effort == "")
            Assert.StartsWith("No changes", result);
        else
            Assert.DoesNotContain("Invalid", result);
        Assert.Equal(effort == "" ? null : effort, Stored().Effort);
    }

    [Fact]
    public async Task Off_SkipsTheCloudVocabulary_Check()
    {
        // The resulting agent is local (base URL set in the same request), so the cloud list must
        // not reject off before the shared V7 rule runs.
        Seed();

        var result = await Tool().UpdateAgentConfigAsync("agent1",
            anthropic_base_url: "http://host.docker.internal:11434", model: LocalTag, effort: "off");

        Assert.DoesNotContain("Invalid", result);
        Assert.Equal("off", Stored().Effort);
        Assert.Equal("http://host.docker.internal:11434", Stored().AnthropicBaseUrl);
    }

    [Fact]
    public async Task V8_CloudClaudeOff_RejectedRowUnchanged()
    {
        Seed();

        var result = await Tool().UpdateAgentConfigAsync("agent1", effort: "off");

        Assert.Contains("applies only to local Claude models", result);
        Assert.Null(Stored().Effort);
    }

    [Fact]
    public async Task Enable_WithAnExistingCloudEffort_RejectedUntilItIsValidLocal()
    {
        Seed(effort: "high");

        var rejected = await Tool().UpdateAgentConfigAsync("agent1",
            anthropic_base_url: "http://host.docker.internal:11434", model: LocalTag);
        var accepted = await Tool().UpdateAgentConfigAsync("agent1",
            anthropic_base_url: "http://host.docker.internal:11434", model: LocalTag, effort: "medium");

        Assert.Contains("must be empty (model default, sent as xhigh), off, low, medium or xhigh", rejected);
        Assert.DoesNotContain("Invalid", accepted);
        Assert.Equal("http://host.docker.internal:11434", Stored().AnthropicBaseUrl);
        Assert.Equal("medium", Stored().Effort);
    }

    [Fact]
    public async Task EmptyString_Clears()
    {
        Seed(model: LocalTag, baseUrl: "http://host.docker.internal:11434");

        var result = await Tool().UpdateAgentConfigAsync("agent1", anthropic_base_url: "", model: "claude-sonnet-5");

        Assert.Contains("anthropic_base_url: http://host.docker.internal:11434 → (none)", result);
        Assert.Null(Stored().AnthropicBaseUrl);
    }

    [Fact]
    public async Task SameValueInAnotherSpelling_IsNotAChange()
    {
        Seed(model: LocalTag, baseUrl: "http://host.docker.internal:11434");

        var result = await Tool().UpdateAgentConfigAsync("agent1", anthropic_base_url: "http://host.docker.internal:11434/");

        Assert.StartsWith("No changes", result);
    }

    [Fact]
    public async Task AgentWithoutTheField_IsNotAffected()
    {
        Seed(model: "claude-opus-5-5");

        var result = await Tool().UpdateAgentConfigAsync("agent1", effort: "max", model: "opus");

        Assert.DoesNotContain("Invalid", result);
        Assert.DoesNotContain("anthropic_base_url", result);
        Assert.Equal("max", Stored().Effort);
    }
}
