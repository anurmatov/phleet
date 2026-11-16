using Fleet.Agent.Models;

namespace Fleet.Agent.Tests;

public class GroupChatBufferTests
{
    [Fact]
    public void FormatContext_LegacyReplyTo_DoesNotRenderSender()
    {
        var buffer = new GroupChatBuffer();
        buffer.Add("@u", "hi", "untrusted_sender", DateTimeOffset.UtcNow,
            telegramMessageId: 7, replyToTelegramMessageId: 5);
        const string expected = "[telegram_message_id: 7] [reply_to_message_id: 5] @u: hi";
        Assert.Equal(expected, buffer.FormatContext());
        Assert.Equal(expected, buffer.FormatNewMessages());
    }

    [Fact]
    public void FormatContext_IncludesTelegramMessageId_ForTextEntries()
    {
        var buffer = new GroupChatBuffer();
        buffer.Add("alice", "hello", null, DateTimeOffset.UtcNow, telegramMessageId: 10);
        buffer.Add("bob", "reply here", "alice", DateTimeOffset.UtcNow, telegramMessageId: 20, replyToTelegramMessageId: 10);
        buffer.Add("carol", "no id message", null, DateTimeOffset.UtcNow, telegramMessageId: 0);

        var result = buffer.FormatContext();
        var lines = result.Split('\n');

        Assert.Equal(3, lines.Length);
        Assert.Equal("[telegram_message_id: 10] alice: hello", lines[0]);
        Assert.Equal("[telegram_message_id: 20] [reply_to_message_id: 10] bob: reply here", lines[1]);
        Assert.Equal("carol: no id message", lines[2]);
    }

    /// <summary>
    /// The buffer holds messages and nothing else. Tool calls were once stored here as entries of
    /// their own, which nothing read and which pushed real messages out of the ring of 50.
    /// </summary>
    [Fact]
    public void TheBuffer_HasNoWayToStoreAToolCall()
    {
        var members = typeof(GroupChatBuffer).GetMembers().Select(m => m.Name).ToList();

        Assert.DoesNotContain(members, name => name.Contains("Tool", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            typeof(BufferedMessage).GetProperties().Select(p => p.Name),
            name => name.Contains("Type", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheRing_KeepsTheLastFiftyMessages()
    {
        var buffer = new GroupChatBuffer();
        for (var i = 1; i <= 60; i++)
            buffer.Add("alice", $"m{i}", null, DateTimeOffset.UtcNow);

        var entries = buffer.GetEntries();
        Assert.Equal(50, entries.Count);
        Assert.Equal("m11", entries[0].Text);
        Assert.Equal("m60", entries[^1].Text);
    }

    [Fact]
    public void FormatNewMessages_IncludesTelegramMessageId_AndPreservesOrder()
    {
        var buffer = new GroupChatBuffer();
        buffer.Add("alice", "first", null, DateTimeOffset.UtcNow, telegramMessageId: 1);
        buffer.Add("bob", "second", null, DateTimeOffset.UtcNow, telegramMessageId: 2);

        var result = buffer.FormatNewMessages();
        var lines = result.Split('\n');

        Assert.Equal(2, lines.Length);
        Assert.Equal("[telegram_message_id: 1] alice: first", lines[0]);
        Assert.Equal("[telegram_message_id: 2] bob: second", lines[1]);
    }

    [Fact]
    public void FormatNewMessages_ZeroMessageId_FallsBackToLegacyFormat()
    {
        var buffer = new GroupChatBuffer();
        buffer.Add("alice", "no id", null, DateTimeOffset.UtcNow, telegramMessageId: 0);

        var result = buffer.FormatNewMessages();

        Assert.Equal("alice: no id", result);
    }
}
