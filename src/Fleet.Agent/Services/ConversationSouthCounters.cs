using System.Collections.Concurrent;

namespace Fleet.Agent.Services;

/// <summary>
/// Observability for the south seam (#303).
/// </summary>
/// <remarks>
/// <para>
/// Labels are identifiers, kinds, outcomes and status codes. Never a bearer token, never a broker
/// connection string, never conversation text (MUST NOT 19) — a counter label ends up in a metrics
/// scrape, which is a wider audience than a log line.
/// </para>
/// <para>
/// Several of these exist to make a <b>correct but lossy</b> path visible. A refused hand-off, a
/// dropped terminal and a fence rejection all degrade to something the store already handles; the
/// point of counting them is that "degraded" and "healthy" are otherwise indistinguishable from
/// outside.
/// </para>
/// </remarks>
public sealed class ConversationSouthCounters
{
    // south_deliveries_dispositioned_total{kind}
    public const string DispositionCancel = "cancel";
    public const string DispositionUnknownKind = "unknown_kind";
    public const string DispositionIntakeRejected = "intake_rejected";

    private readonly ConcurrentDictionary<string, long> _claims = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _dispositions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _handoffRefusals = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _simple = new(StringComparer.Ordinal);

    /// <summary>One per <c>/deliveries:claim</c>, labelled by outcome.</summary>
    public void Claim(string outcome) => Bump(_claims, outcome);

    /// <summary>One per disposition or terminal-on-arrival completion, labelled by kind.</summary>
    public void Disposition(string kind) => Bump(_dispositions, kind);

    /// <summary>Events appended through <c>/events:append</c>.</summary>
    public void Appended(int count) => Add(_simple, "appended", count);

    /// <summary>
    /// Events the store dropped after a terminal, returning a <c>null</c> seq. Expected, not an
    /// error — the client already has its terminal.
    /// </summary>
    public void AppendDroppedAfterTerminal(int count) => Add(_simple, "append_dropped_after_terminal", count);

    /// <summary>Terminals committed.</summary>
    public void Commit() => Add(_simple, "commits", 1);

    /// <summary>
    /// Commits where the reconciler had already abandoned the attempt and the answer was preserved
    /// as <c>turn.recovered_answer</c>. A success, never a retry (D6).
    /// </summary>
    public void RecoveredAnswer() => Add(_simple, "recovered_answers", 1);

    /// <summary>Turn starts.</summary>
    public void TurnStarted() => Add(_simple, "turn_starts", 1);

    /// <summary>A <c>/turns:start</c> refused with 409 — a different turn on a running attempt.</summary>
    public void TurnStartConflict() => Add(_simple, "turn_start_conflicts", 1);

    /// <summary>Heartbeat round trips that failed. The turn keeps running regardless.</summary>
    public void HeartbeatFailure() => Add(_simple, "heartbeat_failures", 1);

    /// <summary>Attempts the heartbeat reported this process no longer owns.</summary>
    public void HeartbeatNotOwned(int count) => Add(_simple, "heartbeat_not_owned", count);

    /// <summary>Broker messages negatively acknowledged with requeue.</summary>
    public void Nack() => Add(_simple, "nacks", 1);

    /// <summary>A hand-off enqueue the per-conversation queue refused.</summary>
    public void HandoffRefused(string kind) => Bump(_handoffRefusals, kind);

    /// <summary>
    /// A refused hand-off whose event was a TERMINAL. Separate from the general refusal counter
    /// because the consequence is different in kind: the attempt now degrades to lease expiry and
    /// <c>attempt_abandoned</c> for a turn that actually answered.
    /// </summary>
    public void TerminalDropped() => Add(_simple, "terminals_dropped", 1);

    /// <summary>
    /// A stale-epoch or out-of-order rejection. With send-site allocation and in-order transmission
    /// this can only mean a second appender or a rewound counter (D7a).
    /// </summary>
    public void FenceRejection() => Add(_simple, "fence_rejections", 1);

    /// <summary>Client conversations registered in this process (D9a — nothing evicts them).</summary>
    public void ConversationRegistered() => Add(_simple, "registered_conversations", 1);

    /// <summary>An event the adapter could not route to the seam and discarded.</summary>
    public void AdapterDiscarded(string reason) => Bump(_handoffRefusals, $"discarded:{reason}");

    public long ClaimCount(string outcome) => Read(_claims, outcome);
    public long DispositionCount(string kind) => Read(_dispositions, kind);
    public long HandoffRefusedCount(string kind) => Read(_handoffRefusals, kind);
    public long AdapterDiscardedCount(string reason) => Read(_handoffRefusals, $"discarded:{reason}");
    public long AppendedCount => Read(_simple, "appended");
    public long AppendDroppedAfterTerminalCount => Read(_simple, "append_dropped_after_terminal");
    public long CommitCount => Read(_simple, "commits");
    public long RecoveredAnswerCount => Read(_simple, "recovered_answers");
    public long TurnStartedCount => Read(_simple, "turn_starts");
    public long TurnStartConflictCount => Read(_simple, "turn_start_conflicts");
    public long HeartbeatFailureCount => Read(_simple, "heartbeat_failures");
    public long HeartbeatNotOwnedCount => Read(_simple, "heartbeat_not_owned");
    public long NackCount => Read(_simple, "nacks");
    public long TerminalDroppedCount => Read(_simple, "terminals_dropped");
    public long FenceRejectionCount => Read(_simple, "fence_rejections");
    public long RegisteredConversationCount => Read(_simple, "registered_conversations");

    private static void Bump(ConcurrentDictionary<string, long> map, string key) => Add(map, key, 1);

    private static void Add(ConcurrentDictionary<string, long> map, string key, long delta)
    {
        if (delta <= 0) return;
        map.AddOrUpdate(key, delta, (_, c) => c + delta);
    }

    private static long Read(ConcurrentDictionary<string, long> map, string key) =>
        map.TryGetValue(key, out var value) ? value : 0L;
}
