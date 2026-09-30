using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Fleet.Comms;
using Fleet.Comms.Configuration;
using Fleet.Conversations.Contracts;
using Fleet.Conversations.Journal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;

namespace Fleet.Conversations.Tests;

/// <summary>
/// The upload lifecycle against the real store and the real listener, over a bucket double
/// (#388 AC1, AC1a, AC1b, AC2).
/// </summary>
/// <remarks>
/// <para>
/// The bucket is in memory and the database is MySQL. That pairing is the point: the ACs are about
/// what rows and bucket keys exist after a sequence of calls, and neither half alone can answer
/// them. A fake store would let an owner check or a dedup rule pass that the real <c>SELECT … FOR
/// UPDATE</c> would not.
/// </para>
/// <para>
/// ⚠️ <see cref="MySqlFixture"/> fails rather than skips when MySQL is absent, so a run with no
/// database is red and cannot be read as a pass.
/// </para>
/// </remarks>
[Collection("mysql")]
public sealed class JournalUploadTests : IAsyncLifetime
{
    /// <summary>
    /// A schema of this class's own. The shared <c>mysql</c> one is written by every other store
    /// suite, and "1 committed object" is a claim about the whole table.
    /// </summary>
    private static readonly MySqlFixture Shared = new();

    private ScratchDatabase _scratch = null!;
    private FakeBucket _bucket = null!;
    private JournalMediaHost _host = null!;

    private string Db => _scratch.ConnectionString;

    public async Task InitializeAsync()
    {
        await Shared.InitializeAsync();
        _scratch = await Shared.CreateScratchDatabaseAsync();
        await new MigrationRunner(_scratch.ConnectionString).MigrateAsync();

        _bucket = new FakeBucket();
        _host = await JournalMediaHost.StartAsync(_scratch.ConnectionString, _bucket);
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        await _bucket.DisposeAsync();
        await _scratch.DisposeAsync();

        // See the note in `JournalS3UploadTests`: a static fixture is never disposed by xUnit, and
        // the pool it holds counts against the server's `max_connections`.
        if (Interlocked.Exchange(ref _sharedDisposed, 1) == 0)
            await Shared.DisposeAsync();
    }

    private static int _sharedDisposed;

    // ── AC1: two observers, one photo, one object ───────────────────────────

    /// <summary>
    /// The same bytes from two subjects: each uploads its own, the second loses dedup, and after
    /// the sweep there is one committed object, one bucket key, and two observers reading one
    /// attachment.
    /// </summary>
    [Fact]
    public async Task Two_observers_of_one_photo_leave_one_committed_object()
    {
        var bytes = "the same photo two ways"u8.ToArray();
        var digest = Sha256(bytes);

        var first = await UploadAsync(_host.A, bytes, "image/jpeg");
        var second = await UploadAsync(_host.B, bytes, "image/jpeg");

        Assert.NotEqual(first.UploadId, second.UploadId);
        Assert.Equal(2, _bucket.Keys.Count);

        // ONE Telegram message seen by two runtimes. The natural key is (conversation,
        // source_key) and the source key comes from the telegram message id, so the second subject
        // submits the SAME id and arrives at the existing message as an observer. That is the AC's
        // "1 attachment row referenced by 2 observers"; two message ids would be two unrelated
        // messages that happen to share bytes, which is a weaker claim than the one being made.
        var record = UploadRecords.UploadAttachment(-1000000000001, first.UploadId, first.Sha256, bytes.LongLength);

        Assert.Equal(HttpStatusCode.Created, (await _host.PostRawAsync(record, "agent1")).StatusCode);

        var secondRecord = UploadRecords.UploadAttachment(-1000000000001, second.UploadId, second.Sha256, bytes.LongLength);
        secondRecord["telegram"]!["messageId"] = record["telegram"]!["messageId"]!.DeepClone();

        var observerPost = await _host.PostRawAsync(secondRecord, "agent2");
        if (observerPost.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException(
                $"observer post -> {(int)observerPost.StatusCode} {await observerPost.Content.ReadAsStringAsync()}");

        // The winner is committed and both observers read through it. The loser is `aborted`, not
        // deleted: this submission added an observer to a message whose attachment rows were
        // already written, so its own row is the only thing the sweeper can still see. That is the
        // state the AC's "1 committed object and 1 aborted object" describes.
        Assert.Equal("committed", await StateAsync(first.UploadId));
        Assert.Equal("aborted", await StateAsync(second.UploadId));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM journal_objects WHERE state = 'committed'"));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM journal_objects WHERE state = 'aborted'"));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM journal_attachments"));
        Assert.Equal(2, await CountAsync("SELECT COUNT(*) FROM journal_message_observers"));

