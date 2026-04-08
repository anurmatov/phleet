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
    string? Phase = null);
