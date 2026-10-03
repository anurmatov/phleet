using Fleet.Conversations.Contracts;
using Fleet.Conversations.Journal;
using Fleet.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Conversations.Tests;

[Collection("mysql")]
public sealed class JournalAttachmentSendMigrationTests(MySqlFixture fixture)
{
    [Fact]
    public async Task CopyRecord_PersistsBotProvenanceAndSourceLink()
    {
        var record = new JournalRecord
        {
            EventId = Ulid.NewUlid(),
            Telegram = new JournalTelegramRef
            {
                BotId = 5005, ChatId = 1001, ChatKind = JournalChatKind.Private,
                MessageId = 100,
            },
            Direction = JournalDirection.Outbound,
            Sender = new JournalSender { Kind = JournalSenderKind.Agent, Id = "5005" },
            SentAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Text = null,
            Origin = JournalRecordOrigin.AgentCopy,
            Attachments = [new JournalAttachment
            {
                Ordinal = 0, Kind = JournalAttachmentKind.Document, MimeType = "application/pdf",
                ByteSize = 9, FileId = "FILE-SYNTH-1", NotArchivedReason = JournalNotArchivedReason.Copied,
                CopiedFrom = new JournalCopiedFrom { MessageId = "01K00000000000000000000000", Ordinal = 0 },
            }],
        };
        var store = new MySqlJournalStore(fixture.ConnectionString, NullLogger.Instance);
        var result = await store.IngestAsync(record, "agent1");
        Assert.Equal(JournalIngestOutcome.Created, result.Outcome);
        Assert.Equal("copied|agent_copy|not_archived|copied|FILE-SYNTH-1|5005|01K00000000000000000000000|0",
            await fixture.ScalarRowAsync("SELECT m.delivery_state, m.origin, a.state, a.not_archived_reason, "
                + "a.telegram_file_id, a.telegram_file_id_bot_id, a.copied_from_message_id, a.copied_from_ordinal "
                + "FROM journal_messages m JOIN journal_attachments a ON a.message_id = m.id "
                + $"WHERE m.id = '{result.MessageId}'"));
    }

    [Fact]
    public async Task Migration_RepeatedRun_DoesNotReapply()
    {
        var before = await fixture.ScalarRowAsync("SELECT COUNT(*) FROM schema_migrations WHERE version = 6");
        await new MigrationRunner(fixture.MigrationConnectionString).MigrateAsync();
        Assert.Equal("1", before);
        Assert.Equal(before, await fixture.ScalarRowAsync("SELECT COUNT(*) FROM schema_migrations WHERE version = 6"));
        Assert.Equal("4", await fixture.ScalarRowAsync("SELECT COUNT(*) FROM information_schema.columns "
            + "WHERE table_schema = DATABASE() AND table_name = 'journal_attachments' "
            + "AND column_name IN ('telegram_file_id', 'telegram_file_id_bot_id', 'copied_from_message_id', 'copied_from_ordinal')"));
    }
}
