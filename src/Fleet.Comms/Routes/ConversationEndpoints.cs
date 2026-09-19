using System.Text.Json;
using System.Text.Json.Nodes;
using Fleet.Comms.Auth;
using Fleet.Comms.Configuration;
using Fleet.Comms.Contracts;
using Fleet.Conversations;
using Fleet.Conversations.Contracts;
using Fleet.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Fleet.Comms.Routes;

/// <summary>
/// The five conversation routes of docs/first-party-api.md §5, plus the stream, mapped on the
/// NORTH listener only when the conversation feature is configured.
/// </summary>
/// <remarks>
/// <para>
/// These are client-reachable, so everything they accept is treated as hostile: the principal is
/// always the one on the validated token and never a field in the body, a conversation belonging to
/// someone else is reported byte-identically to one that does not exist, and neither the cursor nor
/// the page limit is ever clamped.
/// </para>
/// <para>
/// <b>Not clamping is the load-bearing part.</b> A client that asked for 5000 events and silently
/// received 1000 would conclude it had the whole suffix; a cursor ahead of the server means client
/// corruption or a restored backup, and clamping it hands back partial history the client believes
/// is complete. Both are refused instead, which is the only version a client can detect.
/// </para>
/// </remarks>
public static class ConversationEndpoints
{
    /// <summary>The six routes this slice adds. The north table is these plus the four auth ones.</summary>
    public static readonly IReadOnlyList<string> Routes =
    [
        "POST /v1/conversations",
        "GET /v1/conversations/{id}/events",
        "POST /v1/conversations/{id}/submissions",
        "POST /v1/conversations/{id}:cancel",
        "POST /v1/conversations/{id}/cursor",
        "GET /v1/conversations/{id}/stream",
    ];

    /// <summary>The channel every client conversation belongs to. Fixed; there is no fan-out.</summary>
    public const string ClientChannelId = "client";

    public static IEndpointRouteBuilder MapConversationApi(this IEndpointRouteBuilder app)
    {
        // The conversation limiter, not the auth one. Authenticated routes partition on the caller's
        // credential rather than its address, so two devices behind one forwarded address cannot
        // exhaust each other's budget (§12).
        app.MapPost("/v1/conversations", OpenAsync)
            .RequireRateLimiting(ConversationRateLimits.PolicyName);

        app.MapGet("/v1/conversations/{id}/events", CatchUpAsync)
            .RequireRateLimiting(ConversationRateLimits.PolicyName);

        app.MapPost("/v1/conversations/{id}/submissions", SubmitAsync)
            .RequireRateLimiting(ConversationRateLimits.PolicyName);

        // `:cancel` is a literal suffix on the id segment, matching §5 and the existing
        // `:revoke` route.
        app.MapPost("/v1/conversations/{id}:cancel", CancelAsync)
            .RequireRateLimiting(ConversationRateLimits.PolicyName);

        app.MapPost("/v1/conversations/{id}/cursor", CursorAsync)
            .RequireRateLimiting(ConversationRateLimits.PolicyName);

        app.MapGet("/v1/conversations/{id}/stream", StreamEndpoint.HandleAsync)
            .RequireRateLimiting(ConversationRateLimits.PolicyName);

        return app;
    }

    // ── open ─────────────────────────────────────────────────────────────────

