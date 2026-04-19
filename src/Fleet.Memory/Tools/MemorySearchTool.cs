using System.ComponentModel;
using System.Text;
using Fleet.Memory.Models;
using Fleet.Memory.Services;
using ModelContextProtocol.Server;

namespace Fleet.Memory.Tools;

[McpServerToolType]
public sealed class MemorySearchTool(MemoryService memoryService)
{
    [McpServerTool(Name = "memory_search")]
    [Description("Semantic search across all memories. Returns the most relevant memories matching the query. Use this to find prior knowledge, past decisions, error resolutions, and learnings.")]
    public async Task<string> SearchAsync(
        [Description("Natural language search query")] string query,
        [Description("Maximum number of results (default 5)")] int limit = 5,
        [Description("Filter by memory type (optional)")] string? type = null,
        [Description("Filter by project (optional)")] string? project = null,
        [Description("Filter by agent name (optional)")] string? agent = null)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "memory_search: missing required parameter 'query'.\nHint: pass a natural language search query describing what you're looking for.";

        if (type is not null && !MemoryDocument.ValidTypes.Contains(type))
            return $"memory_search: invalid value for 'type' filter: '{type}'.\nValid types: {string.Join(", ", MemoryDocument.ValidTypes)}.";

        var results = await memoryService.SearchAsync(query, limit, type, project, agent);

        if (results.Count == 0)
            return "No memories found matching the query.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {results.Count} memories:");
        sb.AppendLine();

        foreach (var doc in results)
        {
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
            sb.AppendLine();

            // Truncate content for search results
            var preview = doc.Content.Length > 500 ? doc.Content[..500] + "..." : doc.Content;
            sb.AppendLine(preview);
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
