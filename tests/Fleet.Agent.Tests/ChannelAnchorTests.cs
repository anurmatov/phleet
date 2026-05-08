using Fleet.Agent.Interfaces;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using NSubstitute;
using Telegram.Bot.Types;

namespace Fleet.Agent.Tests;

/// <summary>
/// Tests for the [channel: ...] anchor emitted in agent prompts (issue #162).
/// </summary>
public class ChannelAnchorTests
{
    // ── GroupChatBuffer.RenderHeader ──────────────────────────────────────────

    [Fact]
    public void RenderHeader_ChatIdZero_ReturnsNull()
    {
        var buffer = new GroupChatBuffer(); // ChatId defaults to 0
        Assert.Null(buffer.RenderHeader());
    }

    [Fact]
    public void RenderHeader_GroupWithTitle_ReturnsGroupAnchor()
    {
        var buffer = new GroupChatBuffer { ChatId = -1009999L, ChatTitle = "Test Group" };
        Assert.Equal("[channel: group chat_id=-1009999 title=\"Test Group\"]", buffer.RenderHeader());
    }

    [Fact]
    public void RenderHeader_GroupNoTitle_ReturnsGroupAnchorWithoutTitle()
    {
        var buffer = new GroupChatBuffer { ChatId = -1009999L };
        Assert.Equal("[channel: group chat_id=-1009999]", buffer.RenderHeader());
    }

    [Fact]
    public void RenderHeader_GroupTitleContainsQuote_EscapesQuote()
    {
        var buffer = new GroupChatBuffer { ChatId = -1009999L, ChatTitle = "Quote\"Test" };
        Assert.Equal("[channel: group chat_id=-1009999 title=\"Quote\\\"Test\"]", buffer.RenderHeader());
    }

    [Fact]
    public void RenderHeader_DmWithLabel_ReturnsDmAnchorWithLabel()
    {
        var buffer = new GroupChatBuffer { ChatId = 100001L, ChatLabel = "user=@user1" };
        Assert.Equal("[channel: dm chat_id=100001 user=@user1]", buffer.RenderHeader());
    }

    [Fact]
    public void RenderHeader_DmNoLabel_ReturnsBareIdAnchor()
    {
        var buffer = new GroupChatBuffer { ChatId = 100001L };
        Assert.Equal("[channel: dm chat_id=100001]", buffer.RenderHeader());
    }

    // ── PromptAssembler.ForDm ────────────────────────────────────────────────

    private static PromptAssembler MakeAssembler(bool warm)
    {
        var executor = Substitute.For<IAgentExecutor>();
        executor.IsProcessWarm.Returns(warm);
        return new PromptAssembler(executor);
    }

    [Fact]
    public void ForDm_WithUsername_EmitsDmAnchorWithUser()
    {
        var asm = MakeAssembler(warm: true);
        var buffer = new GroupChatBuffer { ChatId = 100001L, ChatLabel = "user=@user1" };

        var result = asm.ForDm(buffer, "hello", telegramMessageId: 42);

        Assert.Contains("[telegram_message_id: 42]", result);
        Assert.Contains("[channel: dm chat_id=100001 user=@user1]", result);
        // channel anchor must come after the message id tag
        Assert.True(result.IndexOf("[channel:", StringComparison.Ordinal)
                    > result.IndexOf("[telegram_message_id:", StringComparison.Ordinal));
    }

    [Fact]
    public void ForDm_WithFirstName_EmitsDmAnchorWithName()
    {
        var asm = MakeAssembler(warm: true);
        var buffer = new GroupChatBuffer { ChatId = 100001L, ChatLabel = "name=\"Alice\"" };

        var result = asm.ForDm(buffer, "hello", telegramMessageId: 42);

        Assert.Contains("[channel: dm chat_id=100001 name=\"Alice\"]", result);
    }

    [Fact]
    public void ForDm_NoLabel_EmitsBareIdAnchor()
    {
        var asm = MakeAssembler(warm: true);
        var buffer = new GroupChatBuffer { ChatId = 100001L };

        var result = asm.ForDm(buffer, "hello", telegramMessageId: 42);

        Assert.Contains("[channel: dm chat_id=100001]", result);
    }

    [Fact]
    public void ForDm_NoChatId_NoAnchorEmitted()
    {
        var asm = MakeAssembler(warm: true);
        var buffer = new GroupChatBuffer(); // ChatId = 0

        var result = asm.ForDm(buffer, "hello", telegramMessageId: 42);

        Assert.DoesNotContain("[channel:", result);
    }

