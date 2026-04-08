using System.Text.Json.Serialization;

namespace Fleet.Agent.Models;

/// <summary>
/// Represents a single event from `claude -p --output-format stream-json --verbose`.
/// The stream produces one JSON object per line with varying shapes.
/// </summary>
public sealed class ClaudeStreamEvent
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("subtype")]
    public string? Subtype { get; set; }

    /// <summary>Result text for "result" events.</summary>
    [JsonPropertyName("result")]
    public string? Result { get; set; }

    /// <summary>Nested message for "assistant" events.</summary>
    [JsonPropertyName("message")]
    public ClaudeMessage? Message { get; set; }

    /// <summary>Session ID returned in system/result events.</summary>
    [JsonPropertyName("session_id")]
    public string? SessionId { get; set; }

    /// <summary>True when the result represents an error (e.g. max-turns exceeded).</summary>
    [JsonPropertyName("is_error")]
    public bool? IsError { get; set; }

    /// <summary>Number of turns consumed, returned in result events.</summary>
    [JsonPropertyName("num_turns")]
    public int? NumTurns { get; set; }

    /// <summary>Structured output object when --json-schema is used and result validates against schema.</summary>
    [JsonPropertyName("structured_output")]
    public System.Text.Json.JsonElement? StructuredOutput { get; set; }

    // --- Subagent / background task fields (present on system events with subtype task_*) ---

    /// <summary>Unique ID of the background task (task_started, task_progress, task_notification).</summary>
    [JsonPropertyName("task_id")]
    public string? TaskId { get; set; }

    /// <summary>Human-readable description of the background task.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>Task type: local_agent, local_bash, remote_agent.</summary>
    [JsonPropertyName("task_type")]
    public string? TaskType { get; set; }

    /// <summary>Completion status for task_notification events: completed, failed, stopped.</summary>
    [JsonPropertyName("status")]
    public string? TaskStatus { get; set; }

    /// <summary>Summary text from task_progress / task_notification.</summary>
    [JsonPropertyName("summary")]
    public string? TaskSummary { get; set; }

    /// <summary>Raw JSON for fields we don't explicitly model.</summary>
    [JsonExtensionData]
    public Dictionary<string, object>? ExtensionData { get; set; }
}

public sealed class ClaudeMessage
{
    [JsonPropertyName("content")]
    public List<ClaudeContentBlock>? Content { get; set; }
}

public sealed class ClaudeContentBlock
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>Text content when type is "text".</summary>
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    /// <summary>Tool name when type is "tool_use".</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Tool use ID.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>Tool input/arguments when type is "tool_use".</summary>
    [JsonPropertyName("input")]
    public Dictionary<string, object>? Input { get; set; }
}
