using Fleet.Protocol;

namespace Fleet.Conversations.Contracts;

/// <summary>What the agent decided to do with a command it was handed (#276 §6).</summary>
/// <remarks>
/// <para><see cref="QueueFull"/> and <see cref="Dropped"/> are dispositions on a submission that is
/// ALREADY DURABLE, not errors. Reporting either as an HTTP failure would tell a client that a
/// committed submission had failed, and a client that retried would create a second one.</para>
/// </remarks>
public enum SubmissionDisposition
{
    /// <summary>A turn started for it immediately.</summary>
    Ran,

    /// <summary>Merged into a turn already running; it has no turn of its own.</summary>
    Injected,

    /// <summary>Accepted and waiting behind a running turn. Its turn starts later, same attempt.</summary>
    Queued,

    /// <summary>Refused for want of queue capacity. Terminal on arrival; no turn ever starts.</summary>
    QueueFull,

    /// <summary>Discarded by the agent's dispatch. Terminal on arrival; no turn ever starts.</summary>
    Dropped,
}

/// <summary>Lifecycle state of a submission as the store records it (#276 §6).</summary>
public enum SubmissionState
{
    PendingDispatch,
    Running,
    Queued,
    Merged,
    Terminal,
}

/// <summary>Lifecycle state of one execution attempt (#276 §6).</summary>
public enum AttemptState
{
    Pending,
    Running,
    Merged,
    Committed,
    Abandoned,
}

/// <summary>Whether an event survives garbage collection (#276 §4.3).</summary>
public enum EventRetentionClass
{
    /// <summary>Prunable. Its absence at a seq is NOT a gap.</summary>
    Ephemeral,

    /// <summary>Retained for the durable horizon. Its absence beneath the floor IS a gap.</summary>
    Durable,
}

// ─────────────────────────────────────────────────────────────── open

/// <param name="ClientInstanceId">
/// Opaque bookkeeping label, never a credential and never device identity.
/// </param>
public sealed record OpenConversationRequest
{
    public required string ChannelId { get; init; }
    public required string ExternalRef { get; init; }
    public required string PrincipalId { get; init; }
    public string? ClientInstanceId { get; init; }
}

public sealed record OpenConversationResult
{
    public required string ConversationId { get; init; }
    public required ulong NextSeq { get; init; }
    public required ulong RetainedFloorSeq { get; init; }
}

// ─────────────────────────────────────────────────────────────── accept (TX1a)

public sealed record AcceptSubmissionRequest
{
    public required string ConversationId { get; init; }
    public required string ExternalSubmissionId { get; init; }
    public required string PayloadFingerprint { get; init; }
    public string? IdempotencyKey { get; init; }
}

/// <summary>Which of #276 §7's four accept outcomes occurred.</summary>
public enum AcceptOutcome
{
    /// <summary>A new submission was created. The client sees <c>201</c>.</summary>
    Accepted,

    /// <summary>
    /// The same key and the same fingerprint, and the original is resolved. The client sees the
    /// original <c>201</c> again, byte-identically.
    /// </summary>
    Replay,

    /// <summary>
    /// The same key and the same fingerprint, but the disposition transaction is still outstanding,
    /// so there is no disposition to report yet. The client sees <c>202</c>.
    ///
    /// <para>A FIRST accept is never this — it is <see cref="Accepted"/>. This is only ever a
    /// same-key retry arriving mid-flight.</para>
    /// </summary>
    ReplayPending,

    /// <summary>Same key, different fingerprint. The client sees <c>409</c> and nothing changes.</summary>
    Conflict,
}

public sealed record AcceptSubmissionResult
{
    public required AcceptOutcome Outcome { get; init; }

    /// <summary>Internal submission id. Null only on <see cref="AcceptOutcome.Conflict"/>.</summary>
    public string? SubmissionId { get; init; }

    /// <summary>Wire submission id, echoed to the client.</summary>
    public string? ExternalSubmissionId { get; init; }

    /// <summary>
    /// The wire <c>acceptedSeq</c>: the conversation's <c>next_seq</c> as read INSIDE the accept
    /// transaction — the first seq any event about this submission can occupy.
    ///
    /// <para>⚠️ This is NOT #276 §6's <c>accepted_seq</c> column, and the names are close enough to
    /// be a trap. That column is the seq of the <c>submission.accepted</c> event and is NULL until
    /// the disposition; returning it here would hand back <c>null</c> on every first accept. This
    /// value is stored on the submission row as <c>accept_floor_seq</c> so the first response, an
    /// idempotent replay and the abandoned replay all return the SAME number.</para>
    ///
    /// <para>A client catching up on the whole lifecycle asks for
    /// <c>afterSeq = acceptedSeq - 1</c>, because <c>afterSeq</c> means "processed up to and
    /// including", while this is the seq the first event will take.</para>
    ///
    /// <para>Null only on <see cref="AcceptOutcome.ReplayPending"/> and
    /// <see cref="AcceptOutcome.Conflict"/>.</para>
    /// </summary>
    public ulong? AcceptedSeq { get; init; }
}

