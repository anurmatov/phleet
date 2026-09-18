using System.Diagnostics.Metrics;

namespace Fleet.Conversations;

/// <summary>
/// The counters this slice publishes.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>Labels carry route, status, kind, disposition, outcome and reason — and nothing else.</b>
/// No submission text, no event text, no tool name, no principal id, no device id, no conversation
/// id, no token, no path, no exception message.
/// </para>
/// <para>
/// That list is short on purpose and the reason is not tidiness: a metric label is retained by the
/// scrape target, duplicated into every dashboard and alert, and shipped wherever those go. A
/// conversation id there is a per-user identifier in a system nobody thinks of as holding user
/// data. An unbounded label is also a cardinality explosion, so the privacy rule and the
/// operational one point the same way.
/// </para>
/// </remarks>
public static class ConversationMetrics
{
    public const string MeterName = "Fleet.Conversations";

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    // ── outboxes ─────────────────────────────────────────────────────────────

    /// <summary>Rows confirmed by the broker, per outbox.</summary>
    public static readonly Counter<long> OutboxPublished =
        Meter.CreateCounter<long>("fleet.conversations.outbox.published", "rows");

    /// <summary>
    /// Publish attempts that were not confirmed, per outbox.
    /// </summary>
    /// <remarks>
    /// Rising while <see cref="OutboxPublished"/> is flat is a broker problem. The client is
    /// unaffected — its submission is durable — so this is the signal that dispatch has stopped,
    /// and it is the one that would otherwise be invisible.
    /// </remarks>
    public static readonly Counter<long> OutboxUnconfirmed =
        Meter.CreateCounter<long>("fleet.conversations.outbox.unconfirmed", "rows");

    /// <summary>
    /// Pending depth and the age of the oldest pending row, observed per outbox.
    /// </summary>
    /// <remarks>
    /// The AGE is the one that matters. Depth alone cannot distinguish a busy minute from a broker
    /// that has been unreachable for an hour, and the second is the one that needs an operator.
    /// </remarks>
    public static void ObservePending(Func<IEnumerable<Measurement<long>>> depth,
        Func<IEnumerable<Measurement<double>>> oldestAgeSeconds)
    {
        Meter.CreateObservableGauge("fleet.conversations.outbox.pending", depth, "rows");
        Meter.CreateObservableGauge(
            "fleet.conversations.outbox.oldest_pending_age", oldestAgeSeconds, "s");
    }

    // ── claims ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Claim outcomes, tagged by kind: <c>claimed</c>, <c>duplicate_done</c>, <c>held_elsewhere</c>.
    /// </summary>
    /// <remarks>
    /// <c>held_elsewhere</c> rising is a redelivery meeting a live claim — ordinary in small
    /// numbers, and a loop if it is sustained. <c>duplicate_done</c> rising is the guard doing its
    /// job: a redelivered command that started no second turn.
    /// </remarks>
    public static readonly Counter<long> ClaimOutcomes =
        Meter.CreateCounter<long>("fleet.conversations.claims", "claims");

    // ── reconciler ───────────────────────────────────────────────────────────

    /// <summary>
    /// Reconciler actions by outcome: <c>abandoned</c>, <c>held_within_grace</c>, <c>skipped</c>.
    /// </summary>
    public static readonly Counter<long> ReconcilerActions =
        Meter.CreateCounter<long>("fleet.conversations.reconciler.actions", "attempts");

    /// <summary>
    /// <c>turn.outcome_unknown</c> by reason.
    /// </summary>
    /// <remarks>
    /// Split by reason because each has exactly one producer and they mean different things:
    /// <c>attempt_abandoned</c> is this service's reconciler, <c>turn_reaped</c> is the agent's own
    /// run loop, and <c>terminal_event_oversize</c> is a terminal that could not be truncated small
    /// enough. Aggregating them hides which component is failing.
    /// </remarks>
    public static readonly Counter<long> OutcomeUnknown =
        Meter.CreateCounter<long>("fleet.conversations.outcome_unknown", "turns");

    // ── retention ────────────────────────────────────────────────────────────

    /// <summary>Rows removed by garbage collection, tagged by what they were.</summary>
    public static readonly Counter<long> Collected =
        Meter.CreateCounter<long>("fleet.conversations.gc.collected", "rows");
}
