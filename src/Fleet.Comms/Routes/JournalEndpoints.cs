using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fleet.Conversations.Contracts;
using Fleet.Conversations.Journal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Fleet.Comms.Routes;

/// <summary>
/// The journal's two routes, served on the JOURNAL listener and nowhere else.
/// </summary>
/// <remarks>
/// <para>
/// Never mapped on north, south or ops. A publisher holds a journal token and nothing else; it never
/// needs the south bearer, which drives conversation delivery.
/// </para>
/// <para>
/// ⚠️ No text, transcript, token or key in any log line or metric label. Logs carry the reason, the
/// subject and ids only.
/// </para>
/// </remarks>
public static class JournalEndpoints
{
    public const string MessagesPath = "/journal/v1/messages";
    public const string StatusPath = "/journal/v1/status";

    /// <summary>Shared with the upload routes, so one JSON shape answers the whole listener.</summary>
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static JsonSerializerOptions Json => JsonOptions;

    /// <param name="media">
    /// The media gate, when the deployment configured a bucket. Null leaves the two upload routes
    /// unmapped and every byte reference in a record answered <c>409 media_disabled</c> — the
    /// pre-slice-4 behaviour, and what the S2 drainer already handles.
    /// </param>
    /// <param name="mediaStats">
    /// Object and byte counts for the status route. Null with media off, so the status body gains
    /// no <c>media</c> key at all rather than one full of nulls.
    /// </param>
    /// <param name="readAllSubjects">
    /// The subjects granted read scope <c>all</c> (#394), for the status route's <c>read</c> block.
    /// </param>
    public static void Map(
        WebApplication app, IJournalStore store, JournalRuntimeStats stats,
        IReadOnlySet<long> excludedChatIds, TimeProvider time, ILogger logger,
        JournalMediaGate? media = null, IJournalObjectStore? bytes = null,
        MySqlJournalObjectStore? objects = null, IReadOnlyList<string>? readAllSubjects = null)
    {

        app.MapPost(MessagesPath, async Task<IResult> (HttpContext context, CancellationToken ct) =>
        {
            var subject = (string)context.Items[JournalAuth.SubjectItem]!;
            var started = Stopwatch.GetTimestamp();

            try
            {
                return await IngestAsync(
                    context, subject, store, stats, excludedChatIds, time, logger,
                    media is not null, ct);
            }
            finally
            {
                stats.RecordIngestDuration(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        });

        app.MapGet(StatusPath, async Task<IResult> (CancellationToken ct) =>
        {
            // Read once per status request, never per field: two counts from two queries would let
            // `objects` and `bytes_stored` describe different moments.
            JournalMediaStats? mediaStats = null;

            if (objects is not null)
            {
                try
                {
                    mediaStats = await objects.ReadStatsAsync(ct);
                }
                catch (Exception e) when (e is MySqlConnector.MySqlException or InvalidOperationException)
                {
                    logger.LogWarning("journal media stats unavailable: {Error}", e.GetType().Name);
                }
            }

            JournalStoreStatus status;
            try
            {
                status = await store.GetStatusAsync(ct);
            }
            catch (JournalStoreUnavailableException e)
            {
                stats.Rejected("store_unavailable");
                return Error(StatusCodes.Status503ServiceUnavailable, new { error = "store_unavailable", reason = e.Reason });
            }

            var runtime = stats.Read();
            var bindings = app.Services.GetRequiredService<JournalTurnBindings>().Counts();

            // Counts and times only. Never message text.
            return Results.Json(new
            {
                schemaVersion = status.SchemaVersion,
                turnBindings = new { active = bindings.Active, expired = bindings.Expired },
                attachmentFetch = new { inFlight = stats.AttachmentFetchInFlight },
                enabled = true,
                observers = status.Observers.Select(o => new
                {
                    observer = o.Observer,
                    messages = o.Messages,
                    lastIngestAt = o.LastIngestAt,
                }),
                rejectedSinceStart = runtime.RejectedSinceStart,
                gc = new
                {
                    lastRunAt = runtime.GcLastRunAt,
                    deletedSinceStart = new
                    {
                        messages = runtime.GcDeletedMessages,
                        conversations = runtime.GcDeletedConversations,
                    },
                    failedSinceStart = runtime.GcFailures,
                },
                ingest = new
                {
                    windowSeconds = 3600,
                    samples = runtime.LatencySamples,
                    p50Ms = runtime.IngestP50Milliseconds,
                    p95Ms = runtime.IngestP95Milliseconds,
                },

                // The read tools (#394): who holds scope `all`, and calls per tool and result code.
                // Subjects are runtime names, never tokens.
                read = new
                {
                    allScopeSubjects = readAllSubjects ?? [],
                    requestsSinceStart = runtime.ReadsSinceStart,
                },

                // Absent, not null, when the deployment has no bucket — the same rule as the
                // heartbeat's journal block: an install that did not opt in reports exactly what it
                // reported before this slice.
                media = media is null ? null : new
                {
                    state = media.MediaState,
                    objects = mediaStats?.Objects ?? 0,
                    bytesStored = mediaStats?.BytesStored ?? 0,
                    lastSweepAt = runtime.ObjectsLastSweepAt,
                    deletedSinceStart = new
                    {
                        abandoned = runtime.ObjectsAbandonedDeleted,
                        retired = runtime.ObjectsRetiredDeleted,
                        orphans = runtime.ObjectsOrphansDeleted,
                    },
                    sweepFailures = runtime.ObjectSweepFailures,
                    upload = new
                    {
                        windowSeconds = 3600,
                        samples = runtime.UploadSamples,
                        p50Ms = runtime.UploadP50Milliseconds,
                        p95Ms = runtime.UploadP95Milliseconds,
                    },
                },
            }, Json);
        });
    }

    private static async Task<IResult> IngestAsync(
        HttpContext context, string subject, IJournalStore store, JournalRuntimeStats stats,
        IReadOnlySet<long> excludedChatIds, TimeProvider time, ILogger logger,
        bool mediaEnabled, CancellationToken ct)
    {
        // 1. Size, before parsing: the declared length first, then the bytes actually sent.
        if (context.Request.ContentLength > JournalRecordParser.MaxBodyBytes)
            return Refused(stats, "too_large", StatusCodes.Status413PayloadTooLarge, new { error = "too_large" });

        var body = await ReadBoundedAsync(context.Request.Body, JournalRecordParser.MaxBodyBytes, ct);
        if (body is null)
            return Refused(stats, "too_large", StatusCodes.Status413PayloadTooLarge, new { error = "too_large" });

        // 2. A byte reference on a deployment without media is refused BEFORE the record is parsed,
        //    and with the same answer the parser gives: `409 media_disabled`. Ordered ahead of
        //    validation so a caller that sent a byte reference to a media-less Comms learns the
        //    feature is off rather than that its record also had a bad field.
        if (!mediaEnabled && JournalRecordParser.ReferencesBytes(body.Value))
        {
            JournalRuntimeStats.Ingest("media_disabled");
            stats.Rejected("media_disabled");
            return Error(StatusCodes.Status409Conflict, new { error = "media_disabled" });
        }

        // 3. The record, field by field.
        var record = JournalRecordParser.Parse(body.Value, time.GetUtcNow(), out var failure);
        if (record is null)
        {
            logger.LogInformation("journal record from {Subject} refused: {Error} {Field}",
                subject, failure!.Error, failure.Field);
            return Refused(stats, failure.Error, failure.Status,
                failure.Field is null
                    ? new { error = failure.Error }
                    : (object)new { error = failure.Error, field = failure.Field });
        }

        // 4. Classification BEFORE any write. Human and a null allowlist: only "no chat" and
        //    "operational chat" can apply on the server, and chat id 0 was already refused above.
        var decision = JournalClassifier.Classify(
            record.Direction, record.Telegram.ChatId, record.Telegram.ChatKind,
            JournalTaskOrigin.Human, allowlist: null, excludedChatIds);

        if (!decision.Include)
        {
            JournalRuntimeStats.Ingest("excluded");
            stats.Rejected("excluded_chat");
            return Error(StatusCodes.Status422UnprocessableEntity, new { error = "excluded_chat" });
        }

        // 5. The store: one transaction.
        JournalIngestResult result;
        try
        {
            result = await store.IngestAsync(record, subject, ct);
        }
        catch (JournalStoreUnavailableException e)
        {
            JournalRuntimeStats.Ingest("unavailable");
            stats.Rejected("store_unavailable");
            return Error(StatusCodes.Status503ServiceUnavailable, new { error = "store_unavailable", reason = e.Reason });
        }

        switch (result.Outcome)
        {
            case JournalIngestOutcome.Created:
                JournalRuntimeStats.Ingest("created");
                return Results.Json(new { messageId = result.MessageId, result = "created" }, Json,
                    statusCode: StatusCodes.Status201Created);

            case JournalIngestOutcome.Duplicate:
                JournalRuntimeStats.Ingest("duplicate");
                return Results.Json(new { messageId = result.MessageId, result = "duplicate" }, Json);

            case JournalIngestOutcome.ObserverAdded:
                JournalRuntimeStats.Ingest("observer_added");
                return Results.Json(new { messageId = result.MessageId, result = "observer_added" }, Json);

            case JournalIngestOutcome.UploadIncomplete:
                // Not a conflict and not a permanent failure: the object is gone, was never
                // uploaded, or belongs to someone else. The agent re-uploads from its spool and
                // resends, so the ordinals are what it needs to know which attachment to redo.
                JournalRuntimeStats.Ingest("upload_incomplete");
                stats.Rejected("upload_incomplete");
                return Error(StatusCodes.Status409Conflict,
                    new { error = "upload_incomplete", attachments = result.UploadOrdinals });

            case JournalIngestOutcome.EventIdReused:
                logger.LogInformation("journal event id reused by {Subject} with a different record", subject);
                JournalRuntimeStats.Ingest("conflict");
                stats.Rejected("idempotency_conflict");
                return Error(StatusCodes.Status409Conflict, new { error = "idempotency_conflict", reason = "event_id_reused" });

            default:
                logger.LogInformation("journal record from {Subject} conflicts with the stored message", subject);
                JournalRuntimeStats.Ingest("conflict");
                stats.Rejected("idempotency_conflict");
                return Error(StatusCodes.Status409Conflict, new { error = "idempotency_conflict" });
        }
    }

    /// <summary>The body, or null when it is longer than <paramref name="limit"/> bytes.</summary>
    private static async Task<ReadOnlyMemory<byte>?> ReadBoundedAsync(Stream body, int limit, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];

        while (true)
        {
            var read = await body.ReadAsync(chunk, ct);
            if (read == 0) break;

            if (buffer.Length + read > limit) return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static IResult Refused(JournalRuntimeStats stats, string reason, int status, object body)
    {
        JournalRuntimeStats.Ingest("invalid");
        stats.Rejected(reason);
        return Error(status, body);
    }

    private static IResult Error(int status, object body) => Results.Json(body, Json, statusCode: status);
}
