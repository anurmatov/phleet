using Fleet.Agent.Models;

namespace Fleet.Agent.Tests;

public class GroupChatBufferTests
{
    private static GroupChatBuffer BuildBuffer()
    {
        var buffer = new GroupChatBuffer();
        buffer.Add("alice", "hello world", null, DateTimeOffset.UtcNow, telegramMessageId: 101);
        buffer.Add("bob", "how are you", null, DateTimeOffset.UtcNow, telegramMessageId: 202);
        buffer.Add("carol", "doing great", null, DateTimeOffset.UtcNow, telegramMessageId: 303);
        return buffer;
    }

    [Fact]
    public void TryGetByMessageId_ReturnsCorrectSenderAndText_ForEachEntry()
    {
        var buffer = BuildBuffer();

        Assert.True(buffer.TryGetByMessageId(101, out var sender, out var text));
        Assert.Equal("alice", sender);
        Assert.Equal("hello world", text);

        Assert.True(buffer.TryGetByMessageId(202, out sender, out text));
        Assert.Equal("bob", sender);
        Assert.Equal("how are you", text);

        Assert.True(buffer.TryGetByMessageId(303, out sender, out text));
        Assert.Equal("carol", sender);
        Assert.Equal("doing great", text);
    }

    [Fact]
    public void TryGetByMessageId_ReturnsFalse_ForZeroId()
    {
        var buffer = BuildBuffer();

        Assert.False(buffer.TryGetByMessageId(0, out var sender, out var text));
        Assert.Equal("", sender);
        Assert.Equal("", text);
    }

    [Fact]
    public void TryGetByMessageId_ReturnsFalse_ForNegativeId()
    {
        var buffer = BuildBuffer();

        Assert.False(buffer.TryGetByMessageId(-1, out var sender, out var text));
        Assert.Equal("", sender);
        Assert.Equal("", text);
    }

    [Fact]
    public void TryGetByMessageId_ReturnsFalse_ForUnknownId()
    {
        var buffer = BuildBuffer();

        Assert.False(buffer.TryGetByMessageId(999, out var sender, out var text));
        Assert.Equal("", sender);
        Assert.Equal("", text);
    }

    [Fact]
    public void TryGetByMessageId_ReturnsFalse_ForToolUseEntry()
    {
        var buffer = new GroupChatBuffer();
        // tool_use entries don't carry telegramMessageId — add a regular entry then a tool_use with same id via AddToolUse.
        // AddToolUse uses default TelegramMessageId=0, so we test via Add with matching id but tool_use type via LoadEntries.
        buffer.LoadEntries([
            new SerializedEntry("my-tool", "used something", null, DateTimeOffset.UtcNow, "tool_use", TelegramMessageId: 500),
        ]);

        Assert.False(buffer.TryGetByMessageId(500, out var sender, out var text));
        Assert.Equal("", sender);
        Assert.Equal("", text);
    }
}
