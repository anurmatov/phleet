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

namespace Fleet.Conversations.Tests;

/// <summary>
/// The upload lifecycle end to end against a REAL bucket (#388).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="JournalUploadTests"/> proves the state machine against a fake bucket, which is the
/// right instrument for "which row is in which state". It cannot prove the claim the whole design
/// rests on: that the bytes a subject PUT through the route are the bytes that end up in the bucket,
/// under the key the row names, with the digest the row records. A fake accepts whatever the client
/// sent and reports whatever digest it computed; that is the assertion, not the thing under test.
/// </para>
/// <para>
/// These run the same Comms journal app, with the only swap being <c>FakeBucket → S3ObjectStore</c>.
/// A commit that succeeds here means an object exists in a real bucket that a real GET can read back.
/// </para>
/// <para>
/// ⚠️ See <see cref="S3Fixture"/> for what the SeaweedFS fixture does not establish.
/// </para>
/// </remarks>
[Collection("s3")]
public sealed class JournalS3UploadTests(S3Fixture fixture) : IAsyncLifetime
{
    private static readonly MySqlFixture Shared = new();

    private ScratchDatabase _scratch = null!;
    private S3ObjectStore _bucket = null!;
    private S3UploadHost _host = null!;

    public string Db => _scratch.ConnectionString;

    public async Task InitializeAsync()
    {
        await Shared.InitializeAsync();
        _scratch = await Shared.CreateScratchDatabaseAsync();
        await new MigrationRunner(_scratch.ConnectionString).MigrateAsync();

        _bucket = fixture.Store();
        _host = await S3UploadHost.StartAsync(Db, _bucket);
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        _bucket.Dispose();
        await _scratch.DisposeAsync();
    }

    /// <summary>
    /// AC1's shape, with the bucket being the real thing: two subjects each prove their own bytes,
    /// one object survives, and the bytes that survive are readable from the bucket afterwards.
    /// </summary>
    [Fact]
    public async Task Two_observers_upload_their_own_bytes_and_the_committed_object_reads_back_from_the_bucket()
    {
        var photo = new byte[8192];
        Random.Shared.NextBytes(photo);
        var sha = Convert.ToHexString(SHA256.HashData(photo)).ToLowerInvariant();

        var a = await _host.UploadAsync(_host.CredentialA.Subject, photo);
        var b = await _host.UploadAsync(_host.CredentialB.Subject, photo);

        Assert.NotEqual(a.UploadId, b.UploadId);
        Assert.True(await _bucket.ExistsAsync(JournalObjectKeys.For(a.UploadId)));
        Assert.True(await _bucket.ExistsAsync(JournalObjectKeys.For(b.UploadId)));

        // ONE Telegram message seen by two runtimes, each naming the upload it proved. The natural
        // key is (conversation, source_key) derived from the telegram message id, so the second
        // subject submits the SAME message id and arrives at the existing message as an observer —
        // that is AC1's "1 attachment row referenced by 2 observers". `eventId` is per submission,
        // not per message, so it must NOT be cloned: two submissions with one eventId are one
        // submission replayed, and the second answer is `duplicate` rather than `observer_added`.
        var record = UploadRecords.UploadAttachment(9_100_001, a.UploadId, a.Sha256, photo.LongLength);
        var clone = UploadRecords.UploadAttachment(9_100_001, b.UploadId, b.Sha256, photo.LongLength);
        clone["telegram"]!["messageId"] = record["telegram"]!["messageId"]!.DeepClone();

        Assert.Equal(HttpStatusCode.Created,
            (await _host.CommitAsync(_host.CredentialA.Subject, record.ToJsonString())).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await _host.CommitAsync(_host.CredentialB.Subject, clone.ToJsonString())).StatusCode);

        // Exactly one object holds the bytes; the loser is parked as `aborted`, awaiting the sweep.
        // Counted by STATE, not by the declared `sha256`: that column is stamped on every row the
        // subject opened, while `committed_sha256` is written only when a row becomes the winner, so
        // a loser still carries its own declared digest. Selecting on the declared value therefore
        // counts the loser as if it had committed.
        Assert.Equal("1", await _host.ScalarAsync("SELECT COUNT(*) FROM journal_objects WHERE state = 'committed'"));
        Assert.Equal("1", await _host.ScalarAsync("SELECT COUNT(*) FROM journal_objects WHERE state = 'aborted'"));

