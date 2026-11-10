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
    public TaskCompletionSource<AgentTaskResult> Register(string taskId)
    {
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
    public bool TryComplete(string taskId, AgentTaskResult result)
    {
        if (!_pending.TryRemove(taskId, out var tcs))
        {
            _logger.LogWarning("No pending task for TaskId={TaskId} — ignoring response", taskId);
            return false;
        }

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

            tcs.TrySetCanceled();
            return true;
        }

        var removed = _pending.TryRemove(
            new KeyValuePair<string, TaskCompletionSource<AgentTaskResult>>(taskId, owned));

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
    }

    public int PendingCount => _pending.Count;
}
