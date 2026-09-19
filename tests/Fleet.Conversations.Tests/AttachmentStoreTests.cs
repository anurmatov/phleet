using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fleet.Conversations.Contracts;
using Fleet.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Conversations.Tests;

/// <summary>
/// Attachment reservation, binding, the caps and the sweeps — against a real MySQL (#308).
/// </summary>
/// <remarks>
/// <para>
/// Not a fake, and the reason is the same one the transcript suite gives: the conversation row lock,
/// the conditional binding UPDATE and the affected-row count ARE the mechanism. A substitute agrees
/// with whatever it was told, so it would pass while the property it is meant to prove was absent.
/// </para>
/// <para>
/// Almost every assertion reads the store back rather than trusting a return value. "The accept
/// rolled back whole" is a claim about what a later reader finds, and nothing a write returns can
/// establish it.
/// </para>
/// </remarks>
[Collection("mysql")]
public sealed class AttachmentStoreTests(MySqlFixture fixture)
{
    private readonly MySqlConversationStore _store = fixture.CreateStore();

    private const string Owner = "p_owner";

    // ── binding ──────────────────────────────────────────────────────────────

    /// <summary>
    /// AC-1 and AC-9: one accepted submission produces exactly ONE transcript entry carrying the
    /// descriptor, and the bound row's <c>event_seq</c> is the seq the accept reported.
    /// </summary>
    [Fact]
    public async Task A_submission_with_an_attachment_produces_exactly_one_transcript_entry()
    {
        var conversation = await OpenAsync();
        var attachment = await SealedAsync(conversation.ConversationId);

        var accept = await AcceptAsync(conversation.ConversationId, "what is this?", [attachment]);

        Assert.Equal(AcceptOutcome.Accepted, accept.Outcome);

        var page = await ReadFromAsync(conversation.ConversationId, accept.AcceptedSeq!.Value - 1);

        // ONE, not two. A sibling kind per attachment would produce N+1 entries for one submission.
        var entry = Assert.Single(page.Events);
        Assert.Equal(ConversationEventKind.SubmissionText, entry.Kind);

        var payload = JsonDocument.Parse(entry.PayloadJson!).RootElement;
        Assert.Equal("what is this?", payload.GetProperty("text").GetString());

        var descriptor = Assert.Single(payload.GetProperty("attachments").EnumerateArray());
        Assert.Equal(attachment, descriptor.GetProperty("attachmentId").GetString());
        Assert.Equal("image", descriptor.GetProperty("kind").GetString());
        Assert.Equal("image/png", descriptor.GetProperty("contentType").GetString());

        // AC-9: the stamped seq is the accept floor, which is the entry's own seq.
        Assert.Equal(
            accept.AcceptedSeq!.Value.ToString(),
            await fixture.ScalarRowAsync(
                $"SELECT event_seq FROM conversation_attachments WHERE id = '{attachment}'"));

        Assert.Equal(
            "bound",
            await fixture.ScalarRowAsync(
                $"SELECT state FROM conversation_attachments WHERE id = '{attachment}'"));
    }

    /// <summary>
    /// AC-5: one attachment binds to at most one submission, and the first submission is untouched
    /// by the second's refusal.
    /// </summary>
    /// <remarks>
    /// Without this the retention argument in D3 is false: two live transcript entries would share
    /// one row and one retention clock, and pruning the older would delete the bytes under the
    /// newer while it sat well inside its own horizon.
    /// </remarks>
    [Fact]
    public async Task An_attachment_bound_once_cannot_be_bound_again()
    {
        var conversation = await OpenAsync();
        var attachment = await SealedAsync(conversation.ConversationId);

        var first = await AcceptAsync(conversation.ConversationId, "first", [attachment]);

        await Assert.ThrowsAsync<AttachmentNotBindableException>(
            () => AcceptAsync(conversation.ConversationId, "second", [attachment]));

        // The first entry still carries its descriptor, and the row still points at the first
        // submission.
        var page = await ReadFromAsync(conversation.ConversationId, first.AcceptedSeq!.Value - 1);
        var entry = Assert.Single(page.Events);

        Assert.Equal("first", JsonDocument.Parse(entry.PayloadJson!).RootElement
            .GetProperty("text").GetString());

        Assert.Equal(
            first.SubmissionId,
            await fixture.ScalarRowAsync(
                $"SELECT submission_id FROM conversation_attachments WHERE id = '{attachment}'"));
    }

