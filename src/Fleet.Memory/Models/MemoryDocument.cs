namespace Fleet.Memory.Models;

public sealed class MemoryDocument
{
    public required string Id { get; set; }
    public required string Type { get; set; }
    public required string Title { get; set; }
    public string Agent { get; set; } = "";
    public string Project { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public string Source { get; set; } = "";
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset Updated { get; set; } = DateTimeOffset.UtcNow;
    public required string Content { get; set; }
    public string FilePath { get; set; } = "";

    public static readonly string[] ValidTypes =
    [
        "task_result",
        "learning",
        "user_preference",
        "codebase_knowledge",
        "decision",
        "error_resolution",
        "conversation_summary",
        "reference"
    ];

    /// <summary>
    /// Normalizes a project name: strips hyphens, underscores, spaces, and lowercases.
    /// e.g. "My-Project" → "myproject", "my project" → "myproject"
    /// </summary>
    public static string NormalizeProject(string project) =>
        string.IsNullOrWhiteSpace(project)
            ? project
            : project.Replace("-", "").Replace("_", "").Replace(" ", "").ToLowerInvariant();
}
