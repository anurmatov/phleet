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
}
