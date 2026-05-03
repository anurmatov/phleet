using System.ComponentModel;
using System.Text;
using Fleet.Memory.Models;
using Fleet.Memory.Services;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;

namespace Fleet.Memory.Tools;

[McpServerToolType]
public sealed class MemoryListTool(
    MemoryService memoryService,
    AclCacheService aclCache,
    IHttpContextAccessor httpContextAccessor)
{
    [McpServerTool(Name = "memory_list")]
    [Description("List memories with optional filters. Returns a summary of matching memories without full content. Use memory_get to read full content.")]
    public async Task<string> ListAsync(
        [Description("Filter by memory type (optional)")] string? type = null,
        [Description("Filter by project (optional)")] string? project = null,
        [Description("Filter by agent name (optional)")] string? agent = null,
        [Description("Filter by tag (optional)")] string? tag = null)
    {
        if (type is not null && !MemoryDocument.ValidTypes.Contains(type))
            return $"memory_list: invalid value for 'type' filter: '{type}'.\nValid types: {string.Join(", ", MemoryDocument.ValidTypes)}.";

        var agentName = httpContextAccessor.HttpContext?.Request.Query["agent"].FirstOrDefault();

        if (aclCache.IsAclEnabled && !aclCache.IsAvailable)
            return "memory_list: ACL cache unavailable — orchestrator unreachable. Try again shortly.";

        if (aclCache.IsAclEnabled && string.IsNullOrWhiteSpace(agentName))
            return "memory_list: agent identity required — missing '?agent=' query parameter.";

        var results = await memoryService.ListDocumentsAsync(type, project, agent, tag);

        // Post-filter by ACL using the typed Project field (consistent with memory_get / memory_search)
        if (aclCache.IsAclEnabled)
        {
            results = results.Where(d =>
            {
                var (allowed, _) = aclCache.CanRead(agentName!, d.Project ?? "");
                return allowed;
            }).ToList();
        }

        if (results.Count == 0)
            return "No memories found matching the filters.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {results.Count} memories:");
        sb.AppendLine();

        var sorted = results.OrderByDescending(d => d.Created);

        foreach (var doc in sorted)
        {
            var createdDate = doc.Created.ToString("yyyy-MM-dd");

            sb.AppendLine($"- **{doc.Title}** ({doc.Type})");
            sb.Append($"  ID: `{doc.Id}` | Created: {createdDate}");
            if (!string.IsNullOrEmpty(doc.Project))
                sb.Append($" | Project: {doc.Project}");
            if (doc.Tags.Count > 0)
                sb.Append($" | Tags: {string.Join(", ", doc.Tags)}");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
