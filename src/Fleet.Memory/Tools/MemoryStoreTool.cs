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
        if (string.IsNullOrWhiteSpace(type))
            return $"memory_store: missing required parameter 'type'.\nHint: pass one of: {string.Join(", ", MemoryDocument.ValidTypes)}.";

        if (!MemoryDocument.ValidTypes.Contains(type))
            return $"memory_store: invalid value for 'type': '{type}'.\nValid types: {string.Join(", ", MemoryDocument.ValidTypes)}.";

        if (string.IsNullOrWhiteSpace(title))
            return "memory_store: missing required parameter 'title'.\nHint: pass a short descriptive title (5-10 words).";

        if (string.IsNullOrWhiteSpace(content))
            return "memory_store: missing required parameter 'content'.\nHint: pass the full memory content to store.";

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

        try
        {
            var (stored, similarMemories, indexingWarning) = await memoryService.StoreAsync(doc);

            var result = indexingWarning is not null
                ? $"Stored memory '{stored.Title}' (id: {stored.Id}, path: {stored.FilePath})\n{indexingWarning}"
                : $"Stored memory '{stored.Title}' (id: {stored.Id}, path: {stored.FilePath})";

            if (similarMemories.Count > 0)
            {
                result += "\n\nNote: Found similar existing memories — consider merging or updating instead:";
                foreach (var (existingId, existingTitle, score) in similarMemories)
                    result += $"\n- {existingId[..8]} — {existingTitle} (similarity: {score:F2})";
            }

            return result;
        }
        catch (InvalidDataException ex)
        {
            return $"error_serialization_validation: {ex.Message}";
        }
        catch (IOException ex)
        {
            return $"error_write_failed: {ex.Message}";
        }
    }
}