        // The committed object is readable FROM THE BUCKET and matches the digest the row claims.
        // This is the assertion a fake bucket cannot make.
        var committedRow = (await _host.QueryAsync(
            "SELECT id, object_key FROM journal_objects WHERE state = 'committed'")).Single();
        var committedId = committedRow.Split('|')[0];
        var committedKey = committedRow.Split('|')[1];
        Assert.NotNull(committedKey);

        var read = await _bucket.GetAsync(committedKey!);
        Assert.NotNull(read);
        using var ms = new MemoryStream();
        using (read!.Content) await read.Content.CopyToAsync(ms);
        Assert.Equal(photo, ms.ToArray());

        // AC1's last clause: the sweeper with the clock advanced leaves one row, one bucket object,
        // and the attachment still readable by BOTH observers.
        var sweep = await _host.SweepAsync(24);
        Assert.Equal(0, sweep.Failures);

        Assert.Single(await _host.QueryAsync("SELECT id FROM journal_objects"));
        Assert.Equal(1, (await _bucket.ListAsync(JournalObjectKeys.Prefix))
            .Count(k => k.Key == committedKey));

        // Both observers still reach the one object, which is the state that makes reads work
        // without either of them owning it. (Authorising the READ itself is D4's observership
        // rule, and JournalUploadTests covers it; what matters here is that the dedup did not
        // strand either observer.)
        //
        // ⚠️ Observership is its own table, not a count of attachment rows: one Telegram message
        // has ONE attachment row and N observer rows. Counting attachments by object and expecting
        // 2 asks the schema for something it does not model.
        Assert.Equal("1", await _host.ScalarAsync("SELECT COUNT(*) FROM journal_attachments"));
        Assert.Equal(committedId, await _host.ScalarAsync("SELECT object_id FROM journal_attachments"));
        Assert.Equal("2", await _host.ScalarAsync("SELECT COUNT(*) FROM journal_message_observers"));

        // The object the observers share is still readable from the bucket after the sweep — the
        // reference guard is what makes that true, and this is the assertion that proves it.
        var afterSweep = await _bucket.GetAsync(committedKey!);
        Assert.NotNull(afterSweep);
    }

    /// <summary>
    /// AC2: a PUT that dies partway leaves a row and no committed object; the sweep then removes
    /// both, and the bucket ends empty.
    /// </summary>
    [Fact]
    public async Task An_upload_declared_but_never_completed_leaves_no_row_and_no_object_after_the_sweep()
    {
        var photo = new byte[2048];
        Random.Shared.NextBytes(photo);
        var sha = Convert.ToHexString(SHA256.HashData(photo)).ToLowerInvariant();

        // Declared, never PUT. The row exists in `uploading`, which is the state AC2 names.
        var uploadId = await _host.DeclareAsync(_host.CredentialA.Subject, sha, photo.LongLength, "image/jpeg");
        Assert.Equal("uploading", await _host.ScalarAsync(
            $"SELECT state FROM journal_objects WHERE id = '{uploadId}'"));
        Assert.False(await _bucket.ExistsAsync(JournalObjectKeys.For(uploadId)));

        var sweep = await _host.SweepAsync(24);
        Assert.Equal(0, sweep.Failures);

        Assert.Empty(await _host.QueryAsync("SELECT id FROM journal_objects"));
        Assert.Empty((await _bucket.ListAsync(JournalObjectKeys.Prefix))
            .Where(k => k.Key == JournalObjectKeys.For(uploadId)));
    }

    /// <summary>
    /// AC3's end state against a real bucket: an upload that succeeds after a failed attempt leaves
    /// ONE committed object, not two.
    /// </summary>
    [Fact]
    public async Task A_retry_that_uses_a_new_upload_leaves_one_committed_object_when_one_upload_is_abandoned()
    {
        var photo = new byte[3000];
        Random.Shared.NextBytes(photo);
        var sha = Convert.ToHexString(SHA256.HashData(photo)).ToLowerInvariant();

        // First attempt: declared and PUT, but the agent never commits it (the shape of a crash
        // between the PUT and the message).
        var abandoned = await _host.UploadAsync(_host.CredentialA.Subject, photo);
        // The retry: a NEW upload, because the agent cannot reuse a foreign row and must not pretend
        // a hash declaration is proof.
        var retried = await _host.UploadAsync(_host.CredentialA.Subject, photo);

        // `UploadAttachment` ALREADY returns a whole record — passing its JSON to `WithAttachment`
        // again nests a record inside `attachments[0]`, and the parser answers 422.
        var record = UploadRecords.UploadAttachment(9_100_002, retried.UploadId, retried.Sha256, photo.LongLength);
        Assert.Equal(HttpStatusCode.Created,
            (await _host.CommitAsync(_host.CredentialA.Subject, record.ToJsonString())).StatusCode);

        var sweep = await _host.SweepAsync(24);
        Assert.Equal(0, sweep.Failures);

        var committed = await _host.QueryAsync(
            $"SELECT id FROM journal_objects WHERE committed_sha256 = '{sha}' AND state = 'committed'");
        Assert.Single(committed);

        var keys = (await _bucket.ListAsync(JournalObjectKeys.Prefix)).Select(k => k.Key).ToArray();
        Assert.Single(keys);
        Assert.Equal(JournalObjectKeys.For(committed[0]), keys[0]);
    }
}