    /// <summary>
    /// AC-7: one unknown id refuses the WHOLE submission — no submission row, no transcript event,
    /// no command — and leaves the valid attachment bindable.
    /// </summary>
    [Fact]
    public async Task One_unresolvable_attachment_rolls_the_whole_accept_back()
    {
        var conversation = await OpenAsync();
        var valid = await SealedAsync(conversation.ConversationId);

        var before = await CountsAsync(conversation.ConversationId);

        await Assert.ThrowsAsync<AttachmentNotBindableException>(
            () => AcceptAsync(
                conversation.ConversationId, "two images", [valid, "01JNOSUCHATTACHMENT0000001"]));

        var after = await CountsAsync(conversation.ConversationId);

        // Four tables, asserted by row count rather than by absence of an error.
        Assert.Equal(before, after);

        // Still sealed, still bindable — and the proof is that it binds.
        Assert.Equal(
            "sealed",
            await fixture.ScalarRowAsync(
                $"SELECT state FROM conversation_attachments WHERE id = '{valid}'"));

        var retry = await AcceptAsync(conversation.ConversationId, "one image", [valid]);
        Assert.Equal(AcceptOutcome.Accepted, retry.Outcome);
    }

    /// <summary>An attachment belonging to another conversation is not bindable here.</summary>
    [Fact]
    public async Task An_attachment_from_another_conversation_cannot_be_bound()
    {
        var mine = await OpenAsync();
        var theirs = await OpenAsync();

        var foreign = await SealedAsync(theirs.ConversationId);

        await Assert.ThrowsAsync<AttachmentNotBindableException>(
            () => AcceptAsync(mine.ConversationId, "borrowed", [foreign]));

        Assert.Equal(
            "sealed",
            await fixture.ScalarRowAsync(
                $"SELECT state FROM conversation_attachments WHERE id = '{foreign}'"));
    }

    /// <summary>A reservation with no bytes is not bindable — only a sealed row is.</summary>
    [Fact]
    public async Task A_reserved_attachment_cannot_be_bound()
    {
        var conversation = await OpenAsync();
        var reserved = await ReservedAsync(conversation.ConversationId);

        await Assert.ThrowsAsync<AttachmentNotBindableException>(
            () => AcceptAsync(conversation.ConversationId, "too early", [reserved]));
    }

    /// <summary>
    /// AC-10: the same key with a DIFFERENT attachment is a conflict, and an identical replay binds
    /// nothing further.
    /// </summary>
    [Fact]
    public async Task The_idempotency_key_is_bound_to_the_attachment_ids()
    {
        var conversation = await OpenAsync();
        var first = await SealedAsync(conversation.ConversationId);
        var second = await SealedAsync(conversation.ConversationId);

        const string key = "k-attachments";

        var accepted = await AcceptAsync(conversation.ConversationId, "look", [first], key);
        Assert.Equal(AcceptOutcome.Accepted, accepted.Outcome);

        // Same key, same text, DIFFERENT photo.
        var conflict = await AcceptAsync(conversation.ConversationId, "look", [second], key);
        Assert.Equal(AcceptOutcome.Conflict, conflict.Outcome);

        // The second attachment was never touched — the conflict is decided before the bind.
        Assert.Equal(
            "sealed",
            await fixture.ScalarRowAsync(
                $"SELECT state FROM conversation_attachments WHERE id = '{second}'"));

        // An identical replay returns the original seq and binds nothing further.
        var replay = await AcceptAsync(conversation.ConversationId, "look", [first], key);
        Assert.Equal(AcceptOutcome.Replay, replay.Outcome);
        Assert.Equal(accepted.AcceptedSeq, replay.AcceptedSeq);

        Assert.Equal(
            accepted.SubmissionId,
            await fixture.ScalarRowAsync(
                $"SELECT submission_id FROM conversation_attachments WHERE id = '{first}'"));
    }

