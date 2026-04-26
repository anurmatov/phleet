using Fleet.Agent.Models;
using Fleet.Agent.Services;
using NSubstitute;

namespace Fleet.Agent.Tests;

public class PromptAssemblerTests
{
    private const long GroupChatId = -1001234567890L;
    private const long DmChatId = 987654321L;

    private static PromptAssembler MakeAssembler(bool warm)
    {
        var executor = Substitute.For<IAgentExecutor>();
        executor.IsProcessWarm.Returns(warm);
        return new PromptAssembler(executor);
    }

    // ── ForRelayDirective ────────────────────────────────────────────────────

    [Fact]
    public void ForRelayDirective_IncludesChatId()
    {
        var assembler = MakeAssembler(warm: false);
        var buffer = new GroupChatBuffer();
        buffer.Add("alice", "some chat message", null, DateTimeOffset.UtcNow);

        var result = assembler.ForRelayDirective(GroupChatId, buffer, "temporal-bridge", "do the task");

        Assert.StartsWith($"[Chat: {GroupChatId}]", result);
        Assert.Contains("[Directive from temporal-bridge]", result);
        Assert.Contains("do the task", result);
        Assert.DoesNotContain("some chat message", result);
        Assert.DoesNotContain("Recent group conversation", result);
    }

    // ── ForGroupMessage ──────────────────────────────────────────────────────

    [Fact]
    public void ForGroupMessage_WarmProcess_IncludesChatId()
    {
        var assembler = MakeAssembler(warm: true);
        var buffer = new GroupChatBuffer();

        var result = assembler.ForGroupMessage(GroupChatId, buffer, "alice", "hello");

        Assert.StartsWith($"[Chat: {GroupChatId}]", result);
        Assert.Contains("[New message]", result);
        Assert.Contains("[From: alice]", result);
        Assert.Contains("hello", result);
    }

    [Fact]
    public void ForGroupMessage_ColdProcess_WithContext_IncludesChatId()
    {
        var assembler = MakeAssembler(warm: false);
        var buffer = new GroupChatBuffer();
        buffer.Add("bob", "hi there", null, DateTimeOffset.UtcNow);

        var result = assembler.ForGroupMessage(GroupChatId, buffer, "alice", "hello");

        var chatTagIndex = result.IndexOf($"[Chat: {GroupChatId}]", StringComparison.Ordinal);
        var contextIndex = result.IndexOf("Recent group conversation", StringComparison.Ordinal);

        Assert.True(contextIndex >= 0, "Context block should be present");
        Assert.True(chatTagIndex > contextIndex, "[Chat:] should appear after the context block");
        Assert.Contains("[New message]", result);
    }

    // ── ForDm ────────────────────────────────────────────────────────────────

    [Fact]
    public void ForDm_WarmProcess_WithReply_IncludesChatId()
    {
        var assembler = MakeAssembler(warm: true);
        var buffer = new GroupChatBuffer();

        var result = assembler.ForDm(DmChatId, buffer, "task text", replyToText: "previous message");

        Assert.StartsWith($"[Chat: {DmChatId}]", result);
        Assert.Contains("[Replying to:", result);
        Assert.Contains("task text", result);
    }

    [Fact]
    public void ForDm_WarmProcess_NoReply_IncludesChatId()
    {
        var assembler = MakeAssembler(warm: true);
        var buffer = new GroupChatBuffer();

        var result = assembler.ForDm(DmChatId, buffer, "task text");

        Assert.StartsWith($"[Chat: {DmChatId}]", result);
        Assert.Contains("task text", result);
        Assert.DoesNotContain("[Replying to:", result);
    }

    [Fact]
    public void ForDm_ColdProcess_WithContext_IncludesChatId()
    {
        var assembler = MakeAssembler(warm: false);
        var buffer = new GroupChatBuffer();
        buffer.Add("alice", "hi", null, DateTimeOffset.UtcNow);

        var result = assembler.ForDm(DmChatId, buffer, "task text");

        var chatTagIndex = result.IndexOf($"[Chat: {DmChatId}]", StringComparison.Ordinal);
        var contextIndex = result.IndexOf("Recent conversation", StringComparison.Ordinal);

        Assert.True(contextIndex >= 0, "Context block should be present");
        Assert.True(chatTagIndex > contextIndex, "[Chat:] should appear after the context block");
        Assert.Contains("[New message]", result);
    }

    [Fact]
    public void ForDm_ColdProcess_NoContext_NoReply_IncludesChatId()
    {
        var assembler = MakeAssembler(warm: false);
        var buffer = new GroupChatBuffer();

        var result = assembler.ForDm(DmChatId, buffer, "task text");

        Assert.StartsWith($"[Chat: {DmChatId}]", result);
        Assert.Contains("task text", result);
    }

    // ── ForCheckIn ───────────────────────────────────────────────────────────

    [Fact]
    public void ForCheckIn_WithContext_IncludesChatId()
    {
        var assembler = MakeAssembler(warm: false);
        var buffer = new GroupChatBuffer();
        buffer.Add("alice", "hi", null, DateTimeOffset.UtcNow);

        var result = assembler.ForCheckIn(GroupChatId, buffer, "Debounce check-in", "review");

        var chatTagIndex = result.IndexOf($"[Chat: {GroupChatId}]", StringComparison.Ordinal);
        var contextIndex = result.IndexOf("Recent group conversation", StringComparison.Ordinal);

        Assert.True(contextIndex >= 0, "Context block should be present");
        Assert.True(chatTagIndex > contextIndex, "[Chat:] should appear after the context block");
        Assert.Contains("[Debounce check-in]", result);
    }

    [Fact]
    public void ForCheckIn_NoContext_IncludesChatId()
    {
        var assembler = MakeAssembler(warm: false);
        var buffer = new GroupChatBuffer();

        var result = assembler.ForCheckIn(GroupChatId, buffer, "Debounce check-in", "review");

        Assert.StartsWith($"[Chat: {GroupChatId}]", result);
        Assert.Contains("[Debounce check-in]", result);
    }
}