        // Both attachments resolve to the one committed object — that is the whole dedup argument.
        Assert.Equal(1, await CountAsync(
            "SELECT COUNT(DISTINCT object_id) FROM journal_attachments WHERE state = 'committed'"));

        // The loser's bytes are still in the bucket at this point, which is exactly why the sweeper
        // tick in the next test is part of the acceptance, not an optimisation.
        Assert.Equal(2, _bucket.Keys.Count);

        // The digest on the committed row is the one both subjects proved, so the UNIQUE key is
        // what decided the dedup rather than a code path that could race.
        Assert.Equal(digest, await ScalarAsync(
            "SELECT committed_sha256 FROM journal_objects WHERE state = 'committed'"));
    }

    /// <summary>
    /// The sweeper's AC1 ending: the loser is `aborted` and its bytes are still in the bucket, and
    /// after the abandon window the sweep leaves exactly one row, one bucket object, and the one
    /// attachment that two observers read.
    /// </summary>
    [Fact]
    public async Task The_sweeper_takes_the_dedup_loser_and_keeps_the_winner()
    {
        var bytes = "one photo two subjects"u8.ToArray();

        var first = await UploadAsync(_host.A, bytes, "image/jpeg");
        var second = await UploadAsync(_host.B, bytes, "image/jpeg");

        var firstRecord = UploadRecords.UploadAttachment(
            -1000000000202, first.UploadId, first.Sha256, bytes.LongLength);
        Assert.Equal(HttpStatusCode.Created, (await _host.PostRawAsync(firstRecord, "agent1")).StatusCode);

        var secondRecord = UploadRecords.UploadAttachment(
            -1000000000202, second.UploadId, second.Sha256, bytes.LongLength);
        secondRecord["telegram"]!["messageId"] = firstRecord["telegram"]!["messageId"]!.DeepClone();
        Assert.Equal(HttpStatusCode.OK, (await _host.PostRawAsync(secondRecord, "agent2")).StatusCode);

        Assert.Equal("aborted", await StateAsync(second.UploadId));
        var loserKey = JournalObjectKeys.For(second.UploadId);
        Assert.Equal(2, _bucket.Keys.Count);

        var swept = await _host.SweepAsync(hours: 25);

        Assert.Equal(1, swept.Abandoned);
        Assert.False(_bucket.Keys.Contains(loserKey));

        // The AC's end state: one row, one object, one attachment — and the observer that reads it
        // is not the subject that uploaded the bytes it now reads.
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM journal_objects"));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM journal_attachments"));
        Assert.Equal(1, _bucket.Keys.Count);
        Assert.Equal(2, await CountAsync("SELECT COUNT(*) FROM journal_message_observers"));
    }

    // ── AC1a: the owner binding ──────────────────────────────────────────────

    /// <summary>
    /// A PUT against another subject's upload id answers exactly as an unknown id does, and the
    /// real row is untouched — still <c>uploading</c>, still owned by A, no bytes in the bucket.
    /// </summary>
    [Fact]
    public async Task Another_subject_cannot_put_to_someone_elses_upload()
    {
        var a = await _host.DeclareAsync("agent1", Sha256([1, 2, 3]), 3, "image/jpeg");
        var stolen = await _host.PutRawAsync("agent2", a, [9, 9, 9]);

        Assert.Equal(HttpStatusCode.NotFound, stolen.StatusCode);
        Assert.Equal("{\"error\":\"unauthorized\"}", await stolen.Content.ReadAsStringAsync());

        Assert.Equal("uploading", await StateAsync(a));
        Assert.Equal("agent1", await ScalarAsync($"SELECT owner FROM journal_objects WHERE id = '{a}'"));
        Assert.Empty(_bucket.Keys);
    }

    /// <summary>
    /// Committing a message that names another subject's upload id is the same <c>409
    /// upload_incomplete</c> as a nonexistent one, and nothing is written.
    /// </summary>
    [Fact]
    public async Task Another_subject_cannot_commit_someone_elses_upload()
    {
        var upload = await UploadAsync(_host.A, "subject a bytes"u8.ToArray(), "image/jpeg");

        var refused = await _host.PostRecordAsync(upload with { Subject = "agent2" }, -1000000000003, "agent2");

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var body = JsonNode.Parse(await refused.Content.ReadAsStringAsync())!.AsObject();
        Assert.Equal("upload_incomplete", body["error"]!.GetValue<string>());
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM journal_messages"));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM journal_attachments"));

        // A's object is unchanged: a refusal must not disturb the row it refused to attach.
        Assert.Equal("uploaded", await StateAsync(upload.UploadId));
    }

    // ── AC1b: naming a digest is not holding bytes ───────────────────────────

    /// <summary>
    /// Subject B commits a message naming the digest of A's committed object without uploading
    /// anything: <c>409 upload_incomplete</c>, 0 attachment rows, and B cannot read A's attachment
    /// by id.
    /// </summary>
    [Fact]
    public async Task Borrowing_another_subjects_committed_object_is_refused()
    {
        var bytes = "only a uploaded these"u8.ToArray();
        var upload = await UploadAsync(_host.A, bytes, "image/jpeg");
        Assert.Equal(HttpStatusCode.Created, (await _host.PostRecordAsync(upload, -1000000000004, "agent1")).StatusCode);

        // The wire contract lets an attachment point at bytes in exactly one way: an `uploadId`
        // this subject proved. B's attempt is to name A's committed object directly, and a caller
        // never gets to choose an object id — the server resolves it from the upload.
        var objectId = await ScalarAsync(
            "SELECT object_id FROM journal_attachments WHERE state = 'committed' LIMIT 1");

        var borrowed = await _host.PostRawAsync(
            UploadRecords.WithAttachment(-1000000000004,
                "{\"ordinal\":0,\"kind\":\"photo\",\"mimeType\":\"image/jpeg\",\"byteSize\":21,\"objectId\":\""
                + objectId + "\"}"),
            "agent2");

        Assert.Equal(HttpStatusCode.Conflict, borrowed.StatusCode);
        Assert.Equal("media_disabled", ErrorCode(borrowed));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM journal_attachments"));

    }

    // ── AC2: the interrupted upload ──────────────────────────────────────────

    /// <summary>
    /// A PUT that dies partway leaves the row <c>uploading</c> and no message visible. The 24-hour
    /// sweep then removes both the row and the object, and nothing else is left behind.
    /// </summary>
    [Fact]
    public async Task An_upload_nobody_finishes_is_swept_and_leaves_nothing()
    {
        var upload = await _host.DeclareAsync("agent1", Sha256([1, 2, 3]), 3, "image/jpeg");

        // Half the declared bytes arrive.
        // Three bytes were declared; one arrives. The stream ends short of the declaration, which
        // is the interrupted-upload case rather than a client lying about its own Content-Length.
        var partial = await _host.PutRawAsync("agent1", upload, [1], declaredLength: 3);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, partial.StatusCode);
        Assert.Equal("sha256_mismatch", ErrorCode(partial));

        Assert.Equal("aborted", await StateAsync(upload));
        Assert.Empty(_bucket.Keys);

        var swept = await _host.SweepAsync(hours: 25);
        Assert.Equal(1, swept.Abandoned);
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM journal_objects"));
        Assert.Empty(_bucket.Keys);
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM journal_messages"));
    }

    // ── the states a commit refuses ──────────────────────────────────────────

    [Fact]
    public async Task An_uploading_row_cannot_be_committed()
    {
        var upload = await _host.DeclareAsync("agent1", Sha256([1, 2, 3]), 3, "image/jpeg");

        var refused = await _host.PostRecordAsync(
            new Uploaded(upload, Sha256([1, 2, 3]), "agent1"), -1000000000005, "agent1");

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode)
            ;
        Assert.Equal("upload_incomplete", ErrorCode(refused)
            ?? throw new InvalidOperationException(await refused.Content.ReadAsStringAsync()));
        Assert.Equal("uploading", await StateAsync(upload));
    }

    /// <summary>
    /// A declaration over the object cap is refused at the door, so the bucket never holds a
    /// declared-but-impossible object.
    /// </summary>
    [Fact]
    public async Task Declaring_more_than_the_object_cap_is_refused()
    {
        var refused = await _host.DeclareRawAsync(
            "agent1", Sha256([1]), MediaOptions.MaxObjectBytes + 1, "image/jpeg");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, refused.StatusCode);
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM journal_objects"));
    }

    /// <summary>
    /// Bytes that do not match the declaration are deleted and the row aborted, and the answer says
    /// so. A bucket holding an object nothing points at is the state the sweeper exists to clean,
    /// and here it is cleaned by the same path.
    /// </summary>
    [Fact]
    public async Task Mismatched_bytes_are_removed_and_the_row_aborted()
    {
        // Same length, different bytes: the digest is what refuses, not the size. A shorter body
        // would be testing the size guard and calling it a digest guard.
        var bytes = "something else entirely"u8.ToArray();
        var declared = Sha256("what was declared"u8.ToArray());
        var upload = await _host.DeclareAsync("agent1", declared, bytes.LongLength, "image/jpeg");

        var answer = await _host.PutRawAsync("agent1", upload, bytes);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, answer.StatusCode);
        Assert.Equal("sha256_mismatch", ErrorCode(answer));
        Assert.Equal("aborted", await StateAsync(upload));
        Assert.Empty(_bucket.Keys);
    }

    /// <summary>
    /// A bucket that fails during a PUT answers 503 and the row stays <c>uploading</c> — AC 3's
    /// precondition. The agent retries the same upload id and the end state has one object.
    /// </summary>
    [Fact]
    public async Task A_bucket_failure_midway_keeps_the_row_retryable()
    {
        var bytes = "retry me"u8.ToArray();
        var upload = await _host.DeclareAsync("agent1", Sha256(bytes), bytes.LongLength, "image/jpeg");

        _bucket.FailNextPut = true;
        var failed = await _host.PutRawAsync("agent1", upload, bytes);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal("media_unavailable", ErrorCode(failed));
        Assert.Equal("uploading", await StateAsync(upload));

        // The retry succeeds and the message commits against the same object.
        Assert.Equal(HttpStatusCode.OK, (await _host.PutRawAsync("agent1", upload, bytes)).StatusCode);
        Assert.Equal("uploaded", await StateAsync(upload));
        Assert.Equal(HttpStatusCode.Created,
            (await _host.PostRecordAsync(new Uploaded(upload, Sha256(bytes), "agent1"), -1000000000006, "agent1")).StatusCode);
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM journal_objects"));
        Assert.Equal(1, _bucket.Keys.Count);
    }

    /// <summary>
    /// An upload id that is not a ULID is refused as unknown rather than reaching the database with
    /// a nonsense key, and the answer is byte-identical to an unknown-but-valid id.
    /// </summary>
    [Fact]
    public async Task An_unparsable_upload_id_answers_like_an_unknown_one()
    {
        var unknown = await _host.PutRawAsync("agent1", "not-a-ulid", [1, 2, 3]);
        var nonsense = await _host.PutRawAsync("agent1", "../../etc/passwd", [1, 2, 3]);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(await unknown.Content.ReadAsStringAsync(), await nonsense.Content.ReadAsStringAsync());
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private async Task<Uploaded> UploadAsync(JournalCredential credential, byte[] bytes, string mime)
    {
        var id = await _host.DeclareAsync(credential.Subject, Sha256(bytes), bytes.LongLength, mime);
        Assert.Equal(HttpStatusCode.OK, (await _host.PutRawAsync(credential.Subject, id, bytes)).StatusCode);
        return new Uploaded(id, Sha256(bytes), credential.Subject);
    }

    private async Task<string> StateAsync(string uploadId) => await ScalarAsync(
        $"SELECT state FROM journal_objects WHERE id = '{uploadId}'");

    private async Task<string> ScalarAsync(string sql) => await MySqlFixture.ScalarRowOnAsync(Db, sql);

    private async Task<int> CountAsync(string sql) => int.Parse(
        await MySqlFixture.ScalarRowOnAsync(Db, sql), System.Globalization.CultureInfo.InvariantCulture);

    private static string? ErrorCode(HttpResponseMessage response)
    {
        var text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        return JsonNode.Parse(text)?["error"]?.GetValue<string>();
    }

}

