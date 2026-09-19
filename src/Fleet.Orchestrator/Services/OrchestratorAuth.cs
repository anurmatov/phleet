namespace Fleet.Orchestrator.Services;

/// <summary>
/// Which requests the orchestrator's bearer middleware protects.
/// </summary>
/// <remarks>
/// <para>
/// The rule itself is unchanged and lives here rather than inline in <c>Program.cs</c> for one
/// reason: a test that re-declares it proves only that the test's copy behaves. The endpoint tests
/// build a host around <b>this</b> predicate, so deleting a path from the exemption list — or
/// making a write method read-only by accident — turns them red.
/// </para>
/// <para>
/// Method-based, not route-based. Every non-GET outside the exempt paths needs the token, which is
/// why a new mutating route inherits the gate without registering anything: that is the property
/// AC6 depends on, and it is a property of this function rather than of any endpoint file.
/// </para>
/// </remarks>
public static class OrchestratorAuth
{
    /// <summary>
    /// True when the request must carry <c>Orchestrator:AuthToken</c>.
    ///
    /// Reads (GET/HEAD/OPTIONS) never do. Neither do the paths with their own auth or no auth at
    /// all: WebSocket upgrades, the MCP endpoint, the separately-tokened config API, and health.
    /// </summary>
    public static bool RequiresBearerToken(string method, string path)
    {
        var isReadOnly = HttpMethods.IsGet(method)
                      || HttpMethods.IsHead(method)
                      || HttpMethods.IsOptions(method);

        var isExemptPath = path.StartsWith("/ws", StringComparison.OrdinalIgnoreCase)
                        || path.StartsWith("/mcp", StringComparison.OrdinalIgnoreCase)
                        || path.StartsWith("/api/config", StringComparison.OrdinalIgnoreCase)
                        || path.Equals("/health", StringComparison.OrdinalIgnoreCase);

        return !isReadOnly && !isExemptPath;
    }
}
