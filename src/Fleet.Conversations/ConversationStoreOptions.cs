using Fleet.Conversations.Contracts;

namespace Fleet.Conversations;

/// <summary>
/// Timings and retention horizons for the durable conversation store.
/// </summary>
/// <remarks>
/// Every value here is pinned rather than left to the implementer, and each is operator-settable.
/// </remarks>
public sealed class ConversationStoreOptions
{
    /// <summary>
    /// How long an attempt's lease is good for without a heartbeat.
    /// </summary>
    /// <remarks>
    /// Initialised from <see cref="ConversationLeaseDefaults.LeaseDuration"/> rather than from a
    /// literal, because the agent validates its own heartbeat interval against that same constant
    /// and two literals would drift.
    /// </remarks>
    public TimeSpan LeaseDuration { get; set; } = ConversationLeaseDefaults.LeaseDuration;

    /// <summary>How often the owner is expected to renew. Four renewals per lease.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How often the reconciler looks for expired leases.</summary>
    public TimeSpan ReconcilerScanInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long after the service itself becomes healthy again before the reconciler may abandon
    /// anything.
    /// </summary>
    /// <remarks>
    /// Without this, a restart abandons every in-flight attempt the instant the process comes back —
    /// the leases all expired while nothing was running to renew them, and none of that work was
    /// actually lost.
    /// </remarks>
    public TimeSpan ReconcilerGraceAfterRecovery { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>How often garbage collection runs.</summary>
    public TimeSpan GarbageCollectionInterval { get; set; } = TimeSpan.FromHours(1);

    // ── Retention ────────────────────────────────────────────────────────────────────

    /// <summary>Progress and notices. Prunable; their absence at a seq is not a gap.</summary>
    public TimeSpan EphemeralEventRetention { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Durable events. Their absence beneath the floor IS a gap.</summary>
    public TimeSpan DurableEventRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>Terminal submissions and their attempts. Must be at least the durable horizon.</summary>
    public TimeSpan SubmissionRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>Published outbox rows. Pending rows are never collected.</summary>
    public TimeSpan OutboxRetention { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Completed delivery claims.
    /// </summary>
    /// <remarks>
    /// ⚠️ MUST exceed the broker's redelivery horizon, AND the longest a submission can legitimately
    /// wait in the queue.
    ///
    /// <para>The second bound is the one that is easy to miss. The <c>done</c> claim, within its
    /// retention, is the ONLY thing standing between a redelivered command and a duplicate turn —
    /// the attempt state machine refuses a <i>second</i> start of a running attempt but not a
    /// <i>first</i> one, and a queued attempt stays pending for as long as it waits. Collect the
    /// claim early and that guard is simply gone.</para>
    /// </remarks>
    public TimeSpan DeliveryClaimRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Idle conversations are closed, never deleted.</summary>
    public TimeSpan IdleConversationRetention { get; set; } = TimeSpan.FromDays(90);

    /// <summary>How long a freshly taken delivery claim is held before it may be taken over.</summary>
    public TimeSpan ClaimHoldDuration { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>Rows claimed per outbox publish batch.</summary>
    public int OutboxBatchSize { get; set; } = 100;

    /// <summary>Default catch-up page size.</summary>
    public int DefaultReadLimit { get; set; } = 200;

    /// <summary>
    /// Largest catch-up page. A larger request is REJECTED, never clamped: a client that asked for
    /// 5000 and silently received 1000 would believe it held the whole suffix.
    /// </summary>
    public int MaxReadLimit { get; set; } = 1000;

    /// <summary>
    /// Fails fast on a combination that cannot hold, rather than letting it show up as data loss
    /// weeks later.
    /// </summary>
    public void Validate()
    {
        if (SubmissionRetention < DurableEventRetention)
            throw new InvalidOperationException(
                "SubmissionRetention must be at least DurableEventRetention: collecting a submission "
                + "while its events survive leaves events that can never be explained.");

        if (HeartbeatInterval >= LeaseDuration)
            throw new InvalidOperationException(
                "HeartbeatInterval must be shorter than LeaseDuration, or a lease expires before its "
                + "owner is ever asked to renew it.");

        if (DefaultReadLimit > MaxReadLimit)
            throw new InvalidOperationException("DefaultReadLimit cannot exceed MaxReadLimit.");

        if (LeaseDuration <= TimeSpan.Zero || ReconcilerScanInterval <= TimeSpan.Zero)
            throw new InvalidOperationException("Lease and reconciler intervals must be positive.");
    }
}
