using Fleet.Comms.Configuration;
using Fleet.Comms.Contracts;
using Fleet.Conversations;
using Fleet.Conversations.Contracts;
using Fleet.Conversations.Journal;
using Fleet.Protocol;
using Fleet.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Fleet.Comms.Routes;

/// <summary>
/// The agent-facing store surface, served on the SOUTH listener and nowhere else.
/// </summary>
/// <remarks>
/// <para>
/// These are never mapped on the north listener. The north surface is a client contract; this is an
/// administrative one, and the two must not be reachable from the same address — a client that could
/// reach <c>/turns:commit</c> could write a terminal for someone else's turn.
/// </para>
/// <para>
/// The agent holds <b>no database credential</b>. Everything it needs — including writing a delivery
/// claim, which #276 §8.7 originally showed the consumer inserting directly — happens through these
/// endpoints, because that direct insert and "the agent holds no credential" cannot both be true.
/// </para>
/// <para>
/// <b>Each agent holds its own token</b> (#530 D8, D9): <c>cj1.south.&lt;agent name&gt;</c>, minted by
/// the orchestrator at provisioning under the key the two services share. Every call names what it
/// acts on — a command, a submission, an attempt, a conversation or an attachment — and is refused,
/// as an unknown conversation, unless that belongs to a conversation with the token's agent. One
/// agent's token cannot act on another agent's conversation (#530 MUST NOT 5).
/// </para>
/// </remarks>
public static class SouthEndpoints
{
    /// <param name="attachments">
    /// The byte store, when attachments are configured. Null leaves <c>/attachments/…</c> unmapped,
    /// so an install without a root answers 404 from the router rather than from a handler.
    /// </param>
    public static void Map(
        WebApplication app, IConversationStore store, CommsOptions options,
        AttachmentStore? attachments = null)
    {
        var keys = options.Journal.Keys();

        // MIDDLEWARE, not an endpoint filter, and the difference is not stylistic.
        //
        // An endpoint filter runs AFTER argument binding. An unauthenticated caller whose body did
        // not parse therefore got the framework's empty-bodied 400 and never reached this check at
        // all — so the "absent and wrong are indistinguishable" property held only for requests that
        // happened to deserialize, and an anonymous caller could probe body shapes by watching 400
        // turn into 401. Measured, not reasoned about: it is what the two disposition endpoints did
        // before this suite sent a request at them.
        //
        // As middleware the credential is checked before routing resolves anything and before a
        // single byte of body is parsed, on EVERY path — including one that matches no endpoint, so
        // the surface does not answer "that route exists" either.
        app.Use(async (context, next) =>
        {
            if (AgentOf(context, keys) is { } agent)
            {
                context.Items[AgentItem] = agent;
                await next(context);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                FleetProtocolJson.Serialize(ErrorResponse.For(ProtocolErrorCode.Unauthorized)));
        });

        var group = app.MapGroup(string.Empty);

        group.MapPost("/deliveries:claim", async Task<IResult> (HttpContext http, ClaimDeliveryRequest request, CancellationToken ct) =>
            await RefuseForeignAsync(http, store, ct, (AgentTargetKind.Message, request.MessageId))
            ?? Results.Json(await store.ClaimDeliveryAsync(request, ct), FleetProtocolJson.Options));

        group.MapPost("/submissions:disposition", async Task<IResult> (HttpContext http, RecordDispositionRequest request, CancellationToken ct) =>
        {
            if (await RefuseForeignAsync(http, store, ct,
                    (AgentTargetKind.Message, request.MessageId),
                    (AgentTargetKind.Submission, request.SubmissionId),
                    (AgentTargetKind.Attempt, request.AttemptId)) is { } refusal)
                return refusal;

            try
            {
                return Results.Json(await store.RecordDispositionAsync(request, ct), FleetProtocolJson.Options);
            }
            catch (ArgumentException)
            {
                // queue_full and dropped belong to /deliveries:complete. Recording a disposition in
                // both would append submission.accepted twice for one submission.
                return Error(ProtocolErrorCode.UnsupportedKind, StatusCodes.Status400BadRequest);
            }
        });

        group.MapPost("/deliveries:complete", async Task<IResult> (HttpContext http, CompleteDeliveryRequest request, CancellationToken ct) =>
        {
            if (await RefuseForeignAsync(http, store, ct,
                    (AgentTargetKind.Message, request.MessageId),
                    (AgentTargetKind.Submission, request.SubmissionId),
                    (AgentTargetKind.Attempt, request.AttemptId)) is { } refusal)
                return refusal;

            try
            {
                return Results.Json(await store.CompleteDeliveryAsync(request, ct), FleetProtocolJson.Options);
            }
            catch (ArgumentException)
            {
                return Error(ProtocolErrorCode.UnsupportedKind, StatusCodes.Status400BadRequest);
            }
        });

        group.MapPost("/turns:start", async Task<IResult> (HttpContext http, StartTurnRequest request, CancellationToken ct) =>
        {
            if (await RefuseForeignAsync(http, store, ct,
                    [(AgentTargetKind.Attempt, request.AttemptId), .. Submissions(request.MergedSubmissionIds)]) is { } refusal)
                return refusal;

            try
            {
                return Results.Json(await store.StartTurnAsync(request, ct), FleetProtocolJson.Options);
            }
            catch (InvalidOperationException)
            {
                // A different turnId on an already-running attempt. Refused rather than silently
                // accepted: two turns for one attempt is what that would record.
                return Error(ProtocolErrorCode.UnsupportedKind, StatusCodes.Status409Conflict);
            }
        });

        group.MapPost("/turns:commit", async Task<IResult> (HttpContext http, CommitTerminalRequest request, CancellationToken ct) =>
            await RefuseForeignAsync(http, store, ct,
                [(AgentTargetKind.Attempt, request.AttemptId), .. Submissions(request.MergedSubmissionIds)])
            ?? Results.Json(await store.CommitTerminalAsync(request, ct), FleetProtocolJson.Options));

        group.MapPost("/events:append", async Task<IResult> (HttpContext http, AppendBatchRequest request, CancellationToken ct) =>
            await RefuseForeignAsync(http, store, ct,
                [
                    (AgentTargetKind.Conversation, request.ConversationId),
                    .. (request.Events ?? []).Select(e => (AgentTargetKind.Submission, e.SubmissionId)),
                    .. (request.Events ?? []).Select(e => (AgentTargetKind.Attempt, e.AttemptId)),
                ])
            ?? Results.Json(await store.AppendBatchAsync(request, ct), FleetProtocolJson.Options));

        group.MapPost("/leases:heartbeat", async Task<IResult> (HttpContext http, HeartbeatRequest request, CancellationToken ct) =>
            await RefuseForeignAsync(http, store, ct,
                [.. (request.AttemptIds ?? []).Select(id => (AgentTargetKind.Attempt, (string?)id))])
            ?? Results.Json(await store.HeartbeatAsync(request, ct), FleetProtocolJson.Options));

        // ── attachment bytes, for the agent (#308 D7) ────────────────────────
        //
        // SOUTH and not north, and that is the whole reason this route exists here rather than being
        // reused from the client surface: the agent must never hold a device session bearer. Giving
        // it one inverts the boundary the entire design rests on (MUST NOT 10).
        //
        // It sits inside the pre-routing middleware above, so it inherits the indistinguishable 401
        // for free — an unauthenticated caller cannot even learn that the route exists.
        //
        // An agent reads only attachments in its own conversations: a foreign one is the same
        // not-found as one that does not exist.
        if (attachments is not null)
        {
            group.MapGet("/attachments/{attachmentId}/content",
                async Task<IResult> (HttpContext http, string attachmentId, CancellationToken ct) =>
                {
                    if (!AttachmentStore.IsSafeId(attachmentId))
                        return Error(ProtocolErrorCode.AttachmentNotFound, StatusCodes.Status404NotFound);

                    var owner = await store.FindAgentAsync(AgentTargetKind.Attachment, attachmentId, ct);
                    if (!string.Equals(owner, (string)http.Items[AgentItem]!, StringComparison.Ordinal))
                        return Error(ProtocolErrorCode.AttachmentNotFound, StatusCodes.Status404NotFound);

                    var row = await store.GetAttachmentAsync(attachmentId, ct);

                    // Only rows with verified bytes. A reserved or failed row has none, and reports
                    // the same not-found an unknown id does.
                    if (row is null || row.State is not (AttachmentState.Sealed or AttachmentState.Bound))
                        return Error(ProtocolErrorCode.AttachmentNotFound, StatusCodes.Status404NotFound);

                    var bytes = attachments.OpenRead(attachmentId);

                    // The row is authoritative; the volume disagreeing with it is the gone state, not
                    // a 500 (MUST NOT 11). The agent turns this into a turn.notice naming the
                    // unavailable image rather than answering as though none was sent.
                    if (bytes is null)
                        return Error(ProtocolErrorCode.AttachmentGone, StatusCodes.Status410Gone);

                    // The SNIFFED type from the row. There is no other type in this system.
                    return Results.Stream(bytes, row.ContentType);
                });
        }
    }