/// <summary>
/// The Comms journal app wired to a real S3 bucket — the same host <see cref="JournalMediaHost"/>
/// builds, with the fake bucket swapped for <see cref="S3ObjectStore"/>.
/// </summary>
internal sealed class S3UploadHost : IAsyncDisposable
{
    private readonly HttpClient _client;
    private readonly MySqlJournalObjectStore _objects;
    private readonly JournalObjectSweeper _sweeper;
    private readonly ManualTime _time;
    private readonly MySqlJournalStore _store;

    private S3UploadHost(
        WebApplication app, HttpClient client, MySqlJournalObjectStore objects,
        JournalObjectSweeper sweeper, ManualTime time, string connectionString, MySqlJournalStore store)
    {
        App = app;
        _client = client;
        _objects = objects;
        _sweeper = sweeper;
        _time = time;
        ConnectionString = connectionString;
        _store = store;
    }

    public WebApplication App { get; }
    public string ConnectionString { get; }
    public JournalCredential CredentialA { get; } = new("agent1");
    public JournalCredential CredentialB { get; } = new("agent2");

    public static async Task<S3UploadHost> StartAsync(string connectionString, IJournalObjectStore bucket)
    {
        var time = new ManualTime(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, string.Empty);
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        var options = new CommsOptions
        {
            ConversationConnectionString = connectionString,
            Journal = new JournalOptions
            {
                Enabled = true,
                TokenKeys = JournalMediaHost.Key,
            },
        };
        options.ValidateJournal();

        var stats = new JournalRuntimeStats(time);
        var objects = new MySqlJournalObjectStore(connectionString, NullLogger.Instance, time) { Bytes = bucket };
        // The clock is passed to BOTH halves of the write path and to the sweeper: `created_at` is
        // stamped from it, and the sweep compares against it. See CommsApp for why a mixed clock is
        // the bug rather than a nicety.
        var store = new MySqlJournalStore(connectionString, NullLogger.Instance, time) { Objects = objects };

        var app = CommsApp.BuildJournalApp(
            builder, store, options, stats, time,
            new CommsApp.JournalMedia(new FakeGate(), bucket, objects));

        await app.StartAsync();

        // The sweeper is handed the SAME bucket the routes write to, so a sweep removes bytes and
        // not merely rows. The reference guard inside it is what stops it deleting the live object.
        var sweeper = new JournalObjectSweeper(connectionString, bucket, NullLogger.Instance, stats, time);

        return new S3UploadHost(
            app, app.GetTestClient(), objects, sweeper, time, connectionString, store);
    }

