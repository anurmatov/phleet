using Fleet.Shared.Security;

namespace Fleet.Memory.Services;

/// <summary>
/// The memory receiver on the shared <see cref="StockServiceIdentityMiddleware"/> boundary: legacy/internal
/// routes are inaccessible and only a verified, memory-bound outside assertion supplies the query identity
/// consumed by existing tools. Memory additionally admits only on a fresh verified ACL snapshot and the
/// agent's current endpoint/tool grants, never on the legacy stale cache.
/// </summary>
public sealed class StockMemoryIdentityMiddleware(string authorityFile, RequestDelegate next,
    ILogger<StockMemoryIdentityMiddleware>? logger = null, string? authorityDirectory = null)
{
    private readonly StockServiceIdentityMiddleware _receiver = new(StockServices.Memory, authorityFile, authorityDirectory, logger);

    public Task InvokeAsync(HttpContext context, AclCacheService acl) => _receiver.InvokeAsync(context, next,
        async (http, record, tool, admission, proceed) =>
        {
            // Unlike the legacy cache policy, this boundary never admits on a failed/stale fetch.
            var snapshot = acl.IsAclEnabled ? await acl.LoadVerifiedSnapshotAsync(admission) : null;
            if (snapshot is null) { http.Response.StatusCode = 503; return; }
            // The scope must span the continuation: an AsyncLocal set here flows into the MCP tools.
            using var aclScope = acl.UseVerifiedSnapshot(snapshot);
            var grants = await acl.CanUseVerifiedMemoryRouteAsync(record.Agent, record.MemoryHost, tool, admission);
            if (!grants.Available) { http.Response.StatusCode = 503; return; }
            if (!grants.Allowed) throw new StockIdentityDenied("route_or_tool_not_granted");
            await proceed();
        });
}
