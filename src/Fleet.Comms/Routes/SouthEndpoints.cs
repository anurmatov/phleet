using System.Security.Cryptography;
using System.Text;
using Fleet.Comms.Configuration;
using Fleet.Comms.Contracts;
using Fleet.Conversations;
using Fleet.Conversations.Contracts;
using Fleet.Protocol;
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
/// </remarks>
public static class SouthEndpoints
{
    public static void Map(WebApplication app, IConversationStore store, CommsOptions options)
    {
        var expected = Encoding.UTF8.GetBytes(options.SouthBearerToken);

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
            if (Authorized(context, expected))
            {
                await next(context);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                FleetProtocolJson.Serialize(ErrorResponse.For(ProtocolErrorCode.Unauthorized)));
        });

        var group = app.MapGroup(string.Empty);

        group.MapPost("/deliveries:claim", async Task<IResult> (ClaimDeliveryRequest request, CancellationToken ct) =>
            Results.Json(await store.ClaimDeliveryAsync(request, ct), FleetProtocolJson.Options));

        group.MapPost("/submissions:disposition", async Task<IResult> (RecordDispositionRequest request, CancellationToken ct) =>
        {
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

        group.MapPost("/deliveries:complete", async Task<IResult> (CompleteDeliveryRequest request, CancellationToken ct) =>
        {
            try
            {
                return Results.Json(await store.CompleteDeliveryAsync(request, ct), FleetProtocolJson.Options);
            }
            catch (ArgumentException)
            {
                return Error(ProtocolErrorCode.UnsupportedKind, StatusCodes.Status400BadRequest);
            }
        });

        group.MapPost("/turns:start", async Task<IResult> (StartTurnRequest request, CancellationToken ct) =>
        {
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

        group.MapPost("/turns:commit", async Task<IResult> (CommitTerminalRequest request, CancellationToken ct) =>
            Results.Json(await store.CommitTerminalAsync(request, ct), FleetProtocolJson.Options));

        group.MapPost("/events:append", async Task<IResult> (AppendBatchRequest request, CancellationToken ct) =>
            Results.Json(await store.AppendBatchAsync(request, ct), FleetProtocolJson.Options));

        group.MapPost("/leases:heartbeat", async Task<IResult> (HeartbeatRequest request, CancellationToken ct) =>
            Results.Json(await store.HeartbeatAsync(request, ct), FleetProtocolJson.Options));
    }

    private static IResult Error(ProtocolErrorCode code, int status) =>
        Results.Json(ErrorResponse.For(code), FleetProtocolJson.Options, statusCode: status);

    /// <summary>
    /// Fixed-time bearer comparison.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A wrong credential and an absent one are deliberately indistinguishable: both take the same
    /// path and produce the same body, so a caller learns nothing from which it got.
    /// </para>
    /// <para>
    /// The comparison runs even when the header is missing, against an empty candidate, so the
    /// absent case cannot be timed apart from the wrong one.
    /// </para>
    /// </remarks>
    private static bool Authorized(HttpContext context, byte[] expected)
    {
        var header = context.Request.Headers.Authorization.ToString();

        var presented = header.StartsWith("Bearer ", StringComparison.Ordinal)
            ? Encoding.UTF8.GetBytes(header["Bearer ".Length..])
            : [];

        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(presented), SHA256.HashData(expected));
    }
}
