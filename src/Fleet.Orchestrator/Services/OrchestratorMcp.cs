using Fleet.Orchestrator.Auth;
using Fleet.Shared.Mcp;

namespace Fleet.Orchestrator.Services;

/// <summary>The orchestrator's own MCP endpoint: its path, and the name agents' grants know it by.</summary>
public static class OrchestratorMcp
{
    /// <summary><c>mcp__fleet-orchestrator__…</c>: the endpoint name every agent's grants carry.</summary>
    public const string ServerName = "fleet-orchestrator";

    public const string Path = "/mcp";

    public static bool IsMcpPath(PathString path) =>
        path.StartsWithSegments(Path, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads who is calling the MCP endpoint. It needs no sign-in credential (agents have none), so
    /// this is where an agent's identity token is read. A signed-in browser or the service bearer
    /// is the operator, and is not held to any agent's grants. An enrolled phone is not: it has no
    /// business calling tools. Goes after <see cref="FleetAccess.UseFleetAccess"/>, which resolves
    /// the sign-in credential this reads.
    /// </summary>
    public static IApplicationBuilder UseMcpAgentIdentity(this IApplicationBuilder app) =>
        app.UseAgentIdentity(
            context => IsMcpPath(context.Request.Path),
            isOperator: context => CallerIdentity.Of(context).Kind is CallerKind.Service or CallerKind.Browser);
}
