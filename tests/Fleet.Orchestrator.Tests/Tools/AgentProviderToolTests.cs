using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Fleet.Orchestrator.Tests.Tools;

/// <summary>
/// #386 PV1 through <c>create_agent</c> and <c>update_agent_config</c> (A30): an unknown provider is
/// refused with the PV1 text and nothing is written; an empty provider stays claude, and the four
/// known names are accepted. The REST twins are in <c>AgentCreateEndpointsTests</c> and
/// <c>AgentConfigEndpointsLocalModelTests</c>.
/// </summary>
public class AgentProviderToolTests
{
    private readonly DbContextOptions<OrchestratorDbContext> _options =
        new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase($"agent-provider-{Guid.NewGuid():N}")
            .Options;

    private sealed class NoOpAclChangeNotifier : IAclChangeNotifier
    {
        public Task PublishAclChangedAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private IServiceScopeFactory Scopes()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => new OrchestratorDbContext(_options));
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private CreateAgentTool Create() => new(Scopes(), new ConfigurationBuilder().Build());

    private UpdateAgentConfigTool Update() => new(Scopes(), new NoOpAclChangeNotifier());

    private static string Pv1(string provider) => $"Unknown provider '{provider}'; use claude, codex, gemini or pi.";

    private List<Agent> Agents()
    {
        using var db = new OrchestratorDbContext(_options);
        return db.Agents.AsNoTracking().ToList();
    }

    private void Seed(string provider)
    {
        using var db = new OrchestratorDbContext(_options);
        db.Agents.Add(new Agent
        {
            Name = "agent1", DisplayName = "agent1", Role = "test", Model = "claude-opus-5-5",
            Provider = provider, MemoryLimitMb = 1024, ContainerName = "agent1",
        });
        db.SaveChanges();
    }

    // ── create_agent ─────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("foo")]
    [InlineData("Claude")]
    [InlineData("pi ")]
    public async Task Create_AnUnknownProvider_IsRefused_AndNoRowIsWritten(string provider)
    {
        var result = await Create().CreateAgentAsync("agent1", "developer", "model-x", provider: provider);

        Assert.Equal(Pv1(provider), result);
        Assert.Empty(Agents());
        await using var db = new OrchestratorDbContext(_options);
        Assert.Empty(db.AgentNetworks);
        Assert.Empty(db.AgentEnvRefs);
    }

    [Theory]
    [InlineData(null, "claude")]
    [InlineData("", "")]
    [InlineData("claude", "claude")]
    [InlineData("codex", "codex")]
    [InlineData("gemini", "gemini")]
    [InlineData("pi", "pi")]
    public async Task Create_AKnownOrEmptyProvider_IsAccepted(string? provider, string stored)
    {
        var result = await Create().CreateAgentAsync("agent1", "developer", "model-x", provider: provider);

        Assert.StartsWith("Agent 'agent1' created in DB", result);
        Assert.Equal(stored, Assert.Single(Agents()).Provider);
    }

    // ── update_agent_config ──────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("foo")]
    [InlineData("CODEX")]
    public async Task Update_AnUnknownProvider_IsRefused_AndTheRowIsUnchanged(string provider)
    {
        Seed("claude");

        var result = await Update().UpdateAgentConfigAsync("agent1", provider: provider, memory_limit_mb: 2048);

        Assert.Equal(Pv1(provider), result);
        var stored = Assert.Single(Agents());
        Assert.Equal(("claude", 1024), (stored.Provider, stored.MemoryLimitMb));
    }

    [Fact]
    public async Task Update_ARowAlreadyHoldingAnUnknownProvider_IsRefusedUntilTheProviderIsFixed()
    {
        Seed("foo");

        var refused = await Update().UpdateAgentConfigAsync("agent1", memory_limit_mb: 2048);
        Assert.Equal(Pv1("foo"), refused);
        Assert.Equal(1024, Assert.Single(Agents()).MemoryLimitMb);

        var fixedRow = await Update().UpdateAgentConfigAsync("agent1", provider: "claude", memory_limit_mb: 2048);
        Assert.Contains("provider: foo → claude", fixedRow);
        Assert.Equal(("claude", 2048), (Agents()[0].Provider, Agents()[0].MemoryLimitMb));
    }

    [Fact]
    public async Task Update_AnEmptyProvider_StaysClaude()
    {
        Seed("claude");

        var result = await Update().UpdateAgentConfigAsync("agent1", provider: "");

        Assert.Contains("provider: claude → ", result);
        Assert.Equal("", Assert.Single(Agents()).Provider);
    }
}