// ─────────────────────────────────────────────────────────────── delivery claim

public sealed record ClaimDeliveryRequest
{
    /// <summary>The broker message id. The claim key is <c>inbound:</c> + this.</summary>
    public required string MessageId { get; init; }

    /// <summary>Who is taking the claim — the agent's own identity, not a credential.</summary>
    public required string Owner { get; init; }
}

public enum ClaimOutcome
{
    /// <summary>This caller now owns the delivery and should proceed.</summary>
    Claimed,

    /// <summary>Already processed. Acknowledge the broker message and stop — start no turn.</summary>
    DuplicateDone,

    /// <summary>A live claim is held by someone else. Negative-acknowledge with requeue.</summary>
    HeldElsewhere,
}

public sealed record ClaimDeliveryResult
{
    public required ClaimOutcome Outcome { get; init; }

    /// <summary>
    /// Set only on <see cref="ClaimOutcome.Claimed"/>. The claim is where ownership actually
    /// transfers, so it is the honest place to hand over the identifiers every later call is keyed
    /// on — putting <c>attemptId</c> in the broker message instead would publish it before anyone
    /// owned it and leave a redelivery carrying an identifier another process is mid-way using.
    ///
    /// <para><see cref="ClaimOutcome.DuplicateDone"/> and <see cref="ClaimOutcome.HeldElsewhere"/>
    /// return no identifiers at all.</para>
    /// </summary>
    public string? SubmissionId { get; init; }

    /// <inheritdoc cref="SubmissionId"/>
    public string? AttemptId { get; init; }

    /// <inheritdoc cref="SubmissionId"/>
    public string? ConversationId { get; init; }
}

// ─────────────────────────────────────────────────────────────── disposition (TX1b)

/// <summary>
/// Records what the agent did with a command, for <see cref="SubmissionDisposition.Ran"/>,
/// <see cref="SubmissionDisposition.Injected"/> and <see cref="SubmissionDisposition.Queued"/>.
///
/// <para>ONE transaction: appends <c>submission.accepted</c>, sets <c>accepted_seq</c>, moves the
/// submission, transfers the lease to the agent, and marks the claim <c>done</c>. The claim
/// completes HERE rather than after the turn, because a turn can outlive the broker's
/// acknowledgement timeout and the requeue that follows would meet a live claim and loop for the
/// duration of the turn.</para>
/// </summary>
public sealed record RecordDispositionRequest
{
    public required string MessageId { get; init; }
    public required string SubmissionId { get; init; }
    public required string AttemptId { get; init; }
    public required SubmissionDisposition Disposition { get; init; }
    public int? QueuePosition { get; init; }
    public required string Epoch { get; init; }
    public required ulong Ordinal { get; init; }

    /// <summary>The agent taking ownership of the lease from this point on.</summary>
    public required string Owner { get; init; }
}

/// <summary>
/// Terminates a command on arrival, for <see cref="SubmissionDisposition.QueueFull"/> and
/// <see cref="SubmissionDisposition.Dropped"/>.
///
/// <para>Replaces the disposition, turn and commit steps entirely rather than running alongside
/// them: <c>submission.accepted</c> is appended exactly ONCE per submission on every path.</para>
/// </summary>
public sealed record CompleteDeliveryRequest
{
    public required string MessageId { get; init; }
    public required string SubmissionId { get; init; }
    public required string AttemptId { get; init; }
    public required SubmissionDisposition Disposition { get; init; }
    public required string Epoch { get; init; }
    public required ulong Ordinal { get; init; }
}

/// <summary>
/// The result of a disposition or delivery-completion write.
///
/// <para><see cref="Replayed"/> distinguishes a write from a retry that found the claim already
/// <c>done</c>: a retried call writes nothing and returns the recorded result, so no event is
/// appended twice and no legitimate retry is refused.</para>
/// </summary>
public sealed record DispositionResult
{
    public required ulong AcceptedSeq { get; init; }
    public required bool Replayed { get; init; }
}

