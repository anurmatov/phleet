using System.ComponentModel;
using System.Text;
using Fleet.Memory.Services;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;

namespace Fleet.Memory.Tools;

[McpServerToolType]
public sealed class MemoryStatsTool(
    MemoryService memoryService,
    AclCacheService aclCache,
    IHttpContextAccessor httpContextAccessor,
    ILogger<MemoryStatsTool> logger)
{
    [McpServerTool(Name = "memory_stats")]
    [Description("Get statistics about stored memories — counts by type, total count, and archived count.")]
    public async Task<string> GetStatsAsync(CancellationToken cancellationToken)
    {
        // ACL disabled — original global counts, unchanged.
        if (!aclCache.IsAclEnabled)
            return Format(memoryService.GetStats());

        // #518: with ACLs enabled the counts are scoped to the caller's readable projects and never fall back to the
        // global counts. Fail closed before any scan: the ordinary cached ACL or the managed fresh request-local snapshot.
        if (!aclCache.IsAvailable)
            return "memory_stats: ACL cache unavailable";

        var agentName = httpContextAccessor.HttpContext?.Request.Query["agent"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(agentName))
            return "memory_stats: agent identity required — missing '?agent=' query parameter.";

        Dictionary<string, int> stats;
        try
        {
            stats = await memoryService.GetScopedStatsAsync(project => aclCache.CanRead(agentName, project).allowed, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Fixed text only: an exception message can name a path. No partial or global totals.
            logger.LogWarning("memory_stats: unavailable");
            return "memory_stats: unavailable";
        }

        return Format(stats);
    }

    private static string Format(Dictionary<string, int> stats)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Memory Statistics");
        sb.AppendLine();

        var total = 0;
        foreach (var (type, count) in stats.OrderByDescending(s => s.Value))
        {
            if (type == "_archived")
                continue;
            sb.AppendLine($"- **{type}**: {count}");
            total += count;
        }

        sb.AppendLine();
        sb.AppendLine($"**Total active**: {total}");
        if (stats.TryGetValue("_archived", out var archived))
            sb.AppendLine($"**Archived**: {archived}");

        return sb.ToString();
    }
}
