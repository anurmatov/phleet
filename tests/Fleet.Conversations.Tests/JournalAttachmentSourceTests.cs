using Fleet.Conversations.Contracts;
using Fleet.Conversations.Journal;
using Fleet.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Conversations.Tests;

public sealed partial class JournalReadStoreTests
{
    [Fact]
    public async Task Attachment_source_refuses_invalid_identifiers_before_opening_a_database()
    {
        IJournalAttachmentSource source = new MySqlJournalReadStore(
            "Server=127.0.0.1;Port=1;Database=unused;User ID=unused;Password=unused;", NullLogger.Instance);
        var reader = new JournalReader("agent1", JournalReadScope.All);
        await Assert.ThrowsAsync<ArgumentException>(() => source.FindAttachmentAsync(reader, "tg:group:-101", null, null, 0));
        await Assert.ThrowsAsync<ArgumentException>(() => source.FindAttachmentAsync(reader, "tg:group:-101", Ulid.NewUlid(), 5, 0));
        await Assert.ThrowsAsync<ArgumentException>(() => source.FindAttachmentAsync(reader, "tg:group:-101", "not-an-id", null, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => source.FindAttachmentAsync(reader, "tg:group:-101", null, 5, 256));
    }

    [Fact]
    public async Task Attachment_source_applies_observership_and_binding_to_both_id_forms()
    {
        var subject = Subject();
        var other = Subject();
        var a = Chat();
        var b = Chat();
        var write = new MySqlJournalStore(Db, NullLogger.Instance);
        var first = (await write.IngestAsync(Message(a, 5, BaseTime(), "alpha", attachments: [Attachment(0)]), subject)).MessageId!;
        var foreign = (await write.IngestAsync(Message(b, 5, BaseTime(), "bravo", attachments: [Attachment(0)]), subject)).MessageId!;
        var hidden = (await write.IngestAsync(Message(a, 6, BaseTime(), "hidden", attachments: [Attachment(0)]), other)).MessageId!;
        var objectId = Ulid.NewUlid();
        var digest = new string('a', 64);
        await fixture.ExecuteAsync($"""
            INSERT INTO journal_objects
              (id, object_key, owner, sha256, committed_sha256, byte_size, mime_type, state, created_at, updated_at)
            VALUES ('{objectId}', 'synthetic/{objectId}', '{other}', '{digest}', '{digest}', 1234,
                    'application/pdf', 'committed', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
            UPDATE journal_attachments SET object_id = '{objectId}', state = 'committed',
              not_archived_reason = NULL, sha256 = '{digest}' WHERE message_id = '{first}';
            """);
        IJournalAttachmentSource source = new MySqlJournalReadStore(Db, NullLogger.Instance);
        var observed = new JournalReader(subject, JournalReadScope.Observed);
        var key = $"tg:group:{a}";
        var byUlid = await source.FindAttachmentAsync(observed, key, first, null, 0);
        var byTelegram = await source.FindAttachmentAsync(observed, key, null, 5, 0);
        Assert.Equal(byUlid, byTelegram);
        Assert.NotNull(byUlid);
        Assert.Equal(first, byUlid.MessageId);
        Assert.Equal("committed", byUlid.AttachmentState);
        Assert.Equal("committed", byUlid.ObjectState);
        Assert.Equal($"synthetic/{objectId}", byUlid.ObjectKey);
        Assert.Equal(digest, byUlid.Sha256);
        Assert.Equal(digest, byUlid.ObjectSha256);
        Assert.Equal(nameof(JournalAttachmentLocator), byUlid.ToString());
        Assert.Equal(1234, byUlid.ObjectByteSize);
        Assert.Null(await source.FindAttachmentAsync(observed, key, foreign, null, 0));
        Assert.Null(await source.FindAttachmentAsync(observed, key, hidden, null, 0));
        Assert.Null(await source.FindAttachmentAsync(observed, key, null, 6, 0));
        Assert.Null(await source.FindAttachmentAsync(observed, key, first, null, 255));
        Assert.Null(await source.FindAttachmentAsync(observed, key, Ulid.NewUlid(), null, 0));
        var all = new JournalReader(subject, JournalReadScope.All);
        Assert.Null(await source.FindAttachmentAsync(all, key, foreign, null, 0));
        var unavailable = await source.FindAttachmentAsync(all, key, hidden, null, 0);
        Assert.NotNull(unavailable);
        Assert.Equal("not_archived", unavailable.AttachmentState);
        Assert.Equal("media_disabled", unavailable.NotArchivedReason);
        Assert.Null(unavailable.ObjectState);
        Assert.Null(unavailable.ObjectKey);
        await fixture.ExecuteAsync($"UPDATE journal_attachments SET state = 'lost' WHERE message_id = '{first}';");
        Assert.Equal("lost", (await source.FindAttachmentAsync(observed, key, first, null, 0))!.AttachmentState);
    }
}
