namespace Fleet.Temporal.Models;

/// <summary>
/// Result returned when an agent completes a delegated task.
/// </summary>
public sealed record AgentTaskResult(
    /// <summary>The agent's response text.</summary>
    string Text,

    /// <summary>
    /// Completion status: "completed", "incomplete" (context limit hit), or "failed".
    /// Parsed from [status: X] prefix that agents include when TaskId is set.
    /// </summary>
    string Status)
{
    public bool IsCompleted => Status == "completed";
    public bool IsIncomplete => Status == "incomplete";
    public bool IsFailed => Status == "failed";
}
