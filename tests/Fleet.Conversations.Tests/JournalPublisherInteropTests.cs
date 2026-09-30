using System.Net;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Services;
using Fleet.Conversations.Contracts;
using Fleet.Journal.Client;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ClientOptions = Fleet.Journal.Client.JournalOptions;

namespace Fleet.Conversations.Tests;

/// <summary>
/// The agent's journal records, exactly as the agent spools them, through the real listener into
/// a real MySQL 8.0 (#377 AC7 and the wire contract between the two slices).
/// </summary>
[Collection("mysql")]
public sealed class JournalPublisherInteropTests(MySqlFixture fixture) : IDisposable
{
    private const long Human = 111;
    private const long Supergroup = -1000000000377;
    private const long Dm = 377;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "journal-interop-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// AC7: two agents in one supergroup receive one voice message and hear it differently. Their
    /// records agree on everything the fingerprint reads, so the first is created and the second is
    /// an observer of the same message — not a conflict and not a second message.
    /// </summary>
    [Fact]
    public async Task Two_agents_journaling_one_voice_message_are_created_then_observer_added()
    {
        await using var host = await JournalHttpHost.StartAsync(fixture.ConnectionString);

        var first = Capture("a", Voice(botId: 7101, transcript: "see you at noon"));
        var second = Capture("b", Voice(botId: 7102, transcript: "see you at new"));

        var created = await host.PostAsync(first, "agent1");
        var observed = await host.PostAsync(second, "agent2");
        if (created.StatusCode != HttpStatusCode.Created)
            throw new InvalidOperationException($"created -> {(int)created.StatusCode} {await created.Content.ReadAsStringAsync()}");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, observed.StatusCode);
        Assert.Contains("\"result\":\"observer_added\"", await observed.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        Assert.Equal("1", await fixture.ScalarRowAsync(
            "SELECT COUNT(*) FROM journal_messages m JOIN journal_conversations c ON c.id = m.conversation_id "
            + $"WHERE c.telegram_chat_id = {Supergroup}"));
        Assert.Equal("2", await fixture.ScalarRowAsync(
            "SELECT COUNT(*) FROM journal_message_observers o JOIN journal_messages m ON m.id = o.message_id "
            + $"JOIN journal_conversations c ON c.id = m.conversation_id WHERE c.telegram_chat_id = {Supergroup}"));
    }

    /// <summary>
    /// A DM with a photo, and a reply split into two messages: every shape the agent writes in S2 —
    /// attachments not archived, a send group — is accepted as written.
    /// </summary>
    [Fact]
    public async Task What_the_agent_spools_is_what_the_listener_accepts()
    {
        await using var host = await JournalHttpHost.StartAsync(fixture.ConnectionString);

        var photo = Capture("dm", new JournalMessage
        {
            BotId = 7101, ChatId = Dm, ChatType = "private", MessageId = 1, Date = Now,
            SenderKind = JournalSenderKind.Human, SenderId = Dm.ToString(), SenderDisplay = "Ann",
            Text = "привет, look", Media = [new JournalMediaItem(JournalAttachmentKind.Photo, "image/jpeg", 3, null, "uq-1", Bytes: [1, 2, 3])],
        });
        Assert.Equal(HttpStatusCode.Created, (await host.PostAsync(photo, "agent1")).StatusCode);

        var spool = new JournalSpool(Path.Combine(_root, "reply"));
        var capture = NewCapture(spool);
        var batch = capture.Outbound(OutboundOrigin.Human);
        for (var part = 1; part <= 2; part++)
        {
            batch.Add(new JournalMessage
            {
                BotId = 7101, ChatId = Dm, ChatType = "private", MessageId = 10 + part, Date = Now,
                SenderKind = JournalSenderKind.Agent, SenderId = "7101", Text = $"<b>part {part}</b>",
                TextFormat = JournalTextFormat.Html,
            });
        }
        batch.Flush();

        foreach (var entry in spool.Pending())
        {
            var response = await host.PostAsync(entry.Record.ToJsonString(JournalRecordJson.StoredOptions), "agent1");
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        Assert.Equal("2|1", await fixture.ScalarRowAsync(
            "SELECT COUNT(*), COUNT(DISTINCT m.send_group_id) FROM journal_messages m "
            + $"JOIN journal_conversations c ON c.id = m.conversation_id WHERE c.telegram_chat_id = {Dm} AND m.direction = 'outbound'"));
    }

    private static readonly DateTime Now = new(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc);

    private static JournalMessage Voice(long botId, string transcript) => new()
    {
        BotId = botId,
        ChatId = Supergroup,
        ChatType = "supergroup",
        ChatTitle = "Team",
        MessageId = 42,
        Date = Now,
        SenderKind = JournalSenderKind.Human,
        SenderId = Human.ToString(),
        SenderDisplay = botId == 7101 ? "Ann" : "ann",
        Transcript = transcript,
        Media = [new JournalMediaItem(JournalAttachmentKind.Voice, "audio/ogg", 3, null, "vq-1")],
    };

    /// <summary>Captures <paramref name="message"/> on a fresh spool and returns the body the drainer would post.</summary>
    private string Capture(string name, JournalMessage message)
    {
        var spool = new JournalSpool(Path.Combine(_root, name));
        NewCapture(spool).Inbound(message);
        return Assert.Single(spool.Pending()).Record.ToJsonString(JournalRecordJson.StoredOptions);
    }

    private static JournalCapture NewCapture(JournalSpool spool) => new(
        spool,
        new JournalCounters(),
        new AllowlistHolder(Options.Create(new TelegramOptions { AllowedUserIds = [Dm], AllowedGroupIds = [Supergroup] })),
        Options.Create(new ClientOptions { IngestToken = "cj1.ingest.agent1.AAAA" }),
        NullLogger<JournalCapture>.Instance);
}
