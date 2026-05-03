using System.ComponentModel;
using System.Text;
using Fleet.Memory.Models;
using Fleet.Memory.Services;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;

namespace Fleet.Memory.Tools;

[McpServerToolType]
public sealed class MemoryGetTool(
    MemoryService memoryService,
    ReadCounterService readCounter,
    AclCacheService aclCache,
    IHttpContextAccessor httpContextAccessor,
    ILogger<MemoryGetTool> logger)
{
    [McpServerTool(Name = "memory_get")]
    [Description("Get the full content of a specific memory by its ID. Use this after memory_search or memory_list to read the complete memory.")]
    public async Task<string> GetAsync(
        [Description("The memory ID (full UUID or first 8 characters)")] string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return "memory_get: missing required parameter 'id'.\nHint: pass the memory ID (full UUID or first 8 characters) from memory_search or memory_list.";

        var agentName = httpContextAccessor.HttpContext?.Request.Query["agent"].FirstOrDefault();

        // When ACL is enabled: fail-closed if cache unavailable, require agent identity
        if (aclCache.IsAvailable is false && aclCache.IsAclEnabled)
            return "memory_get: ACL cache unavailable — orchestrator unreachable. Try again shortly.";

        if (string.IsNullOrWhiteSpace(agentName) && aclCache.IsAclEnabled)
            return "memory_get: agent identity required — missing '?agent=' query parameter.";

        if (aclCache.IsAclEnabled)
        {
            // Fetch to determine project, but deny uniformly (no existence leak to unauthorized agents).
            var probe = await memoryService.GetAsync(id);
            var project = probe?.Project ?? "";
            var (allowed, denyReason) = aclCache.CanRead(agentName!, project);
            if (!allowed)
            {
                logger.LogInformation("memory_get: denied agent '{Agent}' on id '{Id}': {Reason}", agentName, id, denyReason);
                return "memory_get: access denied.";
            }

            if (probe is null)
                return $"Memory not found with ID: {id}";

            readCounter.RecordRead(probe.Id, agentName ?? "unknown");
            return FormatDocument(probe);
        }

        // ACL disabled — original path
        var doc = await memoryService.GetAsync(id);
        if (doc is null)
            return $"Memory not found with ID: {id}";

        readCounter.RecordRead(doc.Id, agentName ?? "unknown");
        return FormatDocument(doc);
    }

    private static string FormatDocument(MemoryDocument doc)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {doc.Title}");
        sb.AppendLine();
        sb.AppendLine($"- **ID**: {doc.Id}");
        sb.AppendLine($"- **Type**: {doc.Type}");
        if (!string.IsNullOrEmpty(doc.Agent))
            sb.AppendLine($"- **Agent**: {doc.Agent}");
        if (!string.IsNullOrEmpty(doc.Project))
            sb.AppendLine($"- **Project**: {doc.Project}");
        if (doc.Tags.Count > 0)
            sb.AppendLine($"- **Tags**: {string.Join(", ", doc.Tags)}");
        if (!string.IsNullOrEmpty(doc.Source))
            sb.AppendLine($"- **Source**: {doc.Source}");
        sb.AppendLine($"- **Created**: {doc.Created:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- **Updated**: {doc.Updated:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine(doc.Content);
        return sb.ToString();
    }
}