    private static async Task<IResult> OpenAsync(
        HttpContext http, AuthService auth, IConversationStore store, CancellationToken ct)
    {
        var caller = await NorthEndpoints.AuthenticateAsync(http, auth, ct);
        if (caller is null) return Unauthorized();

        var body = await NorthEndpoints.ReadBodyAsync<OpenConversationBody>(http, ct);
        if (body is null) return Error(ProtocolErrorCode.UnsupportedKind, StatusCodes.Status400BadRequest);

        if (!ProtocolVersion.IsSupported(body.Protocol))
            return Error(ProtocolErrorCode.UnsupportedProtocol, StatusCodes.Status400BadRequest);

        if (!IsValidIdentifier(body.ExternalRef))
            return Error(ProtocolErrorCode.UnsupportedKind, StatusCodes.Status400BadRequest);

        if (body.ClientInstanceId is not null && !IsValidIdentifier(body.ClientInstanceId))
            return Error(ProtocolErrorCode.UnsupportedKind, StatusCodes.Status400BadRequest);

        return await GuardAsync(async () =>
        {
            var result = await store.OpenConversationAsync(new OpenConversationRequest
            {
                ChannelId = ClientChannelId,
                ExternalRef = body.ExternalRef!,

                // Derived from the device record. Never read from the request — a caller-supplied
                // principal on an authenticated route is an authorization bypass wearing a field name.
                PrincipalId = caller.PrincipalId,
                ClientInstanceId = body.ClientInstanceId,
            }, ct);

            return Json(new OpenConversationResponse
            {
                ConversationId = result.ConversationId,
                NextSeq = result.NextSeq,
                RetainedFloorSeq = result.RetainedFloorSeq,
            }, StatusCodes.Status200OK);
        });
    }

    // ── catch-up ─────────────────────────────────────────────────────────────

    private static async Task<IResult> CatchUpAsync(
        HttpContext http, string id, AuthService auth, IConversationStore store, CancellationToken ct)
    {
        var caller = await NorthEndpoints.AuthenticateAsync(http, auth, ct);
        if (caller is null) return Unauthorized();

        // Parsed from the raw string rather than bound, so a negative or non-integral value is a
        // client error here instead of a model-binding 400 with an empty body — and, more to the
        // point, so it is rejected BEFORE anything compares it against an unsigned column.
        if (!TryParseCursor(http.Request.Query["afterSeq"], out var afterSeq))
            return Error(ProtocolErrorCode.InvalidCursor, StatusCodes.Status400BadRequest);

        if (!TryParseLimit(http.Request.Query["limit"], out var limit))
            return Error(ProtocolErrorCode.UnsupportedKind, StatusCodes.Status400BadRequest);

        return await GuardAsync(async () =>
        {
            var result = await store.ReadAsync(new ReadConversationRequest
            {
                ConversationId = id,
                AfterSeq = afterSeq,
                Limit = limit,
                PrincipalId = caller.PrincipalId,
            }, ct);

            return Json(new CatchUpResponse
            {
                Gap = result.Gap,
                Events = result.Events.Select(e => ToEnvelope(e, caller.PrincipalId, id)).ToList(),
                NextAfterSeq = result.NextAfterSeq,
                HasMore = result.HasMore,
            }, StatusCodes.Status200OK);
        });
    }

    /// <summary>
    /// Rebuild the protocol envelope from a stored row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Shared with the stream, and that sharing is the point: an event delivered over the socket has
    /// to be byte-identical to the same event returned by catch-up, and two construction sites is
    /// how that stops being true.
    /// </para>
    /// <para>
    /// <c>TurnId</c> is absent because <c>conversation_events</c> does not carry one — events are
    /// keyed to an attempt, not a turn. <c>Attempt</c> is 1 because this slice never produces a
    /// second attempt for one submission: nothing is ever automatically re-run.
    /// </para>
    /// </remarks>
    internal static ConversationEvent ToEnvelope(
        StoredEvent stored, string principalId, string conversationId) =>
        new()
        {
            Protocol = ProtocolVersion.Current,
            EventId = stored.EventId,
            Seq = (long)stored.Seq,
            EmittedAt = stored.EmittedAt,
            Kind = stored.Kind,
            Identity = new ConversationIdentity
            {
                PrincipalId = principalId,
                Role = PrincipalRole.Owner,
                ChannelId = ClientChannelId,
                ConversationId = conversationId,
                SubmissionId = stored.SubmissionId ?? string.Empty,
                Attempt = 1,
            },
            Payload = stored.PayloadJson is null ? null : JsonNode.Parse(stored.PayloadJson),
        };

    // ── submit ───────────────────────────────────────────────────────────────

