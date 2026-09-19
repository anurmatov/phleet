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

    // ── attachments (#308) ───────────────────────────────────────────────────

    /// <summary>
    /// Reservation outcomes: <c>reserved</c>, <c>too_large</c>, <c>unsupported_type</c>,
    /// <c>quota_conversation</c>, <c>quota_deployment</c>.
    /// </summary>
    /// <remarks>
    /// <c>quota_deployment</c> is split from <c>quota_conversation</c> on purpose. The first is an
    /// operator condition — the volume is filling and nobody has noticed — and the second is one
    /// conversation doing something ordinary. Aggregating them hides the one that needs a human.
    /// </remarks>
    public static readonly Counter<long> AttachmentReserve =
        Meter.CreateCounter<long>("fleet.conversations.attachment.reserve", "reservations");

    /// <summary>
    /// Seal outcomes: <c>sealed</c>, <c>refused</c>, <c>failed</c>, <c>digest_mismatch</c>,
    /// <c>type_mismatch</c>, <c>too_many_pixels</c>, <c>overflow</c>, <c>volume_unavailable</c>.
    /// </summary>
    /// <remarks>
    /// This is the counter that separates "a client is uploading broken files" from "the volume is
    /// full", and the two have completely different responses.
    /// </remarks>
    public static readonly Counter<long> AttachmentSeal =
        Meter.CreateCounter<long>("fleet.conversations.attachment.seal", "attachments");

    /// <summary>
    /// Fetch outcomes: <c>served</c>, <c>not_found</c>, <c>gone</c>.
    /// </summary>
    /// <remarks>
    /// <c>gone</c> rising is the signal that the volume and the database have diverged — a restored
    /// dump without its volume, or bytes deleted out of band. Nothing else produces it.
    /// </remarks>
    public static readonly Counter<long> AttachmentFetch =
        Meter.CreateCounter<long>("fleet.conversations.attachment.fetch", "fetches");

    /// <summary>Files on the volume with no row, removed by the sweep.</summary>
    public static readonly Counter<long> AttachmentOrphanFiles =
        Meter.CreateCounter<long>("fleet.conversations.attachment.orphan_files_swept", "files");

    /// <summary>
    /// Live attachment bytes, observed. The number the per-deployment cap is measured against.
    /// </summary>
    public static void ObserveAttachmentBytes(Func<IEnumerable<Measurement<long>>> liveBytes) =>
        Meter.CreateObservableGauge("fleet.conversations.attachment.live_bytes", liveBytes, "By");
}
