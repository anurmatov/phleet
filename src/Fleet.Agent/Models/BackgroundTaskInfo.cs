namespace Fleet.Agent.Models;

/// <summary>
/// Snapshot of an active subagent background task spawned via the Claude SDK Agent tool
/// with run_in_background=true.  Populated from task_started / task_progress /
/// task_notification system events in the NDJSON stream.
/// </summary>
public sealed class BackgroundTaskInfo
{
    /// <summary>Unique task ID assigned by the Claude SDK.</summary>
    public required string TaskId { get; init; }

    /// <summary>Human-readable description supplied at task creation time.</summary>
    public required string Description { get; init; }

    /// <summary>Task type: local_agent, local_bash, remote_agent.</summary>
    public required string TaskType { get; init; }

    /// <summary>When the task_started event was received.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Latest summary text from task_progress events.</summary>
    public string? Summary { get; set; }

    /// <summary>Seconds this task has been running (computed on read).</summary>
    public int ElapsedSeconds => (int)(DateTimeOffset.UtcNow - StartedAt).TotalSeconds;
}