    /// <summary>Where the middleware leaves the calling agent's name for the routes.</summary>
    private const string AgentItem = "south.agent";

    private static IEnumerable<(AgentTargetKind, string?)> Submissions(IReadOnlyList<string>? ids) =>
        (ids ?? []).Select(id => (AgentTargetKind.Submission, (string?)id));

    /// <summary>
    /// Null when nothing named belongs to another agent's conversation; otherwise the answer to give.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asked BEFORE the store acts. A target that belongs to another agent — or to a conversation
    /// from before conversations named their agent — is answered as an unknown conversation, so the
    /// route is not an oracle for what other agents are doing.
    /// </para>
    /// <para>
    /// A target that does not exist at all passes, and the store gives its ordinary answer: an
    /// attempt is named here before the disposition that creates it, and refusing it would refuse
    /// the agent's own first call.
    /// </para>
    /// </remarks>
    private static async Task<IResult?> RefuseForeignAsync(
        HttpContext http, IConversationStore store, CancellationToken ct,
        params (AgentTargetKind Kind, string? Id)[] targets)
    {
        var agent = (string)http.Items[AgentItem]!;

        foreach (var (kind, id) in targets.Distinct())
        {
            if (string.IsNullOrEmpty(id)) continue;

            var owner = await store.FindAgentAsync(kind, id, ct);
            if (owner is not null && !string.Equals(owner, agent, StringComparison.Ordinal))
                return Error(ProtocolErrorCode.ConversationNotFound, StatusCodes.Status404NotFound);
        }

        return null;
    }

    private static IResult Error(ProtocolErrorCode code, int status) =>
        Results.Json(ErrorResponse.For(code), FleetProtocolJson.Options, statusCode: status);

    /// <summary>
    /// The agent a request's token names, or null.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A wrong credential and an absent one are deliberately indistinguishable: both take the same
    /// path and produce the same body, so a caller learns nothing from which it got.
    /// </para>
    /// <para>
    /// The MAC is checked even when the header is missing, against an empty candidate, so the absent
    /// case cannot be timed apart from the wrong one. A journal token, the chat service's own
    /// sign-in token and any other purpose verify as nothing here.
    /// </para>
    /// </remarks>
    private static string? AgentOf(HttpContext context, IReadOnlyList<byte[]> keys)
    {
        var header = context.Request.Headers.Authorization.ToString();
        var presented = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header["Bearer ".Length..] : "";

        return JournalTokens.TryVerify(presented, SignIn.AgentTokenPurpose, keys, out var agent) ? agent : null;
    }
}