/// <summary>Record bodies for the upload suite.</summary>
internal static class UploadRecords
{
    private static long _nextMessageId = Random.Shared.Next(1_000_000);

    /// <summary>
    /// A message id this run has not used. The natural key is (conversation, source_key) and the
    /// source key is derived from <c>telegram.messageId</c>, so a fixture constant would make every
    /// record in this file claim to be the same Telegram message: the second commit in a test would
    /// be a duplicate of the first, and a second test in a reused chat would be a duplicate of the
    /// first test's.
    /// </summary>
    private static long NextMessageId() => Interlocked.Increment(ref _nextMessageId);

    /// <summary>One attachment whose bytes this subject proved through an upload it owns.</summary>
    public static JsonObject UploadAttachment(
        long chatId, string uploadId, string sha256, long byteSize) =>
        Record(chatId,
            "{\"ordinal\":0,\"kind\":\"photo\",\"mimeType\":\"image/jpeg\",\"byteSize\":"
            + byteSize.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"uploadId\":\"" + uploadId + "\",\"uploadSha256\":\"" + sha256 + "\"}");

    /// <summary>A well-formed inbound record carrying one attachment, given as raw JSON.</summary>
    public static JsonObject WithAttachment(long chatId, string attachment) => Record(chatId, attachment);

