using Microsoft.AspNetCore.Http;

namespace Fleet.Memory.Services;

/// <summary>Which of fleet-memory's routes are whose, for the identity checks.</summary>
public static class MemoryIdentity
{
    /// <summary><c>mcp__fleet-memory__…</c>: the name agents' grants know this server by.</summary>
    public const string ServerName = "fleet-memory";

    /// <summary>The REST API the orchestrator reads for the dashboard's Memory page.</summary>
    public static bool IsInternal(PathString path) => path.StartsWithSegments("/internal", StringComparison.OrdinalIgnoreCase);

    public static bool IsHealth(PathString path) => path.Equals("/health", StringComparison.OrdinalIgnoreCase);

    /// <summary>The MCP endpoint is mapped at the root: every route that is neither of the above.</summary>
    public static bool IsMcp(PathString path) => !IsInternal(path) && !IsHealth(path);
}
