using System.Security.Cryptography;
using System.Text;
using Fleet.Comms.Auth;
using Fleet.Comms.Contracts;
using Fleet.Conversations;
using Fleet.Conversations.Contracts;
using Fleet.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Fleet.Comms.Routes;

/// <summary>
/// The two byte-moving routes (#308 D2, D6). <b>They use different credentials and neither handler
/// can reach the other's.</b>
/// </summary>
/// <remarks>
/// <para>
/// This is the D6 mechanism, and it is a property of the routing rather than of a check someone
/// remembered to write. The north listener has no blanket authentication filter — every conversation
/// handler calls <see cref="NorthEndpoints.AuthenticateAsync"/> explicitly — so:
/// </para>
/// <list type="bullet">
///   <item><see cref="UploadAsync"/> <b>never calls <c>AuthenticateAsync</c></b>. It resolves the row
///   by id and compares an upload-capability digest. A session bearer presented here fails that
///   comparison and gets <c>404 attachment_not_found</c>: no oracle, and no accidental
///   acceptance.</item>
///   <item><see cref="DownloadAsync"/> <b>calls <c>AuthenticateAsync</c> and never reads
///   <c>upload_token_sha256</c></b>. An upload token presented here gets the ordinary
///   <c>401 unauthorized</c>.</item>
/// </list>
/// <para>
/// Keep the two credential paths textually separate. Factoring "the common part" out of them is how
/// a shared filter gets introduced, and a shared filter is exactly what would let one credential
/// satisfy the other route by oversight.
/// </para>
/// </remarks>
public static class AttachmentEndpoints
{
    /// <summary>The two routes this adds to the north table when attachments are configured.</summary>
    public static readonly IReadOnlyList<string> Routes =
    [
        "PUT /v1/attachments/{attachmentId}/content",
        "GET /v1/attachments/{attachmentId}/content",
    ];

    /// <summary>The upload URL a reservation hands back. One definition, used by both sides.</summary>
    public static string UploadUrlFor(string attachmentId) =>
        $"/v1/attachments/{attachmentId}/content";

    public static IEndpointRouteBuilder MapAttachmentApi(this IEndpointRouteBuilder app)
    {
        app.MapPut("/v1/attachments/{attachmentId}/content", UploadAsync)
            .RequireRateLimiting(Configuration.ConversationRateLimits.PolicyName);

        app.MapGet("/v1/attachments/{attachmentId}/content", DownloadAsync)
            .RequireRateLimiting(Configuration.ConversationRateLimits.PolicyName);

        return app;
    }

    // ── upload: the capability, and ONLY the capability ──────────────────────

