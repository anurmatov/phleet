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

/// <summary>
/// The transcript entry for an accepted submission: the text the user sent (#305).
/// </summary>
/// <remarks>
/// <para>
/// One field, deliberately. Whether the submission was a <c>create</c> or a <c>steer</c> is already
/// on the wire as <c>submission.accepted { disposition }</c> — <c>injected</c> is precisely a steer
/// folded into a running turn — and a second encoding of the same fact is a second thing to keep in
/// step.
/// </para>
/// <para>
/// There is no <c>truncated</c> flag because this text is never truncated. The inbound cap
/// (<see cref="ProtocolLimits.MaxInboundTextBytes"/>) is enforced BEFORE the submission becomes
/// durable, so an over-cap submission has no transcript entry rather than a shortened one.
/// </para>
/// </remarks>
public sealed record SubmissionTextPayload
{
    public required string Text { get; init; }
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

    /// <summary>
    /// Opaque, owner-scoped bookkeeping label identifying this client installation, so a durable
    /// cursor can be attributed to it (#276 §4.10).
    ///
    /// <para>NOT a credential, NOT device identity, and it confers no authorization. Two
    /// installations of the same owner have different values and independent cursors; presenting
    /// someone else's buys nothing, because the principal is established by the bearer token.</para>
    /// </summary>
    public string? ClientInstanceId { get; init; }
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

    /// <summary>
    /// Client-chosen key making a retry of this submission safe (#276 §7). At most 128 characters.
    ///
    /// <para>The key is bound to a fingerprint of the payload: the same key with the same payload
    /// replays the original result, and the same key with a DIFFERENT payload is
    /// <see cref="ProtocolErrorCode.IdempotencyConflict"/> rather than a silent second
    /// submission.</para>
    ///
    /// <para>Its honest upper bound is the garbage-collection horizon — once the submission row is
    /// collected the key means nothing, and client-facing copy says so rather than implying
    /// forever.</para>
    /// </summary>
    public string? IdempotencyKey { get; init; }
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

/// <summary>
/// Request the durable suffix after a cursor (#276 §4.8).
/// </summary>
public sealed record ConversationCatchupPayload
{
    /// <summary>"I have processed up to and including this seq." The reply starts at the next one.</summary>
    public required ulong AfterSeq { get; init; }

    /// <summary>
    /// Page size. Defaults to 200 and is REJECTED above 1000 rather than clamped: a client that
    /// asked for 5000 and silently received 1000 would believe it held the whole suffix.
    /// </summary>
    public int? Limit { get; init; }
}

/// <summary>
/// Advance this client instance's durable cursor (#276 §4.10). Monotonic: both values move forward
/// or not at all, and a lower value is not an error, it is simply not a move.
/// </summary>
public sealed record ConversationAckPayload
{
    public required ulong DeliveredSeq { get; init; }
    public ulong? ReadSeq { get; init; }
}

/// <summary>
/// Durable history the reader will never receive, because garbage collection passed its cursor
/// (#276 §4.8).
///
/// <para>Synthetic: it is computed per request and NEVER stored, and its envelope carries
/// <c>seq: null</c>. Sequencing it would give it the newest seq and sort it after the suffix it
/// announces.</para>
///
/// <para>A pruned EPHEMERAL event is not a gap. Only durable events beneath the floor are — which
/// is why the floor is computed from the durable tier alone.</para>
/// </summary>
public sealed record ConversationReplayGapPayload
{
    /// <summary>First seq that is missing — the reader's cursor plus one.</summary>
    public required ulong FromSeq { get; init; }

    /// <summary>Last seq that is missing — the retained floor minus one.</summary>
    public required ulong ToSeq { get; init; }

    /// <summary>The lowest surviving durable seq; the suffix that follows starts here.</summary>
    public required ulong RetainedFloorSeq { get; init; }
}
