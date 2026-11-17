using Fleet.Orchestrator.Services;

namespace Fleet.Orchestrator.Endpoints;

/// <summary>
/// <c>GET /internal/agent-mcp-grants</c>: which tools of the fleet's own MCP servers each agent is
/// granted. fleet-memory, fleet-telegram and the Temporal bridge read it to hold a verified agent
/// to its grants; the orchestrator's own MCP endpoint reads the same data directly.
/// </summary>
/// <remarks>
/// Service-authenticated, like <c>/internal/agent-project-access</c>: names
/// of agents and of tools, nothing an agent could not list for itself. It is what a server
/// *believes*, so a server MUST read it from the orchestrator's address and nowhere else.
/// </remarks>
public static class AgentIdentityEndpoints
{
    /// <summary>Only the two authorization-map routes, including routing's trailing-slash aliases.</summary>
    public static bool IsAuthorizationBootstrap(HttpContext context)
    {
        var path = context.Request.Path.Value?.TrimEnd('/');
        return string.Equals(path, "/internal/agent-mcp-grants", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, "/internal/agent-project-access", StringComparison.OrdinalIgnoreCase);
    }

    public static IEndpointRouteBuilder MapAgentIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/internal/agent-mcp-grants", async (DbAgentGrantSource grants, CancellationToken ct) =>
        {
            var agents = await grants.AllAsync(ct);
            return agents is null
                ? Results.Problem("Database is not configured on this orchestrator", statusCode: StatusCodes.Status503ServiceUnavailable)
                : Results.Ok(new { agents });
        });

        return app;
    }
}
