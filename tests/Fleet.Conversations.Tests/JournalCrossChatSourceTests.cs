using Fleet.Conversations.Contracts;
using Fleet.Conversations.Journal;
using Fleet.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
namespace Fleet.Conversations.Tests;
[Collection("mysql")]
public sealed class JournalCrossChatSourceTests(MySqlFixture fixture)
{
    private static JournalRecord Record(long chat, long message, DateTimeOffset at, string sender, bool attachment) => new()
    {
        EventId = Ulid.NewUlid(), Telegram = new() { BotId = 5005, ChatId = chat, ChatKind = JournalChatKind.Supergroup, MessageId = message },
        Direction = JournalDirection.Inbound, Origin = JournalRecordOrigin.TelegramUpdate, Sender = new() { Kind = JournalSenderKind.Human, Id = sender }, SentAt = at,
        Attachments = attachment ? [new() { Ordinal = 0, Kind = JournalAttachmentKind.Document, MimeType = "application/pdf", ByteSize = 9,
            FileId = "SYNTHETIC-FILE", NotArchivedReason = JournalNotArchivedReason.MediaDisabled }] : [],
    };
    [Theory]
    [InlineData(-1, "agent1", true)]
    [InlineData(1, "agent1", false)]
    [InlineData(-1, "agent2", false)]
    public async Task Participation_IsObservedAndAtOrBeforeSource(int offset, string participantObserver, bool expected)
    {
        var chat = -Random.Shared.NextInt64(10000, long.MaxValue);
        var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var store = new MySqlJournalStore(fixture.ConnectionString, NullLogger.Instance);
        await store.IngestAsync(Record(chat, 1, at.AddMinutes(offset), "1001", false), participantObserver);
        var created = await store.IngestAsync(Record(chat, 2, at, "1002", true), "agent1");
        var reads = new MySqlJournalReadStore(fixture.ConnectionString, NullLogger.Instance);
        var found = await reads.FindSendSourceAsync("agent1", "tg:dm:5005:1001", 1001, created.MessageId, null, 0);
        Assert.NotNull(found); Assert.Equal(expected, found.RequesterWasPresent); Assert.Equal(chat, found.ChatId);
        Assert.Equal("SYNTHETIC-FILE", found.FileId); Assert.Equal(5005, found.FileIdBotId);
    }
    [Fact]
    public async Task HiddenAndMissing_AreIdenticalEvenForAnAllScopeReader()
    {
        var store = new MySqlJournalStore(fixture.ConnectionString, NullLogger.Instance);
        var created = await store.IngestAsync(Record(-Random.Shared.NextInt64(10000, long.MaxValue), 1, DateTimeOffset.UtcNow, "1002", true), "agent2");
        var reads = new MySqlJournalReadStore(fixture.ConnectionString, NullLogger.Instance);
        // The ordinary read-all surface can see this row. Send resolution never accepts that scope.
        Assert.NotNull(await reads.GetMessageAsync(new("agent1", JournalReadScope.All), created.MessageId));
        Assert.Null(await reads.FindSendSourceAsync("agent1", "tg:dm:5005:1001", 1001, created.MessageId, null, 0));
        Assert.Null(await reads.FindSendSourceAsync("agent1", "tg:dm:5005:1001", 1001, Ulid.NewUlid(), null, 0));
    }
    [Fact]
    public async Task TelegramIdentifier_IsBoundOnlyButUlidCanResolveObservedGroup()
    {
        var chat = -Random.Shared.NextInt64(10000, long.MaxValue); var store = new MySqlJournalStore(fixture.ConnectionString, NullLogger.Instance);
        var created = await store.IngestAsync(Record(chat, 987, DateTimeOffset.UtcNow, "1002", true), "agent1");
        var reads = new MySqlJournalReadStore(fixture.ConnectionString, NullLogger.Instance);
        Assert.Null(await reads.FindSendSourceAsync("agent1", "tg:dm:5005:1001", 1001, null, 987, 0));
        Assert.NotNull(await reads.FindSendSourceAsync("agent1", "tg:dm:5005:1001", 1001, created.MessageId, null, 0));
    }
}
