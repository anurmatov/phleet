using Fleet.Comms.Routes;
using Fleet.Conversations.Journal;

namespace Fleet.Comms.Tests;

public sealed partial class JournalReadMcpTests
{
    [Fact]
    public async Task BoundLookup_AC5_AllRowsUseTheCurrentConversation()
    {
        var clock = new BindingClock();
        await using var host = await McpHost.StartAsync(time: clock, excluded: "404");
        var t = DateTimeOffset.UnixEpoch;
        var conversation = Fleet.Protocol.Ulid.NewUlid();
        var a = host.Reads.Add(conversation, "private", 101, 5, t, "alpha", [AgentA]);
        host.Reads.Add(conversation, "private", 101, 7, t, "reply", [AgentA], replyTo: 5);
        host.Reads.Add(Fleet.Protocol.Ulid.NewUlid(), "supergroup", -202, 5, t, "bravo", [AgentA]);
        host.Reads.Add(Fleet.Protocol.Ulid.NewUlid(), "private", 303, 5, t, "hidden", [AgentB]);
        var token = Token(JournalTokens.PurposeRead, AgentA);
        var absent = await host.CallAsync(token, "get_message", new { telegram_message_id = 5 });
        Assert.True(absent.IsError);
        Assert.Equal("{\"error\":\"unavailable\",\"reason\":\"no_bound_conversation\"}", absent.Text);
        Assert.Equal(0, host.Reads.Calls);
        await host.BindAsync(AgentA, "private", 101);
        Assert.Equal("alpha", (await host.CallAsync(token, "get_message", new { telegram_message_id = 5 })).Json.GetProperty("text").GetString());
        var reply = (await host.CallAsync(token, "get_message", new { telegram_message_id = 7 })).Json.GetProperty("reply_to");
        Assert.Equal(5, reply.GetProperty("telegram_message_id").GetInt64());
        Assert.Equal(a, reply.GetProperty("message_id").GetString());
        var calls = host.Reads.Calls;
        var steered = await host.CallAsync(token, "get_message", new { telegram_message_id = 5, telegram_chat_id = -202 });
        var fabricated = await host.CallAsync(token, "get_message", new { telegram_message_id = 5, telegram_chat_id = -909 });
        Assert.True(steered.IsError);
        Assert.Equal("{\"error\":\"invalid_argument\",\"field\":\"telegram_chat_id\"}", steered.Text);
        Assert.Equal(steered.Body, fabricated.Body);
        Assert.Equal(calls, host.Reads.Calls);
        await host.BindAsync(AgentA, "supergroup", -202);
        Assert.Equal("bravo", (await host.CallAsync(token, "get_message", new { telegram_message_id = 5 })).Json.GetProperty("text").GetString());
        await host.BindAsync(AgentA, "private", 303);
        var hidden = await host.CallAsync(token, "get_message", new { telegram_message_id = 5 });
        var missing = await host.CallAsync(token, "get_message", new { telegram_message_id = 99 });
        Assert.True(hidden.IsError);
        Assert.Equal(JournalReadTools.NotFoundBody, hidden.Text);
        Assert.Equal(hidden.Body, missing.Body);
        clock.Advance(180);
        Assert.Equal(absent.Body, (await host.CallAsync(token, "get_message", new { telegram_message_id = 5 })).Body);
        await host.BindAsync(AgentA, "private", 404);
        var excluded = await host.CallAsync(token, "get_message", new { telegram_message_id = 5 });
        Assert.True(excluded.IsError);
        Assert.Equal("{\"error\":\"unavailable\",\"reason\":\"conversation_not_journaled\"}", excluded.Text);
        // ULID lookup deliberately keeps its existing, unbound scope.
        Assert.Equal("alpha", (await host.CallAsync(token, "get_message", new { message_id = a })).Json.GetProperty("text").GetString());
    }

    [Fact]
    public async Task BoundLookup_StoreFailure_IsRetryableWithoutPartialData()
    {
        await using var host = await McpHost.StartAsync();
        await host.BindAsync(AgentA, "private", 101);
        host.Reads.Unavailable = true;
        var result = await host.CallAsync(Token(JournalTokens.PurposeRead, AgentA), "get_message",
            new { telegram_message_id = 5 });
        Assert.True(result.IsError);
        Assert.Equal("{\"error\":\"store_unavailable\",\"retryable\":true}", result.Text);
    }

    internal static async Task AssertTwoSubjectIsolationAsync()
    {
        await using var host = await McpHost.StartAsync();
        World.Seed(host.Reads);
        await host.BindAsync(AgentA, "private", World.ChatA);
        var other = await host.CallAsync(Token(JournalTokens.PurposeRead, AgentB), "get_message", new { telegram_message_id = 10 });
        Assert.True(other.IsError);
        Assert.Equal("{\"error\":\"unavailable\",\"reason\":\"no_bound_conversation\"}", other.Text);
        Assert.Equal(0, host.Reads.Calls);
        var owner = await host.CallAsync(Token(JournalTokens.PurposeRead, AgentA), "get_message", new { telegram_message_id = 10 });
        Assert.Equal("hello from alpha", owner.Json.GetProperty("text").GetString());
    }

    private sealed class BindingClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(int seconds) => _now += TimeSpan.FromSeconds(seconds);
    }
}
