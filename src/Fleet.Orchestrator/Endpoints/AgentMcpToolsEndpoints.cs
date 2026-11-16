using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services.McpTools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Fleet.Orchestrator.Endpoints;

/// <summary>
/// <c>POST /api/agents/{name}/mcp-tools</c>: the tools each of the agent's saved MCP endpoints
/// offers, asked of the servers now.
/// </summary>
/// <remarks>
/// A POST although it changes nothing here: it makes the orchestrator send requests, some carrying
/// an Env Ref's value as a header, so it needs a credential on every path, the same as a write. An
/// unmarked internal GET would not. The answer holds tool names, descriptions and fixed sentences;
/// MUST NOT return or log a header value.
/// </remarks>
public static class AgentMcpToolsEndpoints
{
    public static WebApplication MapAgentMcpToolsEndpoints(this WebApplication app)
    {
        app.MapPost("/api/agents/{name}/mcp-tools", async (
            string name, IServiceProvider services, CancellationToken ct) =>
        {
            using (var scope = services.CreateScope())
            {
                if (scope.ServiceProvider.GetService<OrchestratorDbContext>() is null)
                    return Results.Problem("Database is not configured on this orchestrator");
            }

            var result = await services.GetRequiredService<McpToolDiscovery>().DiscoverAsync(name, ct);
            return result is null
                ? Results.NotFound(new { error = $"Agent '{name}' not found in DB" })
                : Results.Json(result);
        });

        return app;
    }
}
