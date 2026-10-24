using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;
using Fleet.Orchestrator.Services;

namespace Fleet.Orchestrator.Endpoints;

public static class AgentRuntimeEndpoints
{
    public static void MapAgentRuntimeEndpoints(this WebApplication app)
    {
        app.MapPost("/api/agents/{name}/start", async (string name, IAgentRuntimeLifecycle lifecycle, CancellationToken ct) =>
            ToResult(await lifecycle.StartAsync(name, ct)));
        app.MapPost("/api/agents/{name}/stop", async (string name, IAgentRuntimeLifecycle lifecycle, CancellationToken ct) =>
            ToResult(await lifecycle.StopAsync(name, ct)));
        app.MapPost("/api/agents/{name}/restart", async (string name, IAgentRuntimeLifecycle lifecycle, CancellationToken ct) =>
            ToResult(await lifecycle.RestartAsync(name, ct)));
    }

    public static void MapOpenShellReconciliationEndpoint(this WebApplication app)
    {
        // Covered by the same fail-closed outside operator middleware as all /openshell children.
        // Deliberately no MCP issuance/recovery tool and no automatic background replay.
        app.MapPost("/api/agents/{name}/openshell/reconcile", async (string name,
            OpenShellReconciliationRequest request, OpenShellRetirementService retirement, CancellationToken ct) =>
            ToResult(await retirement.ReconcileAsync(name, request.ExpectedLaunchId, request.ExpectedRequestId, request.Replay, ct)));
    }

    public static void MapAgentDeleteEndpoint(this WebApplication app)
    {
        // REST: delete an agent (deprovision container + remove DB records)
        app.MapDelete("/api/agents/{name}", async (string name, IServiceScopeFactory scopeFactory, IAgentRuntimeLifecycle provisioning, AgentRegistry registry, IAclChangeNotifier aclNotifier) =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
            if (db is null)
                return Results.Problem("Database is not configured on this orchestrator");

            var agent = await db.Agents
                .Include(a => a.Tools)
                .Include(a => a.Projects)
                .Include(a => a.McpEndpoints)
                .Include(a => a.Networks)
                .Include(a => a.EnvRefs)
                .Include(a => a.TelegramUsers)
                .Include(a => a.TelegramGroups)
                .Include(a => a.Instructions)
                .AsSplitQuery()
                .FirstOrDefaultAsync(a => a.Name == name);

            if (agent is null)
                return Results.NotFound(new { error = $"Agent '{name}' not found in DB" });

            // Do not erase recovery/ACL state unless the shared lifecycle boundary confirms deprovision.
            var deprovision = await provisioning.DeprovisionAsync(name);
            if (!deprovision.Success)
                return Results.Json(new { error = deprovision.Message }, statusCode: deprovision.StatusCode);

            db.Agents.Remove(agent);
            await db.SaveChangesAsync();

            // agent_project_access is keyed by name, not by a foreign key, so deleting the agent leaves its
            // rows behind. Deleting an agent unassigns every project, so the same hook applies: the rows it
            // created go, an operator's manual rows and any wildcard stay.
            await AgentProjectAccessSync.SyncAndBroadcastAsync(db, aclNotifier, agent.Name, []);

            // Remove from in-memory registry so the agent doesn't persist as a ghost in the dashboard.
            registry.Remove(name);

            return Results.Ok(new
            {
                message = $"Agent '{name}' deleted after confirmed deprovision"
            });
        });

    }

    private static IResult ToResult(ProvisionResult result) => result.Success
        ? Results.Ok(new { message = result.Message })
        : Results.Json(new { error = result.Message }, statusCode: result.StatusCode);
}

public sealed record OpenShellReconciliationRequest(Guid ExpectedLaunchId, Guid ExpectedRequestId, bool Replay = false);