    /// <summary>
    /// Accept the bytes for one reservation, verify them, and seal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Single-use.</b> The first successful PUT seals the row; a second finds it no longer
    /// <c>reserved</c> and gets <c>409</c>, with the sealed bytes untouched.
    /// </para>
    /// <para>
    /// Verification is not acceptance of a claim: the byte count must equal the reservation, the
    /// digest must equal the reserved digest, the container magic must be one of the four accepted
    /// types, and the header-declared pixel count must be within bounds. Any failure discards the
    /// bytes, marks the row <c>failed</c>, and returns <c>422</c> — the id is never usable again.
    /// </para>
    /// <para>
    /// ⚠️ No <see cref="AuthService"/> parameter. That absence is the guarantee; adding one would let
    /// a session bearer satisfy this route.
    /// </para>
    /// </remarks>
    private static async Task<IResult> UploadAsync(
        HttpContext http, string attachmentId, IConversationStore store, AttachmentStore files,
        ILoggerFactory loggers, CancellationToken ct)
    {
        // Rejected before a row is looked up. A ULID is the only thing that can name a file here, and
        // the check is the one standing between a client string and a filesystem path.
        if (!AttachmentStore.IsSafeId(attachmentId)) return NotFound();

        var presented = BearerOf(http);
        if (presented.Length == 0) return NotFound();

        var row = await store.GetAttachmentAsync(attachmentId, ct);

        // One answer for every negative case: unknown id, wrong credential, already sealed, failed,
        // or past the upload window. A caller learns nothing from which it got.
        if (row is null || row.State != AttachmentState.Reserved) return NotFound();

        if (DateTimeOffset.UtcNow - row.CreatedAt > ProtocolLimits.AttachmentUploadWindow)
            return NotFound();

        // SHA-256 and not the Argon2id the auth routes use, deliberately. This is a 256-bit uniform
        // random value with no guessable structure, where a fast digest is already beyond brute
        // force — and this is the one route that accepts megabytes, so a 19 MiB-per-call KDF in front
        // of it turns an upload into an amplifier (MUST NOT 4).
        //
        // Compared in constant time all the same: the cost argument is about the KDF, not about
        // leaking the comparison.
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(Convert.ToHexStringLower(SHA256.HashData(presented))),
                Encoding.ASCII.GetBytes(row.UploadTokenSha256)))
        {
            return NotFound();
        }

        var write = await files.WriteAsync(attachmentId, http.Request.Body, row.ByteSize, ct);

        if (write.VolumeUnavailable)
        {
            // 507, not 503 and not 500. The volume is full or unwritable, which is an attachment
            // condition and nothing else: text submissions keep being accepted and `/ready` does not
            // go red (MUST NOT 17).
            await store.FailAttachmentAsync(attachmentId, ct);
            ConversationMetrics.AttachmentSeal.Add(
                1, new KeyValuePair<string, object?>("result", "volume_unavailable"));

            loggers.CreateLogger(typeof(AttachmentEndpoints))
                .LogWarning("attachment volume refused a write; uploads are degraded");

            return Error(ProtocolErrorCode.Internal, StatusCodes.Status507InsufficientStorage);
        }

        if (write.Overflow) return await Reject(store, attachmentId, "overflow", ct);

        if (write.ByteSize != row.ByteSize)
            return await RejectAndDelete(store, files, attachmentId, "size_mismatch", ct);

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(write.Sha256), Encoding.ASCII.GetBytes(row.Sha256)))
        {
            return await RejectAndDelete(store, files, attachmentId, "digest_mismatch", ct);
        }

        var sniffed = ImageSniffer.Sniff(write.Prefix);

        // The DECLARED type is not what is recorded, and a declared/sniffed disagreement fails the
        // seal outright. Serving a client-declared type is how an uploaded HTML file becomes stored
        // XSS in the one client that renders it (MUST NOT 8).
        if (sniffed.ContentType is null || !string.Equals(sniffed.ContentType, row.ContentType, StringComparison.Ordinal))
            return await RejectAndDelete(store, files, attachmentId, "type_mismatch", ct);

        // PNG, GIF and WebP declare their dimensions in the fixed prefix. JPEG does not — its SOF
        // marker sits past any prefix worth buffering — so it is read here by seeking the written
        // file's segment chain. Without this the 50 MP bound would never fire for the one type the
        // client is instructed to produce.
        var pixels = sniffed.Pixels ?? ReadJpegPixels(files, attachmentId, sniffed.ContentType);

        if (pixels is { } declared && declared > ProtocolLimits.MaxAttachmentPixels)
            return await RejectAndDelete(store, files, attachmentId, "too_many_pixels", ct);

        var sealed_ = await store.SealAttachmentAsync(new SealAttachmentRequest
        {
            AttachmentId = attachmentId,
            ByteSize = write.ByteSize,
            Sha256 = write.Sha256,
            SniffedContentType = sniffed.ContentType,
        }, ct);

        if (!sealed_)
        {
            // Lost a race with another PUT, or the window closed between the read and the update.
            // The winner's bytes are on disk under the same path and are correct, so this deletes
            // nothing — it only declines to claim the seal.
            return Error(ProtocolErrorCode.AttachmentNotFound, StatusCodes.Status409Conflict);
        }

        return Json(new SealAttachmentResponse
        {
            AttachmentId = attachmentId,
            ContentType = sniffed.ContentType,
            ByteSize = write.ByteSize,
        }, StatusCodes.Status201Created);
    }

    // ── download: the session bearer, and ONLY the session bearer ────────────

    /// <summary>
    /// Serve the bytes of a <c>sealed</c> or <c>bound</c> attachment to the principal who owns its
    /// conversation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No capability URL.</b> A second credential type is a second leak surface, and its only
    /// advantage — pasting the URL into an <c>&lt;img src&gt;</c> without a header — is worthless to a
    /// native client that fetches with an authenticated request anyway (MUST NOT 9).
    /// </para>
    /// <para>
    /// The response headers are load-bearing: the sniffed type, <c>nosniff</c>, an attachment
    /// disposition, no store, and a CSP that permits nothing. Together they mean a byte sequence that
    /// somehow got past the seal still cannot execute in a browser.
    /// </para>
    /// <para>
    /// ⚠️ No <c>upload_token_sha256</c> read anywhere in this method. That absence is the other half
    /// of the D6 guarantee.
    /// </para>
    /// </remarks>
    private static async Task<IResult> DownloadAsync(
        HttpContext http, string attachmentId, AuthService auth, IConversationStore store,
        AttachmentStore files, CancellationToken ct)
    {
        var caller = await NorthEndpoints.AuthenticateAsync(http, auth, ct);
        if (caller is null) return Error(ProtocolErrorCode.Unauthorized, StatusCodes.Status401Unauthorized);

        if (!AttachmentStore.IsSafeId(attachmentId)) return NotFound();

        // Scoped to the caller's own conversations by a JOIN predicate, so a foreign attachment and a
        // nonexistent one are the same answer (AC-17).
        var row = await store.GetAttachmentForPrincipalAsync(attachmentId, caller.PrincipalId, ct);

        // A reserved or failed row is not fetchable, and reports the same not-found. Only sealed and
        // bound rows have bytes worth talking about.
        if (row is null || row.State is not (AttachmentState.Sealed or AttachmentState.Bound))
        {
            ConversationMetrics.AttachmentFetch.Add(
                1, new KeyValuePair<string, object?>("result", "not_found"));
            return NotFound();
        }

        var bytes = files.OpenRead(attachmentId);

        if (bytes is null)
        {
            // The row is authoritative and the volume disagrees with it. A distinct, permanent state
            // the client renders as "image no longer available" — never a 500, never an empty 200
            // (MUST NOT 11).
            ConversationMetrics.AttachmentFetch.Add(
                1, new KeyValuePair<string, object?>("result", "gone"));
            return Error(ProtocolErrorCode.AttachmentGone, StatusCodes.Status410Gone);
        }

        ConversationMetrics.AttachmentFetch.Add(
            1, new KeyValuePair<string, object?>("result", "served"));

        var headers = http.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Content-Disposition"] = "attachment";
        headers["Cache-Control"] = "private, no-store";
        headers["Content-Security-Policy"] = "default-src 'none'; sandbox";

        // The SNIFFED type from the row, which is the only type this system ever recorded.
        return Results.Stream(bytes, row.ContentType);
    }

    // ── shared ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The JPEG dimension read, on the file that was just written.
    /// </summary>
    /// <remarks>
    /// Null for every other type, and null when the chain is not walkable — a truncated file, or a
    /// <c>SOS</c> reached without a <c>SOF</c>. A null is "no pixel count available", not "within the
    /// bound": the attachment is then held by the 8 MiB byte cap alone, which the limits table says
    /// explicitly rather than leaving it to be discovered.
    /// </remarks>
    private static long? ReadJpegPixels(
        AttachmentStore files, string attachmentId, string? contentType)
    {
        if (contentType != "image/jpeg") return null;

        using var bytes = files.OpenRead(attachmentId);
        return bytes is null ? null : ImageSniffer.TryReadJpegPixels(bytes);
    }

    private static async Task<IResult> Reject(
        IConversationStore store, string attachmentId, string reason, CancellationToken ct)
    {
        await store.FailAttachmentAsync(attachmentId, ct);
        ConversationMetrics.AttachmentSeal.Add(
            1, new KeyValuePair<string, object?>("result", reason));

        return Error(ProtocolErrorCode.AttachmentNotFound, StatusCodes.Status422UnprocessableEntity);
    }

    /// <summary>Same as <see cref="Reject"/>, plus removing bytes that did reach the volume.</summary>
    /// <remarks>
    /// <b>No file is retained on a failed verification</b> (AC-11). The row survives briefly as
    /// <c>failed</c> so a retry gets a consistent answer, and the sweep collects it.
    /// </remarks>
    private static async Task<IResult> RejectAndDelete(
        IConversationStore store, AttachmentStore files, string attachmentId, string reason,
        CancellationToken ct)
    {
        files.Delete(attachmentId);
        return await Reject(store, attachmentId, reason, ct);
    }

    private static byte[] BearerOf(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";

        return header.StartsWith(scheme, StringComparison.Ordinal)
            ? Encoding.UTF8.GetBytes(header[scheme.Length..])
            : [];
    }

    private static IResult NotFound() =>
        Error(ProtocolErrorCode.AttachmentNotFound, StatusCodes.Status404NotFound);

    private static IResult Error(ProtocolErrorCode code, int status) =>
        Json(ErrorResponse.For(code), status);

    private static IResult Json<T>(T body, int status) =>
        Results.Json(body, FleetProtocolJson.Options, statusCode: status);
}