    [Fact]
    public void ForDm_ColdContext_HistoryHeaderEmittedWithNewMessageAnchor()
    {
        var asm = MakeAssembler(warm: false);
        var buffer = new GroupChatBuffer { ChatId = 100001L, ChatLabel = "user=@user1" };
        buffer.Add("user1", "earlier message", null, DateTimeOffset.UtcNow.AddMinutes(-5));

        var result = asm.ForDm(buffer, "new message", telegramMessageId: 42);

        // One in history section, one in the new-message block
        var count = CountOccurrences(result, "[channel:");
        Assert.Equal(2, count);
        // FormatEntry lines must remain unchanged (no [channel:] injected per line)
        Assert.Contains("earlier message", result);
    }

    [Fact]
    public void ForDm_ColdContextChatIdZero_NoHistoryHeader()
    {
        var asm = MakeAssembler(warm: false);
        var buffer = new GroupChatBuffer(); // ChatId = 0
        buffer.Add("user1", "earlier message", null, DateTimeOffset.UtcNow.AddMinutes(-5));

        var result = asm.ForDm(buffer, "new message", telegramMessageId: 42);

        Assert.DoesNotContain("[channel:", result);
        Assert.Contains("earlier message", result);
    }

    // ── PromptAssembler.ForGroupMessage ──────────────────────────────────────

    [Fact]
    public void ForGroupMessage_WithTitle_EmitsGroupAnchor()
    {
        var asm = MakeAssembler(warm: true);
        var buffer = new GroupChatBuffer { ChatId = -1009999L, ChatTitle = "Test Group" };

        var result = asm.ForGroupMessage(buffer, "alice", "hello", telegramMessageId: 42);

        Assert.Contains("[channel: group chat_id=-1009999 title=\"Test Group\"]", result);
        Assert.Contains("[telegram_message_id: 42]", result);
        // channel anchor after message id tag
        Assert.True(result.IndexOf("[channel:", StringComparison.Ordinal)
                    > result.IndexOf("[telegram_message_id:", StringComparison.Ordinal));
        // [From: alice] after channel anchor
        Assert.True(result.IndexOf("[From: alice]", StringComparison.Ordinal)
                    > result.IndexOf("[channel:", StringComparison.Ordinal));
    }

    [Fact]
    public void ForGroupMessage_NoTitle_EmitsGroupAnchorWithoutTitle()
    {
        var asm = MakeAssembler(warm: true);
        var buffer = new GroupChatBuffer { ChatId = -1009999L };

        var result = asm.ForGroupMessage(buffer, "alice", "hello", telegramMessageId: 42);

        Assert.Contains("[channel: group chat_id=-1009999]", result);
        Assert.DoesNotContain("title=", result);
    }

    [Fact]
    public void ForGroupMessage_TitleContainsQuote_Escaped()
    {
        var asm = MakeAssembler(warm: true);
        var buffer = new GroupChatBuffer { ChatId = -1009999L, ChatTitle = "Quote\"Test" };

        var result = asm.ForGroupMessage(buffer, "alice", "hello", telegramMessageId: 42);

        Assert.Contains("[channel: group chat_id=-1009999 title=\"Quote\\\"Test\"]", result);
    }

    [Fact]
    public void ForGroupMessage_ColdContext_HistoryHeaderAndNewMessageAnchor()
    {
        var asm = MakeAssembler(warm: false);
        var buffer = new GroupChatBuffer { ChatId = -1009999L, ChatTitle = "Test Group" };
        buffer.Add("alice", "first message", null, DateTimeOffset.UtcNow.AddMinutes(-5));
        buffer.Add("bob", "second message", null, DateTimeOffset.UtcNow.AddMinutes(-4));

        var result = asm.ForGroupMessage(buffer, "alice", "new message", telegramMessageId: 43);

        // One header before history, one in [New message] block
        var count = CountOccurrences(result, "[channel:");
        Assert.Equal(2, count);
        Assert.Contains("alice: first message", result);
        Assert.Contains("bob: second message", result);
    }

    [Fact]
    public void ForGroupMessage_ColdContextChatIdZero_NoHistoryHeader()
    {
        var asm = MakeAssembler(warm: false);
        var buffer = new GroupChatBuffer(); // ChatId = 0
        buffer.Add("alice", "first message", null, DateTimeOffset.UtcNow.AddMinutes(-5));

        var result = asm.ForGroupMessage(buffer, "alice", "new message", telegramMessageId: 43);

        Assert.DoesNotContain("[channel:", result);
        Assert.Contains("alice: first message", result);
    }

    // ── PromptAssembler.ForRelayDirective ────────────────────────────────────

