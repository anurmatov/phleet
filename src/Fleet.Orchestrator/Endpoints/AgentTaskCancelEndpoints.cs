using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Endpoints;

/// <summary>
/// The REST surface for cancelling work on an agent: every running task, one background task, or
/// one task by its bridge task id. Each is a proxy to the agent's own HTTP endpoint.
/// </summary>
public static class AgentTaskCancelEndpoints
{
    public static IEndpointRouteBuilder MapAgentTaskCancelEndpoints(this IEndpointRouteBuilder app)
    {
        // REST: cancel all running tasks on an agent by proxying to the agent's HTTP /cancel endpoint.
        // Resolves the agent URL via container name on the Docker network (fleet-orchestrator is containerized).
        // Falls back to host-port routing when container name is unavailable (not yet provisioned agents).
        app.MapPost("/api/agents/{name}/cancel", async (string name, IServiceScopeFactory scopeFactory, IHttpClientFactory httpClientFactory, ContainerProvisioningService provisioning) =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
            if (db is null)
                return Results.Problem("Database is not configured on this orchestrator");

            var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Name == name);
            if (agent is null)
                return Results.NotFound(new { error = $"Agent '{name}' not found" });

            var agentUrl = $"{provisioning.GetAgentBaseUrl(agent)}/cancel";

            try
            {
                var client = httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(10);
                var response = await client.PostAsync(agentUrl, null);
                return response.IsSuccessStatusCode
                    ? Results.Ok(new { message = $"Cancel request sent to agent '{name}'" })
                    : Results.Problem($"Agent returned {(int)response.StatusCode} — cancel may have failed");
            }
            catch (Exception ex)
            {
                return Results.Problem($"Failed to reach agent '{name}' at {agentUrl}: {ex.Message}");
            }
        });

        // REST: cancel a specific background task on an agent by proxying to the agent's HTTP /cancel_bg/{taskId} endpoint.
        // Uses container-name routing — see /cancel above for rationale.
        app.MapPost("/api/agents/{name}/cancel_bg/{taskId}", async (string name, string taskId, IServiceScopeFactory scopeFactory, IHttpClientFactory httpClientFactory, ContainerProvisioningService provisioning) =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
            if (db is null)
                return Results.Problem("Database is not configured on this orchestrator");

            var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Name == name);
            if (agent is null)
                return Results.NotFound(new { error = $"Agent '{name}' not found" });

            var agentUrl = $"{provisioning.GetAgentBaseUrl(agent)}/cancel_bg/{Uri.EscapeDataString(taskId)}";

            try
            {
                var client = httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(10);
                var response = await client.PostAsync(agentUrl, null);
                return response.IsSuccessStatusCode
                    ? Results.Ok(new { message = $"Cancel requested for background task '{taskId}' on agent '{name}'" })
                    : response.StatusCode == System.Net.HttpStatusCode.NotFound
                        ? Results.NotFound(new { error = $"Background task '{taskId}' not found on agent '{name}'" })
                        : Results.Problem($"Agent returned {(int)response.StatusCode} — cancel may have failed");
            }
            catch (Exception ex)
            {
                return Results.Problem($"Failed to reach agent '{name}' at {agentUrl}: {ex.Message}");
            }
        });

        // REST: cancel a specific foreground task on an agent by proxying to the agent's HTTP /cancel/{taskId} endpoint.
        // Targets only the task with the given bridge taskId, leaving all other tasks unaffected.
        // Uses container-name routing — see /cancel above for rationale.
        app.MapPost("/api/agents/{name}/cancel/{**taskId}", async (string name, string taskId, IServiceScopeFactory scopeFactory, IHttpClientFactory httpClientFactory, ContainerProvisioningService provisioning) =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
            if (db is null)
                return Results.Problem("Database is not configured on this orchestrator");

            var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Name == name);
            if (agent is null)
                return Results.NotFound(new { error = $"Agent '{name}' not found" });

            var decodedTaskId = Uri.UnescapeDataString(taskId);
            var agentUrl = $"{provisioning.GetAgentBaseUrl(agent)}/cancel/{Uri.EscapeDataString(decodedTaskId)}";

            try
            {
                var client = httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(10);
                var response = await client.PostAsync(agentUrl, null);
                return response.IsSuccessStatusCode
                    ? Results.Ok(new { message = $"Cancel requested for task '{decodedTaskId}' on agent '{name}'" })
                    : Results.Problem($"Agent returned {(int)response.StatusCode} — cancel may have failed");
            }
            catch (Exception ex)
            {
                return Results.Problem($"Failed to reach agent '{name}' at {agentUrl}: {ex.Message}");
            }
        });

        return app;
    }
}
