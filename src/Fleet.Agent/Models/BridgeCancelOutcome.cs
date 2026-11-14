namespace Fleet.Agent.Models;

/// <summary>
/// What became of a cancel that named the execution it was for
/// (<see cref="Services.TaskManager.CancelBridgeExecutionAsync"/>). Every value is final: the
/// caller has nothing to wait for and nothing to send again.
/// </summary>
public enum BridgeCancelOutcome
{
    /// <summary>
    /// The task was held under that execution id: running and told to stop, queued and removed,
    /// or not yet admitted and now never to be.
    /// </summary>
    Cancelled,

    /// <summary>No task is held under the task id: it has finished, or it never arrived.</summary>
    NotFound,

    /// <summary>The task id is held by a different execution. It was left alone.</summary>
    OtherExecution,

    /// <summary>
    /// The task id is held by a task whose directive carried no execution id. It was left alone:
    /// whatever started it, it was not the delegation this cancel was issued for.
    /// </summary>
    NoExecutionId,
}

/// <summary>
/// The agent's answer to <c>POST /cancel/{taskId}?executionId=…</c>. Echoing the id is how the
/// caller knows the agent compared it; an agent from before the id answers without it.
/// </summary>
public sealed record BridgeCancelAnswer(bool Cancelled, string TaskId, string ExecutionId, string Outcome)
{
    public static BridgeCancelAnswer From(string taskId, string executionId, BridgeCancelOutcome outcome) =>
        new(outcome == BridgeCancelOutcome.Cancelled, taskId, executionId, outcome switch
        {
            BridgeCancelOutcome.Cancelled => TaskCancel.Outcome.Cancelled,
            BridgeCancelOutcome.NotFound => TaskCancel.Outcome.NotFound,
            BridgeCancelOutcome.OtherExecution => TaskCancel.Outcome.OtherExecution,
            _ => TaskCancel.Outcome.NoExecutionId,
        });
}
