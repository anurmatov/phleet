namespace Fleet.Protocol;

// D12: enforcement of the privacy allowlist is STRUCTURAL. Every payload below is a sealed
// record carrying exactly the fields D5 enumerates, so a denied field — raw reasoning, tool
// arguments or results, local filesystem paths, provider session ids, credentials, capability
// URLs, correlation ids, workflow ids, relay sender names, the execution stats block — cannot
// be attached without editing this file. ProtocolAllowlistTests asserts the serialized property
// set of every kind is a subset of the allowlist.

/// <summary>
/// The intake refused an inbound event, before any turn exists (D7). Only <c>channelId</c> is
/// required in the identity; conversation, submission and turn ids are all optional.
/// <see cref="Message"/> comes from the fixed table in <see cref="ProtocolErrors"/> — never from
/// runtime text, provider text or an exception message (Constraint 4).
/// </summary>
public sealed record ProtocolRejectedPayload
{
    public required ProtocolErrorCode Code { get; init; }
    public required string Message { get; init; }
}

/// <summary>What dispatch did with the submission (D5).</summary>
public sealed record SubmissionAcceptedPayload
{
    public required SubmissionDisposition Disposition { get; init; }
    public int? QueuePosition { get; init; }
}

/// <summary>A turn was registered and began running. No fields.</summary>
public sealed record TurnStartedPayload;

/// <summary>
/// In-turn activity. Only <see cref="ToolName"/> leaves the runtime — the Telegram path emits
/// truncated tool ARGUMENTS alongside the tool name, and that string must never be reused here
/// (Constraint 4).
/// </summary>
public sealed record TurnProgressPayload
{
    public required ProgressActivity Activity { get; init; }
    public string? ToolName { get; init; }
}

/// <summary>A user-facing runtime warning emitted mid-turn.</summary>
public sealed record TurnNoticePayload
{
    public required string Text { get; init; }
    public bool? Truncated { get; init; }
}

/// <summary>A stale answer from a prior turn, preserved across a drain.</summary>
public sealed record TurnRecoveredAnswerPayload
{
    public required string Text { get; init; }
    public bool? Truncated { get; init; }
}

/// <summary>
/// Terminal. <see cref="Text"/> is the CHAT-VISIBLE text, matching what the main reply path
/// delivers. The concatenated all-texts form the workflow callback receives is deliberately a
/// different string and stays exclusive to that callback — assuming one canonical final string
/// silently changes one of the two consumers.
///
/// <see cref="MergedSubmissionIds"/> lists every submission this turn answered, so coalesced and
/// mid-turn-injected submissions terminate on the client instead of hanging forever.
/// </summary>
public sealed record TurnFinalPayload
{
    public required string Text { get; init; }
    public required TurnCompletion Completion { get; init; }
    public required bool IsPartial { get; init; }
    public required bool Truncated { get; init; }
    public required IReadOnlyList<string> MergedSubmissionIds { get; init; }
}

/// <summary>
/// Terminal. <see cref="Message"/> comes from the fixed table in <see cref="ProtocolErrors"/>.
/// The runtime's own error text is logged server-side and never serialized (Constraint 4).
/// </summary>
public sealed record TurnErrorPayload
{
    public required ProtocolErrorCode Code { get; init; }
    public required string Message { get; init; }
}

/// <summary>Terminal.</summary>
public sealed record TurnCanceledPayload
{
    public required TurnCancelReason Reason { get; init; }
    public required IReadOnlyList<string> MergedSubmissionIds { get; init; }
}

/// <summary>
/// Terminal. The runtime could assert neither success nor failure (D10). Clients render this as
/// indeterminate, never as success.
/// </summary>
public sealed record TurnOutcomeUnknownPayload
{
    public required OutcomeUnknownReason Reason { get; init; }
}

/// <summary>
/// Acknowledges a control request. <see cref="Accepted"/> means the REQUEST was accepted, not
/// that the turn stopped — the authoritative outcome is the subsequent <c>turn.canceled</c>.
/// When <see cref="HadRunningTask"/> is false no <c>turn.canceled</c> follows and the ack is the
/// whole story (D8).
/// </summary>
public sealed record ControlAckPayload
{
    public required ControlTarget Target { get; init; }
    public required bool Accepted { get; init; }
    public required bool HadRunningTask { get; init; }
}

// --- Client → runtime ---

/// <summary>Open (or re-resolve) a conversation and bind its principal (D9).</summary>
public sealed record ConversationOpenPayload
{
    public required string ChannelId { get; init; }
    public required PrincipalBinding PrincipalBinding { get; init; }
    public PrincipalRole? Role { get; init; }
    public string? ExternalConversationRef { get; init; }
}

/// <summary>
/// Submit conversation content. <see cref="Attachments"/> exists so clients can be written
/// against the shape, but any non-empty array is rejected outright in Phase 0 — the submission
/// must NOT silently proceed as text-only (D14, Constraint 9).
/// </summary>
public sealed record SubmissionCreatePayload
{
    public required string Text { get; init; }
    public string? ReplyToEventId { get; init; }
    public IReadOnlyList<AttachmentDescriptor>? Attachments { get; init; }
}

/// <summary>
/// Submit content while a turn may already be running. Not a separate code path — inject-vs-queue
/// is decided by the runtime's dispatch exactly as for Telegram (D5).
/// </summary>
public sealed record SubmissionSteerPayload
{
    public required string Text { get; init; }
    public IReadOnlyList<AttachmentDescriptor>? Attachments { get; init; }
}

/// <summary>Request cancellation.</summary>
public sealed record SubmissionCancelPayload
{
    public required CancelScope Scope { get; init; }
}

/// <summary>Which turns a cancel targets.</summary>
public enum CancelScope
{
    Current,
    All,
}

/// <summary>
/// Attachment metadata (D14). No bytes, no URL, no storage reference. <see cref="AttachmentId"/>
/// is opaque and reserved for a later phase that actually has object storage.
/// </summary>
public sealed record AttachmentDescriptor
{
    public required string AttachmentId { get; init; }
    public required AttachmentKind Kind { get; init; }
    public string? ContentType { get; init; }
    public long? ByteSize { get; init; }
    public string? FileName { get; init; }
}
