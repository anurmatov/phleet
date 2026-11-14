using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Endpoints;

/// <summary>
/// The REST reads of the agent registry: every known agent, one agent, and an agent's task history.
/// </summary>
public static class AgentListEndpoints
{
    public static IEndpointRouteBuilder MapAgentListEndpoints(this IEndpointRouteBuilder app)
    {
        // REST: list all known agents
        app.MapGet("/api/agents", async (AgentRegistry registry, IServiceScopeFactory scopeFactory) =>
        {
            // Merge DB agent records with in-memory registry so DB-only (not yet provisioned) agents are visible.
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
                if (db is not null)
                {
                    var dbAgents = await db.Agents.AsNoTracking().ToListAsync();
                    foreach (var a in dbAgents)
                        registry.PreloadFromDbConfig(a); // no-op if agent already in registry
                }
            }
            catch { /* DB unavailable — return what we have */ }
            return Results.Ok(registry.GetAll());
        });

        // REST: get a specific agent by name
        app.MapGet("/api/agents/{name}", (string name, AgentRegistry registry) =>
        {
            var agent = registry.Get(name);
            return agent is not null ? Results.Ok(agent) : Results.NotFound();
        });

        // REST: task history for a specific agent
        app.MapGet("/api/agents/{name}/history", (string name, TaskHistoryStore taskHistory) =>
            Results.Ok(taskHistory.GetHistory(name)));

        return app;
    }
}