    /// <summary>AC-4 and AC-26 — a text-only entry omits the key entirely.</summary>
    [Fact]
    public async Task A_text_only_submission_omits_the_attachments_key()
    {
        var conversation = await OpenAsync();
        var accept = await AcceptAsync(conversation.ConversationId, "no photo", []);

        var page = await ReadFromAsync(conversation.ConversationId, accept.AcceptedSeq!.Value - 1);
        var entry = Assert.Single(page.Events);

        // Not "an empty array" — the key is absent from the serialized payload, so a deployed client
        // sees the pre-#308 shape byte for byte.
        Assert.DoesNotContain("attachments", entry.PayloadJson!, StringComparison.Ordinal);
        Assert.False(JsonDocument.Parse(entry.PayloadJson!).RootElement
            .TryGetProperty("attachments", out _));
    }

    // ── caps ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// AC-15: concurrent reservations that would collectively exceed the per-conversation cap are
    /// refused, and the committed total never exceeds it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asserted with <c>reserved</c> rows and no uploads at all, because that is the case a
    /// sealed-only accounting gets wrong: N concurrent reserves each read the same under-cap total
    /// and collectively pass it.
    /// </para>
    /// <para>
    /// The reservations really are concurrent — they are started together and awaited together, so
    /// the serialization is the row lock's doing rather than the test's.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Concurrent_reservations_cannot_collectively_exceed_the_conversation_cap()
    {
        var conversation = await OpenAsync();

        // Eight at the maximum per-attachment size against a cap that admits far fewer.
        var each = ProtocolLimits.MaxAttachmentBytes;
        var admissible = ProtocolLimits.MaxConversationAttachmentBytes / each;

        var attempts = Enumerable.Range(0, (int)admissible + 4)
            .Select(_ => ReserveAsync(conversation.ConversationId, each))
            .ToArray();

        var results = await Task.WhenAll(attempts);

        Assert.Contains(results, r => r.Outcome == ReserveOutcome.QuotaExceeded);

        var live = long.Parse(await fixture.ScalarRowAsync(
            $"""
             SELECT COALESCE(SUM(declared_byte_size), 0) FROM conversation_attachments
              WHERE conversation_id = '{conversation.ConversationId}' AND state <> 'failed'
             """));

        Assert.True(
            live <= ProtocolLimits.MaxConversationAttachmentBytes,
            $"committed live bytes {live} exceeded the cap");

        Assert.Equal(
            results.Count(r => r.Outcome == ReserveOutcome.Reserved) * each, live);
    }

    /// <summary>A failed row does not count against the cap; an unswept reservation does.</summary>
    [Fact]
    public async Task Failed_rows_do_not_count_and_unswept_reservations_do()
    {
        var conversation = await OpenAsync();
        var each = ProtocolLimits.MaxAttachmentBytes;
        var admissible = (int)(ProtocolLimits.MaxConversationAttachmentBytes / each);

        for (var i = 0; i < admissible; i++)
            Assert.Equal(ReserveOutcome.Reserved, (await ReserveAsync(conversation.ConversationId, each)).Outcome);

        // Full: an expired-but-unswept reservation still counts, which is what stops a reserve storm
        // from racing the sweeper.
        await AgeReservationsAsync(conversation.ConversationId, ProtocolLimits.AttachmentUploadWindow * 2);
        Assert.Equal(
            ReserveOutcome.QuotaExceeded,
            (await ReserveAsync(conversation.ConversationId, each)).Outcome);

        // Failing them all releases the whole budget.
        await fixture.ExecuteAsync(
            $"""
             UPDATE conversation_attachments SET state = 'failed'
              WHERE conversation_id = '{conversation.ConversationId}'
             """);

        Assert.Equal(
            ReserveOutcome.Reserved,
            (await ReserveAsync(conversation.ConversationId, each)).Outcome);
    }

