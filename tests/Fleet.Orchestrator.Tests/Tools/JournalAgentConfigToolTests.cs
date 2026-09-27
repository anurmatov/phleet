using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleet.Orchestrator.Tests.Tools;

public sealed class JournalAgentConfigToolTests
{
    [Fact]
    public async Task Update_and_get_expose_journal_enabled()
    {
        var database = $"journal-config-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
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

    private sealed class NoOpAclChangeNotifier : IAclChangeNotifier
    {
        public Task PublishAclChangedAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
