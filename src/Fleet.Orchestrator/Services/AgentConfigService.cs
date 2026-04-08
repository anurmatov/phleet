using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// Loads agent configuration from the database on startup and pre-populates
/// AgentRegistry so agents appear in the REST API before their first heartbeat.
/// Degrades gracefully if the DB is unavailable.
/// </summary>
public sealed class AgentConfigService(
    IServiceScopeFactory scopeFactory,
    AgentRegistry registry,
    ILogger<AgentConfigService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

            var agents = await db.Agents
                .Where(a => a.IsEnabled)
                .AsNoTracking()
                .ToListAsync(cancellationToken);

            foreach (var agent in agents)
                registry.PreloadFromDbConfig(agent);

            logger.LogInformation("AgentConfigService: loaded {Count} agent(s) from DB", agents.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AgentConfigService: failed to load agent config from DB — continuing without DB-backed config");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
