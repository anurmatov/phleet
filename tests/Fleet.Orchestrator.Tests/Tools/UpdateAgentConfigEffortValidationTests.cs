using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleet.Orchestrator.Tests.Tools;

/// <summary>
/// Tests per-provider effort validation in UpdateAgentConfigTool.
/// </summary>
public class UpdateAgentConfigEffortValidationTests
{
    private static OrchestratorDbContext CreateDb(string name)
    {
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return new OrchestratorDbContext(options);
    }

    private static IServiceScopeFactory BuildScopeFactory(OrchestratorDbContext db)
    {
        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddScoped<OrchestratorDbContext>(_ => db);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static Agent SeedAgent(OrchestratorDbContext db, string name, string provider)
    {
        var agent = new Agent
        {
            Name = name,
            DisplayName = name,
            Role = "test",
            Model = "claude-sonnet-4-6",
            Provider = provider,
            MemoryLimitMb = 1024,
            ContainerName = name,
        };
        db.Agents.Add(agent);
        db.SaveChanges();
        return agent;
    }

    /// <summary>
    /// These tests never touch projects, so the ACL broadcast is not exercised here — see
    /// AgentProjectAccessSyncTests for that.
    /// </summary>
    private sealed class NoOpAclChangeNotifier : IAclChangeNotifier
    {
        public Task PublishAclChangedAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    // ── Claude valid tiers ────────────────────────────────────────────────────

    [Theory]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    [InlineData("xhigh")]
    [InlineData("max")]
    public async Task Claude_ValidEffort_Accepted(string effort)
    {
        var db = CreateDb($"claude-valid-{effort}");
        SeedAgent(db, "agent1", "claude");
        var tool = new UpdateAgentConfigTool(BuildScopeFactory(db), new NoOpAclChangeNotifier());

        var result = await tool.UpdateAgentConfigAsync("agent1", effort: effort);

        Assert.Contains("effort:", result);
        Assert.DoesNotContain("Invalid", result);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("minimal")]
    [InlineData("bogus")]
    [InlineData("XHIGH")]
    public async Task Claude_InvalidEffort_ReturnsError(string effort)
    {
        var db = CreateDb($"claude-invalid-{effort}");
        SeedAgent(db, "agent1", "claude");
        var tool = new UpdateAgentConfigTool(BuildScopeFactory(db), new NoOpAclChangeNotifier());

        var result = await tool.UpdateAgentConfigAsync("agent1", effort: effort);

        Assert.Contains("Invalid effort", result);
        Assert.Contains("claude", result);
    }

    // ── Codex valid tiers ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("none")]
    [InlineData("minimal")]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    [InlineData("xhigh")]
    public async Task Codex_ValidEffort_Accepted(string effort)
    {
        var db = CreateDb($"codex-valid-{effort}");
        SeedAgent(db, "agent1", "codex");
        var tool = new UpdateAgentConfigTool(BuildScopeFactory(db), new NoOpAclChangeNotifier());

        var result = await tool.UpdateAgentConfigAsync("agent1", effort: effort);

        Assert.Contains("effort:", result);
        Assert.DoesNotContain("Invalid", result);
    }

    [Theory]
    [InlineData("max")]
    [InlineData("bogus")]
    public async Task Codex_InvalidEffort_ReturnsError(string effort)
    {
        var db = CreateDb($"codex-invalid-{effort}");
        SeedAgent(db, "agent1", "codex");
        var tool = new UpdateAgentConfigTool(BuildScopeFactory(db), new NoOpAclChangeNotifier());

        var result = await tool.UpdateAgentConfigAsync("agent1", effort: effort);

        Assert.Contains("Invalid effort", result);
        Assert.Contains("codex", result);
    }

    // ── Gemini rejects all effort values ─────────────────────────────────────

    [Theory]
    [InlineData("low")]
    [InlineData("high")]
    [InlineData("xhigh")]
    public async Task Gemini_AnyEffort_ReturnsNotSupportedError(string effort)
    {
        var db = CreateDb($"gemini-{effort}");
        SeedAgent(db, "agent1", "gemini");
        var tool = new UpdateAgentConfigTool(BuildScopeFactory(db), new NoOpAclChangeNotifier());

        var result = await tool.UpdateAgentConfigAsync("agent1", effort: effort);

        Assert.Contains("not supported on gemini", result);
    }

    // ── Clearing effort (empty string) always allowed ─────────────────────────

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("gemini")]
    public async Task AnyProvider_ClearEffort_AlwaysAllowed(string provider)
    {
        var db = CreateDb($"clear-effort-{provider}");
        var agent = SeedAgent(db, "agent1", provider);
        agent.Effort = "high";
        db.SaveChanges();
        var tool = new UpdateAgentConfigTool(BuildScopeFactory(db), new NoOpAclChangeNotifier());

        var result = await tool.UpdateAgentConfigAsync("agent1", effort: "");

        Assert.DoesNotContain("Invalid", result);
        Assert.DoesNotContain("not supported", result);
    }


    /// <summary>The stored row, read through a fresh context: the tool's scope disposes the seeded one.</summary>
    private static Agent Stored(string dbName)
    {
        using var db = CreateDb(dbName);
        return db.Agents.AsNoTracking().Single();
    }

    [Fact]
    public async Task UnknownProvider_IsRefusedByPV1_BeforeTheEffortCheck()
    {
        var db = CreateDb("unknown-provider-effort");
        SeedAgent(db, "agent1", "foo");
        var tool = new UpdateAgentConfigTool(BuildScopeFactory(db), new NoOpAclChangeNotifier());

        var result = await tool.UpdateAgentConfigAsync("agent1", effort: "high");

        Assert.Equal("Unknown provider 'foo'; use claude, codex or gemini.", result);
    }

    [Fact]
    public async Task EmptyProvider_ValidatesEffortAsClaude()
    {
        SeedAgent(CreateDb("empty-provider-effort"), "agent1", "");

        var rejected = await new UpdateAgentConfigTool(BuildScopeFactory(CreateDb("empty-provider-effort")), new NoOpAclChangeNotifier())
            .UpdateAgentConfigAsync("agent1", effort: "none");
        var accepted = await new UpdateAgentConfigTool(BuildScopeFactory(CreateDb("empty-provider-effort")), new NoOpAclChangeNotifier())
            .UpdateAgentConfigAsync("agent1", effort: "max");

        Assert.Contains("Invalid effort 'none' for claude", rejected);
        Assert.DoesNotContain("Invalid", accepted);
        Assert.Equal("max", Stored("empty-provider-effort").Effort);
    }

    // ── Validation uses the new provider when provider is also being changed ──

    [Fact]
    public async Task ProviderAndEffortChanged_ValidatesAgainstNewProvider()
    {
        // agent starts as claude; caller switches to codex and sets effort=none
        // (none is valid for codex but not claude — must validate against codex)
        var db = CreateDb("provider-switch-effort");
        SeedAgent(db, "agent1", "claude");
        var tool = new UpdateAgentConfigTool(BuildScopeFactory(db), new NoOpAclChangeNotifier());

        var result = await tool.UpdateAgentConfigAsync("agent1", provider: "codex", effort: "none");

        Assert.DoesNotContain("Invalid", result);
        Assert.DoesNotContain("not supported", result);
    }
}
