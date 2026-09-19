namespace Fleet.Protocol;

/// <summary>
/// The v1 wire values of the envelope's <c>kind</c> field (D5).
///
/// Every kind listed here has a real producer in the runtime. Two kinds that revision 1 of the
/// design carried — <c>conversation.notification</c> and <c>attachment.offered</c> — are
/// deliberately absent: the MCP send path is out-of-process and invisible to the runtime, and
/// there is no outbound attachment producer. Shipping a kind with no producer is forbidden by
/// Constraint 15. They are named in the docs so a later phase can add them additively.
/// </summary>
public static class ConversationEventKind
{
    // --- Runtime → client ---

    /// <summary>Intake refused the inbound event, before any turn exists (D7).</summary>
    public const string ProtocolRejected = "protocol.rejected";

    /// <summary>
    /// The transcript entry for an accepted submission: the text the user actually sent (#305).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A SEPARATE kind from <see cref="SubmissionAccepted"/>, not a field added to it, for two
    /// reasons. <c>submission.accepted</c> is appended at the agent's DISPOSITION and is never
    /// appended at all when the agent never claims the command — so a field on it would lose
    /// exactly the accepted-but-never-answered case, which is the one that matters most. And it is
    /// a kind deployed clients already parse; widening its meaning is a change to something in use,
    /// while a new kind is additive.
    /// </para>
    /// <para>
    /// Appended inside the accept transaction, so it takes the seq the accept already reports as
    /// <c>acceptedSeq</c> and sorts before every event the resulting turn produces. Durable: it is
    /// the transcript, and its absence beneath the retained floor IS a gap.
    /// </para>
    /// </remarks>
    public const string SubmissionText = "submission.text";

    /// <summary>What dispatch did with a submission.</summary>
    public const string SubmissionAccepted = "submission.accepted";

    /// <summary>A turn was registered and began running.</summary>
    public const string TurnStarted = "turn.started";

    /// <summary>In-turn activity: typing heartbeat or a tool invocation.</summary>
    public const string TurnProgress = "turn.progress";

    /// <summary>A user-facing runtime warning emitted mid-turn.</summary>
    public const string TurnNotice = "turn.notice";

    /// <summary>A stale answer from a prior turn, preserved across a drain.</summary>
    public const string TurnRecoveredAnswer = "turn.recovered_answer";

    /// <summary>Terminal: the turn produced its chat-visible answer.</summary>
    public const string TurnFinal = "turn.final";

    /// <summary>Terminal: the turn failed.</summary>
    public const string TurnError = "turn.error";

    /// <summary>Terminal: the turn was cancelled.</summary>
    public const string TurnCanceled = "turn.canceled";

    /// <summary>Terminal: the runtime can assert neither success nor failure (D10).</summary>
    public const string TurnOutcomeUnknown = "turn.outcome_unknown";

    /// <summary>Acknowledgement of a control request. v1: cancel only.</summary>
    public const string ControlAck = "control.ack";

    // --- Client → runtime ---

    /// <summary>Open (or re-resolve) a conversation and bind its principal.</summary>
    public const string ConversationOpen = "conversation.open";

    /// <summary>Submit conversation content.</summary>
    public const string SubmissionCreate = "submission.create";

    /// <summary>
    /// Submit content while a turn may already be running. Not a separate code path —
    /// inject-vs-queue is decided by the runtime's dispatch exactly as it is for Telegram.
    /// </summary>
    public const string SubmissionSteer = "submission.steer";

    /// <summary>Request cancellation of the current turn, or of all of this conversation's turns.</summary>
    public const string SubmissionCancel = "submission.cancel";

    /// <summary>
    /// Request the durable suffix after a cursor (#276 §4.8). Carries <c>afterSeq</c> and an
    /// optional <c>limit</c>.
    /// </summary>
    public const string ConversationCatchup = "conversation.catchup";

    /// <summary>
    /// Advance this client instance's durable cursor (#276 §4.10). Carries <c>deliveredSeq</c> and
    /// an optional <c>readSeq</c>. Cursors are monotonic and never move backwards.
    /// </summary>
    public const string ConversationAck = "conversation.ack";

    /// <summary>
    /// Announces durable history the client will never receive because garbage collection passed
    /// its cursor (#276 §4.8).
    ///
    /// <para>SYNTHETIC AND NEVER STORED, and it carries <c>seq: null</c> rather than a number. It is
    /// computed per request from the reader's cursor and the conversation's retained floor, so
    /// sequencing it would give it the NEWEST seq and sort it after the very suffix it announces —
    /// and would write per-client-instance state into a log every other instance reads.</para>
    ///
    /// <para><c>null</c>, not <c>0</c>: seq 0 never reaches a client, and the distinction is
    /// asserted rather than assumed.</para>
    /// </summary>
    public const string ConversationReplayGap = "conversation.replay_gap";

    /// <summary>
    /// Terminal kinds. These route through the per-conversation terminal outbox rather than the
    /// shared progress channel, so a chatty progress stream can never evict one (D11).
    /// </summary>
    public static readonly IReadOnlySet<string> Terminal = new HashSet<string>(StringComparer.Ordinal)
    {
        TurnFinal,
        TurnError,
        TurnCanceled,
        TurnOutcomeUnknown,
    };

    /// <summary>Every kind the runtime may emit to a client in v1.</summary>
    public static readonly IReadOnlySet<string> Outbound = new HashSet<string>(StringComparer.Ordinal)
    {
        ProtocolRejected,
        SubmissionText,
        SubmissionAccepted,
        TurnStarted,
        TurnProgress,
        TurnNotice,
        TurnRecoveredAnswer,
        TurnFinal,
        TurnError,
        TurnCanceled,
        TurnOutcomeUnknown,
        ControlAck,
        ConversationReplayGap,
    };

    /// <summary>Every kind a client may send in v1. This is the complete Phase-0 command set.</summary>
    public static readonly IReadOnlySet<string> Inbound = new HashSet<string>(StringComparer.Ordinal)
    {
        ConversationOpen,
        SubmissionCreate,
        SubmissionSteer,
        SubmissionCancel,
        ConversationCatchup,
        ConversationAck,
    };

    /// <summary>True when the runtime recognises the inbound kind (D17).</summary>
    public static bool IsKnownInbound(string? kind) => kind is not null && Inbound.Contains(kind);
}
