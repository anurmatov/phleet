namespace Fleet.Agent.Models;

/// <summary>
/// Describes what happened to a task submitted via <see cref="Services.TaskManager.StartTask"/>.
/// </summary>
public enum TaskDispatchOutcome
{
    /// <summary>The task was accepted and started running immediately.</summary>
    Ran,

    /// <summary>The task was injected into the currently-running mid-turn session.</summary>
    Injected,

    /// <summary>The task was placed in the FIFO queue or the turn-end inbox and will run later.</summary>
    Queued,

    /// <summary>The global queue was full; the message was dropped (queue-full path).</summary>
    QueueFull,

    /// <summary>
    /// The task was silently dropped because it is not eligible to queue
    /// (e.g. <see cref="TaskSource.CheckIn"/> at capacity).
    /// </summary>
    Dropped,
}
