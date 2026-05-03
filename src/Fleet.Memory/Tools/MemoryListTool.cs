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

        var results = await memoryService.ListAsync(type, project, agent, tag);

        // Post-filter by ACL (disallowed memories simply don't appear in results)
        if (aclCache.IsAclEnabled)
        {
            results = results.Where(d =>
            {
                var memProject = d.GetValueOrDefault("project") ?? "";
                var (allowed, _) = aclCache.CanRead(agentName!, memProject);
                return allowed;
            }).ToList();
        }

        if (results.Count == 0)
            return "No memories found matching the filters.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {results.Count} memories:");
        sb.AppendLine();

        var sorted = results.OrderByDescending(d => d.GetValueOrDefault("created", ""));

        foreach (var doc in sorted)
        {
            var title = doc.GetValueOrDefault("title", "Untitled");
            var memType = doc.GetValueOrDefault("memory_type", "unknown");
            var memId = doc.GetValueOrDefault("memory_id", "");
            var created = doc.GetValueOrDefault("created", "");
            var project2 = doc.GetValueOrDefault("project", "");
            var tags = doc.GetValueOrDefault("tags", "");

            var createdDate = created.Length >= 10 ? created[..10] : created;

            sb.AppendLine($"- **{title}** ({memType})");
            sb.Append($"  ID: `{memId}` | Created: {createdDate}");
            if (!string.IsNullOrEmpty(project2))
                sb.Append($" | Project: {project2}");
            if (!string.IsNullOrEmpty(tags))
                sb.Append($" | Tags: {tags}");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
