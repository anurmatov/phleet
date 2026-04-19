using System.ComponentModel;
using Fleet.Memory.Services;
using ModelContextProtocol.Server;

namespace Fleet.Memory.Tools;

[McpServerToolType]
public sealed class MemoryUpdateTool(MemoryService memoryService)
{
    [McpServerTool(Name = "memory_update")]
    [Description("Update an existing memory's title, content, tags, or project. The memory will be automatically re-indexed for search.")]
    public async Task<string> UpdateAsync(
        [Description("The memory ID to update")] string id,
        [Description("New title (optional, pass empty to keep current)")] string? title = null,
        [Description("New content (optional, pass empty to keep current)")] string? content = null,
        [Description("New comma-separated tags (optional, pass empty to keep current)")] string? tags = null,
        [Description("New project name (optional, pass empty to keep current)")] string? project = null)
    {
        if (string.IsNullOrWhiteSpace(id))
            return "memory_update: missing required parameter 'id'.\nHint: pass the memory ID (full UUID or first 8 characters) from memory_search or memory_list.";

        try
        {
            List<string>? tagList = tags is not null
                ? tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList()
                : null;

            // Pass null for unchanged fields
            var actualTitle = string.IsNullOrEmpty(title) ? null : title;
            var actualContent = string.IsNullOrEmpty(content) ? null : content;
            var actualProject = string.IsNullOrEmpty(project) ? null : project;

            var doc = await memoryService.UpdateAsync(id, actualTitle, actualContent, tagList, actualProject);
            return $"Updated memory '{doc.Title}' (id: {doc.Id})";
        }
        catch (FileNotFoundException)
        {
            return $"Memory not found with ID: {id}";
        }
    }
}