// ─────────────────────────────────────────────────────────────── turns

public sealed record StartTurnRequest
{
    public required string AttemptId { get; init; }
    public required string TurnId { get; init; }
    public required string Epoch { get; init; }
    public required ulong Ordinal { get; init; }
    public IReadOnlyList<string>? MergedSubmissionIds { get; init; }
}

public sealed record StartTurnResult
{
    /// <summary>Seq of the <c>turn.started</c> event.</summary>
    public required ulong Seq { get; init; }
    public required bool Replayed { get; init; }
}

public sealed record CommitTerminalRequest
{
    public required string AttemptId { get; init; }
    public required ConversationEvent TerminalEvent { get; init; }
    public IReadOnlyList<string>? MergedSubmissionIds { get; init; }
    public required string Epoch { get; init; }
    public required ulong Ordinal { get; init; }
}

public sealed record CommitTerminalResult
{
    public required ulong Seq { get; init; }
    public required bool Replayed { get; init; }

    /// <summary>
    /// True when the terminal arrived after the reconciler had already abandoned the attempt, and
    /// was therefore preserved as <c>turn.recovered_answer</c> rather than overwriting the terminal
    /// that won. A successful answer is never destroyed by a store outage.
    /// </summary>
    public required bool RecoveredAnswer { get; init; }
}

// ─────────────────────────────────────────────────────────────── append, read, cursor

public sealed record AppendBatchRequest
{
    public required string ConversationId { get; init; }
    public required string Epoch { get; init; }
    public required IReadOnlyList<StagedEvent> Events { get; init; }
}

public sealed record StagedEvent
{
    public required ConversationEvent Event { get; init; }
    public required ulong Ordinal { get; init; }
    public string? SubmissionId { get; init; }
    public string? AttemptId { get; init; }
    public required EventRetentionClass RetentionClass { get; init; }
}

public sealed record AppendBatchResult
{
    /// <summary>Seq assigned per event, in request order. An event dropped after a terminal is null.</summary>
    public required IReadOnlyList<ulong?> Seqs { get; init; }
}

public sealed record ReadConversationRequest
{
    public required string ConversationId { get; init; }
    public required ulong AfterSeq { get; init; }
    public required int Limit { get; init; }

    /// <summary>
    /// The caller's principal. A conversation belonging to someone else is reported as
    /// not-found, byte-identically to one that does not exist, so there is no existence oracle.
    /// </summary>
    public required string PrincipalId { get; init; }
}

public sealed record ReadConversationResult
{
    /// <summary>
    /// Owed durable history, when garbage collection has passed the reader's cursor. Delivered
    /// FIRST, before the suffix — catch-up is request/response, so ordering is array order.
    /// Synthetic and never stored.
    /// </summary>
    public ConversationReplayGapPayload? Gap { get; init; }

    public required IReadOnlyList<StoredEvent> Events { get; init; }
    public required ulong NextAfterSeq { get; init; }
    public required bool HasMore { get; init; }
}

public sealed record StoredEvent
{
    public required ulong Seq { get; init; }
    public required string EventId { get; init; }
    public required string Kind { get; init; }
    public required DateTimeOffset EmittedAt { get; init; }
    public string? PayloadJson { get; init; }
    public required bool IsTerminal { get; init; }
    public required EventRetentionClass RetentionClass { get; init; }
}

public sealed record AckCursorRequest
{
    public required string ClientInstanceId { get; init; }
    public required string ConversationId { get; init; }
    public required ulong DeliveredSeq { get; init; }
    public ulong? ReadSeq { get; init; }
}

// ─────────────────────────────────────────────────────────────── leases

public sealed record HeartbeatRequest
{
    /// <summary>
    /// EVERY attempt this caller owns and has not committed — running ones and
    /// dispositioned-but-unstarted ones alike.
    ///
    /// <para>Heartbeating only the running attempt abandons a full queue at the lease bound and
    /// reports it to the client as an unknown outcome, which is the failure #276 D-15c exists to
    /// prevent.</para>
    /// </summary>
    public required IReadOnlyList<string> AttemptIds { get; init; }

    public required string Owner { get; init; }
}

public sealed record HeartbeatResult
{
    /// <summary>Attempts whose lease was renewed.</summary>
    public required IReadOnlyList<string> Renewed { get; init; }

    /// <summary>
    /// Attempts this caller no longer owns — already abandoned, committed, or leased elsewhere.
    /// Reported rather than silently ignored so a caller can stop working on them.
    /// </summary>
    public required IReadOnlyList<string> NotOwned { get; init; }
}
