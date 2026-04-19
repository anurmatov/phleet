using System.ComponentModel;
using System.Text;
using Fleet.Memory.Services;
using ModelContextProtocol.Server;

namespace Fleet.Memory.Tools;

[McpServerToolType]
public sealed class MemoryGetTool(MemoryService memoryService)
{
    [McpServerTool(Name = "memory_get")]
    [Description("Get the full content of a specific memory by its ID. Use this after memory_search or memory_list to read the complete memory.")]
    public async Task<string> GetAsync(
        [Description("The memory ID (full UUID or first 8 characters)")] string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return "memory_get: missing required parameter 'id'.\nHint: pass the memory ID (full UUID or first 8 characters) from memory_search or memory_list.";

        var doc = await memoryService.GetAsync(id);

        if (doc is null)
            return $"Memory not found with ID: {id}";

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
