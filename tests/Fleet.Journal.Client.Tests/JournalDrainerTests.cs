using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Fleet.Comms;
using Fleet.Conversations.Contracts;
using Microsoft.Extensions.Logging;

namespace Fleet.Journal.Client.Tests;

/// <summary>
/// Every row of the drainer response table (#377 AC10), plus the non-FIFO guarantee (AC9) and a
/// restart resuming <c>pending/</c>. Each test asserts the spool state and the counter.
/// </summary>
public sealed class JournalDrainerTests
{
    private static JournalAttachment Photo(int ordinal = 0) => new()
    {
        Ordinal = ordinal,
        Kind = JournalAttachmentKind.Photo,
        MimeType = "image/jpeg",
        ByteSize = 3,
        NotArchivedReason = JournalNotArchivedReason.MediaDisabled,
    };

    [Theory]
    [InlineData(201, "{\"result\":\"created\"}")]
    [InlineData(200, "{\"result\":\"duplicate\"}")]
    [InlineData(200, "{\"result\":\"observer_added\"}")]
    public async Task Delivered_deletes_the_record_and_its_media(int status, string body)
    {
        using var rig = new DrainerRig();
        var media = Path.Combine(rig.Root, "source.jpg");
        await File.WriteAllBytesAsync(media, [1, 2, 3]);
        var id = rig.Write(Records.Record(1, attachments: [Photo()]), [new SpoolMedia(0, media, null, SpoolMediaMode.Copy)]);
        Assert.True(File.Exists(rig.Spool.MediaPath(id, 0)));
        rig.Listener.Respond = _ => FakeListener.Status(status, body);

        await rig.Drainer.RunOnceAsync(default);

        Assert.Empty(rig.Spool.PendingIds());
        Assert.False(File.Exists(rig.Spool.MediaPath(id, 0)));
        Assert.Equal(1, rig.Counters.Get("journal_delivered"));
        Assert.Equal(0, rig.Spool.PendingCount);
    }

    [Fact]
    public async Task The_request_carries_the_bearer_token_and_the_record_as_serialized()
    {
        using var rig = new DrainerRig();
        rig.Write(Records.Record(1, text: "привет"));

        await rig.Drainer.RunOnceAsync(default);

        Assert.Equal("Bearer " + Records.Token, Assert.Single(rig.Listener.Authorizations));
        Assert.Equal("привет", rig.Listener.Posted[0]["text"]!.GetValue<string>());
        Assert.Null(rig.Listener.Posted[0]["attempts"]);
    }

    [Fact]
    public async Task Idempotency_conflict_moves_to_dead_with_an_error()
    {
        using var rig = new DrainerRig();
        var id = rig.Write(Records.Record(1));
        rig.Listener.Respond = _ => FakeListener.Status(409, "{\"error\":\"idempotency_conflict\",\"reason\":\"event_id_reused\"}");

        await rig.Drainer.RunOnceAsync(default);

        Assert.Empty(rig.Spool.PendingIds());
        Assert.True(File.Exists(Path.Combine(rig.Spool.DeadDir, id + ".json")));
        Assert.Equal(1, rig.Counters.Get("journal_dead{reason=idempotency_conflict}"));
        Assert.Equal(1, rig.Log.Count(LogLevel.Error));
        Assert.Equal(1, rig.Drainer.Snapshot().Dead);
    }

