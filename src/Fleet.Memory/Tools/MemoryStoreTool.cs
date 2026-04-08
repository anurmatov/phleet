using System.ComponentModel;
using Fleet.Memory.Models;
using Fleet.Memory.Services;
using ModelContextProtocol.Server;

namespace Fleet.Memory.Tools;

[McpServerToolType]
public sealed class MemoryStoreTool(MemoryService memoryService)
{
    [McpServerTool(Name = "memory_store")]
    [Description("Store a new memory. Creates a markdown file and indexes it for semantic search. Use this to persist learnings, task results, decisions, error resolutions, and other knowledge.")]
    public async Task<string> StoreAsync(
        [Description("Memory type: task_result, learning, user_preference, codebase_knowledge, decision, error_resolution, conversation_summary, reference")] string type,
        [Description("Short descriptive title for this memory")] string title,
        [Description("The memory content — detailed information to remember")] string content,
        [Description("Agent name storing this memory")] string agent = "",
        [Description("Project this memory relates to")] string project = "",
        [Description("Comma-separated tags for categorization")] string tags = "",
        [Description("Source context (e.g., session ID, task ID)")] string source = "")
    {
        if (!MemoryDocument.ValidTypes.Contains(type))
            return $"Error: Invalid type '{type}'. Valid types: {string.Join(", ", MemoryDocument.ValidTypes)}";

        var doc = new MemoryDocument
        {
            Id = Guid.NewGuid().ToString(),
            Type = type,
            Title = title,
            Content = content,
            Agent = agent,
            Project = project,
            Tags = string.IsNullOrWhiteSpace(tags) ? [] : tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList(),
            Source = source
        };

        var (stored, similarMemories) = await memoryService.StoreAsync(doc);
        var result = $"Stored memory '{stored.Title}' (id: {stored.Id}, path: {stored.FilePath})";

        if (similarMemories.Count > 0)
        {
            result += "\n\nNote: Found similar existing memories — consider merging or updating instead:";
            foreach (var (existingId, existingTitle, score) in similarMemories)
                result += $"\n- {existingId[..8]} — {existingTitle} (similarity: {score:F2})";
        }

        return result;
    }
}
