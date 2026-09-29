using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Fleet.Conversations.Contracts;

namespace Fleet.Journal.Client.Tests;

/// <summary>
/// The agent's uploader (#388): the order it proves bytes in, the reason it declines with, and the
/// states it refuses to guess about.
/// </summary>
/// <remarks>
/// These run against a fake transport, so the Bot API is provably not called: the handler here is
/// the only network the test has, and every assertion about "no upload calls" is an assertion about
/// that handler's request log.
/// </remarks>
public sealed class JournalMediaUploaderTests
{
    private static readonly byte[] Payload = "the bytes the journal must keep"u8.ToArray();
    private static readonly string Digest = Convert.ToHexStringLower(SHA256.HashData(Payload));

    private static readonly string UploadId = Fleet.Protocol.Ulid.NewUlid().ToString();

    [Fact]
    public async Task A_declared_upload_is_put_and_returns_the_upload_id_and_digest()
    {
        using var rig = new MediaRig();

        var upload = await rig.Uploader.UploadAsync(null, Payload, Payload.LongLength, "image/jpeg");

        Assert.True(upload.IsUploaded);
        Assert.Equal(UploadId, upload.UploadId);
        Assert.Equal(Digest, upload.Sha256);

        Assert.Equal(2, rig.Media.Requests.Count);
        Assert.Equal("POST", rig.Media.Requests[0].Method);
        Assert.Equal("PUT", rig.Media.Requests[1].Method);
        Assert.Equal($"/journal/v1/uploads/{UploadId}", rig.Media.Requests[1].Path);
    }

    /// <summary>
    /// The declaration is the digest of these exact bytes, the exact length, and the mime type the
    /// attachment carries. A declaration that disagreed with the PUT would be a store that hashed
    /// something and compared it to something else.
    /// </summary>
    [Fact]
    public async Task The_declaration_names_the_digest_the_size_and_the_mime_type()
    {
        using var rig = new MediaRig();

        await rig.Uploader.UploadAsync(null, Payload, Payload.LongLength, "audio/ogg");

        var declared = JsonNode.Parse(rig.Media.Requests[0].Body!)!.AsObject();
        Assert.Equal(Digest, declared["sha256"]!.GetValue<string>());
        Assert.Equal(Payload.LongLength, declared["byteSize"]!.GetValue<long>());
        Assert.Equal("audio/ogg", declared["mimeType"]!.GetValue<string>());
    }

    /// <summary>
    /// AC4: a file the Bot API will not hand over is declined before anything is read or sent, and
    /// the reason is the platform's limit rather than a generic failure.
    /// </summary>
    [Fact]
    public async Task Over_the_bot_api_cap_declines_before_any_request()
    {
        using var rig = new MediaRig();

        var upload = await rig.Uploader.UploadAsync(
            null, null, JournalMediaUploader.MaxObjectBytes + 1, "application/pdf");

        Assert.Equal(JournalMediaReason.OverBotApiLimit, upload.Reason);
        Assert.Empty(rig.Media.Requests);
    }

    [Fact]
    public async Task A_local_file_over_the_cap_declines_with_the_size_cap()
    {
        using var rig = new MediaRig();
        var file = Path.Combine(rig.Root, "big.bin");
        await File.WriteAllBytesAsync(file, new byte[4096]);

        // The platform declared nothing; the bytes on disk are what is over the limit the agent
        // enforces locally.
        var upload = await rig.Uploader.UploadAsync(
            file, null, null, "application/octet-stream", maxBytes: 1024);

        Assert.Equal(JournalMediaReason.OverSizeCap, upload.Reason);
        Assert.Empty(rig.Media.Requests);
    }

    /// <summary>
    /// A missing spool file is <c>source_expired</c>: the hardlink's source is gone. This is the
    /// state a redriven dead record reaches, and the record still journals.
    /// </summary>
    [Fact]
    public async Task An_absent_spool_file_declines_as_source_expired()
    {
        using var rig = new MediaRig();

        var upload = await rig.Uploader.UploadAsync(
            Path.Combine(rig.Root, "gone.jpg"), null, 10, "image/jpeg");

        Assert.Equal(JournalMediaReason.SourceExpired, upload.Reason);
        Assert.Empty(rig.Media.Requests);
    }