    // ── the two windows ──────────────────────────────────────────────────────

    /// <summary>
    /// AC-16: the upload and submit windows have DIFFERENT anchors, so a row sealed after the upload
    /// window closed is still bindable — and one past the submit window is not.
    /// </summary>
    /// <remarks>
    /// This is the assertion that pins the two apart. Sharing the 15-minute anchor would leave a
    /// client that sealed at minute 14 exactly one minute to send its message, and a single-window
    /// implementation would pass every other test in this file.
    /// </remarks>
    [Fact]
    public async Task A_row_sealed_past_the_upload_window_is_still_bindable()
    {
        var conversation = await OpenAsync();
        var attachment = await SealedAsync(conversation.ConversationId);

        // Created 20 minutes ago — past the 15-minute UPLOAD window — but sealed just now.
        await fixture.ExecuteAsync(
            $"""
             UPDATE conversation_attachments
                SET created_at = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 20 MINUTE)
              WHERE id = '{attachment}'
             """);

        var accept = await AcceptAsync(conversation.ConversationId, "late but fine", [attachment]);
        Assert.Equal(AcceptOutcome.Accepted, accept.Outcome);
    }

    [Fact]
    public async Task A_row_past_the_submit_window_is_not_bindable()
    {
        var conversation = await OpenAsync();
        var attachment = await SealedAsync(conversation.ConversationId);

        await fixture.ExecuteAsync(
            $"""
             UPDATE conversation_attachments
                SET sealed_at = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 61 MINUTE)
              WHERE id = '{attachment}'
             """);

        await Assert.ThrowsAsync<AttachmentNotBindableException>(
            () => AcceptAsync(conversation.ConversationId, "too late", [attachment]));
    }

    // ── retention and the sweeps ─────────────────────────────────────────────

    /// <summary>
    /// AC-22: advancing the retained floor past a <c>submission.text</c> deletes its attachment rows
    /// in the SAME transaction, and its files after the commit.
    /// </summary>
    [Fact]
    public async Task Advancing_the_floor_deletes_the_attachment_row_and_then_its_file()
    {
        var conversation = await OpenAsync();
        var attachment = await SealedAsync(conversation.ConversationId);
        await AcceptAsync(conversation.ConversationId, "old photo", [attachment]);

        var root = TempRoot();
        var files = new AttachmentStore(root, NullLogger.Instance);
        await WriteFileAsync(files, attachment);

        // Age the transcript entry past the durable horizon.
        await fixture.ExecuteAsync(
            $"""
             UPDATE conversation_events
                SET emitted_at = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 40 DAY)
              WHERE conversation_id = '{conversation.ConversationId}'
             """);

        var collector = new GarbageCollector(
            fixture.ConnectionString, fixture.Options, NullLogger.Instance, files);

        var swept = await collector.SweepOnceAsync();

        Assert.Equal(1, swept.PrunedAttachments);
        Assert.Equal("0", await fixture.ScalarRowAsync(
            $"SELECT COUNT(*) FROM conversation_attachments WHERE id = '{attachment}'"));
        Assert.False(File.Exists(files.PathFor(attachment)));

        Directory.Delete(root, recursive: true);
    }

