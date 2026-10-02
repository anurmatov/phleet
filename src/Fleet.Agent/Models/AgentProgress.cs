namespace Fleet.Agent.Models;

/// <summary>
/// A processed progress event emitted by the Claude executor,
/// suitable for forwarding to Telegram or CLI output.
/// </summary>
public sealed class AgentProgress
{
    /// <summary>Whether this update is worth sending to the user (filters noise).</summary>
    public bool IsSignificant { get; init; }

    /// <summary>Human-readable summary of what's happening.</summary>
    public required string Summary { get; init; }

    /// <summary>The raw event type from the Claude stream.</summary>
    public required string EventType { get; init; }

    /// <summary>Tool name if this progress relates to a tool call.</summary>
    public string? ToolName { get; init; }

    /// <summary>Serialized JSON args for the tool call (truncated to 500 chars). Null for non-tool events.</summary>
    public string? ToolArgs { get; init; }

    /// <summary>Set when the stream is complete. Contains final assistant text.</summary>
    public string? FinalResult { get; init; }

    /// <summary>Session ID for resuming the conversation.</summary>
    public string? SessionId { get; init; }

    /// <summary>Execution size stats (sent/received bytes). Set on the final stats event.</summary>
    public ExecutionStats? Stats { get; init; }

    /// <summary>True when the result event indicates an error (max-turns, tool failure, etc.).</summary>
    public bool IsErrorResult { get; init; }

    /// <summary>True only when the provider process exited mid-turn and injected input may need at-least-once redelivery.</summary>
    public bool IsProcessExit { get; init; }

    /// <summary>Structured JSON output when --json-schema is used and response validates against schema.</summary>
    public string? StructuredOutput { get; init; }

    /// <summary>
    /// On a <c>recovered_answer</c>, the preserved stale text split at each stale result and tagged
    /// by provenance. <see cref="Summary"/> stays the full concatenation; null when not segmented.
    /// </summary>
    public IReadOnlyList<RecoveredSegment>? RecoveredSegments { get; init; }
}

/// <summary>One preserved stale-text segment and the provenance of the turn that produced it.</summary>
public sealed record RecoveredSegment(string Text, string Origin)
{
    /// <summary>Ended by a result whose origin is absent or human: a stdin-started turn.</summary>
    public const string User = "user";
    /// <summary>Ended by a result with any other origin, such as a background task notification.</summary>
    public const string Notification = "notification";
    /// <summary>No result arrived in the drained events: unknown provenance.</summary>
    public const string Open = "open";
}
