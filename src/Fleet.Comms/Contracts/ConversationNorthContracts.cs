using Fleet.Protocol;

namespace Fleet.Comms.Contracts;

// The exact wire shapes of docs/first-party-api.md §5.1 for the six conversation routes. Every
// body carries `protocol`, and every one is serialized through FleetProtocolJson.Options — the
// same options the auth bodies use, so the wire form is the protocol's rather than this project's.
//
// Request records use nullable properties and no `required`: a missing field is a client error this
// boundary answers with a fixed body, never a deserialization exception that would reach the client
// as `500 internal`.

/// <summary>`POST /v1/conversations` request.</summary>
public sealed record OpenConversationBody
{
    public string? Protocol { get; init; }

    /// <summary>The client's own stable name for the thread. Resolution is by this, per §5.</summary>
    public string? ExternalRef { get; init; }

    /// <summary>
    /// Opaque bookkeeping label for cursors (§4). <b>Not a credential and not device identity</b>,
    /// so it is never used for authorisation — the principal comes from the validated token.
    /// </summary>
    public string? ClientInstanceId { get; init; }
}

/// <summary>`POST /v1/conversations` response.</summary>
public sealed record OpenConversationResponse
{
    public string Protocol { get; init; } = ProtocolVersion.Current;
    public required string ConversationId { get; init; }
    public required ulong NextSeq { get; init; }
    public required ulong RetainedFloorSeq { get; init; }
}

/// <summary>`GET /v1/conversations/{id}/events` response.</summary>
public sealed record CatchUpResponse
{
    public string Protocol { get; init; } = ProtocolVersion.Current;

    /// <summary>
    /// Owed durable history, when garbage collection has passed the reader's cursor. Present only
    /// then, and <b>first</b> — it is a field rather than an element of <see cref="Events"/>,
    /// because the gap payload carries `seq: null` and is never stored (#276 D-16).
    /// </summary>
    public ConversationReplayGapPayload? Gap { get; init; }

    public required IReadOnlyList<ConversationEvent> Events { get; init; }
    public required ulong NextAfterSeq { get; init; }
    public required bool HasMore { get; init; }
}

/// <summary>`POST /v1/conversations/{id}/submissions` request. One route, two types.</summary>
public sealed record SubmitBody
{
    public string? Protocol { get; init; }

    /// <summary>`create` or `steer`. There is no second route and no second code path.</summary>
    public string? Type { get; init; }

    public string? SubmissionId { get; init; }
    public string? IdempotencyKey { get; init; }
    public string? Text { get; init; }
    public string? ReplyToEventId { get; init; }

    /// <summary>
    /// Attachments this submission references (#308 D2). Each names a <c>sealed</c>, still-unbound
    /// attachment of this same conversation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Refused as a whole when the feature is not configured — explicitly, never as a silently
    /// ignored field, because a client that attached a photo and got a text-only answer has been
    /// lied to.
    /// </para>
    /// <para>
    /// An explicit <c>[]</c> is treated as absent rather than as an error: it says the same thing
    /// omitting the key says, and refusing it would make a perfectly clear request an error.
    /// </para>
    /// </remarks>
    public IReadOnlyList<SubmitAttachmentRef>? Attachments { get; init; }
}

/// <summary>
/// One attachment reference on a submission. Only the id is load-bearing.
/// </summary>
/// <remarks>
/// <c>kind</c> is accepted because the protocol descriptor carries it, and is deliberately NOT
/// trusted: the stored row's kind is what the transcript entry reports. A client cannot relabel an
/// attachment by re-declaring it at submit time.
/// </remarks>
public sealed record SubmitAttachmentRef
{
    public string? AttachmentId { get; init; }
    public string? Kind { get; init; }
}

/// <summary>`POST /v1/conversations/{id}/attachments` request — reserve, before any byte moves.</summary>
/// <remarks>
/// Every field here is a CLAIM about bytes that do not exist yet. <see cref="ByteSize"/> and
/// <see cref="Sha256"/> are recorded so the seal can check what actually arrives against them;
/// <see cref="ContentType"/> is checked against the accepted set here and then decided again, from
/// the bytes themselves, at seal.
/// </remarks>
public sealed record ReserveAttachmentBody
{
    public string? Protocol { get; init; }

    /// <summary>`image` in v1. The four accepted container types are all images.</summary>
    public string? Kind { get; init; }