    [Fact]
    public void ForRelayDirective_EmitsRelayAnchor()
    {
        var asm = MakeAssembler(warm: false);
        var buffer = new GroupChatBuffer();

        var result = asm.ForRelayDirective(buffer, "temporal-bridge", "do the task");

        Assert.Contains("[channel: relay]", result);
        Assert.Contains("[Directive from temporal-bridge]", result);
        Assert.Contains("do the task", result);
        // relay anchor before the directive tag
        Assert.True(result.IndexOf("[channel: relay]", StringComparison.Ordinal)
                    < result.IndexOf("[Directive from", StringComparison.Ordinal));
    }

    // ── PromptAssembler.ForCheckIn ────────────────────────────────────────────

    [Fact]
    public void ForCheckIn_NoContext_EmitsRelayAnchor()
    {
        var asm = MakeAssembler(warm: true);
        var buffer = new GroupChatBuffer();

        var result = asm.ForCheckIn(buffer, "Proactive check-in", "review tasks");

        Assert.Contains("[channel: relay]", result);
        Assert.Contains("review tasks", result);
    }

    [Fact]
    public void ForCheckIn_WithContext_EmitsRelayAnchorAtTop()
    {
        var asm = MakeAssembler(warm: false);
        var buffer = new GroupChatBuffer { ChatId = -1009999L, ChatTitle = "Test Group" };
        buffer.Add("alice", "context message", null, DateTimeOffset.UtcNow.AddMinutes(-5));

        var result = asm.ForCheckIn(buffer, "All-messages check-in", "review");

        Assert.Contains("[channel: relay]", result);
        // relay anchor must be before the context/label
        Assert.True(result.IndexOf("[channel: relay]", StringComparison.Ordinal)
                    < result.IndexOf("context message", StringComparison.Ordinal));
    }

    // ── AgentTransport.BuildChannelAnchorFromChat ────────────────────────────

    [Fact]
    public void BuildChannelAnchorFromChat_GroupWithTitle_ReturnsGroupAnchor()
    {
        var chat = new Chat { Id = -1009999L, Title = "Test Group" };
        var anchor = AgentTransport.BuildChannelAnchorFromChat(chat);
        Assert.Equal("[channel: group chat_id=-1009999 title=\"Test Group\"]", anchor);
    }

    [Fact]
    public void BuildChannelAnchorFromChat_GroupNoTitle_ReturnsGroupAnchorWithoutTitle()
    {
        var chat = new Chat { Id = -1009999L };
        var anchor = AgentTransport.BuildChannelAnchorFromChat(chat);
        Assert.Equal("[channel: group chat_id=-1009999]", anchor);
    }

    [Fact]
    public void BuildChannelAnchorFromChat_DmWithUsername_ReturnsDmAnchorWithUser()
    {
        var chat = new Chat { Id = 100001L, Username = "user1" };
        var anchor = AgentTransport.BuildChannelAnchorFromChat(chat);
        Assert.Equal("[channel: dm chat_id=100001 user=@user1]", anchor);
    }

    [Fact]
    public void BuildChannelAnchorFromChat_DmNoUsernameWithFirstName_ReturnsDmAnchorWithName()
    {
        var chat = new Chat { Id = 100001L, FirstName = "Alice" };
        var anchor = AgentTransport.BuildChannelAnchorFromChat(chat);
        Assert.Equal("[channel: dm chat_id=100001 name=\"Alice\"]", anchor);
    }

    [Fact]
    public void BuildChannelAnchorFromChat_DmNoUsernameNoFirstName_ReturnsBareIdAnchor()
    {
        var chat = new Chat { Id = 100001L };
        var anchor = AgentTransport.BuildChannelAnchorFromChat(chat);
        Assert.Equal("[channel: dm chat_id=100001]", anchor);
    }

    [Fact]
    public void BuildChannelAnchorFromChat_DmUsernameHasPriority_OverFirstName()
    {
        var chat = new Chat { Id = 100001L, Username = "user1", FirstName = "Alice" };
        var anchor = AgentTransport.BuildChannelAnchorFromChat(chat);
        Assert.Contains("user=@user1", anchor);
        Assert.DoesNotContain("name=", anchor);
    }

    [Fact]
    public void BuildChannelAnchorFromChat_GroupTitleContainsQuote_Escaped()
    {
        var chat = new Chat { Id = -1009999L, Title = "Quote\"Test" };
        var anchor = AgentTransport.BuildChannelAnchorFromChat(chat);
        Assert.Equal("[channel: group chat_id=-1009999 title=\"Quote\\\"Test\"]", anchor);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }
}
