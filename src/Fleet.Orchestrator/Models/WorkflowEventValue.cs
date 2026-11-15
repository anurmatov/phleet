namespace Fleet.Orchestrator.Models;

/// <summary>One explicitly requested field from one exact workflow execution's history.</summary>
public sealed record WorkflowEventValue(
    string Namespace,
    string WorkflowId,
    string RunId,
    long EventId,
    string Field,
    long? SourceEventId,
    string Status,
    IReadOnlyList<WorkflowEventText> Values,
    string? Reason,
    int LimitBytes)
{
    public const int MaximumBytes = 1048576;
}

/// <summary>Stored text, not a parsed JSON value or a reconstructed history preview.</summary>
public sealed record WorkflowEventText(string Encoding, string Text);

// Distinguish this reader's total deadline from an earlier transport timeout.
internal sealed class WorkflowEventValueTimeoutException : TimeoutException;
