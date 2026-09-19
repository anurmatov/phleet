using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fleet.Conversations.Contracts;
using Fleet.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fleet.Comms.Tests;

/// <summary>
/// The three attachment routes as a CLIENT sees them (#308).
/// </summary>
/// <remarks>
/// <para>
/// These grade the north surface, not a client app. Every D5 rendering state is a distinct server
/// response, and that is what is asserted here — the phone's composer states and placeholders are
/// the companion issue's, and nothing in this file opens that repository.
/// </para>
/// <para>
/// The store is the in-process substitute. It answers "which status does this request produce" and
/// "what does the body look like"; it makes no claim about MySQL, where the transactional behaviour
/// is exercised against a real database that fails rather than skips.
/// </para>
/// </remarks>
public sealed class AttachmentRouteTests : IAsyncDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"fleet-attach-{Guid.NewGuid():N}");

    // ── reserve ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reserve_IssuesACapabilityAndAnUploadUrl()
    {
        var store = new FakeConversationStore();
        await using var host = await StartAsync(store);
        var (_, _, token) = await host.EnrolledDeviceAsync();

        var response = await ReserveAsync(host, token, store.ConversationId);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await ReadAsync(response);
        Assert.Equal(store.NextReserve.AttachmentId, body.GetProperty("attachmentId").GetString());
        Assert.Equal(
            $"/v1/attachments/{store.NextReserve.AttachmentId}/content",
            body.GetProperty("uploadUrl").GetString());
        Assert.Equal(ProtocolLimits.MaxAttachmentBytes, body.GetProperty("maxBytes").GetInt64());

        // The capability is a real secret, not a derivation of the id.
        var uploadToken = body.GetProperty("uploadToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(uploadToken));
        Assert.DoesNotContain(store.NextReserve.AttachmentId!, uploadToken!, StringComparison.Ordinal);
    }

    /// <summary>
    /// AC-20: the store is handed a DIGEST, and the plaintext capability appears in no request the
    /// store ever sees.
    /// </summary>
    [Fact]
    public async Task Reserve_StoresOnlyTheDigestOfTheCapability()
    {
        var store = new FakeConversationStore();
        await using var host = await StartAsync(store);
        var (_, _, token) = await host.EnrolledDeviceAsync();

        var response = await ReserveAsync(host, token, store.ConversationId);
        var uploadToken = (await ReadAsync(response)).GetProperty("uploadToken").GetString()!;

        var reserved = Assert.Single(store.Reservations);

        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(uploadToken))),
            reserved.UploadTokenSha256);

        // The plaintext is nowhere in what the store was given.
        Assert.DoesNotContain(uploadToken, JsonSerializer.Serialize(reserved), StringComparison.Ordinal);
    }

    /// <summary>AC-14 — a HEIC never gets to spend a byte of the owner's mobile data.</summary>
    [Fact]
    public async Task Reserve_RefusesHeicBeforeAnyByteMoves()
    {
        var store = new FakeConversationStore
        {
            NextReserve = new ReserveAttachmentResult { Outcome = ReserveOutcome.UnsupportedType },
        };

        await using var host = await StartAsync(store);
        var (_, _, token) = await host.EnrolledDeviceAsync();

        var response = await ReserveAsync(
            host, token, store.ConversationId, contentType: "image/heic");

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal("unsupported_media_type", await CodeAsync(response));
    }

    [Fact]
    public async Task Reserve_RefusesOverTheByteCap()
    {
        var store = new FakeConversationStore
        {
            NextReserve = new ReserveAttachmentResult { Outcome = ReserveOutcome.TooLarge },
        };

        await using var host = await StartAsync(store);
        var (_, _, token) = await host.EnrolledDeviceAsync();

        var response = await ReserveAsync(
            host, token, store.ConversationId, byteSize: ProtocolLimits.MaxAttachmentBytes + 1);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("payload_too_large", await CodeAsync(response));
    }

    [Fact]
    public async Task Reserve_RefusesWhenTheLiveByteCapIsReached()
    {
        var store = new FakeConversationStore
        {
            NextReserve = new ReserveAttachmentResult { Outcome = ReserveOutcome.QuotaExceeded },
        };

        await using var host = await StartAsync(store);
        var (_, _, token) = await host.EnrolledDeviceAsync();

        var response = await ReserveAsync(host, token, store.ConversationId);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("attachment_limit", await CodeAsync(response));
    }

    // ── upload ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Upload_SealsAndThenServesTheSameBytes()
    {
        var store = new FakeConversationStore();
        await using var host = await StartAsync(store);
        var (_, _, token) = await host.EnrolledDeviceAsync();

        var png = Png(2, 2);
        var (id, uploadToken) = await ReserveFor(host, store, png, token);

        var upload = await UploadAsync(host, id, uploadToken, png);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);

        var sealed_ = await ReadAsync(upload);
        Assert.Equal("image/png", sealed_.GetProperty("contentType").GetString());
        Assert.Equal(png.Length, sealed_.GetProperty("byteSize").GetInt64());

        // AC-2: byte-identical, verified by digest.
        var download = await DownloadAsync(host, id, token);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);

        var served = await download.Content.ReadAsByteArrayAsync();
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(png)),
            Convert.ToHexStringLower(SHA256.HashData(served)));
    }

    /// <summary>AC-11 — a digest that does not match retains no file and burns the id.</summary>
    [Fact]
    public async Task Upload_WithAWrongDigest_FailsTheRowAndRetainsNoFile()
    {
        var store = new FakeConversationStore();
        await using var host = await StartAsync(store);
        var (_, _, token) = await host.EnrolledDeviceAsync();

        var png = Png(2, 2);
        var (id, uploadToken) = await ReserveFor(host, store, png, token);

        // Same declared length, different bytes.
        var tampered = (byte[])png.Clone();
        tampered[^1] ^= 0xFF;

        var upload = await UploadAsync(host, id, uploadToken, tampered);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, upload.StatusCode);
        Assert.Equal("attachment_not_found", await CodeAsync(upload));
        Assert.Contains(id, store.Failed);
        Assert.False(File.Exists(PathFor(id)));
    }

    /// <summary>AC-12 — HTML wearing a .jpg never becomes a stored attachment.</summary>
    [Fact]
    public async Task Upload_WhoseMagicBytesAreHtml_FailsTheSeal()
    {
        var store = new FakeConversationStore();
        await using var host = await StartAsync(store);
        var (_, _, token) = await host.EnrolledDeviceAsync();

        var html = Encoding.UTF8.GetBytes("<!DOCTYPE html><script>alert(1)</script>");
        var (id, uploadToken) = await ReserveFor(host, store, html, token, declaredType: "image/jpeg");

        var upload = await UploadAsync(host, id, uploadToken, html);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, upload.StatusCode);
        Assert.Contains(id, store.Failed);
        // Nothing was sealed at all, so in particular nothing was sealed as text/html — which is the
        // shape that would make an uploaded HTML file executable in a browser.
        Assert.Empty(store.Seals);
        Assert.False(File.Exists(PathFor(id)));
    }

    /// <summary>
    /// AC-13, the pixel bound — and specifically for JPEG, the type the client is instructed to
    /// produce.
    /// </summary>
    /// <remarks>
    /// The positive control for a gap an earlier revision had: the sniffer reported no pixel count
    /// for JPEG, so the 50 MP bound never fired for it and every test stayed green. This drives the
    /// real route, with a frame header sitting past a 60 KiB EXIF segment.
    /// </remarks>
    [Fact]
    public async Task Upload_AJpegOverThePixelBound_FailsTheSeal()
    {
        var store = new FakeConversationStore();
        await using var host = await StartAsync(store);
        var (_, _, token) = await host.EnrolledDeviceAsync();

        var jpeg = Jpeg(10_000, 5_001, leadingSegmentBytes: 60 * 1024);
        var (id, uploadToken) = await ReserveFor(host, store, jpeg, token, "image/jpeg");

        var upload = await UploadAsync(host, id, uploadToken, jpeg);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, upload.StatusCode);
        Assert.Contains(id, store.Failed);
        Assert.False(File.Exists(PathFor(id)));
    }

    /// <summary>The boundary itself passes, so the check is a bound rather than a blanket refusal.</summary>
    [Fact]
    public async Task Upload_AJpegExactlyAtThePixelBound_Seals()
    {
        var store = new FakeConversationStore();
        await using var host = await StartAsync(store);
        var (_, _, token) = await host.EnrolledDeviceAsync();

        var jpeg = Jpeg(10_000, 5_000, leadingSegmentBytes: 60 * 1024);
        var (id, uploadToken) = await ReserveFor(host, store, jpeg, token, "image/jpeg");

        var upload = await UploadAsync(host, id, uploadToken, jpeg);

        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        Assert.Equal("image/jpeg", (await ReadAsync(upload)).GetProperty("contentType").GetString());
    }

    /// <summary>AC-18 — the capability is single-use.</summary>
    [Fact]
    public async Task Upload_ASecondTime_IsRefusedAndLeavesTheSealedBytes()
    {
        var store = new FakeConversationStore();
        await using var host = await StartAsync(store);
        var (_, _, token) = await host.EnrolledDeviceAsync();

        var png = Png(2, 2);
        var (id, uploadToken) = await ReserveFor(host, store, png, token);

        Assert.Equal(HttpStatusCode.Created, (await UploadAsync(host, id, uploadToken, png)).StatusCode);

        // The row is now sealed — by the seal above, not by the test reaching in — so the second
        // PUT finds nothing reserved.
        Assert.Equal(AttachmentState.Sealed, store.Attachments[id].State);

        var second = await UploadAsync(host, id, uploadToken, Png(4, 4));
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);

        var served = await (await DownloadAsync(host, id, token)).Content.ReadAsByteArrayAsync();
        Assert.Equal(png, served);
    }

    /// <summary>AC-19, first direction — a session bearer on the PUT is a 404, with no oracle.</summary>
    [Fact]
    public async Task Upload_WithASessionBearer_IsNotFound()
    {
        var store = new FakeConversationStore();
        await using var host = await StartAsync(store);
        var (_, _, token) = await host.EnrolledDeviceAsync();

        var png = Png(2, 2);
        var (id, _) = await ReserveFor(host, store, png, token);

        var response = await UploadAsync(host, id, token, png);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("attachment_not_found", await CodeAsync(response));

        // Byte-identical to an id that never existed — so presenting a session bearer tells the
        // caller nothing about whether the attachment is real.
        var unknown = await UploadAsync(host, Id("NOSUCH"), token, png);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(await CodeAsync(response), await CodeAsync(unknown));
    }

    /// <summary>AC-19, second direction — an upload token on the GET is an ordinary 401.</summary>
    [Fact]
    public async Task Download_WithAnUploadToken_IsUnauthorized()
    {
        var store = new FakeConversationStore();
        await using var host = await StartAsync(store);
        var (_, _, token) = await host.EnrolledDeviceAsync();

        var png = Png(2, 2);
        var (id, uploadToken) = await ReserveFor(host, store, png, token);
        await UploadAsync(host, id, uploadToken, png);

        var response = await DownloadAsync(host, id, uploadToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("unauthorized", await CodeAsync(response));
    }

    // ── download ─────────────────────────────────────────────────────────────

    /// <summary>AC-17 — a foreign attachment is byte-identical to one that never existed.</summary>
    [Fact]
    public async Task Download_ByAForeignPrincipal_IsTheSameNotFoundAsAnUnknownId()
    {
        var store = new FakeConversationStore();
        await using var host = await StartAsync(store);
        var (_, _, token) = await host.EnrolledDeviceAsync();

        var png = Png(2, 2);
        var (id, uploadToken) = await ReserveFor(host, store, png, token);
        await UploadAsync(host, id, uploadToken, png);

        // The row now belongs to someone else.
        store.OwnerPrincipalId = "p_someone_else";

        var foreign = await DownloadAsync(host, id, token);
        var unknown = await DownloadAsync(host, Id("NOSUCH"), token);

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(unknown.StatusCode, foreign.StatusCode);
        Assert.Equal(await CodeAsync(unknown), await CodeAsync(foreign));
    }

    /// <summary>AC-23 — a row whose file vanished is `410`, never a 500 and never an empty 200.</summary>
    [Fact]
    public async Task Download_WhenTheFileIsGone_IsGoneAndNotAnEmptySuccess()
    {
        var store = new FakeConversationStore();
        await using var host = await StartAsync(store);
        var (_, _, token) = await host.EnrolledDeviceAsync();

        var png = Png(2, 2);
        var (id, uploadToken) = await ReserveFor(host, store, png, token);
        await UploadAsync(host, id, uploadToken, png);

        File.Delete(PathFor(id));

        var response = await DownloadAsync(host, id, token);

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.Equal("attachment_gone", await CodeAsync(response));
    }

    /// <summary>
    /// AC-25 — the four client-visible outcomes are four DISTINCT status+code pairs, so a client can
    /// map them without guessing.
    /// </summary>
    [Fact]
    public async Task TheFourFailureStates_NeverShareAStatusAndCodePair()
    {
        var store = new FakeConversationStore();
        await using var host = await StartAsync(store);
        var (_, _, token) = await host.EnrolledDeviceAsync();

        var png = Png(2, 2);
        var (id, uploadToken) = await ReserveFor(host, store, png, token);
        await UploadAsync(host, id, uploadToken, png);

        // permanently gone
        File.Delete(PathFor(id));
        var gone = await DownloadAsync(host, id, token);

        // never existed
        var missing = await DownloadAsync(host, Id("NOSUCH"), token);

        // unauthenticated
        var unauthorized = await DownloadAsync(host, id, bearer: null);

        var pairs = new[]
        {
            ((int)gone.StatusCode, await CodeAsync(gone)),
            ((int)missing.StatusCode, await CodeAsync(missing)),
            ((int)unauthorized.StatusCode, await CodeAsync(unauthorized)),
        };

        Assert.Equal(pairs.Length, pairs.Distinct().Count());

        Assert.Equal((410, "attachment_gone"), pairs[0]);
        Assert.Equal((404, "attachment_not_found"), pairs[1]);
        Assert.Equal((401, "unauthorized"), pairs[2]);
    }

    /// <summary>The serving rules that stop an uploaded file becoming stored XSS.</summary>
    [Fact]
    public async Task Download_CarriesTheSniffedTypeAndTheHardeningHeaders()
    {
        var store = new FakeConversationStore();
        await using var host = await StartAsync(store);
        var (_, _, token) = await host.EnrolledDeviceAsync();

        var png = Png(2, 2);

        // DECLARED as png and actually png — the point here is which type is SERVED, and it comes
        // from the row rather than from anything the client said at submit time.
        var (id, uploadToken) = await ReserveFor(host, store, png, token);
        await UploadAsync(host, id, uploadToken, png);

        var response = await DownloadAsync(host, id, token);

        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Equal("attachment", Header(response, "Content-Disposition"));
        Assert.Equal("default-src 'none'; sandbox", Header(response, "Content-Security-Policy"));
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? "");
    }

    // ── submit-side bounds, refused before any transaction ───────────────────

    /// <summary>AC-6 — a list naming the same id twice is malformed, not a missing attachment.</summary>
    [Fact]
    public async Task Submit_WithADuplicateId_IsRefusedBeforeTheStoreIsCalled()
    {
        var store = new FakeConversationStore();
        await using var host = await StartAsync(store);
        var (_, _, token) = await host.EnrolledDeviceAsync();

        var id = Id("A1");
        var response = await SubmitAsync(host, token, store.ConversationId, [id, id]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("attachment_limit", await CodeAsync(response));
        Assert.Empty(store.Accepted);
    }

    [Fact]
    public async Task Submit_WithMoreThanFourAttachments_IsRefused()
    {
        var store = new FakeConversationStore();
        await using var host = await StartAsync(store);
        var (_, _, token) = await host.EnrolledDeviceAsync();

        var ids = Enumerable.Range(1, ProtocolLimits.MaxAttachmentsPerSubmission + 1)
            .Select(n => Id($"B{n}"))
            .ToArray();

        var response = await SubmitAsync(host, token, store.ConversationId, ids);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("attachment_limit", await CodeAsync(response));
        Assert.Empty(store.Accepted);
    }

    /// <summary>Exactly four is the boundary, and it passes.</summary>
    [Fact]
    public async Task Submit_WithExactlyFourAttachments_ReachesTheStore()
    {
        var store = new FakeConversationStore();
        await using var host = await StartAsync(store);
        var (_, _, token) = await host.EnrolledDeviceAsync();

        var ids = Enumerable.Range(1, ProtocolLimits.MaxAttachmentsPerSubmission)
            .Select(n => Id($"B{n}"))
            .ToArray();

        var response = await SubmitAsync(host, token, store.ConversationId, ids);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(ids, Assert.Single(store.Accepted).AttachmentIds);
    }

    /// <summary>AC-26 — an explicit empty array behaves exactly like omitting the key.</summary>
    [Fact]
    public async Task Submit_WithAnExplicitEmptyArray_IsTheTextOnlyPath()
    {
        var store = new FakeConversationStore();
        await using var host = await StartAsync(store);
        var (_, _, token) = await host.EnrolledDeviceAsync();

        var response = await SubmitAsync(host, token, store.ConversationId, []);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Null(Assert.Single(store.Accepted).AttachmentIds);
    }

    /// <summary>
    /// AC-10 — the fingerprint covers the ordered id list, so the same key with a different photo
    /// cannot replay the first submission's result.
    /// </summary>
    [Fact]
    public async Task Submit_TheFingerprintChangesWithTheAttachmentIds()
    {
        var store = new FakeConversationStore();
        await using var host = await StartAsync(store);
        var (_, _, token) = await host.EnrolledDeviceAsync();

        await SubmitAsync(host, token, store.ConversationId, [Id("A1")]);
        await SubmitAsync(host, token, store.ConversationId, [Id("A2")]);
        await SubmitAsync(host, token, store.ConversationId, []);

        var fingerprints = store.Accepted.Select(a => a.PayloadFingerprint).ToList();

        Assert.Equal(3, fingerprints.Distinct().Count());
    }

    /// <summary>
    /// AC-4 — a text-only submission's fingerprint is unchanged by #308, so an in-flight idempotency
    /// key survives the deployment.
    /// </summary>
    [Fact]
    public async Task Submit_TextOnly_KeepsThePreChangeFingerprint()
    {
        var store = new FakeConversationStore();
        await using var host = await StartAsync(store);
        var (_, _, token) = await host.EnrolledDeviceAsync();

        await SubmitAsync(host, token, store.ConversationId, attachmentIds: null);

        Assert.Equal(
            Fleet.Conversations.PayloadFingerprint.Compute("hello", null, store.ConversationId),
            Assert.Single(store.Accepted).PayloadFingerprint);
    }

    // ── feature off ──────────────────────────────────────────────────────────

    /// <summary>
    /// AC-27 — with no attachment root the three routes are not mapped at all, and a non-empty
    /// array is refused exactly as it was before this change.
    /// </summary>
    [Fact]
    public async Task WithNoAttachmentRoot_TheRoutesAreUnmappedAndTheArrayIsRefused()
    {
        var store = new FakeConversationStore();
        await using var host = await NorthTestHost.StartAsync(conversations: store);
        var (_, _, token) = await host.EnrolledDeviceAsync();

        // Router 404s, from an absent route rather than from a handler that decided to refuse.
        Assert.Equal(HttpStatusCode.NotFound, (await ReserveAsync(host, token, store.ConversationId)).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await DownloadAsync(host, Id("A1"), token)).StatusCode);

        var refused = await SubmitAsync(
            host, token, store.ConversationId, [Id("A1")]);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("unsupported_attachments", await CodeAsync(refused));
        Assert.Empty(store.Accepted);

        // And a text-only submission is completely unaffected.
        Assert.Equal(
            HttpStatusCode.Created,
            (await SubmitAsync(host, token, store.ConversationId, null)).StatusCode);
    }

    /// <summary>
    /// With a root configured the table is the published ten PLUS exactly the three attachment
    /// routes — no more, and no south route.
    /// </summary>
    /// <remarks>
    /// Asserted as an exact set rather than as containment. A route that appeared here by accident —
    /// a south store endpoint, say — would let a device write arbitrary events into the durable log,
    /// and a containment assertion would never notice it.
    /// </remarks>
    [Fact]
    public async Task WithAnAttachmentRoot_TheTableIsTheTenPublishedRoutesPlusExactlyThree()
    {
        await using var host = await StartAsync(new FakeConversationStore());

        var registered = host.Services
            .GetServices<Microsoft.AspNetCore.Routing.EndpointDataSource>()
            .SelectMany(s => s.Endpoints)
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Select(e =>
            {
                var methods = e.Metadata
                    .GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods ?? [];
                return $"{string.Join(",", methods)} /{e.RoutePattern.RawText?.TrimStart('/')}";
            })
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        var expected = Fleet.Comms.Routes.NorthEndpoints.Routes
            .Concat(Fleet.Comms.Routes.ConversationEndpoints.Routes)
            .Concat(Fleet.Comms.Routes.ConversationEndpoints.AttachmentRoutes)
            .Concat(Fleet.Comms.Routes.AttachmentEndpoints.Routes)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(expected, registered);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private Task<NorthTestHost> StartAsync(FakeConversationStore store) =>
        NorthTestHost.StartAsync(conversations: store, attachmentRoot: _root);

    private static Task<HttpResponseMessage> ReserveAsync(
        NorthTestHost host, string bearer, string conversationId,
        string contentType = "image/png", long byteSize = 4, string? sha256 = null)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post, $"/v1/conversations/{conversationId}/attachments")
        {
            Content = JsonContent.Create(new
            {
                protocol = ProtocolVersion.Current,
                kind = "image",
                contentType,
                byteSize,
                sha256 = sha256 ?? new string('a', 64),
                fileName = "photo.png",
            }, options: FleetProtocolJson.Options),
        };

        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearer}");
        return host.Client.SendAsync(request);
    }

    /// <summary>Reserve a slot sized and digested for the bytes a test is about to upload.</summary>
    private async Task<(string Id, string UploadToken)> ReserveFor(
        NorthTestHost host, FakeConversationStore store, byte[] bytes, string bearer,
        string declaredType = "image/png")
    {
        var id = Id($"R{store.Attachments.Count}");
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));

        store.NextReserve = new ReserveAttachmentResult
        {
            Outcome = ReserveOutcome.Reserved,
            AttachmentId = id,
            ExpiresAt = DateTimeOffset.UtcNow + ProtocolLimits.AttachmentUploadWindow,
        };

        // The CALLER's bearer, not a fresh enrollment: the owner may hold exactly one active
        // device, so enrolling again here would revoke the token every other assertion uses.
        var response = await ReserveAsync(
            host, bearer, store.ConversationId, declaredType, bytes.Length, sha256);

        var uploadToken = (await ReadAsync(response)).GetProperty("uploadToken").GetString()!;

        // The fake does not keep rows on its own, so the reservation is recorded here — the real
        // store writes it inside the reservation transaction.
        store.Attachments[id] = FakeConversationStore.Attachment(
            id, AttachmentState.Reserved, declaredType, bytes.Length,
            sha256: sha256,
            uploadTokenSha256: Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(uploadToken))));

        return (id, uploadToken);
    }

    private static Task<HttpResponseMessage> UploadAsync(
        NorthTestHost host, string attachmentId, string bearer, byte[] bytes)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Put, $"/v1/attachments/{attachmentId}/content")
        {
            Content = new ByteArrayContent(bytes),
        };

        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearer}");
        return host.Client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> DownloadAsync(
        NorthTestHost host, string attachmentId, string? bearer)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get, $"/v1/attachments/{attachmentId}/content");

        if (bearer is not null)
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearer}");

        return host.Client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> SubmitAsync(
        NorthTestHost host, string bearer, string conversationId, IReadOnlyList<string>? attachmentIds)
    {
        object body = attachmentIds is null
            ? new
            {
                protocol = ProtocolVersion.Current,
                type = "create",
                submissionId = Ulid.NewUlid(),
                text = "hello",
            }
            : new
            {
                protocol = ProtocolVersion.Current,
                type = "create",
                submissionId = Ulid.NewUlid(),
                text = "hello",
                attachments = attachmentIds
                    .Select(id => new { attachmentId = id, kind = "image" })
                    .ToArray(),
            };

        var request = new HttpRequestMessage(
            HttpMethod.Post, $"/v1/conversations/{conversationId}/submissions")
        {
            Content = JsonContent.Create(body, options: FleetProtocolJson.Options),
        };

        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearer}");
        return host.Client.SendAsync(request);
    }

    /// <summary>
    /// A well-formed ULID for a test.
    /// </summary>
    /// <remarks>
    /// 26 characters of Crockford base32 — and the length matters rather than being cosmetic:
    /// <c>AttachmentStore.IsSafeId</c> is the check standing between a client string and a
    /// filesystem path, so a 25-character id is refused as not-found and a test built on one would
    /// be asserting the wrong refusal.
    /// </remarks>
    private static string Id(string suffix) =>
        ("01JATTACHMENT" + suffix.ToUpperInvariant()).PadRight(26, '0');

    private string PathFor(string attachmentId) =>
        Path.Combine(_root, attachmentId[..2], attachmentId[2..4], attachmentId);

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await ReadAsync(response)).GetProperty("code").GetString();

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Content.Headers.TryGetValues(name, out var content) ? content.FirstOrDefault()
        : response.Headers.TryGetValues(name, out var headers) ? headers.FirstOrDefault()
        : null;

    /// <summary>
    /// A structurally real JPEG whose frame header sits past a leading segment, so the route has to
    /// walk the chain to find it.
    /// </summary>
    private static byte[] Jpeg(int width, int height, int leadingSegmentBytes)
    {
        var bytes = new List<byte> { 0xFF, 0xD8 };

        var length = leadingSegmentBytes + 2;
        bytes.AddRange([0xFF, 0xE1, (byte)(length >> 8), (byte)(length & 0xFF)]);
        bytes.AddRange(new byte[leadingSegmentBytes]);

        bytes.AddRange(
        [
            0xFF, 0xC0,
            0x00, 0x0B,
            0x08,
            (byte)(height >> 8), (byte)(height & 0xFF),
            (byte)(width >> 8), (byte)(width & 0xFF),
            0x01, 0x00,
        ]);

        return bytes.ToArray();
    }

    /// <summary>A real PNG header: the 8-byte signature plus an IHDR carrying the dimensions.</summary>
    private static byte[] Png(uint width, uint height)
    {
        var bytes = new byte[33];
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes);

        // IHDR length and type.
        bytes[11] = 13;
        "IHDR"u8.CopyTo(bytes.AsSpan(12));

        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20, 4), height);

        return bytes;
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }
}
