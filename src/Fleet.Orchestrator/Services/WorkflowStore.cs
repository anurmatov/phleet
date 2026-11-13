using Fleet.Orchestrator.Models;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// In-memory snapshot of currently running Temporal workflows, refreshed by TemporalPollerService.
/// </summary>
public sealed class WorkflowStore
{
    private IReadOnlyList<WorkflowSummary> _current = [];
    private IReadOnlyList<string> _namespaces = [];
    private readonly object _lock = new();

    public IReadOnlyList<WorkflowSummary> GetAll()
    {
        lock (_lock) return _current;
    }

    /// <summary>
    /// The snapshot with the namespaces it covers: those the poller listed successfully on its last
    /// pass. A namespace that is not covered was not looked at, which is not the same as having no
    /// running workflows; empty until the first poll.
    /// </summary>
    public (IReadOnlyList<WorkflowSummary> Runs, IReadOnlyList<string> Namespaces) Snapshot()
    {
        lock (_lock) return (_current, _namespaces);
    }

    /// <summary>
    /// Replaces the current snapshot and always returns true so that every poll cycle
    /// triggers a WebSocket broadcast. This ensures phase transitions, new search attributes
    /// (PrNumber, IssueNumber, Repo, DocPrs), and workflow completions are reflected in the
    /// dashboard within one poll cycle without requiring a page refresh.
    /// The workflow list is small (~10–50 entries) so the extra traffic is negligible.
    /// </summary>
    public bool Update(IReadOnlyList<WorkflowSummary> next, IReadOnlyList<string>? namespaces = null)
    {
        lock (_lock)
        {
            _current = next;
            _namespaces = namespaces ?? [.. next.Select(w => w.Namespace).Distinct(StringComparer.OrdinalIgnoreCase)];
            return true;
        }
    }
}
