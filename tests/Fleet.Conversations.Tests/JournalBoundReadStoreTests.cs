using Fleet.Conversations.Contracts;
using Fleet.Conversations.Journal;
using Fleet.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Conversations.Tests;

public sealed partial class JournalReadStoreTests
{
    [Fact]
    public async Task Bound_lookup_AC5_reads_only_the_runtime_conversation_in_real_MySql()
    {
        var reader = Subject();
        var other = Subject();
        var a = Math.Abs(Chat());
        var b = Chat();
        var c = Math.Abs(Chat());
        var excluded = Math.Abs(Chat());
        var clock = new LookupClock();
        var store = new MySqlJournalStore(Db, NullLogger.Instance);
        JournalRecord Private(long chat, long id, string text, long? reply = null, long bot = 7001) =>
            Message(chat, id, BaseTime(), text, replyTo: reply) with
            { Telegram = new JournalTelegramRef { ChatKind = JournalChatKind.Private, ChatId = chat, BotId = bot, MessageId = id, ReplyToMessageId = reply } };
        var alpha = (await store.IngestAsync(Private(a, 5, "alpha"), reader)).MessageId!;
        await store.IngestAsync(Private(a, 7, "reply", 5), reader);
        await store.IngestAsync(Private(a, 5, "other bot", bot: 7002), other);
        await store.IngestAsync(Message(b, 5, BaseTime(), "bravo"), reader);
        await store.IngestAsync(Private(c, 5, "hidden"), other);
        await using var host = await ReadHost.StartAsync(Db, readAllSubjects: other, time: clock, excluded: excluded.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var token = host.ReadToken(reader);
        var noBinding = await host.CallAsync(token, "get_message", new { telegram_message_id = 5 });
        Assert.True(noBinding.IsError);
        Assert.Equal("{\"error\":\"unavailable\",\"reason\":\"no_bound_conversation\"}", noBinding.Text);
        await host.BindAsync(reader, a, "private");
        Assert.Equal("alpha", (await host.CallAsync(token, "get_message", new { telegram_message_id = 5 })).Json.GetProperty("text").GetString());
        var reply = (await host.CallAsync(token, "get_message", new { telegram_message_id = 7 })).Json.GetProperty("reply_to");
        Assert.Equal(5, reply.GetProperty("telegram_message_id").GetInt64());
        Assert.Equal(alpha, reply.GetProperty("message_id").GetString());
        var steered = await host.CallAsync(token, "get_message", new { telegram_message_id = 5, telegram_chat_id = b });
        Assert.True(steered.IsError);
        Assert.Equal("{\"error\":\"invalid_argument\",\"field\":\"telegram_chat_id\"}", steered.Text);
        Assert.Equal(steered.Body, (await host.CallAsync(token, "get_message", new { telegram_message_id = 5, telegram_chat_id = Chat() })).Body);
        await host.BindAsync(reader, b);
        Assert.Equal("bravo", (await host.CallAsync(token, "get_message", new { telegram_message_id = 5 })).Json.GetProperty("text").GetString());
        await host.BindAsync(reader, c, "private");
        var hidden = await host.CallAsync(token, "get_message", new { telegram_message_id = 5 });
        Assert.True(hidden.IsError);
        Assert.Equal("{\"error\":\"not_found\"}", hidden.Text);
        Assert.Equal(hidden.Body, (await host.CallAsync(token, "get_message", new { telegram_message_id = 99 })).Body);
        clock.Advance(180);
        Assert.Equal(noBinding.Body, (await host.CallAsync(token, "get_message", new { telegram_message_id = 5 })).Body);
        await host.BindAsync(reader, excluded, "private");
        Assert.Equal("{\"error\":\"unavailable\",\"reason\":\"conversation_not_journaled\"}",
            (await host.CallAsync(token, "get_message", new { telegram_message_id = 5 })).Text);
        // `all` sees both private bot conversations, but lookup stays in the bound one.
        await host.BindAsync(other, a, "private");
        Assert.Equal("alpha", (await host.CallAsync(host.ReadToken(other), "get_message", new { telegram_message_id = 5 })).Json.GetProperty("text").GetString());
        await host.BindAsync(other, a, "private", 7002);
        Assert.Equal("other bot", (await host.CallAsync(host.ReadToken(other), "get_message", new { telegram_message_id = 5 })).Json.GetProperty("text").GetString());
    }

    private sealed class LookupClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(int seconds) => _now += TimeSpan.FromSeconds(seconds);
    }
}
