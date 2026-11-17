using System.ComponentModel;
using System.Globalization;
using System.Text;
using Fleet.Memory.Models;
using Fleet.Memory.Services;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using Fleet.Shared.Mcp;

namespace Fleet.Memory.Tools;

[McpServerToolType]
public sealed class MemorySearchTool(
    MemoryService memoryService,
    AclCacheService aclCache,
    IHttpContextAccessor httpContextAccessor,
    AclDeniedCounterService deniedCounter,
    ILogger<MemorySearchTool> logger)
{
    [McpServerTool(Name = "memory_search")]
    [Description("Search across all memories. A query that is a memory id (or the first 6 or more characters of one) finds that memory; otherwise memories are found by title, by tag, by the words of the query and by its meaning, in that order. Each result says how it matched and shows the passage that matched. Use this to find prior knowledge, past decisions, error resolutions, and learnings; use memory_get to read a result in full.")]
    public async Task<string> SearchAsync(
        [Description("A memory id, a title, a tag, keywords or a natural language question")] string query,
        [Description("Maximum number of results (default 5)")] int limit = 5,
        [Description("Filter by memory type (optional)")] string? type = null,
        [Description("Filter by project (optional)")] string? project = null,
        [Description("Filter by agent name (optional)")] string? agent = null,
        [Description("Filter by tag, exact (optional)")] string? tag = null,
        [Description("Only memories updated on or after this ISO 8601 date, e.g. 2026-03-01 (optional)")] string? since = null)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "memory_search: missing required parameter 'query'.\nHint: pass a natural language search query describing what you're looking for.";

        DateTimeOffset? sinceDate = null;
        if (!string.IsNullOrWhiteSpace(since))
        {
            if (!SearchDates.TryParseSince(since, out var parsedSince))
                return $"memory_search: invalid value for 'since' filter: '{since}'.\nHint: pass an ISO 8601 date such as 2026-03-01.";
            sinceDate = parsedSince;
        }

        if (type is not null && !MemoryDocument.ValidTypes.Contains(type))
            return $"memory_search: invalid value for 'type' filter: '{type}'.\nValid types: {string.Join(", ", MemoryDocument.ValidTypes)}.";

        var agentName = AgentCallerHttp.AgentNameOf(httpContextAccessor);

        if (aclCache.IsAclEnabled && !aclCache.IsAvailable)
            return "memory_search: ACL cache unavailable — orchestrator unreachable. Try again shortly.";

        if (aclCache.IsAclEnabled && string.IsNullOrWhiteSpace(agentName))
            return "memory_search: agent identity required — this call was not made as an agent.";

        // When ACL is active, request a 3x candidate pool to compensate for post-filter evictions.
        // Known precision trade-off: allowed memories may score lower than disallowed neighbors.
        // We query 3x, filter, and return up to the original limit — no further expansion.
        var fetchLimit = aclCache.IsAclEnabled ? limit * 3 : limit;

        var results = await memoryService.SearchAsync(query, fetchLimit, type, project, agent,
            string.IsNullOrWhiteSpace(tag) ? null : tag, sinceDate);

        // Post-filter by ACL with explicit denial logging
        if (aclCache.IsAclEnabled)
        {
            var filtered = new List<MemorySearchHit>();
            foreach (var d in results)
            {
                var (allowed, _) = aclCache.CanRead(agentName!, d.Project ?? "");
                if (allowed)
                {
                    filtered.Add(d);
                }
                else
                {
                    deniedCounter.Increment(agentName!, "memory_search");
                    // #512: fixed tool/reason fields only; no record, agent or project identifier (the ACL reason names them).
                    logger.LogInformation("memory_search: denied ({Reason})",
                        string.IsNullOrWhiteSpace(d.Project) ? "no_project" : "project_not_allowed");
                }
            }
            results = filtered.Take(limit).ToList();
        }

        if (results.Count == 0)
            return "No memories found matching the query.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {results.Count} memories:");
        sb.AppendLine();

        foreach (var hit in results)
        {
            var doc = hit.Document;
            sb.AppendLine($"## {doc.Title}");
            sb.AppendLine($"- **ID**: {doc.Id}");
            sb.AppendLine($"- **Type**: {doc.Type}");
            if (!string.IsNullOrEmpty(doc.Project))
                sb.AppendLine($"- **Project**: {doc.Project}");
            if (!string.IsNullOrEmpty(doc.Agent))
                sb.AppendLine($"- **Agent**: {doc.Agent}");
            if (doc.Tags.Count > 0)
                sb.AppendLine($"- **Tags**: {string.Join(", ", doc.Tags)}");
            sb.AppendLine($"- **Created**: {doc.Created:yyyy-MM-dd HH:mm}");
            sb.AppendLine($"- **Match**: {DescribeMatch(hit)}");
            sb.AppendLine();

            sb.AppendLine(hit.Snippet.Text);
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>How a hit matched, for the reader: the kind, and the similarity of a hit found by meaning alone.</summary>
    private static string DescribeMatch(MemorySearchHit hit) => hit.Match switch
    {
        MemoryMatch.Meaning when hit.Score is { } score =>
            $"meaning (similarity {score.ToString("0.00", CultureInfo.InvariantCulture)}; no word of the query matched)",
        _ => hit.Match,
    };
}