    [Fact]
    public async Task Media_disabled_rewrites_uploaded_attachments_and_resends()
    {
        using var rig = new DrainerRig();
        var id = rig.Write(Records.Record(1, attachments: [Photo(0), Photo(1)]));

        // An S4-shaped record: one attachment references uploaded bytes, one does not.
        var entry = rig.Spool.Load(id)!;
        var uploaded = entry.Record["attachments"]!.AsArray()[0]!.AsObject();
        uploaded.Remove("notArchivedReason");
        uploaded["uploadId"] = "01J00000000000000000000001";
        rig.Spool.Save(entry);

        var answers = new Queue<HttpResponseMessage>([
            FakeListener.Status(409, "{\"error\":\"media_disabled\"}"),
            FakeListener.Status(201, "{\"result\":\"created\"}"),
        ]);
        rig.Listener.Respond = _ => answers.Dequeue();

        await rig.Drainer.RunOnceAsync(default);

        var rewritten = rig.Spool.Load(id)!.Record["attachments"]!.AsArray();
        Assert.Null(rewritten[0]!["uploadId"]);
        Assert.Equal("media_disabled", rewritten[0]!["notArchivedReason"]!.GetValue<string>());
        Assert.Equal(1, rig.Counters.Get("journal_media_disabled"));

        await rig.Drainer.RunOnceAsync(default);

        Assert.Empty(rig.Spool.PendingIds());
        Assert.Equal(1, rig.Counters.Get("journal_delivered"));
        Assert.Null(rig.Listener.Posted[1]["attachments"]![0]!["uploadId"]);
    }

    [Fact]
    public async Task Media_disabled_with_nothing_to_rewrite_is_dead_rather_than_a_loop()
    {
        using var rig = new DrainerRig();
        rig.Write(Records.Record(1, attachments: [Photo()]));
        rig.Listener.Respond = _ => FakeListener.Status(409, "{\"error\":\"media_disabled\"}");

        await rig.Drainer.RunOnceAsync(default);

        Assert.Empty(rig.Spool.PendingIds());
        Assert.Equal(1, rig.Counters.Get("journal_dead{reason=media_disabled}"));
        Assert.Single(rig.Listener.Posted);
    }

    [Fact]
    public async Task Too_large_moves_to_dead_with_an_error()
    {
        using var rig = new DrainerRig();
        rig.Write(Records.Record(1));
        rig.Listener.Respond = _ => FakeListener.Status(413, "{\"error\":\"too_large\"}");

        await rig.Drainer.RunOnceAsync(default);

        Assert.Empty(rig.Spool.PendingIds());
        Assert.Single(Directory.GetFiles(rig.Spool.DeadDir, "*.json"));
        Assert.Equal(1, rig.Counters.Get("journal_dead{reason=too_large}"));
        Assert.Equal(1, rig.Log.Count(LogLevel.Error));
    }

    [Theory]
    [InlineData("excluded_chat")]
    [InlineData("unknown_conversation")]
    public async Task A_policy_refusal_is_dropped_not_dead_and_warned_once_an_hour(string reason)
    {
        using var rig = new DrainerRig();
        rig.Listener.Respond = _ => FakeListener.Status(422, $"{{\"error\":\"{reason}\"}}");

        rig.Write(Records.Record(1));
        await rig.Drainer.RunOnceAsync(default);
        rig.Write(Records.Record(2));
        await rig.Drainer.RunOnceAsync(default);

        Assert.Empty(rig.Spool.PendingIds());
        Assert.Empty(Directory.GetFiles(rig.Spool.DeadDir));
        Assert.Equal(2, rig.Counters.Get($"journal_dropped{{reason={reason}}}"));
        Assert.Equal(1, rig.Log.Count(LogLevel.Warning));

        rig.Time.Advance(TimeSpan.FromHours(1));
        rig.Write(Records.Record(3));
        await rig.Drainer.RunOnceAsync(default);

        Assert.Equal(2, rig.Log.Count(LogLevel.Warning));
    }

