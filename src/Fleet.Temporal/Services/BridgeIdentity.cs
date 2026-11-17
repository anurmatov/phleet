using Microsoft.AspNetCore.Http;

namespace Fleet.Temporal.Services;

/// <summary>Which of the Temporal bridge's routes are the MCP endpoint, for the identity check.</summary>
public static class BridgeIdentity
{
    /// <summary><c>mcp__fleet-temporal__…</c>: the name agents' grants know this server by.</summary>
    public const string ServerName = "fleet-temporal";

    private static readonly string[] StatusRoutes = ["/health", "/workflow-failures", "/pending-delegations"];

    /// <summary>
    /// The MCP endpoint is mapped at the root: every route but the status routes, which carry
    /// identifiers, counters and times and are read by the compose healthcheck and the operator.
    /// </summary>
    public static bool IsMcp(PathString path) =>
        !StatusRoutes.Any(route => path.Equals(route, StringComparison.OrdinalIgnoreCase));
}