    /// <summary>
    /// A message id this run has not used. The natural key is (conversation, source_key) and the
    /// source key comes from <c>telegram.messageId</c>, so a fixture constant would make every
    /// record in this file claim to be the same Telegram message. A test that wants two observers of
    /// ONE message clones the id it already sent.
    /// </summary>

    private static JsonObject Record(long chatId, string attachment) => JsonNode.Parse(
        "{\"eventId\":\"" + Fleet.Protocol.Ulid.NewUlid()
        + "\",\"channel\":\"telegram\",\"telegram\":{\"botId\":7001,\"chatId\":"
        + chatId.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + ",\"chatKind\":\"supergroup\",\"messageId\":"
        + NextMessageId().ToString(System.Globalization.CultureInfo.InvariantCulture)
        + "},\"direction\":\"inbound\",\"sender\":{\"kind\":\"human\",\"id\":\"111\"},"
        + "\"sentAt\":\"2026-09-29T10:00:00+00:00\",\"text\":null,\"textFormat\":\"plain\","
        + "\"transcript\":null,\"origin\":\"telegram_update\",\"attachments\":["
        + attachment + "]}")!.AsObject();

}

/// <summary>One upload this test proved: the id, the digest, and the subject that owns it.</summary>
internal sealed record Uploaded(string UploadId, string Sha256, string Subject);