    private static async Task<IResult> SubmitAsync(
        HttpContext http, string id, AuthService auth, IConversationStore store,
        CancellationToken ct)
    {
        var caller = await NorthEndpoints.AuthenticateAsync(http, auth, ct);
        if (caller is null) return Unauthorized();

        var body = await NorthEndpoints.ReadBodyAsync<SubmitBody>(http, ct);
        if (body is null) return Error(ProtocolErrorCode.UnsupportedKind, StatusCodes.Status400BadRequest);

        if (!ProtocolVersion.IsSupported(body.Protocol))
            return Error(ProtocolErrorCode.UnsupportedProtocol, StatusCodes.Status400BadRequest);

        // Refused explicitly rather than ignored. A silently dropped attachment array is a client
        // that believes it sent something the agent will never see.
        if (body.Attachments is { Count: > 0 })
            return Error(ProtocolErrorCode.UnsupportedAttachments, StatusCodes.Status400BadRequest);

        var kind = body.Type switch
        {
            "create" => ConversationEventKind.SubmissionCreate,
            "steer" => ConversationEventKind.SubmissionSteer,
            _ => null,
        };

        if (kind is null || !IsValidIdentifier(body.SubmissionId) || body.Text is null)
            return Error(ProtocolErrorCode.UnsupportedKind, StatusCodes.Status400BadRequest);

        if (body.IdempotencyKey is not null && !IsValidIdentifier(body.IdempotencyKey))
            return Error(ProtocolErrorCode.UnsupportedKind, StatusCodes.Status400BadRequest);

        // Measured in BYTES, not characters: the bound is a wire bound, and a character count would
        // admit roughly four times the payload for text outside the Basic Latin block.
        if (System.Text.Encoding.UTF8.GetByteCount(body.Text) > ProtocolLimits.MaxInboundTextBytes)
            return Error(ProtocolErrorCode.PayloadTooLarge, StatusCodes.Status413PayloadTooLarge);

        return await GuardAsync(async () =>
        {
            var result = await store.AcceptSubmissionAsync(new AcceptSubmissionRequest
            {
                ConversationId = id,
                ExternalSubmissionId = body.SubmissionId!,
                PayloadFingerprint = PayloadFingerprint.Compute(body.Text, body.ReplyToEventId, id),
                IdempotencyKey = body.IdempotencyKey,
                CommandKind = kind,
                CommandPayloadJson = CommandPayload(
                    kind, caller, id, body.SubmissionId!, body.IdempotencyKey, body.Text,
                    body.ReplyToEventId),

                // #305. Every refusal above returns before this point, so a submission that is not
                // durable has no transcript entry — an over-cap 413, an idempotency 409 and a
                // malformed 400 all leave the conversation exactly as they found it.
                TranscriptText = body.Text,
            }, ct);

            return result.Outcome switch
            {
                AcceptOutcome.Conflict => Error(
                    ProtocolErrorCode.IdempotencyConflict, StatusCodes.Status409Conflict),

                AcceptOutcome.ReplayPending => Json(new SubmitPendingResponse
                {
                    SubmissionId = result.ExternalSubmissionId ?? body.SubmissionId!,
                }, StatusCodes.Status202Accepted),

                // Accepted and Replay answer identically, which is what makes a replay
                // byte-identical to the response it replays.
                _ => Json(new SubmitAcceptedResponse
                {
                    SubmissionId = result.ExternalSubmissionId ?? body.SubmissionId!,
                    AcceptedSeq = result.AcceptedSeq!.Value,
                }, StatusCodes.Status201Created),
            };
        });
    }

    // ── cancel ───────────────────────────────────────────────────────────────

