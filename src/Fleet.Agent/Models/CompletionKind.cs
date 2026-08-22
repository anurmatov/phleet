namespace Fleet.Agent.Models;

/// <summary>How a delegated task ended from the caller's perspective.</summary>
public enum CompletionKind
{
    /// <summary>Task ran to completion with output.</summary>
    Completed,
    /// <summary>Task failed, was cancelled, or produced an error.</summary>
    Failed,
    /// <summary>Task ran but produced only an IDLE marker — no substantive output.</summary>
    Idle,
    /// <summary>
    /// Task produced partial output but hit max turns or an executor error —
    /// a retry may complete it. Distinct from <see cref="Failed"/> so the
    /// DelegateToAgentActivity continuation loop can differentiate truncation
    /// from a hard failure.
    /// </summary>
    Incomplete,
}