    [Theory]
    [InlineData(422, "{\"error\":\"invalid_record\",\"field\":\"sentAt\"}", "invalid_record")]
    [InlineData(422, "{\"error\":\"text_too_large\"}", "text_too_large")]
    [InlineData(400, "{\"error\":\"bad_request\"}", "bad_request")]
    public async Task A_refused_record_moves_to_dead_with_its_reason(int status, string body, string reason)
    {
        using var rig = new DrainerRig();
        var id = rig.Write(Records.Record(1));
        rig.Listener.Respond = _ => FakeListener.Status(status, body);

        await rig.Drainer.RunOnceAsync(default);

        Assert.Empty(rig.Spool.PendingIds());
        Assert.Equal(1, rig.Counters.Get($"journal_dead{{reason={reason}}}"));
        Assert.Equal(1, rig.Log.Count(LogLevel.Error));

        var dead = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(rig.Spool.DeadDir, id + ".json")))!;
        Assert.StartsWith(reason, dead["lastError"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unauthorized_stalls_the_whole_drainer_keeps_every_record_and_errors_every_five_minutes()
    {
        using var rig = new DrainerRig();
        rig.Write(Records.Record(1));
        rig.Write(Records.Record(2));
        rig.Listener.Respond = _ => FakeListener.Status(401, "{\"error\":\"unauthorized\"}");

        await rig.Drainer.RunOnceAsync(default);

        Assert.Equal(2, rig.Spool.PendingIds().Count);
        Assert.True(rig.Drainer.Snapshot().AuthFailed);
        Assert.Equal(1, rig.Log.Count(LogLevel.Error));

        // Stalled: no request at all until the five minutes are up.
        var wait = await rig.Drainer.RunOnceAsync(default);
        Assert.Single(rig.Listener.Posted);
        Assert.True(wait > TimeSpan.FromMinutes(4));

        rig.Time.Advance(TimeSpan.FromMinutes(5));
        await rig.Drainer.RunOnceAsync(default);
        Assert.Equal(2, rig.Listener.Posted.Count);
        Assert.Equal(2, rig.Log.Count(LogLevel.Error));

        // A token that works again clears the flag and delivers.
        rig.Listener.Respond = _ => FakeListener.Status(201, "{\"result\":\"created\"}");
        rig.Time.Advance(TimeSpan.FromMinutes(5));
        await rig.Drainer.RunOnceAsync(default);
        Assert.False(rig.Drainer.Snapshot().AuthFailed);
        Assert.Single(rig.Spool.PendingIds());

        // A stall is not the record's fault: it did not spend an attempt.
        Assert.Equal(0, rig.Spool.Load(rig.Spool.PendingIds()[0])!.Attempts);
    }

    [Fact]
    public async Task A_missing_route_pauses_five_minutes_and_keeps_every_record()
    {
        using var rig = new DrainerRig();
        rig.Write(Records.Record(1));
        rig.Listener.Respond = _ => FakeListener.Status(404);

        await rig.Drainer.RunOnceAsync(default);
        var wait = await rig.Drainer.RunOnceAsync(default);

        Assert.Single(rig.Spool.PendingIds());
        Assert.Single(rig.Listener.Posted);
        Assert.True(wait > TimeSpan.FromMinutes(4));
        Assert.Equal(1, rig.Counters.Get("journal_endpoint_missing"));
        Assert.Equal(1, rig.Log.Count(LogLevel.Warning));

        rig.Time.Advance(TimeSpan.FromMinutes(5));
        await rig.Drainer.RunOnceAsync(default);
        Assert.Equal(2, rig.Counters.Get("journal_endpoint_missing"));
        Assert.Equal(2, rig.Log.Count(LogLevel.Warning));
    }

    [Fact]
    public async Task Too_many_requests_retries_that_record_after_retry_after()
    {
        using var rig = new DrainerRig();
        var id = rig.Write(Records.Record(1));
        rig.Listener.Respond = _ =>
        {
            var response = FakeListener.Status(429, "{\"error\":\"too_many_requests\"}");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return response;
        };

        await rig.Drainer.RunOnceAsync(default);

        var entry = rig.Spool.Load(id)!;
        Assert.Equal(rig.Time.GetUtcNow() + TimeSpan.FromSeconds(7), entry.NextAttemptAt);
        Assert.Equal("http_429", entry.LastError);

        // Not due yet: no second request.
        await rig.Drainer.RunOnceAsync(default);
        Assert.Single(rig.Listener.Posted);

        rig.Time.Advance(TimeSpan.FromSeconds(7));
        await rig.Drainer.RunOnceAsync(default);
        Assert.Equal(2, rig.Listener.Posted.Count);
    }

    [Theory]
    [InlineData(503)]
    [InlineData(500)]
    [InlineData(0)]
    public async Task A_server_or_transport_failure_backs_off_that_record(int status)
    {
        using var rig = new DrainerRig();
        var id = rig.Write(Records.Record(1));
        rig.Listener.Respond = _ => status == 0 ? FakeListener.Refused() : FakeListener.Status(status);

        await rig.Drainer.RunOnceAsync(default);

        var first = rig.Spool.Load(id)!;
        Assert.Equal(1, first.Attempts);
        Assert.Equal(rig.Time.GetUtcNow() + TimeSpan.FromSeconds(1), first.NextAttemptAt);
        Assert.Equal(status == 0 ? "connection" : $"http_{status}", first.LastError);

        rig.Time.Advance(TimeSpan.FromSeconds(1));
        await rig.Drainer.RunOnceAsync(default);

        var second = rig.Spool.Load(id)!;
        Assert.Equal(2, second.Attempts);
        Assert.Equal(rig.Time.GetUtcNow() + TimeSpan.FromSeconds(2), second.NextAttemptAt);
    }

    [Fact]
    public void Backoff_doubles_from_one_second_to_a_five_minute_cap()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), JournalDrainer.Backoff(1));
        Assert.Equal(TimeSpan.FromSeconds(2), JournalDrainer.Backoff(2));
        Assert.Equal(TimeSpan.FromSeconds(256), JournalDrainer.Backoff(9));
        Assert.Equal(TimeSpan.FromMinutes(5), JournalDrainer.Backoff(10));
        Assert.Equal(TimeSpan.FromMinutes(5), JournalDrainer.Backoff(500));
    }

    [Fact]
    public async Task A_timeout_is_a_per_record_retry()
    {
        using var rig = new DrainerRig();
        var id = rig.Write(Records.Record(1));
        var drainer = rig.NewDrainer(rig.Spool, new HangingHandler(), timeout: TimeSpan.FromMilliseconds(100));

        await drainer.RunOnceAsync(default).WaitAsync(TimeSpan.FromSeconds(30));

        var entry = rig.Spool.Load(id)!;
        Assert.Equal("timeout", entry.LastError);
        Assert.Equal(1, entry.Attempts);
        Assert.Equal(rig.Time.GetUtcNow() + TimeSpan.FromSeconds(1), entry.NextAttemptAt);
    }

    [Fact]
    public void The_request_budget_is_fifteen_seconds() =>
        Assert.Equal(TimeSpan.FromSeconds(15), JournalHttpClient.RequestTimeout);

    [Fact]
    public async Task Persistent_failure_is_dead_only_after_twenty_attempts_and_a_day()
    {
        using var rig = new DrainerRig();
        var id = rig.Write(Records.Record(1));
        rig.Listener.Respond = _ => FakeListener.Status(503, "{\"error\":\"store_unavailable\"}");

        // Twenty quick attempts: still pending, because a day has not passed.
        for (var i = 0; i < 20; i++)
        {
            rig.Time.Advance(TimeSpan.FromMinutes(5));
            await rig.Drainer.RunOnceAsync(default);
        }

        Assert.Single(rig.Spool.PendingIds());
        Assert.Equal(20, rig.Spool.Load(id)!.Attempts);

        // A day later, the next failure is the last.
        rig.Time.Advance(TimeSpan.FromHours(24));
        await rig.Drainer.RunOnceAsync(default);

        Assert.Empty(rig.Spool.PendingIds());
        Assert.Equal(1, rig.Counters.Get("journal_dead{reason=persistent_5xx}"));
        Assert.True(rig.Log.Count(LogLevel.Error) >= 1);
    }

    [Fact]
    public async Task Five_consecutive_failures_pause_the_drainer_thirty_seconds()
    {
        using var rig = new DrainerRig();
        for (var i = 1; i <= 6; i++) rig.Write(Records.Record(i));
        rig.Listener.Respond = _ => FakeListener.Status(503);

        for (var i = 0; i < 5; i++) await rig.Drainer.RunOnceAsync(default);
        Assert.Equal(5, rig.Listener.Posted.Count);

        // Record 6 is due, but the drainer is paused.
        var wait = await rig.Drainer.RunOnceAsync(default);
        Assert.Equal(5, rig.Listener.Posted.Count);
        Assert.Equal(TimeSpan.FromSeconds(30), wait);

        rig.Time.Advance(TimeSpan.FromSeconds(30));
        await rig.Drainer.RunOnceAsync(default);
        Assert.Equal(6, rig.Listener.Posted.Count);
    }

    /// <summary>
    /// AC9: A older and failing forever, B newer and accepted — B is delivered within 2 s and A
    /// stays pending with a growing <c>nextAttemptAt</c>. Run on the real clock and the real loop.
    /// </summary>
    [Fact]
    public async Task A_failing_older_record_never_blocks_a_newer_one()
    {
        using var dir = new TempDir();
        var spool = new JournalSpool(dir.Path);
        var listener = new FakeListener
        {
            Respond = body => Records.MessageId(body) == 1 ? FakeListener.Status(503) : FakeListener.Status(201, "{\"result\":\"created\"}"),
        };
        var counters = new JournalCounters();
        var drainer = new JournalDrainer(
            spool, new JournalHttpClient(new HttpClient(listener) { BaseAddress = new Uri("http://journal.test") }, Records.Token),
            counters, new CapturingLogger<JournalDrainer>());

        spool.Write(Records.Json(Records.Record(1)), "inbound", []);
        var a = spool.PendingIds().Single();
        await Task.Delay(5); // a later ULID millisecond: B is strictly newer
        spool.Write(Records.Json(Records.Record(2)), "inbound", []);

        var clock = Stopwatch.StartNew();
        await drainer.StartAsync(default);
        try
        {
            while (counters.Get("journal_delivered") == 0 && clock.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Delay(20);

            Assert.Equal(1, counters.Get("journal_delivered"));
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"B took {clock.Elapsed}");

            var firstNext = spool.Load(a)!.NextAttemptAt;
            var deadline = Stopwatch.StartNew();
            while (spool.Load(a)!.Attempts < 2 && deadline.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Delay(50);

            var later = spool.Load(a)!;
            Assert.Equal([a], spool.PendingIds());
            Assert.True(later.Attempts >= 2);
            Assert.True(later.NextAttemptAt > firstNext);
        }
        finally
        {
            await drainer.StopAsync(default);
        }
    }

    /// <summary>
    /// A record whose outcome the spool cannot write only delays itself: its attempt and backoff
    /// are kept in memory, the pass does not throw, and the next record is delivered.
    /// </summary>
    [Fact]
    public async Task A_record_the_spool_cannot_update_delays_only_itself()
    {
        using var rig = new DrainerRig();
        var a = rig.Write(Records.Record(1));
        rig.Time.Advance(TimeSpan.FromMilliseconds(5));
        var b = rig.Write(Records.Record(2));
        Assert.True(string.CompareOrdinal(a, b) < 0, "A must be the older record");

        rig.Spool.WriteFaultForTesting = id => id == a ? new IOException("disk full") : null;
        rig.Listener.Respond = body => Records.MessageId(body) == 1 ? FakeListener.Status(503) : FakeListener.Status(201, "{\"result\":\"created\"}");

        // Pass 1: A fails and its backoff cannot be saved. The pass must not throw.
        await rig.Drainer.RunOnceAsync(default);
        Assert.Equal(rig.Time.GetUtcNow() + TimeSpan.FromSeconds(1), rig.Drainer.ScheduledFor(a));
        Assert.Equal(0, rig.Spool.Load(a)!.Attempts); // nothing reached the disk

        // Pass 2: A is not due, so B goes out.
        await rig.Drainer.RunOnceAsync(default);
        Assert.Equal([a], rig.Spool.PendingIds());
        Assert.Equal(1, rig.Counters.Get("journal_delivered"));
        Assert.Equal([1L, 2L], rig.Listener.Posted.Select(Records.MessageId));

        // The in-memory attempt count still drives the backoff: the second failure waits 2 s.
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        await rig.Drainer.RunOnceAsync(default);
        Assert.Equal(3, rig.Listener.Posted.Count);
        Assert.Equal(rig.Time.GetUtcNow() + TimeSpan.FromSeconds(2), rig.Drainer.ScheduledFor(a));

        // Once the disk recovers, A's real attempt count is written.
        rig.Spool.WriteFaultForTesting = null;
        rig.Time.Advance(TimeSpan.FromSeconds(2));
        await rig.Drainer.RunOnceAsync(default);
        Assert.Equal(3, rig.Spool.Load(a)!.Attempts);
        Assert.Contains(rig.Log.Lines, l => l.Message.Contains("could not be processed (IOException)", StringComparison.Ordinal));
    }

    /// <summary>
    /// A delivered record that cannot be deleted is retried later (the listener answers duplicate)
    /// rather than resent in a tight loop ahead of everything else.
    /// </summary>
    [Fact]
    public async Task A_delivered_record_that_cannot_be_deleted_does_not_block_the_next()
    {
        using var rig = new DrainerRig();
        var a = rig.Write(Records.Record(1));
        rig.Time.Advance(TimeSpan.FromMilliseconds(5));
        rig.Write(Records.Record(2));
        rig.Spool.WriteFaultForTesting = id => id == a ? new IOException("read-only") : null;

        await rig.Drainer.RunOnceAsync(default);
        await rig.Drainer.RunOnceAsync(default);

        Assert.Equal([a], rig.Spool.PendingIds());
        Assert.Equal(1, rig.Counters.Get("journal_delivered")); // B only: A is not counted until it is gone
        Assert.Equal([1L, 2L], rig.Listener.Posted.Select(Records.MessageId));

        rig.Spool.WriteFaultForTesting = null;
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        await rig.Drainer.RunOnceAsync(default);

        Assert.Empty(rig.Spool.PendingIds());
        Assert.Equal(2, rig.Counters.Get("journal_delivered"));
    }

    [Fact]
    public async Task A_restart_resumes_pending_and_delivers_once()
    {
        using var rig = new DrainerRig();
        rig.Write(Records.Record(1));
        rig.Listener.Respond = _ => FakeListener.Refused();

        await rig.Drainer.RunOnceAsync(default);
        Assert.Single(rig.Spool.PendingIds());

        // A new process: a new spool over the same directory and a new drainer.
        var spool = new JournalSpool(rig.Root, rig.Time);
        Assert.Equal(1, spool.PendingCount);
        var drainer = rig.NewDrainer(spool);
        rig.Listener.Respond = _ => FakeListener.Status(201, "{\"result\":\"created\"}");

        rig.Time.Advance(TimeSpan.FromSeconds(1));
        await drainer.RunOnceAsync(default);
        await drainer.RunOnceAsync(default);

        Assert.Empty(spool.PendingIds());
        Assert.Equal(1, rig.Counters.Get("journal_delivered"));
        Assert.Equal(2, rig.Listener.Posted.Count); // one refused, one delivered
    }

    [Fact]
    public async Task No_log_line_carries_text_or_the_token()
    {
        using var rig = new DrainerRig();
        rig.Write(Records.Record(1, text: "secret-looking text"));
        var answers = new Queue<HttpResponseMessage>([
            FakeListener.Status(401), FakeListener.Status(404), FakeListener.Status(422, "{\"error\":\"invalid_record\",\"field\":\"text\"}"),
        ]);
        rig.Listener.Respond = _ => answers.Dequeue();

        for (var i = 0; i < 3; i++)
        {
            await rig.Drainer.RunOnceAsync(default);
            rig.Time.Advance(TimeSpan.FromMinutes(5));
        }

        Assert.NotEmpty(rig.Log.Lines);
        Assert.DoesNotContain(rig.Log.Lines, l => l.Message.Contains("secret-looking", StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Log.Lines, l => l.Message.Contains("cj1.", StringComparison.Ordinal));
    }

    /// <summary>
    /// The record a drained media attachment produces is accepted by the REAL ingest parser.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The capture writes <c>notArchivedReason: "media_disabled"</c> as a placeholder meaning "I had
    /// no bucket to put this in". The parser treats <c>notArchivedReason</c> and <c>uploadId</c> as
    /// mutually exclusive and refuses an attachment carrying both — so if the upload pass set the id
    /// and left the placeholder, every drained record with media would come back <c>422</c> for the
    /// whole submission, and the agent would dead-letter a message Telegram actually delivered.
    /// </para>
    /// <para>
    /// ⚠️ Asserting that the spool JSON has no <c>notArchivedReason</c> key would prove the drainer
    /// agrees with itself. This hands the posted bytes to
    /// <see cref="CommsApp.ValidateIngestRecord"/>, which is the parser the ingest route calls, so
    /// what is asserted is the contract between two processes rather than a field name.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_drained_media_record_is_accepted_by_the_real_ingest_parser()
    {
        using var rig = new DrainerRig();
        var bytes = "the bytes the journal must keep"u8.ToArray();
        var media = Path.Combine(rig.Root, "source.jpg");
        await File.WriteAllBytesAsync(media, bytes);

        // An S4-shaped capture: the attachment declares its bytes and says they were not archived.
        var id = rig.Write(
            Records.Record(1, attachments: [Photo(0)]),
            [new SpoolMedia(0, media, null, SpoolMediaMode.Copy)]);
        Assert.Equal("media_disabled",
            rig.Spool.Load(id)!.Record["attachments"]![0]!["notArchivedReason"]!.GetValue<string>());

        await rig.NewDrainer(rig.Spool, rig.Listener, rig.NewUploader()).RunOnceAsync(default);

        var posted = Assert.Single(rig.Listener.Posted).ToJsonString();
        var attachment = JsonNode.Parse(posted)!["attachments"]![0]!.AsObject();

        // The placeholder is gone and the upload reference took its place — in the same mutation.
        Assert.Null(attachment["notArchivedReason"]);
        Assert.NotNull(attachment["uploadId"]);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)),
            attachment["uploadSha256"]!.GetValue<string>());

        // And the parser that answers real ingests accepts it. `now` is the drainer's clock, so the
        // record's `sentAt` sits inside the parser's future-skew window exactly as it does in
        // production — a record the parser rejects for its date would be a fixture bug, not a finding.
        var (status, error, field) = CommsApp.ValidateIngestRecord(
            Encoding.UTF8.GetBytes(posted), rig.Time.GetUtcNow());
        Assert.True(status == 0,
            $"the ingest parser refused the drained record: {status} {error} {field}");

        Assert.Equal(1, rig.Counters.Get("journal_delivered"));
    }

    /// <summary>
    /// <c>409 upload_incomplete</c> clears the upload references and re-uploads from the spool
    /// instead of dead-lettering a message the agent is owed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The server refuses upload references without saying which of its reasons applies — swept or
    /// aborted object, a spool restored from a backup whose bucket no longer holds the object, a row
    /// that stopped being ours. Every one of them is recoverable locally: the bytes are still in the
    /// spool. Dead-lettering here throws away a delivered message over a bucket hiccup, so the
    /// record goes back into the shape the capture wrote and the next pass uploads again.
    /// </para>
    /// <para>
    /// The PUT count is the assertion that matters. "The fields were cleared" alone is also true of
    /// a record that settled as <c>not_archived</c> and lost its bytes forever; two PUTs of the same
    /// bytes is what proves the re-upload actually happened.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Upload_incomplete_clears_the_references_and_uploads_again_rather_than_dying()
    {
        using var rig = new DrainerRig();
        var bytes = "the bytes the journal must keep"u8.ToArray();
        var media = Path.Combine(rig.Root, "source.jpg");
        await File.WriteAllBytesAsync(media, bytes);
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));

        var id = rig.Write(
            Records.Record(1, attachments: [Photo(0)]),
            [new SpoolMedia(0, media, null, SpoolMediaMode.Copy)]);

        // First post carries upload references and is refused; the second, cleared, is accepted.
        var answers = new Queue<HttpResponseMessage>([
            FakeListener.Status(409, "{\"error\":\"upload_incomplete\"}"),
            FakeListener.Status(201, "{\"result\":\"created\"}"),
        ]);

        var mediaFake = new MediaFake();
        var listener = new FakeListener
        {
            // The server accepts the record only once it no longer claims stored bytes.
            Respond = _ => answers.Dequeue(),
        };
        var drainer = rig.NewDrainer(rig.Spool, listener, rig.NewUploader(mediaFake));

        // Pass 1: uploaded, posted, refused. The references are cleared; the record stays pending.
        await drainer.RunOnceAsync(default);

        Assert.Equal([id], rig.Spool.PendingIds());
        Assert.Equal(0, rig.Spool.DeadCount);
        var cleared = rig.Spool.Load(id)!.Record["attachments"]![0]!.AsObject();
        Assert.Null(cleared["uploadId"]);
        Assert.Null(cleared["uploadSha256"]);
        Assert.Equal("media_disabled", cleared["notArchivedReason"]!.GetValue<string>());
        Assert.Equal(1, rig.Counters.Get("journal_media_reason{reason=upload_incomplete}"));
        Assert.True(rig.Log.Count(LogLevel.Warning) >= 1);

        // Pass 2: the bytes go up AGAIN — that is what "recoverable from the spool" means. The
        // clock moves because a pass takes at most one request and the record's backoff is real.
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        await drainer.RunOnceAsync(default);

        Assert.Empty(rig.Spool.PendingIds());
        Assert.Equal(0, rig.Spool.DeadCount);
        Assert.Equal(1, rig.Counters.Get("journal_delivered"));
        Assert.Equal(2, mediaFake.Requests.Count(r => r.Method == "PUT"));

        // One message, posted twice. The record that went the SECOND time still carries upload
        // references — and it must: the cleared record is re-uploaded from the spool before it is
        // posted, so it arrives with a FRESH id and the same digest. An attachment that arrived
        // with the placeholder instead would journal the message with its bytes silently missing,
        // which is the failure this whole branch exists to avoid.
        Assert.Equal([1L, 1L], listener.Posted.Select(Records.MessageId));
        var resent = listener.Posted[1]["attachments"]![0]!.AsObject();
        Assert.NotNull(resent["uploadId"]);
        Assert.Null(resent["notArchivedReason"]);
        Assert.Equal(digest, resent["uploadSha256"]!.GetValue<string>());
        Assert.Equal(3, resent["byteSize"]!.GetValue<long>());

        // The cleared state is what the SPOOL holds between the two passes — that is the durable
        // half of the fix, and the half that survives a crash between the refuse and the re-upload.
        Assert.Equal("media_disabled", cleared["notArchivedReason"]!.GetValue<string>());

        // The bytes that went up the second time are the bytes the capture spooled — the digest is
        // what the server compares against the object it hashes, so a re-upload of anything else
        // would be a silent corruption that no later stage could detect.
        var puts = mediaFake.Requests.Where(r => r.Method == "PUT").ToList();
        Assert.All(puts, p => Assert.Equal(bytes, p.Bytes));
        Assert.Equal(digest, Convert.ToHexStringLower(SHA256.HashData(puts[1].Bytes!)));
    }

    [Fact]
    public async Task Dead_records_are_swept_after_thirty_days()
    {
        using var rig = new DrainerRig();
        rig.Write(Records.Record(1));
        rig.Listener.Respond = _ => FakeListener.Status(400);
        await rig.Drainer.RunOnceAsync(default);
        Assert.Equal(1, rig.Spool.DeadCount);

        rig.Time.Advance(TimeSpan.FromDays(31));
        await rig.Drainer.RunOnceAsync(default);

        Assert.Equal(0, rig.Spool.DeadCount);
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }
    }
}