    /// <summary>
    /// Move the clock past the abandon window and sweep, the way AC1 and AC2 describe it.
    /// </summary>
    /// <remarks>
    /// ⚠️ The window is <c>created_at &lt; now − 24h</c>, a strict comparison, so advancing the clock
    /// <i>exactly</i> 24 h puts an object created at the start of the window precisely ON the cutoff
    /// and it survives. Advancing the window plus a minute tests the promise the AC makes — "after
    /// the sweep with the clock advanced 24 h" means past the window, not exactly at it. The
    /// one-hour-short case is covered in <see cref="JournalObjectSweeperTests"/>.
    /// </remarks>
    public Task<JournalObjectSweeper.SweepResult> SweepAsync(int hours)
    {
        _time.Advance(TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(1));
        return _sweeper.SweepOnceAsync();
    }

    public async Task<string> DeclareAsync(string subject, string sha, long size, string mime)
    {
        var body = new StringContent(
            "{\"sha256\":\"" + sha + "\",\"byteSize\":" + size + ",\"mimeType\":\"" + mime + "\"}",
            Encoding.UTF8, "application/json");
        var response = await SendAsync(HttpMethod.Post, "/journal/v1/uploads", subject, body);
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        return json["uploadId"]!.GetValue<string>();
    }

    /// <summary>Declare then PUT, the way the S2 drainer does it.</summary>
    public async Task<Uploaded> UploadAsync(string subject, byte[] bytes)
    {
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var uploadId = await DeclareAsync(subject, sha, bytes.LongLength, "image/jpeg");

        // A body that reports no length of its own. `ByteArrayContent` always states one, and TestServer
        // then hands the route a Content-Length request — a shape no real chunked upload ever has. The
        // defect this file reproduces is only reachable when the body cannot speak for itself, so the
        // test body must not be able to either.
        var put = new ChunkedContent(bytes);
        put.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        var response = await SendAsync(HttpMethod.Put, $"/journal/v1/uploads/{uploadId}", subject, put);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"PUT {uploadId} ({bytes.Length} bytes) -> {(int)response.StatusCode} "
                + await response.Content.ReadAsStringAsync());
        return new Uploaded(uploadId, sha, subject);
    }

    public Task<HttpResponseMessage> CommitAsync(string subject, string record) =>
        SendAsync(HttpMethod.Post, "/journal/v1/messages", subject,
            new StringContent(record, Encoding.UTF8, "application/json"));

    public async Task<IReadOnlyList<string>> QueryAsync(string sql)
    {
        await using var connection = new MySqlConnector.MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new MySqlConnector.MySqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();

        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            var cells = new List<string>();
            for (var i = 0; i < reader.FieldCount; i++)
                cells.Add(reader.IsDBNull(i) ? "" : reader.GetValue(i)?.ToString() ?? "");
            rows.Add(string.Join("|", cells));
        }
        return rows;
    }

    public async Task<string?> ScalarAsync(string sql)
    {
        var rows = await QueryAsync(sql);
        return rows.Count == 0 ? null : rows[0].Split('|')[0];
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
    }

    private Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, string subject, HttpContent? content)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            JournalTokens.Mint(JournalTokens.ParseKeys(JournalMediaHost.Key)[0], "ingest", subject));
        return _client.SendAsync(request);
    }
}

/// <summary>
/// A request body that refuses to state its own length, so the route under test sees the shape a
/// real chunked upload has.
/// </summary>
/// <remarks>
/// ⚠️ This class is the reason the file catches a real defect. <c>ByteArrayContent</c> always
/// reports a length, TestServer then presents a Content-Length request, and the SDK happily uploads
/// that. Every one of those requests succeeds against a fake bucket AND a real one, so a store that
/// could not state the length of a body it was handed would pass the whole suite. This body removes
/// the crutch.
/// </remarks>
internal sealed class ChunkedContent(byte[] bytes) : HttpContent
{
    private readonly byte[] _bytes = bytes;
    private int _at;

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        // Written in small pieces so nothing about this body can be computed ahead of time.
        while (_at < _bytes.Length)
        {
            var slice = _bytes.AsMemory(_at, Math.Min(1024, _bytes.Length - _at));
            await stream.WriteAsync(slice);
            _at += slice.Length;
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}