    [Fact]
    public async Task A_spool_file_is_hashed_from_disk_and_uploaded()
    {
        using var rig = new MediaRig();
        var file = Path.Combine(rig.Root, "photo.jpg");
        await File.WriteAllBytesAsync(file, Payload);

        var upload = await rig.Uploader.UploadAsync(file, null, Payload.LongLength, "image/jpeg");

        Assert.True(upload.IsUploaded);
        Assert.Equal(Digest, upload.Sha256);
        Assert.Equal(Payload, rig.Media.Requests[1].Bytes);
    }

    [Fact]
    public async Task Media_disabled_is_a_reason_and_not_a_retry()
    {
        using var rig = new MediaRig();
        rig.Media.Declare = () => MediaFake.Reply(HttpStatusCode.Conflict, "{\"error\":\"media_disabled\"}");

        var upload = await rig.Uploader.UploadAsync(null, Payload, Payload.LongLength, "image/jpeg");

        Assert.Equal(JournalMediaReason.MediaDisabled, upload.Reason);
        Assert.Single(rig.Media.Requests);
    }

    /// <summary>
    /// A 503 is NOT a reason. Turning an outage into <c>not_archived(media_disabled)</c> would
    /// permanently downgrade every attachment during a bucket restart, which is AC 3's whole point:
    /// the spool retries and the final state is one committed object and one message.
    /// </summary>
    [Fact]
    public async Task A_store_outage_throws_retryable_and_keeps_the_attachment_pending()
    {
        using var rig = new MediaRig();
        rig.Media.Declare = () => MediaFake.Reply(HttpStatusCode.ServiceUnavailable, "{\"error\":\"media_unavailable\"}");

        await Assert.ThrowsAsync<JournalMediaRetryableException>(
            () => rig.Uploader.UploadAsync(null, Payload, Payload.LongLength, "image/jpeg"));
    }

    [Fact]
    public async Task A_transport_failure_throws_retryable()
    {
        using var rig = new MediaRig();
        rig.Media.Declare = () => throw new HttpRequestException("connection refused");

        await Assert.ThrowsAsync<JournalMediaRetryableException>(
            () => rig.Uploader.UploadAsync(null, Payload, Payload.LongLength, "image/jpeg"));
    }

    /// <summary>
    /// The store hashed something different from what the agent hashed. Retrying re-sends the same
    /// bytes to the same bucket, so this is terminal — and the honest reason is that the bytes never
    /// made it, not that the store is off.
    /// </summary>
    [Fact]
    public async Task A_digest_mismatch_is_terminal_and_declines()
    {
        using var rig = new MediaRig();
        rig.Media.Put = () => MediaFake.Reply(HttpStatusCode.UnprocessableEntity, "{\"error\":\"sha256_mismatch\"}");

        var upload = await rig.Uploader.UploadAsync(null, Payload, Payload.LongLength, "image/jpeg");

        Assert.Equal(JournalMediaReason.DownloadFailed, upload.Reason);
        Assert.Equal(2, rig.Media.Requests.Count);
    }

    /// <summary>
    /// An object swept between the declaration and the PUT is retryable, because the next pass can
    /// declare a new one. Committing the message with a dead upload id would be a 409 loop instead.
    /// </summary>
    [Fact]
    public async Task A_swept_upload_before_the_put_is_retryable()
    {
        using var rig = new MediaRig();
        rig.Media.Put = () => MediaFake.Reply(HttpStatusCode.Conflict, "{\"error\":\"upload_incomplete\"}");

        await Assert.ThrowsAsync<JournalMediaRetryableException>(
            () => rig.Uploader.UploadAsync(null, Payload, Payload.LongLength, "image/jpeg"));
    }

    /// <summary>
    /// The token is on both calls and nowhere else. An upload id is not a secret but a token in a
    /// path, a query string or an exception message is a token in a log.
    /// </summary>
    [Fact]
    public async Task The_token_travels_only_in_the_authorization_header()
    {
        using var rig = new MediaRig();

        await rig.Uploader.UploadAsync(null, Payload, Payload.LongLength, "image/jpeg");

        Assert.All(rig.Media.Requests, r => Assert.Equal("Bearer " + Records.Token, r.Authorization));
        Assert.All(rig.Media.Requests, r => Assert.DoesNotContain(Records.Token, r.Path + r.Body));
    }

