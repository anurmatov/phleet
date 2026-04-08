namespace Fleet.Orchestrator.Models;

/// <summary>
/// Simplified representation of a single Temporal workflow history event.
/// </summary>
public sealed record WorkflowEventSummary(
    long EventId,
    string EventType,       // "WorkflowStarted" | "WorkflowCompleted" | "WorkflowFailed" | "WorkflowCanceled" | "ActivityScheduled" | "ActivityCompleted" | "ActivityFailed" | "SignalReceived"
    DateTimeOffset Timestamp,
    string? ActivityType,   // for ActivityScheduled/ActivityCompleted/ActivityFailed
    string? Agent,          // extracted from DelegateToAgentActivity input: first string arg
    string? InputSummary,   // decoded JSON payload, truncated to 2000 chars
    string? OutputSummary,  // decoded JSON payload for completed events, truncated to 2000 chars
    string? SignalName,     // for SignalReceived
    string? FailureMessage  // for WorkflowFailed / ActivityFailed
);