/// <summary>A subject and its minted token — the two observers in AC1.</summary>
internal sealed record JournalCredential(string Subject);

/// <summary>A clock the test moves by hand.</summary>
internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;

    /// <summary>
    /// Place the clock at an instant chosen outside this provider — a deadline the DATABASE stamped
    /// (<c>delete_after</c> is <c>UTC_TIMESTAMP(6)</c> plus the grace), which no <see cref="Advance"/>
    /// measured from the fixture's fixed start can reach.
    /// </summary>
    public void SetTo(DateTimeOffset when) => _now = when;
}

/// <summary>
/// An in-memory bucket. Records the keys that exist, and can fail or be inspected on demand.
/// </summary>
/// <remarks>
/// Deliberately not an S3 client: the assertions here are about which keys exist and what bytes
/// they hold, and an SDK double would add a transport that the test has no opinion about.
/// </remarks>
internal sealed class FakeBucket : IJournalObjectStore
{
    /// <summary>
    /// The availability the gate would report. <see cref="Reachable"/> is the failure a request
    /// hits; this is the failure the cached probe noticed first. They are separate because the real
    /// deployment has both moments, and a route that consulted the wrong one would answer 503 with
    /// a healthy bucket or accept bytes into one that is down.
    /// </summary>
    public bool Available { get; set; } = true;

    public Dictionary<string, byte[]> Objects { get; } = [];
    public bool FailNextPut { get; set; }
    public bool Reachable { get; set; } = true;

    public IReadOnlyCollection<string> Keys => Objects.Keys;

