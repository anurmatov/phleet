using System.Collections.Concurrent;
using Fleet.Temporal.Models;
using Microsoft.Extensions.Logging;

namespace Fleet.Temporal.Services;

/// <summary>
/// In-memory registry mapping TaskId → TaskCompletionSource.
/// The TemporalRelayListener calls SetResult when an agent response arrives;
/// DelegateToAgentActivity awaits the completion.
/// </summary>
public sealed class TaskCompletionRegistry
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<AgentTaskResult>> _pending = new();
    // Managed stock delegations (#492): the agent/sandbox/epoch/session a reply for the TaskId must carry.
    private readonly ConcurrentDictionary<string, ManagedCorrelation> _expected = new();
    private readonly ILogger<TaskCompletionRegistry> _logger;

    public TaskCompletionRegistry(ILogger<TaskCompletionRegistry> logger)
    {
        _logger = logger;
    }

    /// <summary>Register a pending task and return its completion source.</summary>
    /// <remarks>
    /// TaskIds are deliberately stable across Temporal retries, so a retried attempt lands on the
    /// same key as its predecessor. The newest registration owning the key is the intended
    /// behaviour — it is the one still being awaited — but a *live* predecessor means two attempts
    /// are in flight at once, which is worth a log line rather than being inferred hours later
    /// from a confusing timeout.
    /// </remarks>
    public TaskCompletionSource<AgentTaskResult> Register(string taskId) => Register(taskId, null);

    /// <summary>
    /// Register a pending task; with <paramref name="expected"/> only a reply carrying exactly that managed
    /// context (agent, sandbox, epoch, session) completes it, and a reply without one never does (#492).
    /// </summary>
    public TaskCompletionSource<AgentTaskResult> Register(string taskId, ManagedCorrelation? expected)
    {
        if (expected is null) _expected.TryRemove(taskId, out _);
        else _expected[taskId] = expected;
        var tcs = new TaskCompletionSource<AgentTaskResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (_pending.TryGetValue(taskId, out var prior) && !prior.Task.IsCompleted)
        {
            _logger.LogWarning(
                "Register overwrote a still-pending registration for TaskId={TaskId} — an earlier attempt is still in flight",
                taskId);
        }

        _pending[taskId] = tcs;
        _logger.LogInformation("Registered pending task {TaskId}", taskId);
        return tcs;
    }

    /// <summary>Complete the pending task with the agent result. Returns true if matched.</summary>
    public bool TryComplete(string taskId, AgentTaskResult result) => TryComplete(taskId, result, null);

    /// <summary>
    /// Complete the pending task when the reply's managed context matches the registered expectation:
    /// a managed registration needs the same agent, sandbox, epoch and session; an ordinary registration
    /// refuses a reply that claims a managed context. Refusals log no content.
    /// </summary>
    public bool TryComplete(string taskId, AgentTaskResult result, ManagedCorrelation? context)
    {
        _expected.TryGetValue(taskId, out var expected);
        if (expected is null ? context is not null : context is null || !expected.Matches(context))
        {
            _logger.LogWarning("Reply for TaskId={TaskId} refused: managed correlation mismatch", taskId);
            return false;
        }
        if (!_pending.TryRemove(taskId, out var tcs))
        {
            _logger.LogWarning("No pending task for TaskId={TaskId} — ignoring response", taskId);
            return false;
        }

        if (expected is not null)
            _expected.TryRemove(new KeyValuePair<string, ManagedCorrelation>(taskId, expected));
        tcs.TrySetResult(result);
        _logger.LogInformation("Completed task {TaskId} with status={Status}", taskId, result.Status);
        return true;
    }

    /// <summary>
    /// Cancel a pending task (e.g. on shutdown, timeout or activity cancellation).
    /// </summary>
    /// <param name="taskId">Correlation ID of the registration.</param>
    /// <param name="owned">
    /// The completion source the caller itself registered. When supplied this is a
    /// COMPARE-and-remove: the entry is dropped only if it is still that exact instance.
    ///
    /// This is load-bearing. TaskIds are stable across Temporal retries, so a later attempt
    /// registers under the same key. Without the identity check, attempt 1's cleanup removed and
    /// cancelled attempt 2's completion source — the live one — and attempt 2 then failed within
    /// seconds reporting a timeout it never waited for, while the agent's real reply arrived to an
    /// empty registry and was dropped (issue #321).
    ///
    /// Pass null only for callers with no registration of their own.
    /// </param>
    /// <returns>True if this call removed the registry entry.</returns>
    public bool TryCancel(string taskId, TaskCompletionSource<AgentTaskResult>? owned = null)
    {
        if (owned is null)
        {
            if (!_pending.TryRemove(taskId, out var tcs))
                return false;

            _expected.TryRemove(taskId, out _);
            tcs.TrySetCanceled();
            return true;
        }

        var removed = _pending.TryRemove(
            new KeyValuePair<string, TaskCompletionSource<AgentTaskResult>>(taskId, owned));

        if (removed) _expected.TryRemove(taskId, out _);

        // Our own source is cancelled either way — whoever awaits it must not hang — but whatever
        // else is under the key is left strictly alone.
        owned.TrySetCanceled();

        if (!removed)
        {
            _logger.LogWarning(
                "TryCancel for TaskId={TaskId} found a different registration — leaving it pending",
                taskId);
        }

        return removed;
    }

    /// <summary>Cancel all pending tasks on shutdown.</summary>
    public void CancelAll()
    {
        foreach (var (id, tcs) in _pending)
        {
            _logger.LogWarning("Cancelling pending task {TaskId} on shutdown", id);
            tcs.TrySetCanceled();
        }
        _pending.Clear();
        _expected.Clear();
    }

    public int PendingCount => _pending.Count;
}

/// <summary>
/// The managed context a workflow reply must carry (#492): the target agent, its sandbox and epoch from the
/// selected authority record, and the per-delegation session nonce sent only on that epoch's control lane.
/// </summary>
public sealed record ManagedCorrelation(string Agent, string Sandbox, Guid Epoch, string Session)
{
    public bool Matches(ManagedCorrelation other) =>
        string.Equals(Agent, other.Agent, StringComparison.OrdinalIgnoreCase) && Sandbox == other.Sandbox
        && Epoch == other.Epoch && Session == other.Session;
}
