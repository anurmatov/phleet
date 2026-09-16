namespace Fleet.Protocol;

/// <summary>
/// The principal's role in a conversation. v1 accepts <see cref="Owner"/> only; the runtime
/// rejects everything else with <c>unsupported_role</c> (D3, D9). <see cref="Member"/> and
/// <see cref="Guest"/> exist so a future multi-principal phase is an additive change (D17),
/// not a major bump.
/// </summary>
public enum PrincipalRole
{
    Owner,
    Member,
    Guest,
}

/// <summary>
/// What the runtime did with a submission. Maps 1:1 onto the runtime's dispatch outcome —
/// do not invent a parallel vocabulary (D5).
/// </summary>
public enum SubmissionDisposition
{
    Ran,
    Injected,
    Queued,
    QueueFull,
    Dropped,
}

/// <summary>Kind of in-turn activity reported by <c>turn.progress</c> (D5).</summary>
public enum ProgressActivity
{
    Typing,
    Tool,
}

/// <summary>
/// How a turn ended. Maps 1:1 onto the runtime's completion kind (D5) — do not invent a
/// parallel vocabulary.
/// </summary>
public enum TurnCompletion
{
    Completed,
    Incomplete,
    Idle,
    Failed,
}

/// <summary>
/// Why a turn was cancelled (D13). There is deliberately no <c>shutdown</c> member: turn
/// cancellation runs on the per-task token source, which is not linked to any host token, so
/// a <c>shutdown</c> member would have no producer (Constraint 15). If a future change links
/// turn cancellation to application stop, that change adds the member — append-only evolution.
/// </summary>
public enum TurnCancelReason
{
    /// <summary>Default when no canceller recorded a reason.</summary>
    Unknown,
    /// <summary>A chat <c>/cancel</c>, or a client <c>submission.cancel</c> through the intake.</summary>
    User,
    /// <summary>The operator HTTP cancel endpoints.</summary>
    Operator,
    /// <summary>Cancellation by bridge task id.</summary>
    Bridge,
}

/// <summary>Why the runtime could assert neither success nor failure (D10).</summary>
public enum OutcomeUnknownReason
{
    /// <summary>The turn left its run loop without ever publishing a terminal event.</summary>
    TurnReaped,
    /// <summary>The terminal event exceeded the hard serialized cap even after truncation.</summary>
    TerminalEventOversize,
}

/// <summary>What a <c>control.ack</c> acknowledges. v1 acknowledges cancels only (D5, D8).</summary>
public enum ControlTarget
{
    Cancel,
}

/// <summary>
/// Stable, append-only error codes (D7, D12). Renaming or removing a member is a major bump.
/// The human-readable message for each is a fixed constant (<see cref="ProtocolErrors"/>) —
/// runtime text, provider text and exception messages are never serialized.
/// </summary>
public enum ProtocolErrorCode
{
    Unauthorized,
    UnsupportedProtocol,
    UnsupportedKind,
    UnsupportedRole,
    UnsupportedAttachments,
    PayloadTooLarge,
    ConversationNotFound,
    RateLimited,
    RuntimeBusy,
    ExecutorError,
    Canceled,
    Internal,
}

/// <summary>Coarse attachment classification (D14). Metadata only — no bytes, no URL.</summary>
public enum AttachmentKind
{
    Image,
    Document,
    Audio,
    Video,
    Other,
}
