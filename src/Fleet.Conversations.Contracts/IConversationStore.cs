namespace Fleet.Conversations.Contracts;

/// <summary>
/// The complete durable-conversation store surface (#276 §4.9, as amended by #298).
/// </summary>
/// <remarks>
/// <para>
/// Every method is marked <b>awaited</b> or <b>staged</b>, because that is what decides whether the
/// no-blocking-the-executor rule applies to it. <i>Awaited</i> calls are made on a request path that
/// already blocks on I/O and never on an executor or chat-send path. <i>Staged</i> calls are
/// enqueued and performed by the appender; the caller never waits.
/// </para>
/// <para>
/// ⚠️ This interface lives in a project that MUST NOT acquire a database dependency. The agent holds
/// no database credential and reaches the store only through the south endpoints; a driver reachable
/// by referencing this contract would make that boundary decorative while every assertion still
/// passed.
/// </para>
/// </remarks>
public interface IConversationStore
{
    /// <summary>
    /// <b>awaited</b> — upsert a conversation and bind its principal.
    /// </summary>
    Task<OpenConversationResult> OpenConversationAsync(
        OpenConversationRequest request, CancellationToken ct = default);

    /// <summary>
    /// <b>awaited</b> — TX1a. Creates the submission, reads and stores the accept floor, and writes
    /// the command outbox row IN THE SAME TRANSACTION.
    /// </summary>
    /// <remarks>
    /// <para>The outbox row is not a second write: a submission row with no command row, or a
    /// command row with no submission row, cannot be produced, including when the process is killed
    /// between them. That is what makes a durable submission always dispatched and a non-durable one
    /// never dispatched.</para>
    /// <para>The returned <c>AcceptedSeq</c> is the stored floor, never #276's internal
    /// <c>accepted_seq</c> column.</para>
    /// </remarks>
    Task<AcceptSubmissionResult> AcceptSubmissionAsync(
        AcceptSubmissionRequest request, CancellationToken ct = default);

    /// <summary>
    /// <b>awaited</b> — take or resolve the durable claim on an inbound broker message.
    /// </summary>
    /// <remarks>
    /// The claim is what stands between a redelivered command and a duplicate turn, and within its
    /// retention it is the ONLY thing standing there. The attempt state machine refuses a SECOND
    /// start of a running attempt; it does not refuse a first one, and a queued attempt is pending
    /// for as long as it waits.
    /// </remarks>
    Task<ClaimDeliveryResult> ClaimDeliveryAsync(
        ClaimDeliveryRequest request, CancellationToken ct = default);

    /// <summary>
    /// <b>awaited</b> — TX1b for <c>ran</c>, <c>injected</c> and <c>queued</c>. One transaction:
    /// append <c>submission.accepted</c>, set <c>accepted_seq</c>, move the submission, transfer the
    /// lease to the agent, complete the claim.
    /// </summary>
    Task<DispositionResult> RecordDispositionAsync(
        RecordDispositionRequest request, CancellationToken ct = default);

    /// <summary>
    /// <b>awaited</b> — the terminal-on-arrival branch, for <c>queue_full</c> and <c>dropped</c>.
    /// </summary>
    /// <remarks>
    /// An ALTERNATIVE to <see cref="RecordDispositionAsync"/>, never a sequence after it: recording
    /// the disposition in both would append <c>submission.accepted</c> twice for one submission.
    /// </remarks>
    Task<DispositionResult> CompleteDeliveryAsync(
        CompleteDeliveryRequest request, CancellationToken ct = default);

    /// <summary>
    /// <b>staged</b> — TX2. Starts the turn. It does NOT transfer ownership; the lease moved to the
    /// agent at the disposition.
    /// </summary>
    Task<StartTurnResult> StartTurnAsync(StartTurnRequest request, CancellationToken ct = default);

    /// <summary>
    /// <b>staged</b> — the general-purpose append entry point for progress and notices.
    /// </summary>
    Task<AppendBatchResult> AppendBatchAsync(
        AppendBatchRequest request, CancellationToken ct = default);

    /// <summary>
    /// <b>staged</b> — TX3 and the late-terminal branch. Commits the terminal; the claim is already
    /// <c>done</c> and is not touched here.
    /// </summary>
    Task<CommitTerminalResult> CommitTerminalAsync(
        CommitTerminalRequest request, CancellationToken ct = default);

    /// <summary>
    /// <b>staged</b> — renews the leases of pending AND running attempts, and records service
    /// liveness for the reconciler's grace period.
    /// </summary>
    Task<HeartbeatResult> HeartbeatAsync(HeartbeatRequest request, CancellationToken ct = default);

    /// <summary>
    /// <b>staged, fire-and-forget</b> — advisory only; nothing branches on it.
    /// </summary>
    Task MarkExternalEffectAsync(string attemptId, CancellationToken ct = default);

    /// <summary>
    /// <b>awaited</b> — catch-up. The floor and the events are read in ONE transaction, so garbage
    /// collection between the two statements cannot produce a silent short read.
    /// </summary>
    Task<ReadConversationResult> ReadAsync(
        ReadConversationRequest request, CancellationToken ct = default);

    /// <summary>
    /// <b>awaited</b> — advance a client instance's durable cursor. Monotonic; never backwards.
    /// </summary>
    Task AckCursorAsync(AckCursorRequest request, CancellationToken ct = default);

    /// <summary>
    /// Streaming tail of the committed log, for the in-process stream reader.
    /// </summary>
    /// <remarks>
    /// Reads the COMMITTED log, never an in-memory bus: nothing is delivered to a client that is not
    /// already durable, because every cursor number in the contract depends on it and a client that
    /// received an event which later vanished has no way to detect the loss.
    /// </remarks>
    IAsyncEnumerable<StoredEvent> TailAsync(
        string conversationId, ulong afterSeq, CancellationToken ct = default);
}