    private static async Task<IResult> CancelAsync(
        HttpContext http, string id, AuthService auth, IConversationStore store, CancellationToken ct)
    {
        var caller = await NorthEndpoints.AuthenticateAsync(http, auth, ct);
        if (caller is null) return Unauthorized();

        var body = await NorthEndpoints.ReadBodyAsync<CancelBody>(http, ct);
        if (body is null) return Error(ProtocolErrorCode.UnsupportedKind, StatusCodes.Status400BadRequest);

        if (!ProtocolVersion.IsSupported(body.Protocol))
            return Error(ProtocolErrorCode.UnsupportedProtocol, StatusCodes.Status400BadRequest);

        var scope = body.Scope is "current" or "all" ? body.Scope : null;
        if (scope is null)
            return Error(ProtocolErrorCode.UnsupportedKind, StatusCodes.Status400BadRequest);

        return await GuardAsync(async () =>
        {
            // A cancel is a submission like any other: it goes through TX1a so the command reaches
            // the agent through the same outbox, in the same transaction, with the same durability.
            // Its own identifier is derived rather than client-supplied, because a client does not
            // name a cancel.
            //
            // It carries NO TranscriptText (#305): a cancel is a control action, not something the
            // user said, and an entry for it would put a button press into the transcript.
            var submissionId = Ulid.NewUlid();

            await store.AcceptSubmissionAsync(new AcceptSubmissionRequest
            {
                ConversationId = id,
                ExternalSubmissionId = submissionId,
                PayloadFingerprint = PayloadFingerprint.Compute(scope, null, id),
                CommandKind = ConversationEventKind.SubmissionCancel,
                CommandPayloadJson = CancelPayload(caller, id, submissionId, scope),
            }, ct);

            // Accepted, and nothing more. `hadRunningTask` is knowable only by the agent runtime and
            // arrives later as a `control.ack` event over the stream and catch-up.
            return Json(new CancelResponse(), StatusCodes.Status200OK);
        });
    }

    // ── cursor ───────────────────────────────────────────────────────────────

    private static async Task<IResult> CursorAsync(
        HttpContext http, string id, AuthService auth, IConversationStore store, CancellationToken ct)
    {
        var caller = await NorthEndpoints.AuthenticateAsync(http, auth, ct);
        if (caller is null) return Unauthorized();

        var body = await NorthEndpoints.ReadBodyAsync<CursorBody>(http, ct);
        if (body is null) return Error(ProtocolErrorCode.UnsupportedKind, StatusCodes.Status400BadRequest);

        if (!ProtocolVersion.IsSupported(body.Protocol))
            return Error(ProtocolErrorCode.UnsupportedProtocol, StatusCodes.Status400BadRequest);

        if (!IsValidIdentifier(body.ClientInstanceId) || body.DeliveredSeq is null)
            return Error(ProtocolErrorCode.InvalidCursor, StatusCodes.Status400BadRequest);

        // A read cursor ahead of the delivered one is incoherent — a client cannot have read an
        // event it was never handed — so it is refused rather than reconciled.
        if (body.ReadSeq is { } read && read > body.DeliveredSeq)
            return Error(ProtocolErrorCode.InvalidCursor, StatusCodes.Status400BadRequest);

        return await GuardAsync(async () =>
        {
            // The principal is checked before the write, so a cursor cannot be advanced on someone
            // else's conversation — and the refusal is the same not-found every other cross-principal
            // read produces.
            await store.ReadAsync(new ReadConversationRequest
            {
                ConversationId = id,
                AfterSeq = 0,
                Limit = 1,
                PrincipalId = caller.PrincipalId,
            }, ct);

            await store.AckCursorAsync(new AckCursorRequest
            {
                ClientInstanceId = body.ClientInstanceId!,
                ConversationId = id,
                DeliveredSeq = body.DeliveredSeq.Value,
                ReadSeq = body.ReadSeq,
            }, ct);

            return Results.StatusCode(StatusCodes.Status204NoContent);
        });
    }