    // ── the decline vocabulary the record can carry ──────────────────────────

    [Theory]
    [InlineData(JournalMediaReason.MediaDisabled, "media_disabled")]
    [InlineData(JournalMediaReason.OverBotApiLimit, "over_bot_api_limit")]
    [InlineData(JournalMediaReason.OverSizeCap, "over_size_cap")]
    [InlineData(JournalMediaReason.UnsupportedKind, "unsupported_kind")]
    [InlineData(JournalMediaReason.DownloadFailed, "download_failed")]
    [InlineData(JournalMediaReason.SourceExpired, "source_expired")]
    public void Every_decline_maps_to_a_wire_reason(JournalMediaReason reason, string wire)
    {
        Assert.Equal(wire, JournalWire.Of(JournalMediaUpload.Declined(reason).NotArchivedReason));
    }

    /// <summary>
    /// The platform's declared size decides which limit was hit, without a Bot API call. 25,000,000
    /// is the number in AC4; the cap is 20,971,520.
    /// </summary>
    [Theory]
    [InlineData(25_000_000L, 33_554_432L, JournalMediaReason.OverBotApiLimit)]
    [InlineData(20_971_521L, 33_554_432L, JournalMediaReason.OverBotApiLimit)]
    [InlineData(20_971_520L, 10_485_760L, JournalMediaReason.OverSizeCap)]
    [InlineData(10_485_761L, 10_485_760L, JournalMediaReason.OverSizeCap)]
    [InlineData(1_000L, 10_485_760L, JournalMediaReason.DownloadFailed)]
    [InlineData(0L, 10_485_760L, JournalMediaReason.DownloadFailed)]
    public void The_absent_reason_distinguishes_the_two_caps(
        long declared, long localCap, JournalMediaReason expected)
    {
        Assert.Equal(expected, JournalMediaAbsent.Reason(declared, localCap));
    }

    [Fact]
    public void An_absent_declared_size_is_never_attributed_to_the_platform()
    {
        // Nothing was declared, so the platform refused nothing. Blaming the Bot API for a file the
        // agent never asked for would send the operator to look at the wrong limit.
        Assert.Equal(JournalMediaReason.DownloadFailed, JournalMediaAbsent.Reason(null, 10_485_760));
    }

    // ── rig ─────────────────────────────────────────────────────────────────

    private sealed class MediaRig : IDisposable
    {
        public TempDir Dir { get; } = new();
        public MediaFake Media { get; } = new();
        public JournalMediaUploader Uploader { get; }

        public string Root => Dir.Path;

        public MediaRig()
        {
            var http = new HttpClient(Media) { BaseAddress = new Uri("http://journal.test") };
            Uploader = new JournalMediaUploader(
                new JournalMediaHttpClient(http, Records.Token), Time);
        }

        private static ManualTime Time { get; } = new(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));

        public void Dispose() => Dir.Dispose();
    }

    /// <summary>The media listener as the uploader sees it, with a request log.</summary>
    private sealed class MediaFake : HttpMessageHandler
    {
        public Func<HttpResponseMessage> Declare { get; set; } =
            () => Reply(HttpStatusCode.Created, $"{{\"uploadId\":\"{UploadId}\"}}");

        public Func<HttpResponseMessage> Put { get; set; } =
            () => Reply(HttpStatusCode.OK, "{\"result\":\"uploaded\"}");

        public List<Recorded> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            var bytes = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(ct);

            lock (Requests)
            {
                Requests.Add(new Recorded(
                    request.Method.Method,
                    request.RequestUri!.AbsolutePath,
                    body,
                    bytes,
                    request.Headers.Authorization?.ToString()));
            }

            try
            {
                return request.Method == HttpMethod.Post ? Declare() : Put();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // The uploader turns a transport failure into a retryable exception; the fake
                // raises one the same way a refused connection would.
                throw new HttpRequestException("connection refused", e);
            }
        }

        public static HttpResponseMessage Reply(HttpStatusCode status, string json) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        public sealed record Recorded(string Method, string Path, string? Body, byte[]? Bytes, string? Authorization);
    }
}
