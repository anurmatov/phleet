namespace Fleet.Orchestrator.Models;

/// <summary>
/// Snapshot of a running Temporal workflow execution.
/// </summary>
public sealed record WorkflowSummary(
    string WorkflowId,
    string RunId,
    string WorkflowType,
    string Namespace,
    string? TaskQueue,
    string Status,
    DateTimeOffset StartTime,
    DateTimeOffset? CloseTime = null,
    int? IssueNumber = null,
    int? PrNumber = null,
    string? Repo = null,
    string? DocPrs = null,
    string? Phase = null,
    string? GateRef = null,
    // What a run belongs to. Appended, and null for a run that stands alone, so a client from before
    // them reads what it always did.
    //
    // The Temporal parent: the run that started this one as a child workflow.
    string? ParentWorkflowId = null,
    string? ParentRunId = null,
    // The run that waits for this one (its input's WaiterWorkflowId): a driver that started it
    // through the start tool, which Temporal does not record as a parent.
    string? WaiterWorkflowId = null);