    // ── shared ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Translate the store's refusals into the documented statuses, and everything else into `503`.
    /// </summary>
    /// <remarks>
    /// <para>
    /// `503`, not `500`. §5.2 reserves `500` for a fault inside this boundary; a database that is
    /// unreachable, out of disk or mid-failover is a dependency failure, and telling a client "the
    /// server is broken, report this" over a transient outage is the wrong instruction.
    /// </para>
    /// <para>
    /// The body is the fixed constant either way, so no exception text ever reaches a client.
    /// </para>
    /// </remarks>
    internal static async Task<IResult> GuardAsync(Func<Task<IResult>> handler)
    {
        try
        {
            return await handler();
        }
        catch (ConversationNotFoundException)
        {
            return Error(ProtocolErrorCode.ConversationNotFound, StatusCodes.Status404NotFound);
        }
        catch (InvalidCursorException)
        {
            return Error(ProtocolErrorCode.InvalidCursor, StatusCodes.Status400BadRequest);
        }
        catch (Exception)
        {
            return Error(ProtocolErrorCode.Internal, StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>
    /// The command envelope the agent consumes, built server-side.
    /// </summary>
    /// <remarks>
    /// It carries the server-derived <c>principalId</c> and no binding token: the server has already
    /// authenticated the caller, so putting a shared secret into a durable broker queue would add a
    /// credential to a new surface and buy nothing. It carries no <c>attemptId</c> either —
    /// publishing one before anyone owns it hands a redelivery an identifier another process is
    /// mid-way through using. The claim returns it, which is where ownership actually transfers.
    /// </remarks>
    private static string CommandPayload(
        string kind, AuthenticatedPrincipal caller, string conversationId, string submissionId,
        string? idempotencyKey, string text, string? replyToEventId) =>
        JsonSerializer.Serialize(new
        {
            kind,
            principalId = caller.PrincipalId,
            channelId = ClientChannelId,
            conversationId,
            submissionId,
            idempotencyKey,
            payload = new { text, replyToEventId },
        }, FleetProtocolJson.Options);

    private static string CancelPayload(
        AuthenticatedPrincipal caller, string conversationId, string submissionId, string scope) =>
        JsonSerializer.Serialize(new
        {
            kind = ConversationEventKind.SubmissionCancel,
            principalId = caller.PrincipalId,
            channelId = ClientChannelId,
            conversationId,
            submissionId,
            payload = new { scope },
        }, FleetProtocolJson.Options);

    /// <summary>
    /// A cursor that is absent, negative, non-integral or larger than the column can hold.
    /// </summary>
    /// <remarks>
    /// Parsed as an unsigned value from the raw string, so `-1` is rejected as a client fault rather
    /// than wrapping to a very large number and being compared against `next_seq` — which is the
    /// specific way an over-range cursor turns into a silent empty page.
    /// </remarks>
    private static bool TryParseCursor(string? raw, out ulong value)
    {
        value = 0;
        if (string.IsNullOrEmpty(raw)) return true;

        return ulong.TryParse(raw, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// The page size: default 200, maximum 1000, and <b>never clamped</b>.
    /// </summary>
    private static bool TryParseLimit(string? raw, out int value)
    {
        value = CommsLimits.CatchUpLimitDefault;
        if (string.IsNullOrEmpty(raw)) return true;

        if (!int.TryParse(raw, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out value))
        {
            return false;
        }

        return value >= 1 && value <= CommsLimits.CatchUpLimitMax;
    }

    /// <summary>
    /// One rule for every client-chosen identifier (§4): non-empty, bounded, and printable ASCII
    /// without whitespace.
    /// </summary>
    internal static bool IsValidIdentifier(string? value) =>
        !string.IsNullOrEmpty(value)
        && value.Length <= CommsLimits.IdentifierMaxLength
        && value.All(c => c is > (char)0x20 and < (char)0x7F);

    private static IResult Unauthorized() =>
        Error(ProtocolErrorCode.Unauthorized, StatusCodes.Status401Unauthorized);

    private static IResult Error(ProtocolErrorCode code, int status) =>
        Json(ErrorResponse.For(code), status);

    private static IResult Json<T>(T body, int status) =>
        Results.Json(body, FleetProtocolJson.Options, statusCode: status);
}
