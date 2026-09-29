using System.Diagnostics;
using Fleet.Comms.Configuration;
using System.Security.Cryptography;
using Fleet.Conversations.Contracts;
using Fleet.Protocol;
using Fleet.Conversations.Journal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Fleet.Comms.Routes;

/// <summary>
/// The two upload routes on the journal listener (#388), and the media half of the ingest route.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bytes are always required.</b> There is no hash-only shortcut anywhere in this file, and that
/// is the security property the whole lifecycle rests on: a subject can only ever attach an object
/// whose bytes it physically sent, so no subject can name a digest and thereby read something
/// another runtime archived. Dedup happens afterwards, after proof, and only ever moves an
/// attachment to an object the caller already proved.
/// </para>
/// <para>
/// <b>The owner binding answers 404, not 403.</b> A PUT for an upload someone else opened is
/// byte-identical to a PUT for an id that does not exist. A 403 would confirm the id exists, which
/// turns the upload surface into an oracle for enumerating other runtimes' uploads.
/// </para>
/// <para>
/// ⚠️ No object key, digest, or bucket name in any log line or response body. An object key is an
/// internal address; a digest is a content identifier that must not be echoed back to a caller who
/// did not compute it.
/// </para>
/// </remarks>
public static class JournalUploadEndpoints
{
    public const string UploadsPath = "/journal/v1/uploads";
    public const string UploadByIdPath = "/journal/v1/uploads/{uploadId}";

    /// <summary>Largest object the store accepts. Equal to the Bot API's file cap.</summary>
    public const long MaxObjectBytes = MediaOptions.MaxObjectBytes;

