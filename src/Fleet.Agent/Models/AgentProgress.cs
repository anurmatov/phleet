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

    /// <summary>Structured JSON output when --json-schema is used and response validates against schema.</summary>
    public string? StructuredOutput { get; init; }
}
