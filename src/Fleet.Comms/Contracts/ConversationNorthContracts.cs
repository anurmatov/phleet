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
    /// Always refused when non-empty. Attachments are not accepted on this channel in this phase,
    /// and the refusal is explicit rather than a silently ignored field.
    /// </summary>
    public IReadOnlyList<object>? Attachments { get; init; }
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
