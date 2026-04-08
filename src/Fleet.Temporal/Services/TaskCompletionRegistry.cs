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
    public TaskCompletionSource<AgentTaskResult> Register(string taskId)
    {
        var tcs = new TaskCompletionSource<AgentTaskResult>(TaskCreationOptions.RunContinuationsAsynchronously);
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

    /// <summary>Cancel a pending task (e.g. on shutdown).</summary>
    public bool TryCancel(string taskId)
    {
        if (!_pending.TryRemove(taskId, out var tcs))
            return false;

        tcs.TrySetCanceled();
        return true;
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