    public static void Map(
        WebApplication app,
        MySqlJournalObjectStore objects,
        IJournalObjectStore bytes,
        JournalRuntimeStats stats,
        TimeProvider time,
        ILogger logger,
        JournalMediaGate? gate = null)
    {
        app.MapPost(UploadsPath, async Task<IResult> (HttpContext context, CancellationToken ct) =>
        {
            var subject = (string)context.Items[JournalAuth.SubjectItem]!;

            if (context.Request.ContentLength > JournalRecordParser.MaxBodyBytes)
                return Error(StatusCodes.Status413PayloadTooLarge, new { error = "too_large" });

            var body = await ReadBoundedAsync(context.Request.Body, 4096, ct);
            if (body is null)
                return Error(StatusCodes.Status413PayloadTooLarge, new { error = "too_large" });

            if (!JournalUploadParser.TryParse(body.Value, out var declaration, out var field)
                || declaration is null)
            {
                stats.Upload("invalid");
                return Error(StatusCodes.Status422UnprocessableEntity,
                    new { error = "invalid_upload", field });
            }

            // Refused before a row exists: a declaration over the cap can never be satisfied, and
            // writing a row for it would be a row only the sweeper can remove.
            if (declaration.ByteSize > MaxObjectBytes)
            {
                stats.Upload("too_large");
                return Error(StatusCodes.Status413PayloadTooLarge, new { error = "too_large" });
            }

            if (!await MediaAvailableAsync(gate, bytes, stats, ct))
                return Error(StatusCodes.Status503ServiceUnavailable, new { error = "media_unavailable" });

            try
            {
                var id = await objects.OpenUploadAsync(
                    subject, declaration.Sha256, declaration.ByteSize, declaration.MimeType, ct);

                stats.Upload("declared");
                return Results.Json(new { uploadId = id }, JournalEndpoints.JsonOptions,
                    statusCode: StatusCodes.Status201Created);
            }
            catch (Exception e) when (e is MySqlConnector.MySqlException or InvalidOperationException)
            {
                logger.LogWarning("journal upload could not be opened for {Subject}: {Error}",
                    subject, e.GetType().Name);
                stats.Upload("unavailable");
                return Error(StatusCodes.Status503ServiceUnavailable, new { error = "store_unavailable" });
            }
        });

        // ⚠️ MaxRequestBodySize is the object cap, and it is set on the route rather than on the
        //    server. Kestrel's default body limit is 30 MB and ASP.NET Core's per-request
        //    MinRequestBodyDataRate/size plumbing sits below that, but the value that matters is the
        //    one this feature states: an upload is allowed exactly as many bytes as an object may
        //    hold, and nothing else on this listener is. A limit inherited from a framework default
        //    is a limit that changes when the framework does.
        app.MapPut(UploadByIdPath, async Task<IResult> (
            HttpContext context, string uploadId, CancellationToken ct) =>
        {
            var subject = (string)context.Items[JournalAuth.SubjectItem]!;
            var started = Stopwatch.GetTimestamp();

            // The one place the object cap is stated as a server limit. Everything else in this
            // route compares against the row's declared size; this is what lets the request be read
            // at all. Set through the feature, because Kestrel's default body limit is a
            // per-server number and this route's limit is a per-route one.
            context.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize = MaxObjectBytes;

            try
            {
                return await PutAsync(context, uploadId, subject, objects, bytes, gate, stats, logger, ct);
            }
            finally
            {
                stats.RecordUploadDurationSample(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        });
    }

    private static async Task<IResult> PutAsync(
        HttpContext context, string uploadId, string subject,
        MySqlJournalObjectStore objects, IJournalObjectStore bytes, JournalMediaGate? gate,
        JournalRuntimeStats stats, ILogger logger, CancellationToken ct)
    {
        // 1. One answer for "no such upload" and "not yours".
        if (!Ulid.IsValid(uploadId)) return NotFoundJson(stats);

        JournalObjectRow? row;
        try
        {
            row = await objects.FindByIdAsync(uploadId, ct);
        }
        catch (Exception e) when (e is MySqlConnector.MySqlException or InvalidOperationException)
        {
            stats.Upload("unavailable");
            return Error(StatusCodes.Status503ServiceUnavailable, new { error = "store_unavailable" });
        }

        if (row is null || !string.Equals(row.Owner, subject, StringComparison.Ordinal))
            return NotFoundJson(stats);

        // 2. Only an `uploading` row accepts bytes: a retried PUT after a commit, an abort or a
        //    sweep must not write a second object or resurrect a dead row.
        if (row.State != JournalObjectState.Uploading) return NotFoundJson(stats);

        // 3. Length, before a byte is read. Declared Content-Length is a claim, so it is a fast
        //    rejection only — the authoritative bound is the hashing stream, which reads at most
        //    byteSize + 1 bytes no matter what the request claims.
        if (context.Request.ContentLength > row.ByteSize)
        {
            stats.Upload("too_large");
            return Error(StatusCodes.Status413PayloadTooLarge, new { error = "too_large" });
        }

        if (!await MediaAvailableAsync(gate, bytes, stats, ct))
            return Error(StatusCodes.Status503ServiceUnavailable, new { error = "media_unavailable" });

        // 4. Stream through SHA-256 into the bucket with a known length. Nothing is buffered: the
        //    cap is 20 MB and the listener is shared by every publisher.
        JournalObjectWriteResult write;
        try
        {
            // ⚠️ The declared length goes to the store twice over: as the read bound, and as the
            //    length the S3 request states. A request body cannot state its own length — a
            //    chunked upload has none, and even a Content-Length one is a stream the transport
            //    refuses to report a length for — and an S3 PutObject with no length fails
            //    client-side before a byte is sent. See S3ObjectStore's remarks for the exact
            //    resolution order; this is the reason the parameter exists.
            write = await bytes.PutAsync(
                row.ObjectKey, context.Request.Body, row.ByteSize, row.MimeType, ct,
                contentLength: row.ByteSize);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning("journal upload {Id} failed for {Subject}: {Error}",
                uploadId, subject, e.GetType().Name);
            stats.Upload("unavailable");
            // The row stays `uploading`: the subject can PUT again, and the sweeper gets it if not.
            return Error(StatusCodes.Status503ServiceUnavailable, new { error = "media_unavailable" });
        }

        if (write.StoreFailure)
        {
            stats.Upload("unavailable");
            return Error(StatusCodes.Status503ServiceUnavailable, new { error = "media_unavailable" });
        }

        // 5. Hash or size mismatch: the object is not what was declared, so it goes.
        if (write.Overflow
            || write.ByteSize != row.ByteSize
            || !CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(write.Sha256), Convert.FromHexString(row.Sha256)))
        {
            await DiscardAsync(bytes, row.ObjectKey, logger, ct);
            await objects.MarkAbortedAsync(uploadId, subject, ct);

            stats.Upload("sha256_mismatch");
            return Error(StatusCodes.Status422UnprocessableEntity, new { error = "sha256_mismatch" });
        }

        // 6. `uploaded`, guarded on owner and state. Zero rows means this upload stopped being live
        //    mid-PUT — swept, or committed by a retry — and the object is discarded.
        if (!await objects.MarkUploadedAsync(uploadId, subject, ct))
        {
            await DiscardAsync(bytes, row.ObjectKey, logger, ct);
            stats.Upload("upload_incomplete");
            return Error(StatusCodes.Status409Conflict, new { error = "upload_incomplete" });
        }

        stats.Upload("stored");
        return Results.Json(new { uploadId }, JournalEndpoints.JsonOptions);
    }

    /// <summary>
    /// The degraded gate. True when the store is reachable; false means media is configured and not
    /// answering, which is <c>503 media_unavailable</c> rather than a partial write.
    /// </summary>
    /// <remarks>
    /// The store's own probe result is cached by the host that built it, so this is not a bucket
    /// round trip per request when the store is healthy.
    /// </remarks>
    /// <param name="gate">
    /// The host's cached availability, which is what a healthy deployment consults. Probing the
    /// store itself per request would put a bucket round trip in front of every upload, and a
    /// healthy bucket is the common case.
    /// </param>
    /// <remarks>
    /// ⚠️ A probe failure here is a <c>503</c>, never a partial write. An upload that opened a row
    /// against a bucket nobody can reach is a row the agent will retry against a store that is not
    /// there, and a PUT that streamed into nothing would report success.
    /// </remarks>
    internal static async Task<bool> MediaAvailableAsync(
        JournalMediaGate? gate, IJournalObjectStore bytes, JournalRuntimeStats stats, CancellationToken ct)
    {
        if (gate is not null) return await gate.IsAvailableAsync(ct);
        if (bytes is IJournalStoreHealth health) return await health.IsAvailableAsync(ct);

        try
        {
            return await bytes.ProbeAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _ = e;
            return false;
        }
    }

    private static async Task DiscardAsync(
        IJournalObjectStore bytes, string objectKey, ILogger logger, CancellationToken ct)
    {
        try
        {
            await bytes.DeleteAsync(objectKey, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The row is `aborted`, so the sweeper removes the object next tick. Failing to discard
            // is a leak with a repair path, not a reason to change the client's answer.
            logger.LogWarning("journal upload bytes could not be discarded and will be swept: {Error}",
                e.GetType().Name);
        }
    }

    private static IResult NotFoundJson(JournalRuntimeStats stats)
    {
        stats.Upload("unauthorized");
        return JournalAuth.Unauthorized();
    }

    private static IResult Error(int status, object body) =>
        Results.Json(body, JournalEndpoints.JsonOptions, statusCode: status);

    private static async Task<ReadOnlyMemory<byte>?> ReadBoundedAsync(Stream body, int limit, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8 * 1024];

        while (true)
        {
            var read = await body.ReadAsync(chunk, ct);
            if (read == 0) break;

            if (buffer.Length + read > limit) return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