    /// <summary>
    /// AC-24: reserved-never-uploaded, sealed-never-submitted and <c>failed</c> are each collected,
    /// and each by the window that belongs to it.
    /// </summary>
    [Fact]
    public async Task The_three_stranded_classes_are_each_swept()
    {
        var conversation = await OpenAsync();

        var expired = await ReservedAsync(conversation.ConversationId);
        var abandoned = await SealedAsync(conversation.ConversationId);
        var failed = await ReservedAsync(conversation.ConversationId);

        // A live reservation and a fresh seal, which must SURVIVE — otherwise this test would pass
        // for an implementation that collected everything.
        var live = await ReservedAsync(conversation.ConversationId);
        var fresh = await SealedAsync(conversation.ConversationId);

        await fixture.ExecuteAsync(
            $"""
             UPDATE conversation_attachments
                SET created_at = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 20 MINUTE)
              WHERE id IN ('{expired}', '{failed}')
             """);

        await fixture.ExecuteAsync(
            $"UPDATE conversation_attachments SET state = 'failed' WHERE id = '{failed}'");

        await fixture.ExecuteAsync(
            $"""
             UPDATE conversation_attachments
                SET sealed_at = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 61 MINUTE)
              WHERE id = '{abandoned}'
             """);

        var root = TempRoot();
        var files = new AttachmentStore(root, NullLogger.Instance);
        foreach (var id in new[] { expired, abandoned, failed, live, fresh })
            await WriteFileAsync(files, id);

        var collector = new GarbageCollector(
            fixture.ConnectionString, fixture.Options, NullLogger.Instance, files);

        var swept = await collector.SweepOnceAsync();

        Assert.Equal(3, swept.StrandedAttachments);

        foreach (var id in new[] { expired, abandoned, failed })
        {
            Assert.Equal("0", await fixture.ScalarRowAsync(
                $"SELECT COUNT(*) FROM conversation_attachments WHERE id = '{id}'"));
            Assert.False(File.Exists(files.PathFor(id)));
        }

        foreach (var id in new[] { live, fresh })
        {
            Assert.Equal("1", await fixture.ScalarRowAsync(
                $"SELECT COUNT(*) FROM conversation_attachments WHERE id = '{id}'"));
            Assert.True(File.Exists(files.PathFor(id)));
        }

        Directory.Delete(root, recursive: true);
    }

    /// <summary>
    /// AC-24, fourth class: a file with no row at all is collected — the safety net for the window
    /// between the row deletion committing and the file delete running.
    /// </summary>
    [Fact]
    public async Task A_file_with_no_row_is_swept()
    {
        var conversation = await OpenAsync();
        var kept = await SealedAsync(conversation.ConversationId);

        var root = TempRoot();
        var files = new AttachmentStore(root, NullLogger.Instance);

        await WriteFileAsync(files, kept);
        await WriteFileAsync(files, "01JORPHANATTACHMENT0000001");

        var collector = new GarbageCollector(
            fixture.ConnectionString, fixture.Options, NullLogger.Instance, files);

        var swept = await collector.SweepOnceAsync();

        Assert.Equal(1, swept.OrphanFiles);
        Assert.False(File.Exists(files.PathFor("01JORPHANATTACHMENT0000001")));
        Assert.True(File.Exists(files.PathFor(kept)));

        Directory.Delete(root, recursive: true);
    }

    // ── fetch ────────────────────────────────────────────────────────────────

    /// <summary>A foreign principal and an unknown id are both null — no existence oracle.</summary>
    [Fact]
    public async Task A_fetch_scoped_to_a_foreign_principal_finds_nothing()
    {
        var conversation = await OpenAsync();
        var attachment = await SealedAsync(conversation.ConversationId);

        Assert.NotNull(await _store.GetAttachmentForPrincipalAsync(attachment, Owner));
        Assert.Null(await _store.GetAttachmentForPrincipalAsync(attachment, "p_someone_else"));
        Assert.Null(await _store.GetAttachmentForPrincipalAsync("01JNOSUCHATTACHMENT0000001", Owner));
    }

