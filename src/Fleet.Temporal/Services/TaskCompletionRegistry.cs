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
    /// <summary>
    /// One registration per TaskId: the waiter and the managed correlation it expects, bound together
    /// (#492 R1/F14). Completion and cancellation compare-and-remove this exact instance (reference
    /// identity), so an old attempt can never remove or complete a replacement registration's waiter or
    /// strip its expected correlation.
    /// </summary>
    private sealed class Registration(TaskCompletionSource<AgentTaskResult> source, ManagedCorrelation? expected)
    {
        public TaskCompletionSource<AgentTaskResult> Source { get; } = source;
        public ManagedCorrelation? Expected { get; } = expected;
    }

    private readonly ConcurrentDictionary<string, Registration> _pending = new();
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
    /// The waiter and its expectation are stored as one entry in a single atomic write.
    /// </summary>
    public TaskCompletionSource<AgentTaskResult> Register(string taskId, ManagedCorrelation? expected)
    {
        var tcs = new TaskCompletionSource<AgentTaskResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = new Registration(tcs, expected);
        Registration? prior = null;
        _pending.AddOrUpdate(taskId, registration, (_, existing) => { prior = existing; return registration; });

        if (prior is not null && !prior.Source.Task.IsCompleted)
        {
            _logger.LogWarning(
                "Register overwrote a still-pending registration for TaskId={TaskId} — an earlier attempt is still in flight",
                taskId);
        }

        _logger.LogInformation("Registered pending task {TaskId}", taskId);
        return tcs;
    }

    /// <summary>Complete the pending task with the agent result. Returns true if matched.</summary>
    public bool TryComplete(string taskId, AgentTaskResult result) => TryComplete(taskId, result, null);

    /// <summary>
    /// Complete the pending task when the reply's managed context matches the registered expectation:
    /// a managed registration needs the same agent, sandbox, epoch and session; an ordinary registration
    /// refuses a reply that claims a managed context. The check and the removal apply to the same
    /// registration instance: if the TaskId is re-registered in between, the reply is re-checked against
    /// the replacement. Refusals log no content.
    /// </summary>
    public bool TryComplete(string taskId, AgentTaskResult result, ManagedCorrelation? context)
    {
        while (true)
        {
            if (!_pending.TryGetValue(taskId, out var registration))
            {
                _logger.LogWarning("No pending task for TaskId={TaskId} — ignoring response", taskId);
                return false;
            }

            var expected = registration.Expected;
            if (expected is null ? context is not null : context is null || !expected.Matches(context))
            {
                _logger.LogWarning("Reply for TaskId={TaskId} refused: managed correlation mismatch", taskId);
                return false;
            }

            if (!_pending.TryRemove(new KeyValuePair<string, Registration>(taskId, registration)))
                continue; // replaced or removed concurrently: decide against whatever owns the key now

            registration.Source.TrySetResult(result);
            _logger.LogInformation("Completed task {TaskId} with status={Status}", taskId, result.Status);
            return true;
        }
    }

    /// <summary>
    /// Cancel a pending task (e.g. on shutdown, timeout or activity cancellation).
    /// </summary>
    /// <param name="taskId">Correlation ID of the registration.</param>
    /// <param name="owned">
    /// The completion source the caller itself registered. When supplied this is a
    /// COMPARE-and-remove: the entry is dropped only if it is still that exact registration.
    ///
    /// This is load-bearing. TaskIds are stable across Temporal retries, so a later attempt
    /// registers under the same key. Without the identity check, attempt 1's cleanup removed and
    /// cancelled attempt 2's completion source — the live one — and attempt 2 then failed within
    /// seconds reporting a timeout it never waited for, while the agent's real reply arrived to an
    /// empty registry and was dropped (issue #321). The expected managed correlation lives in the same
    /// registration, so the cleanup can no longer strip a replacement's expectation either (#492 F14).
    ///
    /// Pass null only for callers with no registration of their own.
    /// </param>
    /// <returns>True if this call removed the registry entry.</returns>
    public bool TryCancel(string taskId, TaskCompletionSource<AgentTaskResult>? owned = null)
    {
        if (owned is null)
        {
            if (!_pending.TryRemove(taskId, out var any))
                return false;

            any.Source.TrySetCanceled();
            return true;
        }

        var removed = _pending.TryGetValue(taskId, out var registration)
            && ReferenceEquals(registration.Source, owned)
            && _pending.TryRemove(new KeyValuePair<string, Registration>(taskId, registration));

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
        foreach (var (id, registration) in _pending)
        {
            _logger.LogWarning("Cancelling pending task {TaskId} on shutdown", id);
            registration.Source.TrySetCanceled();
        }
        _pending.Clear();
    }

    public int PendingCount => _pending.Count;

    // ---- Managed workflow sessions (#492 R5, W6) ----

    /// <summary>Upper bound on retained managed sessions; the oldest are dropped beyond it.</summary>
    public const int MaxRetainedSessions = 1024;

    private sealed class RetainedSession(ManagedCorrelation correlation, long order)
    {
        public ManagedCorrelation Correlation { get; } = correlation;
        public long Order { get; } = order;
    }

    private readonly ConcurrentDictionary<string, RetainedSession> _sessions = new();
    private long _sessionOrder;

    /// <summary>
    /// The session for a managed delegation of <paramref name="taskId"/> to this agent/sandbox/epoch. A session
    /// retained by an earlier attempt of the same TaskId on the same epoch is reused, so a directive that left
    /// before its publication reported failure is still answered into the retry's registration (#492 R5); a
    /// different agent, sandbox or epoch gets a fresh session. Retention is bounded by
    /// <see cref="MaxRetainedSessions"/> and released on completion, cancellation and timeout.
    /// </summary>
    public ManagedCorrelation SessionFor(string taskId, string agent, string sandbox, Guid epoch)
    {
        var fresh = new RetainedSession(
            new ManagedCorrelation(agent, sandbox, epoch, Guid.NewGuid().ToString("N")),
            Interlocked.Increment(ref _sessionOrder));
        var chosen = _sessions.AddOrUpdate(taskId, fresh, (_, existing) =>
            string.Equals(existing.Correlation.Agent, agent, StringComparison.OrdinalIgnoreCase)
            && existing.Correlation.Sandbox == sandbox && existing.Correlation.Epoch == epoch
                ? existing
                : fresh);
        if (_sessions.Count > MaxRetainedSessions) TrimSessions();
        return chosen.Correlation;
    }

    /// <summary>Releases the retained session of <paramref name="taskId"/> only if it is still <paramref name="session"/>.</summary>
    public bool ReleaseSession(string taskId, ManagedCorrelation session) =>
        _sessions.TryGetValue(taskId, out var retained)
        && retained.Correlation == session
        && _sessions.TryRemove(new KeyValuePair<string, RetainedSession>(taskId, retained));

    public int RetainedSessionCount => _sessions.Count;

    private void TrimSessions()
    {
        var excess = _sessions.Count - MaxRetainedSessions;
        if (excess <= 0) return;
        foreach (var (id, retained) in _sessions.OrderBy(entry => entry.Value.Order).Take(excess).ToList())
            _sessions.TryRemove(new KeyValuePair<string, RetainedSession>(id, retained));
        _logger.LogWarning("Dropped {Count} oldest retained managed workflow sessions (bound {Bound})", excess, MaxRetainedSessions);
    }
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
