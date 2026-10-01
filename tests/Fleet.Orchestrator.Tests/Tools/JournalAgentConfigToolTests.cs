using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

namespace Fleet.Orchestrator.Tests.Tools;

public sealed class JournalAgentConfigToolTests
{
    [Fact]
    public async Task Update_and_get_expose_journal_enabled()
    {
        var database = $"journal-config-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddSingleton(new JournalTokenService(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Journal:TokenKey"] = Convert.ToBase64String(new byte[32]).TrimEnd('=') }).Build()));
        services.AddScoped(_ => new OrchestratorDbContext(
            new DbContextOptionsBuilder<OrchestratorDbContext>().UseInMemoryDatabase(database).Options));
        await using var provider = services.BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            db.Agents.Add(new Agent
            {
                Name = "agent1",
                DisplayName = "Agent One",
                Role = "test",
                Model = "model-x",
                Provider = "claude",
                ContainerName = "fleet-agent1",
            });
            await db.SaveChangesAsync();
        }

        var scopes = provider.GetRequiredService<IServiceScopeFactory>();
        var update = new UpdateAgentConfigTool(scopes, new NoOpAclChangeNotifier());
        var get = new GetAgentConfigTool(scopes);

        var changed = await update.UpdateAgentConfigAsync("agent1", journal_enabled: true);
        var current = await get.GetAgentConfigAsync("agent1");

        Assert.Contains("journal_enabled: False → True", changed);
        Assert.Contains("Journal enabled: True", current);
    }

    [Theory]
    [InlineData(false, true, null, "journal_key_missing")]
    [InlineData(false, true, "invalid", "journal_key_invalid")]
    [InlineData(true, true, null, null)]
    [InlineData(true, false, null, null)]
    [InlineData(false, false, null, null)]
    public async Task Update_transition_preserves_row_on_failure(bool before, bool requested, string? key, string? fault)
    {
        var database = $"journal-transition-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddScoped(_ => new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>().UseInMemoryDatabase(database).Options));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Journal:TokenKey"] = key }).Build();
        services.AddSingleton(new JournalTokenService(config));
        await using var provider = services.BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            db.Agents.Add(new Agent { Name = "agent1", DisplayName = "agent1", Role = "test", Model = "model-x", Provider = "claude", ContainerName = "agent1", JournalEnabled = before, ShowStats = true });
            await db.SaveChangesAsync();
        }
        var tool = new UpdateAgentConfigTool(provider.GetRequiredService<IServiceScopeFactory>(), new NoOpAclChangeNotifier());
        var result = await tool.UpdateAgentConfigAsync("agent1", journal_enabled: requested, show_stats: false);
        if (fault is not null)
        {
            Assert.Contains("journal_not_configured", result);
            Assert.Contains(fault, result);
        }
        await using var readScope = provider.CreateAsyncScope();
        var stored = await readScope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Agents.AsNoTracking().SingleAsync();
        Assert.Equal(fault is null ? requested : before, stored.JournalEnabled);
        Assert.Equal(fault is not null, stored.ShowStats);
    }

    private sealed class NoOpAclChangeNotifier : IAclChangeNotifier
    {
        public Task PublishAclChangedAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