    /// <summary>The digest is stored; the capability is not recoverable from the row.</summary>
    [Fact]
    public async Task Only_the_digest_of_the_capability_is_stored()
    {
        var conversation = await OpenAsync();
        const string capability = "an-upload-capability-value";

        var reserved = await _store.ReserveAttachmentAsync(new ReserveAttachmentRequest
        {
            ConversationId = conversation.ConversationId,
            PrincipalId = Owner,
            Kind = AttachmentKind.Image,
            ContentType = "image/png",
            ByteSize = 16,
            Sha256 = new string('a', 64),
            UploadTokenSha256 = Digest(capability),
        });

        var row = await fixture.ScalarRowAsync(
            $"SELECT upload_token_sha256 FROM conversation_attachments WHERE id = '{reserved.AttachmentId}'");

        Assert.Equal(Digest(capability), row);
        Assert.DoesNotContain(capability, row, StringComparison.Ordinal);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private Task<OpenConversationResult> OpenAsync() =>
        _store.OpenConversationAsync(new OpenConversationRequest
        {
            ChannelId = "client",
            ExternalRef = "conv-" + Ulid.NewUlid(),
            PrincipalId = Owner,
        });

    private Task<ReserveAttachmentResult> ReserveAsync(string conversationId, long byteSize) =>
        _store.ReserveAttachmentAsync(new ReserveAttachmentRequest
        {
            ConversationId = conversationId,
            PrincipalId = Owner,
            Kind = AttachmentKind.Image,
            ContentType = "image/png",
            ByteSize = byteSize,
            Sha256 = new string('a', 64),
            UploadTokenSha256 = new string('b', 64),
        });

    private async Task<string> ReservedAsync(string conversationId)
    {
        var reserved = await ReserveAsync(conversationId, 16);
        Assert.Equal(ReserveOutcome.Reserved, reserved.Outcome);
        return reserved.AttachmentId!;
    }

    private async Task<string> SealedAsync(string conversationId)
    {
        var id = await ReservedAsync(conversationId);

        Assert.True(await _store.SealAttachmentAsync(new SealAttachmentRequest
        {
            AttachmentId = id,
            ByteSize = 16,
            Sha256 = new string('a', 64),
            SniffedContentType = "image/png",
        }));

        return id;
    }

    private Task<AcceptSubmissionResult> AcceptAsync(
        string conversationId, string text, IReadOnlyList<string> attachmentIds, string? key = null) =>
        _store.AcceptSubmissionAsync(new AcceptSubmissionRequest
        {
            ConversationId = conversationId,
            ExternalSubmissionId = Ulid.NewUlid(),
            PayloadFingerprint = PayloadFingerprint.Compute(text, null, conversationId, attachmentIds),
            IdempotencyKey = key,
            CommandKind = ConversationEventKind.SubmissionCreate,
            CommandPayloadJson = FleetProtocolJson.Serialize(new { payload = new { text } }),
            TranscriptText = text,
            AttachmentIds = attachmentIds.Count > 0 ? attachmentIds : null,
        });

    private Task<ReadConversationResult> ReadFromAsync(string conversationId, ulong afterSeq) =>
        _store.ReadAsync(new ReadConversationRequest
        {
            ConversationId = conversationId,
            AfterSeq = afterSeq,
            Limit = 50,
            PrincipalId = Owner,
        });

    /// <summary>Row counts on the four tables an accept writes to.</summary>
    private async Task<string> CountsAsync(string conversationId) =>
        string.Join(
            '/',
            await fixture.ScalarRowAsync(
                $"SELECT COUNT(*) FROM submissions WHERE conversation_id = '{conversationId}'"),
            await fixture.ScalarRowAsync(
                $"SELECT COUNT(*) FROM conversation_events WHERE conversation_id = '{conversationId}'"),
            await fixture.ScalarRowAsync(
                $"SELECT COUNT(*) FROM command_outbox WHERE conversation_id = '{conversationId}'"),
            await fixture.ScalarRowAsync(
                $"""
                 SELECT COUNT(*) FROM execution_attempts a
                   JOIN submissions s ON s.id = a.submission_id
                  WHERE s.conversation_id = '{conversationId}'
                 """));

    private Task AgeReservationsAsync(string conversationId, TimeSpan by) =>
        fixture.ExecuteAsync(
            $"""
             UPDATE conversation_attachments
                SET created_at = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL {(int)by.TotalSeconds} SECOND)
              WHERE conversation_id = '{conversationId}' AND state = 'reserved'
             """);

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"fleet-attach-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task WriteFileAsync(AttachmentStore files, string attachmentId)
    {
        using var bytes = new MemoryStream(Encoding.UTF8.GetBytes("bytes"));
        await files.WriteAsync(attachmentId, bytes, maxBytes: 1024, CancellationToken.None);
    }

    private static string Digest(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
