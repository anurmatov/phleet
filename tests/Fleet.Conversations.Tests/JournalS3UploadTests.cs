using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Fleet.Comms;
using Fleet.Comms.Configuration;
using Fleet.Conversations.Contracts;
using Fleet.Comms.Operations;
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

        // Fail at class-initialisation, not inside AC6: a run whose CI job forgot the second
        // SeaweedFS should say so once, in those words, rather than as an operator-verb failure.
        if (!fixture.HasSignedBucket)
            throw new InvalidOperationException(
                $"{S3Fixture.SignedEndpointVariable} is not set. The media suite needs a bucket that "
                + "authenticates for the operator commands and the startup credential classes; those "
                + "tests FAIL rather than skip. CI runs a second SeaweedFS with -s3.iam.config for "
                + "this. See S3Fixture for the local command.");
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        _bucket.Dispose();
        await _scratch.DisposeAsync();

        // ⚠️ `Shared` is a STATIC field, so xUnit never disposes it — and every MySqlFixture holds
        //    pooled connections to the one MySQL 8.0 service container. Left alone, this class and
        //    the two other classes that do the same (`JournalUploadTests`,
        //    `JournalObjectSweeperTests`) each keep a pool open for the whole run, which is how the
        //    MySQL job reached `Too many connections` once the media suite added its own classes.
        //    Interlocked guards it because the collection runs serialised but a static outlives it.
        if (Interlocked.Exchange(ref _sharedDisposed, 1) == 0)
            await Shared.DisposeAsync();
    }

    private static int _sharedDisposed;

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

        // ⚠️ Scoped to the object THIS test committed. `journal_objects` is a scratch database and
        //    is private to the test, but the BUCKET is a shared named resource: xUnit runs test
        //    methods within a class in parallel, so `ListAsync(j1/)` also returns a sibling
        //    method's objects and `Assert.Single` fails on someone else's bytes. The claim AC3 makes
        //    is about the committed object — one row, one object, and nothing left for the sweeper —
        //    which is what is asserted here.
        var committedKey = JournalObjectKeys.For(committed[0]);
        var keys = (await _bucket.ListAsync(committedKey)).Select(k => k.Key).ToArray();
        Assert.Single(keys);
        Assert.Equal(committedKey, keys[0]);

        // The sweeper left nothing else BEHIND in this test's database, which is the half a shared
        // bucket cannot be used to prove and the scratch database can.
        Assert.Equal("1", await _host.ScalarAsync("SELECT COUNT(*) FROM journal_objects"));
    }

    /// <summary>
    /// AC6 against a REAL, AUTHENTICATING bucket: <c>media backup</c> → wipe the bucket →
    /// <c>media restore</c> → <c>journal verify-media</c> exits 0 with the same counts as before.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>This runs against the signed fixture, and it has to.</b> The operator verbs build their
    /// own <see cref="S3ObjectStore"/> from <c>Comms__Media__*</c> through
    /// <c>JournalMediaCommands.OpenStore</c>, which uses the store's real constructor and therefore
    /// SIGNS. The anonymous fixture answers a signed request with 403, so pointing the verbs at it
    /// produces a restore that fails for a reason that has nothing to do with restore. See
    /// <see cref="S3Fixture.SignedEndpointVariable"/> for why that fixture exists and for the
    /// earlier claim on this file that such a fixture was impossible.
    /// </para>
    /// <para>
    /// These are the commands themselves, not a re-implementation of them: routing through
    /// <c>OperatorCommands.RunAsync</c> is part of what is proven, because a verb that reaches the
    /// wrong handler or resolves a different bucket would pass a test that called the handler
    /// directly.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Backup_wipe_restore_and_verify_media_round_trips_through_the_real_bucket()
    {
        var photo = new byte[6000];
        Random.Shared.NextBytes(photo);

        // The signed bucket is a SEPARATE bucket from the one the routes write to, so the journal
        // rows and the objects the operator backs up have to be created through the signed store.
        // The message still goes through the committed journal path; only the bucket differs.
        using var signed = fixture.SignedStore();
        Assert.True(await signed.ProbeAsync(),
            "the signed fixture answered no to HeadBucket — the identity config is not in effect");

        // ⚠️ The key is the one THIS test wrote, never "the one thing in the bucket": the signed
        //    bucket is shared by the collection and other tests put objects in it.
        var objectId = Fleet.Protocol.Ulid.NewUlid();
        var objectKey = JournalObjectKeys.For(objectId);
        await using (var body = new MemoryStream(photo))
        {
            var written = await signed.PutAsync(objectKey, body, photo.LongLength, "image/jpeg");
            Assert.True(written.Succeeded, "the signed fixture refused a PUT");
            Assert.Equal(Convert.ToHexString(SHA256.HashData(photo)).ToLowerInvariant(), written.Sha256);
        }

        // A journal row and an attachment pointing at it, because `verify-media` walks attachments
        // joined to objects — an object with no attachment is a BACKUP candidate, not a verified
        // one, and AC6 is about the verified set.
        var sha = Convert.ToHexString(SHA256.HashData(photo)).ToLowerInvariant();
        await SeedAttachedObjectAsync(objectKey, sha, photo.LongLength);

        var before = await VerifyMediaAsync();
        Assert.True(before.ExitCode == 0, "verify-media(before) => " + before.Output);

        // One object in the verified set, and one object in the bucket. Exact counts are available
        // because the "s3" collection is serialised — see S3Fixture's CollectionDefinition for why
        // that is a correctness requirement and not a speed choice.
        var beforeRows = Field(before.Output, "rows");
        var beforeBytes = Field(before.Output, "bytes");
        Assert.Equal("1", beforeRows);
        Assert.Equal(photo.LongLength.ToString(), beforeBytes);

        var directory = Path.Combine(Path.GetTempPath(), "media-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var backup = await RunMediaAsync("media", "backup", "--out", directory);
            Assert.True(backup.ExitCode == 0, "media backup => " + backup.Output);
            Assert.Equal("1", Field(backup.Output, "copied"));
            Assert.Equal("0", Field(backup.Output, "failures"));

            // ⚠️ Wipe the bucket through the store, not the CLI. After this the ROWS are untouched
            //    and the ATTACHMENT still points at them — a deployment that lost its bucket but
            //    kept its database, which is the only state a restore can help.
            foreach (var listed in await signed.ListAsync(JournalObjectKeys.Prefix))
                await signed.DeleteAsync(listed.Key);
            Assert.Empty(await signed.ListAsync(JournalObjectKeys.Prefix));

            var broken = await VerifyMediaAsync();
            Assert.Equal(1, broken.ExitCode);
            Assert.Equal("1", Field(broken.Output, "missing"));

            var restore = await RunMediaAsync("media", "restore", "--in", directory);
            Assert.True(restore.ExitCode == 0, "media restore => " + restore.Output);
            Assert.Equal("1", Field(restore.Output, "restored"));
            Assert.Equal("0", Field(restore.Output, "failures"));

            // AC6's last clause, and the one that matters: counts EQUAL to before the wipe.
            var after = await VerifyMediaAsync();
            Assert.Equal(0, after.ExitCode);
            Assert.Equal(beforeRows, Field(after.Output, "rows"));
            Assert.Equal(beforeBytes, Field(after.Output, "bytes"));

            // And the bytes that came back are the bytes that went in — verify-media hashes what it
            // reads, so a restore that wrote the right length of the wrong thing exits 1.
            var read = await signed.GetAsync(objectKey);
            Assert.NotNull(read);
            using var ms = new MemoryStream();
            using (read!.Content) await read.Content.CopyToAsync(ms);
            Assert.Equal(photo, ms.ToArray());
        }
        finally
        {
            // A backup directory is real bytes on disk; leaving it behind is how a test suite
            // becomes a disk-full incident on a runner.
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// The startup credential classes, against a bucket that can answer them (#388).
    /// </summary>
    /// <remarks>
    /// ⚠️ This is the assertion the anonymous fixture could never make, and the reason the second
    /// SeaweedFS exists. A wrong secret must be <see cref="JournalProbeFailureException"/> — the
    /// answer Comms exits 1 on — and NOT <c>false</c>, which would start the service degraded and
    /// retry forever against a deployment nobody can fix without an operator.
    /// </remarks>
    [Fact]
    public async Task A_wrong_secret_is_a_startup_failure_not_a_degraded_start()
    {
        using var wrong = new S3ObjectStore(new JournalMediaOptions
        {
            Endpoint = fixture.SignedEndpoint!,
            Bucket = fixture.SignedBucket,
            AccessKey = S3Fixture.SignedAccessKey,
            SecretKey = "not-the-secret-" + Guid.NewGuid().ToString("N")[..8],
            Region = "us-east-1",
        }, NullLogger.Instance);

        await Assert.ThrowsAsync<JournalProbeFailureException>(() => wrong.ProbeAsync());
    }

    /// <summary>
    /// AC5 against a REAL bucket: an attachment whose row is fine but whose bytes are gone is
    /// reported by <c>journal verify-media</c> as <c>missing</c>, and exits 1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The row is the thing that looks healthy, which is why this needs the bucket: a fake bucket
    /// and a row table are the same in-memory truth, so "the pointer is intact and the bytes are
    /// not" is not a state it can express.
    /// </para>
    /// <para>
    /// ⚠️ The object lives in the SIGNED bucket because the command reads it with its own store,
    /// which signs. The attachment and the row are the real journal's, written through the
    /// committed path, so the pointer under test is the one production has.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Verify_media_reports_an_attachment_whose_object_is_gone_from_the_bucket()
    {
        var photo = new byte[4096];
        Random.Shared.NextBytes(photo);
        var sha = Convert.ToHexString(SHA256.HashData(photo)).ToLowerInvariant();

        using var signed = fixture.SignedStore();
        var objectKey = JournalObjectKeys.For(Fleet.Protocol.Ulid.NewUlid());
        await using (var body = new MemoryStream(photo))
            Assert.True((await signed.PutAsync(objectKey, body, photo.LongLength, "image/jpeg")).Succeeded);

        await SeedAttachedObjectAsync(objectKey, sha, photo.LongLength);

        Assert.True((await VerifyMediaAsync()).ExitCode == 0,
            "verify-media should pass before the object is removed");

        await signed.DeleteAsync(objectKey);

        var result = await VerifyMediaAsync();

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("1", Field(result.Output, "missing"));
        Assert.Equal("0", Field(result.Output, "mismatches"));
        // The row is still there and still attached — this is a bucket loss, not a data-model one,
        // and the exit code must not be the only thing that says so.
        Assert.Equal("1", await _host.ScalarAsync("SELECT COUNT(*) FROM journal_attachments"));
    }

    /// <summary>
    /// Retiring the older of two messages that share one object does not retire the object the
    /// surviving message still uses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the case the dedup rule creates and the one the retention query used to get wrong.
    /// Two messages carrying the same bytes land on the SAME committed object — a repeated photo, a
    /// forward, the same file posted twice — and the older one expires while the newer is still
    /// inside retention. Before the fix retention marked that object <c>deleting</c> because an
    /// expiring message pointed at it. The sweeper's reference guard then refused the row delete
    /// forever, and the operator was left with a <c>deleting</c> row that never resolves. The
    /// stronger version of that bug — bytes actually deleted while a live message still served them
    /// — is what the <c>NOT EXISTS</c> in <see cref="JournalRetention.SweepOnceAsync"/> prevents,
    /// and the readable-bytes assertion at the end is the one that would have caught it.
    /// </para>
    /// <para>
    /// ⚠️ The third message exists to stop this test passing for the wrong reason. A guard that
    /// retired NOTHING would satisfy "the shared object survived"; the expired message with its own
    /// unreferenced digest is the case that SHOULD retire, so one sweep has to retire one object and
    /// keep the other. That is exactly what the <c>NOT EXISTS</c> produces, and nothing weaker does.
    /// </para>
    /// <para>
    /// ⚠️ The three messages live in THREE conversations. <c>journal_messages</c> cascades from
    /// <c>journal_messages.conversation_id</c> and retention deletes a conversation whole once it is
    /// empty, so two messages in one conversation cannot be aged apart — a test that tried it would
    /// watch both go and assert nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_object_shared_by_two_messages_survives_retention_of_the_older_one()
    {
        var shared = new byte[5000];
        Random.Shared.NextBytes(shared);
        var unshared = new byte[1500];
        Random.Shared.NextBytes(unshared);

        var sharedUpload = await _host.UploadAsync(_host.CredentialA.Subject, shared);
        var unsharedUpload = await _host.UploadAsync(_host.CredentialA.Subject, unshared);

        // ⚠️ `sent_at` comes from the HOST's clock, and that is the whole test.
        //
        //    `UploadRecords` stamps a fixed fixture date and the host's clock starts at the same
        //    instant, so advancing the clock past the retention horizon expires BOTH messages and
        //    the sweep has nothing to spare — the assertion "the newer one survived" would then be
        //    vacuous rather than wrong, which is worse. Retention compares `sent_at` to the clock it
        //    is handed, so the surviving message must be dated against that same provider.
        //
        //    Ingested through the store rather than the route for that reason: the typed record
        //    carries SentAt directly, and the route would parse the same value anyway. The commit
        //    path — media proof, dedup, attachment rows — is identical either way.
        //
        //    The advance has to land BETWEEN the dates, which is the arithmetic that decided this.
        //    Advancing two days from a message dated "an hour ago" puts the cutoff a day ahead of
        //    the present and retires everything, so the surviving message is dated two days FORWARD
        //    and the expiring ones three days back. A future `sent_at` is not something Telegram can
        //    hand us, but retention only ever compares the two, so the pair is honest about the rule
        //    under test.
        var now = _host.Time.GetUtcNow();
        Assert.Equal(JournalIngestOutcome.Created,
            (await _host.Store.IngestAsync(UploadRecordWithUpload(9_100_020, sharedUpload, shared.LongLength,
                now.AddDays(-3)), _host.CredentialA.Subject)).Outcome);
        Assert.Equal(JournalIngestOutcome.Created,
            (await _host.Store.IngestAsync(UploadRecordWithUpload(9_100_021, sharedUpload,
                shared.LongLength, now.AddDays(2)), _host.CredentialA.Subject)).Outcome);
        Assert.Equal(JournalIngestOutcome.Created,
            (await _host.Store.IngestAsync(UploadRecordWithUpload(9_100_022, unsharedUpload,
                unshared.LongLength, now.AddDays(-3)), _host.CredentialA.Subject)).Outcome);

        var sharedId = (await _host.QueryAsync(
            $"SELECT id FROM journal_objects WHERE sha256 = '{sharedUpload.Sha256}'")).Single();
        var sharedKey = (await _host.QueryAsync(
            $"SELECT object_key FROM journal_objects WHERE id = '{sharedId}'")).Single();

        // The dedup the test claims: two messages, ONE object row. If this ever reads 1, the scenario
        // below is not the shared case and the assertions prove nothing.
        Assert.Equal("2", await _host.ScalarAsync(
            $"SELECT COUNT(*) FROM journal_attachments WHERE object_id = '{sharedId}'"));

        // Move past the retention horizon so the two old messages expire and the shared one does not.
        _host.Time.Advance(TimeSpan.FromDays(2));
        var retention = new JournalRetention(
            _host.ConnectionString, TimeSpan.FromDays(1), batchSize: 100,
            NullLogger.Instance, null, _host.Time, _bucket);

        // The DIAGNOSTIC must distinguish the failure modes it can report. `chat_id` cannot: every
        // record is a DM with the same bot, so the value repeats and the trio reads as a broken
        // fixture. `sent_at` alone says which rows the cutoff can see — the only question this
        // assertion is really asking.
        var preSweep = string.Join(" ; ", await _host.QueryAsync(
            "SELECT m.sent_at FROM journal_messages m ORDER BY m.sent_at"));
        var swept = await retention.SweepOnceAsync();
        Assert.True(swept.Messages == 2,
            $"rows=[{preSweep}] cutoff={(_host.Time.GetUtcNow() - TimeSpan.FromDays(1)):O} got={swept}");

        // ⚠️ THE FIX, in one line: the object two expiring messages referenced but that a surviving
        //    message still uses is NOT retired. `deleting` is the state that leads to its bytes
        //    being removed.
        Assert.Equal("committed", await _host.ScalarAsync(
            $"SELECT state FROM journal_objects WHERE id = '{sharedId}'"));

        // One retired, one kept, from the same sweep. Without the unshared object a guard that
        // retired nothing would pass this test.
        Assert.Equal(1, swept.Objects);
        Assert.Equal("deleting", await _host.ScalarAsync(
            $"SELECT state FROM journal_objects WHERE sha256 = '{unsharedUpload.Sha256}'"));

        // The sweep must not take the shared object either — the guard has to hold at both moments.
        var sweep = await _host.SweepAsync(24);
        Assert.Equal(0, sweep.Failures);

        Assert.Equal("committed", await _host.ScalarAsync(
            $"SELECT state FROM journal_objects WHERE id = '{sharedId}'"));

        // The bytes are still readable — the assertion the whole retention rule exists for.
        var read = await _bucket.GetAsync(sharedKey);
        Assert.NotNull(read);
        using var ms = new MemoryStream();
        using (read!.Content) await read.Content.CopyToAsync(ms);
        Assert.Equal(shared, ms.ToArray());
    }

    /// <summary>
    /// An object whose deadline passed while an attachment still points at it keeps its BYTES, not
    /// merely its row.
    /// </summary>
    /// <remarks>
    /// The reference guard used to run AFTER the bucket delete, so the first tick deleted bytes it
    /// then announced it was "leaving for retention". Every assertion about the row passed. This one
    /// asks the bucket.
    /// </remarks>
    [Fact]
    public async Task A_referenced_object_past_its_deadline_keeps_its_bytes_not_just_its_row()
    {
        var photo = new byte[3500];
        Random.Shared.NextBytes(photo);

        var uploaded = await _host.UploadAsync(_host.CredentialA.Subject, photo);
        var record = UploadRecords.UploadAttachment(9_100_022, uploaded.UploadId, uploaded.Sha256, photo.LongLength);
        Assert.Equal(HttpStatusCode.Created,
            (await _host.CommitAsync(_host.CredentialA.Subject, record.ToJsonString())).StatusCode);

        // Force the state the guard exists for: a COMMITTED row past the delete deadline, which is
        // what retention marks — without deleting the message that references it.
        await _host.ExecuteAsync(
            "UPDATE journal_objects SET state = 'deleting', "
            + "delete_after = TIMESTAMPADD(SECOND, -3600, created_at)");

        var key = JournalObjectKeys.For(uploaded.UploadId);
        Assert.True(await _bucket.ExistsAsync(key));

        var sweep = await _host.SweepAsync(24);
        Assert.Equal(0, sweep.Failures);

        // The row survives (the guard refuses it) AND the bytes survive (the guard now runs first).
        Assert.Equal("deleting", await _host.ScalarAsync("SELECT state FROM journal_objects"));
        Assert.True(await _bucket.ExistsAsync(key));

        // And it is not a one-tick reprieve that turns into a delete: the second tick reads the
        // same answer, because nothing between the two ticks changed.
        var again = await _host.SweepAsync(24);
        Assert.Equal(0, again.Failures);
        Assert.True(await _bucket.ExistsAsync(key));
    }

    /// <summary>
    /// Runs one of the operator media verbs with the signed fixture as its deployment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The verbs resolve <c>Comms__Media__*</c> and <c>Comms__ConversationConnectionString</c> from
    /// the environment through <c>CommsConfiguration</c>, so the test sets that environment and the
    /// commands build their own store from it. That indirection IS the point: a test that handed the
    /// handler a store would pass while <c>OpenStore()</c> resolved a different bucket, and the
    /// backup that "worked" in CI would be the one that restored nothing in production.
    /// </para>
    /// <para>
    /// The environment is set and restored by hand, per call, because xUnit runs classes in parallel
    /// and a leaked <c>Comms__Media__Endpoint</c> would point another class' operator commands at
    /// this bucket. <paramref name="signed"/> is used only to read the endpoint and bucket the
    /// environment must describe — the commands never receive it.
    /// </para>
    /// </remarks>
    private async Task<(int ExitCode, string Output)> RunMediaAsync(params string[] args)
    {
        var names = new[]
        {
            "Comms__Media__Endpoint", "Comms__Media__Bucket", "Comms__Media__AccessKey",
            "Comms__Media__SecretKey", "Comms__Media__Region",
            "Comms__ConversationConnectionString",
        };
        var previous = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);

        try
        {
            // ⚠️ The SIGNED bucket and the SIGNED credentials, and this is the whole reason the AC6
            //    test is written the way it is.
            //
            //    `JournalMediaCommands.OpenStore()` builds the store through its real constructor,
            //    which always builds a SIGNED client. Aimed at the identity-less primary bucket,
            //    every call the operator commands make is refused — so AC6 cannot be run against
            //    that bucket at all, and a test that "passed" there would have been reading a
            //    failure that has nothing to do with backup or restore.
            //
            //    The consequence is that the object under test must live in the SIGNED bucket, and
            //    the journal row pointing at it must be in the SAME scratch database the commands
            //    read. SeedAttachedObjectAsync does exactly that: the row is written directly and
            //    the message goes through the real ingest path, so the attachment→object pointer is
            //    the one production has.
            Environment.SetEnvironmentVariable("Comms__Media__Endpoint", fixture.SignedEndpoint);
            Environment.SetEnvironmentVariable("Comms__Media__Bucket", fixture.SignedBucket);
            Environment.SetEnvironmentVariable("Comms__Media__AccessKey", S3Fixture.SignedAccessKey);
            Environment.SetEnvironmentVariable("Comms__Media__SecretKey", S3Fixture.SignedSecretKey);
            Environment.SetEnvironmentVariable("Comms__Media__Region", "us-east-1");
            Environment.SetEnvironmentVariable("Comms__ConversationConnectionString", _host.ConnectionString);

            var output = new StringWriter();
            var errors = new StringWriter();

            // Dispatched through OperatorCommands, so the ROUTING is proven too — `media backup`
            // reaching BackupAsync is part of the claim, not plumbing.
            var code = await OperatorCommands.RunAsync(args, output, errors);
            return (code, output.ToString() + errors.ToString());
        }
        finally
        {
            foreach (var (name, value) in previous)
                Environment.SetEnvironmentVariable(name, value);
        }
    }

    private Task<(int ExitCode, string Output)> VerifyMediaAsync() =>
        RunMediaAsync("journal", "verify-media");

    /// <summary>
    /// Makes an object the store already holds part of the VERIFIED set: an attachment, on a
    /// message, on a conversation — created by the ingest path, not by hand.
    /// </summary>
    /// <remarks>
    /// <c>journal verify-media</c> walks <c>journal_attachments</c> joined to
    /// <c>journal_objects</c>, not the object table: a broken pointer between the two is the failure
    /// it exists to catch. So the test must create the pointer the same way production does.
    /// </remarks>
    private async Task SeedAttachedObjectAsync(string objectKey, string sha, long byteSize)
    {
        var objectId = objectKey[JournalObjectKeys.Prefix.Length..];
        await _host.SeedUploadedObjectAsync(objectId, sha, byteSize);

        Assert.Equal(System.Net.HttpStatusCode.Created,
            await _host.AttachAndCommitAsync(_host.CredentialA.Subject, objectId, sha, byteSize));
    }

    /// <summary>
    /// An inbound record that names a proven upload, stamped with <paramref name="sentAt"/>.
    /// </summary>
    /// <param name="botId">
    /// Part of the conversation key. Two messages with the same <c>(botId, chatId)</c> are ONE
    /// conversation, and retention deletes a conversation whole once it is empty — so a test that
    /// needs one message to expire and another to survive needs two conversations. The shared
    /// object is what binds them, which is exactly the case under test.
    /// </param>
    private static JournalRecord UploadRecordWithUpload(
        long chatId, Uploaded upload, long byteSize, DateTimeOffset sentAt, long botId = 7001) => new()
    {
        EventId = Fleet.Protocol.Ulid.NewUlid(),
        Telegram = new JournalTelegramRef
        {
            BotId = botId,
            ChatId = chatId,
            // Private, not supergroup: `JournalKeys.ConversationKey` for a supergroup is
            // `tg:group:<chatId>` and deliberately IGNORES BotId, so two records with the same chat
            // id are one conversation no matter what else differs. Retention deletes a conversation
            // whole once it is empty, so a test that needs one message aged out and another kept
            // must put them in two conversations — and a DM key does include the bot.
            ChatKind = JournalChatKind.Private,
            MessageId = Random.Shared.NextInt64(20_000_000, 900_000_000),
        },
        Direction = JournalDirection.Inbound,
        Sender = new JournalSender { Kind = JournalSenderKind.Human, Id = "111" },
        SentAt = sentAt,
        TextFormat = JournalTextFormat.Plain,
        Origin = JournalRecordOrigin.TelegramUpdate,
        Attachments =
        [
            new JournalAttachment
            {
                Ordinal = 0,
                Kind = JournalAttachmentKind.Photo,
                MimeType = "image/jpeg",
                ByteSize = byteSize,
                UploadId = upload.UploadId,
                UploadSha256 = upload.Sha256,
            },
        ],
    };

    private static string Field(string output, string name)
    {
        var line = output.Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Contains(name + "=", StringComparison.Ordinal));
        Assert.NotNull(line);

        var value = line![(line.IndexOf(name + "=", StringComparison.Ordinal) + name.Length + 1)..];
        var end = value.IndexOf(' ');
        return end < 0 ? value : value[..end];
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

    /// <summary>
    /// The host's clock. Public because a retention test has to move it: retention and the object
    /// sweep read the SAME provider the store stamped <c>sent_at</c> from, and a test with its own
    /// clock would compare two clocks and prove nothing.
    /// </summary>
    public ManualTime Time => _time;

    /// <summary>
    /// The journal store the routes use. For the tests that need to set <c>sentAt</c> from the
    /// host's clock, which the wire record cannot express independently of the parser.
    /// </summary>
    public MySqlJournalStore Store => _store;

    /// <summary>One statement against the journal database, for states the routes cannot produce.</summary>
    public async Task ExecuteAsync(string sql)
    {
        await using var connection = new MySqlConnector.MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new MySqlConnector.MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
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

    /// <summary>
    /// Attaches an object this test already wrote to the bucket, and journals the message that
    /// references it, through the REAL ingest path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ Deliberately not INSERT statements. <c>journal_messages</c> requires
    /// <c>order_key</c>, <c>fingerprint</c>, <c>recorded_at</c> and <c>delivery_state</c>, the
    /// conversation is keyed by a derived <c>conversation_key</c>, and the fingerprint is computed
    /// over the server-resolved object id — hand-writing rows produces a journal that is subtly
    /// unlike the one production has, or fails on a column name. A test of
    /// <c>journal verify-media</c> must walk the same attachment→object pointer the deployment walks.
    /// </para>
    /// <para>
    /// The object id is the only input, so the caller controls which bucket holds the bytes; the
    /// store's proof step is skipped by pointing at a row seeded directly into
    /// <c>journal_objects</c> in the <c>uploaded</c> state this subject owns.
    /// </para>
    /// </remarks>
    public async Task<HttpStatusCode> AttachAndCommitAsync(string subject, string objectId, string sha, long byteSize)
    {
        var record = UploadRecords.UploadAttachment(NextChatId(), objectId, sha, byteSize);
        var response = await CommitAsync(subject, record.ToJsonString());
        return response.StatusCode;
    }

    private static long _nextChat = 9_400_000;
    private static long NextChatId() => Interlocked.Increment(ref _nextChat);

    /// <summary>
    /// Seeds an object row the caller has already written to the bucket, in <c>uploaded</c> state,
    /// owned by <paramref name="subject"/> — the state the commit path accepts.
    /// </summary>
    public async Task SeedUploadedObjectAsync(string objectId, string sha, long byteSize,
        string subject = "agent1", string mime = "image/jpeg")
    {
        await ExecuteAsync(
            "INSERT INTO journal_objects (id, object_key, owner, sha256, committed_sha256, byte_size, "
            + "mime_type, state, created_at, updated_at) VALUES ("
            + $"'{objectId}', '{JournalObjectKeys.For(objectId)}', '{subject}', '{sha}', NULL, "
            + $"{byteSize}, '{mime}', 'uploaded', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6))");
    }

    /// <summary>Every object key the journal table names, in insertion order.</summary>
    public Task<IReadOnlyList<string>> ObjectKeysAsync() =>
        QueryAsync("SELECT object_key FROM journal_objects ORDER BY created_at, id");

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