    public string? ContentType { get; init; }
    public long? ByteSize { get; init; }

    /// <summary>64 lowercase hex characters. The digest the uploaded bytes must produce.</summary>
    public string? Sha256 { get; init; }

    /// <summary>A label. Stored and echoed; never used to build a path.</summary>
    public string? FileName { get; init; }
}

/// <summary>`201` — the slot is reserved and the capability is issued.</summary>
/// <remarks>
/// ⚠️ <see cref="UploadToken"/> is the <b>only</b> time the capability's plaintext exists anywhere.
/// The store holds its SHA-256 and nothing else, so it cannot be recovered, re-issued or logged
/// (#308 D6, AC-20).
/// </remarks>
public sealed record ReserveAttachmentResponse
{
    public string Protocol { get; init; } = ProtocolVersion.Current;
    public required string AttachmentId { get; init; }
    public required string UploadUrl { get; init; }
    public required string UploadToken { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required long MaxBytes { get; init; }
}

/// <summary>`201` — the bytes were verified and the attachment is now referenceable.</summary>
/// <remarks>
/// <see cref="ContentType"/> is the SNIFFED type, which may differ from the declared one only by
/// being the truth — a mismatch fails the seal rather than being reported here.
/// </remarks>
public sealed record SealAttachmentResponse
{
    public string Protocol { get; init; } = ProtocolVersion.Current;
    public required string AttachmentId { get; init; }
    public string State { get; init; } = "sealed";
    public required string ContentType { get; init; }
    public required long ByteSize { get; init; }
}

/// <summary>`201` — the submission is durable and dispatched.</summary>
public sealed record SubmitAcceptedResponse
{
    public string Protocol { get; init; } = ProtocolVersion.Current;
    public required string SubmissionId { get; init; }

    /// <summary>
    /// The catch-up floor: the conversation's `next_seq` as read inside the accept transaction —
    /// the first seq any event about this submission can occupy.
    ///
    /// <para>⚠️ NOT #276 §6's internal <c>accepted_seq</c> column, which is the seq of the
    /// <c>submission.accepted</c> event and is NULL until the disposition. A client catching up on
    /// the whole lifecycle asks for <c>afterSeq = acceptedSeq - 1</c>.</para>
    /// </summary>
    public required ulong AcceptedSeq { get; init; }
}

/// <summary>
/// `202` — a same-key retry arriving while the disposition transaction is still outstanding.
/// </summary>
/// <remarks>
/// It carries no <c>acceptedSeq</c>, because it is not yet known. <b>A first accept is never
/// 202</b>; this is only ever a retry.
/// </remarks>
public sealed record SubmitPendingResponse
{
    public string Protocol { get; init; } = ProtocolVersion.Current;
    public required string SubmissionId { get; init; }
}

/// <summary>`POST /v1/conversations/{id}:cancel` request.</summary>
public sealed record CancelBody
{
    public string? Protocol { get; init; }

    /// <summary>`current` or `all`.</summary>
    public string? Scope { get; init; }
}

/// <summary>
/// `POST /v1/conversations/{id}:cancel` response — the request was accepted, and nothing more.
/// </summary>
/// <remarks>
/// ⚠️ It deliberately does NOT carry <c>hadRunningTask</c>. Only the agent runtime knows whether a
/// turn was running, and with cancel dispatched asynchronously through the command outbox the north
/// route cannot know it at response time. Inventing a value here would contradict the
/// <c>control.ack</c> event that follows over the stream and catch-up.
/// </remarks>
public sealed record CancelResponse
{
    public string Protocol { get; init; } = ProtocolVersion.Current;
    public bool Accepted { get; init; } = true;
}

/// <summary>`POST /v1/conversations/{id}/cursor` request. Monotonic; never moves backwards.</summary>
public sealed record CursorBody
{
    public string? Protocol { get; init; }
    public string? ClientInstanceId { get; init; }
    public ulong? DeliveredSeq { get; init; }
    public ulong? ReadSeq { get; init; }
}

/// <summary>The `hello` frame a stream sends before any event (§9).</summary>
public sealed record StreamHello
{
    public string Protocol { get; init; } = ProtocolVersion.Current;
    public string Kind { get; init; } = "hello";
    public required string ConversationId { get; init; }
    public required ulong NextSeq { get; init; }
    public required ulong RetainedFloorSeq { get; init; }
    public required SessionLimits Limits { get; init; }
}