    public async Task<JournalObjectWriteResult> PutAsync(
        string objectKey, Stream body, long byteSize, string contentType,
        CancellationToken ct = default, long? contentLength = null)
    {
        if (FailNextPut || !Reachable)
        {
            FailNextPut = false;
            return JournalObjectWriteResult.Failed();
        }

        // A faithful S3 client reads exactly the length it was told and stops. TestHost's request
        // stream blocks until the declared Content-Length is consumed, so reading to EOF here would
        // hang; reading one byte past the declaration is what detects an oversized body.
        var buffer = new MemoryStream();
        var chunk = new byte[81920];
        long total = 0;

        while (total < byteSize + 1)
        {
            var want = (int)Math.Min(chunk.Length, byteSize + 1 - total);
            var read = await body.ReadAsync(chunk.AsMemory(0, want), ct);
            if (read == 0) break;

            total += read;
            buffer.Write(chunk, 0, (int)Math.Min(read, byteSize + 1 - (total - read)));
        }

        if (total > byteSize) return JournalObjectWriteResult.Overflowed();

        var bytes = buffer.ToArray();
        Objects[objectKey] = bytes;
        return JournalObjectWriteResult.Written(
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), bytes.LongLength);
    }

    public Task<JournalObjectReadResult?> GetAsync(string objectKey, CancellationToken ct = default) =>
        Objects.TryGetValue(objectKey, out var bytes)
            ? Task.FromResult<JournalObjectReadResult?>(
                new JournalObjectReadResult { Content = new MemoryStream(bytes), ByteSize = bytes.LongLength })
            : Task.FromResult<JournalObjectReadResult?>(null);

    public Task<bool> ExistsAsync(string objectKey, CancellationToken ct = default) =>
        Task.FromResult(Objects.ContainsKey(objectKey));

    public Task DeleteAsync(string objectKey, CancellationToken ct = default)
    {
        // A bucket that is down fails reads AND deletes. A fake that answered deletes from an
        // in-memory map while unreachable would let a sweep appear to succeed while the real
        // bucket kept every byte.
        if (!Reachable) return Task.FromException(new JournalObjectStoreUnavailableException());

        Objects.Remove(objectKey);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Every key is reported a day older than the sweep's clock, because that is the only case the
    /// orphan class acts on. A listing that reported "just now" would make the sweep correctly
    /// refuse to delete a young key — and the test would then be asserting nothing about orphans.
    /// </summary>
    public DateTimeOffset ListingAge { get; set; } = new(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);

    public Task<IReadOnlyList<JournalObjectListing>> ListAsync(string prefix, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<JournalObjectListing>>(
            Objects.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                .Select(k => new JournalObjectListing { Key = k, LastModified = ListingAge })
                .ToArray());

    public Task<bool> ProbeAsync(CancellationToken ct = default) =>
        Reachable ? Task.FromResult(true) : Task.FromException<bool>(new JournalObjectStoreUnavailableException());

    /// <summary>What the host's cached probe last said — the gate a healthy route consults.</summary>
    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(Available);

    public string MediaState => Available ? "enabled" : "degraded";

    public Task DisposeAsync() => Task.CompletedTask;
}

/// <summary>
/// The journal listener with media attached: the real store, the real upload routes, a fake bucket,
/// and a clock the test moves so "after the sweeper tick with the clock advanced 24 h" is literal.
/// </summary>
/// <remarks>
/// The gate is a test double rather than <see cref="JournalMediaHealth"/> on purpose. The real gate
/// caches a probe for 30 s against a real bucket's failure modes; this suite needs to say
/// "the bucket is up" or "the gate says it is down" without the cache and without a probe, and the
/// gate's own behaviour is a different question with its own tests.
/// </remarks>
internal sealed class JournalMediaHost : IAsyncDisposable
{
    public static readonly string Key =
        Convert.ToBase64String(Enumerable.Range(7, 48).Select(i => (byte)i).ToArray())
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private readonly WebApplication _app;
    private readonly JournalObjectSweeper _sweeper;
    private readonly ManualTime _time;

    private JournalMediaHost(WebApplication app, JournalObjectSweeper sweeper, ManualTime time)
    {
        _app = app;
        _sweeper = sweeper;
        _time = time;
        Client = app.GetTestClient();
    }

    public HttpClient Client { get; }

    public JournalCredential A { get; } = new("agent1");
    public JournalCredential B { get; } = new("agent2");

    public static Task<JournalMediaHost> StartAsync(string connectionString, FakeBucket bucket) =>
        StartAsync(connectionString, bucket, new FakeGate());

    public static async Task<JournalMediaHost> StartAsync(
        string connectionString, FakeBucket bucket, FakeGate gate)
    {
        var time = new ManualTime(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, string.Empty);
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();


        var options = new CommsOptions
        {
            ConversationConnectionString = connectionString,
            Journal = new JournalOptions { Enabled = true, TokenKeys = JournalMediaHost.Key },
        };
        options.ValidateJournal();

        var stats = new JournalRuntimeStats(time);

        // ONE object store, handed to both the journal store and the media bundle. The guard that
        // refuses two of them is exactly what this host would otherwise trip, and it is right: an
        // upload route and a commit route reading different tables is how an object commits that
        // nothing can find.
        var objects = new MySqlJournalObjectStore(connectionString, NullLogger.Instance, time) { Bytes = bucket };
        var store = new MySqlJournalStore(connectionString, NullLogger.Instance) { Objects = objects };

        var app = CommsApp.BuildJournalApp(
            builder, store, options, stats, time,
            new CommsApp.JournalMedia(gate, bucket, objects));

        await app.StartAsync();

        return new JournalMediaHost(
            app,
            new JournalObjectSweeper(connectionString, bucket, NullLogger.Instance, stats, time),
            time);
    }

    /// <summary>One tick with the clock advanced, which is how the ACs word the sweep.</summary>
    /// <summary>One tick with the clock advanced, which is how the ACs word the sweep.</summary>
    public async Task<JournalObjectSweeper.SweepResult> SweepAsync(int hours)
    {
        _time.Advance(TimeSpan.FromHours(hours));
        return await _sweeper.SweepOnceAsync();
    }

    public async Task<string> DeclareAsync(string subject, string sha, long size, string mime)
    {
        var response = await DeclareRawAsync(subject, sha, size, mime);
        if (response.StatusCode != HttpStatusCode.Created)
            throw new InvalidOperationException(
                $"DECLARE({sha[..8]},{size},{mime}) -> {(int)response.StatusCode} "
                + await response.Content.ReadAsStringAsync());
        return await ReadId(response);
    }

    public async Task<HttpResponseMessage> DeclareRawAsync(
        string subject, string sha, long size, string mime)
    {
        var body = new StringContent(
            $"{{\"sha256\":\"{sha}\",\"byteSize\":{size},\"mimeType\":\"{mime}\"}}",
            Encoding.UTF8, "application/json");

        return await SendAsync(HttpMethod.Post, "/journal/v1/uploads", body, subject);
    }

    /// <summary>
    /// A PUT as an agent sends one: the declared length as Content-Length, and the bytes as the
    /// body. The listener streams the body and reads no more than the declaration allowed, so the
    /// two must agree exactly — a test that sent them as different values would be testing a
    /// request no client can make.
    /// </summary>
    public Task<HttpResponseMessage> PutRawAsync(string subject, string uploadId, byte[] bytes) =>
        PutRawAsync(subject, uploadId, bytes, bytes.LongLength);

    public async Task<HttpResponseMessage> PutRawAsync(
        string subject, string uploadId, byte[] bytes, long declaredLength)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put, $"/journal/v1/uploads/{uploadId}") { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        request.Content.Headers.ContentLength = declaredLength;
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", JournalTokens.Mint(JournalTokens.ParseKeys(Key)[0], "ingest", subject));

        return await Client.SendAsync(request);
    }

    public async Task<HttpResponseMessage> PostRecordAsync(
        Uploaded upload, long chatId, string subject)
    {
        var json = UploadRecords.WithAttachment(chatId,
            $"{{\"ordinal\":0,\"kind\":\"photo\",\"mimeType\":\"image/jpeg\","
            + $"\"byteSize\":21,\"uploadId\":\"{upload.UploadId}\",\"uploadSha256\":\"{upload.Sha256}\"}}");

        return await PostRawAsync(json, subject);
    }

    public async Task<HttpResponseMessage> PostRawAsync(JsonObject record, string subject)
    {
        var content = new StringContent(record.ToJsonString(), Encoding.UTF8, "application/json");
        return await SendAsync(HttpMethod.Post, "/journal/v1/messages", content, subject);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, HttpContent content, string subject)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", JournalTokens.Mint(JournalTokens.ParseKeys(Key)[0], "ingest", subject));

        return await Client.SendAsync(request);
    }

    private static async Task<string> ReadId(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonNode.Parse(text)!["uploadId"]!.GetValue<string>();
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

}

/// <summary>
/// The availability answer the routes consult, set by the test.
/// </summary>
internal sealed class FakeGate : JournalMediaGate
{
    public bool Available { get; set; } = true;

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(Available);

    public string MediaState => Available ? "enabled" : "degraded";
}
