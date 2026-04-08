using System.ComponentModel;
using System.Text;
using Fleet.Memory.Services;
using ModelContextProtocol.Server;

namespace Fleet.Memory.Tools;

[McpServerToolType]
public sealed class MemoryListTool(MemoryService memoryService)
{
    [McpServerTool(Name = "memory_list")]
    [Description("List memories with optional filters. Returns a summary of matching memories without full content. Use memory_get to read full content.")]
    public async Task<string> ListAsync(
        [Description("Filter by memory type (optional)")] string? type = null,
        [Description("Filter by project (optional)")] string? project = null,
        [Description("Filter by agent name (optional)")] string? agent = null,
        [Description("Filter by tag (optional)")] string? tag = null)
    {
        var results = await memoryService.ListAsync(type, project, agent, tag);

        if (results.Count == 0)
            return "No memories found matching the filters.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {results.Count} memories:");
        sb.AppendLine();

        // Sort by created date descending
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
