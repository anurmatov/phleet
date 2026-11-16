using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Fleet.Agent.Tests;

/// <summary>
/// A provider that keeps the conversation (Claude, Codex) has the earlier messages in its session,
/// in this process and after a resume. So no prompt for it carries recent chat history, whatever
/// the state of its process: every prompt has the form a warm process gets. A provider that keeps
/// none (Gemini) still gets the history block, from the in-memory buffer.
/// </summary>
public sealed class KeptConversationPromptTests
{
    private const string DmHistory = "[Recent conversation]";
    private const string GroupHistory = "[Recent group conversation]";

    private static PromptAssembler Assembler(bool keepsConversation, bool processWarm)
    {
        var executor = Substitute.For<IAgentExecutor>();
        executor.KeepsConversation.Returns(keepsConversation);
        executor.IsProcessWarm.Returns(processWarm);
        return new PromptAssembler(executor);
    }

    private static GroupChatBuffer DmBuffer()
    {
        var buffer = new GroupChatBuffer { ChatId = 100, ChatLabel = "user=@u" };
        buffer.Add("@u", "an earlier message", null, DateTimeOffset.UtcNow, telegramMessageId: 5);
        buffer.Add("@agent", "an earlier answer", null, DateTimeOffset.UtcNow, telegramMessageId: 6);
        return buffer;
    }

    private static GroupChatBuffer GroupBuffer()
    {
        var buffer = new GroupChatBuffer { ChatId = -100, ChatTitle = "group A" };
        buffer.Add("@u", "an earlier message", null, DateTimeOffset.UtcNow, telegramMessageId: 5);
        buffer.Add("@agent", "an earlier answer", null, DateTimeOffset.UtcNow, telegramMessageId: 6);
        return buffer;
    }

    /// <summary>Every prompt form one message or check-in can be given, by name.</summary>
    private static Dictionary<string, string> AllForms(PromptAssembler assembler)
    {
        // A check-in buffer with nothing unread, so what is asserted is history and not unread tracking.
        var checkedBuffer = GroupBuffer();
        checkedBuffer.MarkChecked(DateTimeOffset.UtcNow.AddMinutes(1));

        return new Dictionary<string, string>
        {
            ["dm"] = assembler.ForDm(DmBuffer(), "hi", telegramMessageId: 7),
            ["dm, process restarted while queued"] = assembler.ForDmColdStart(DmBuffer(), "hi", telegramMessageId: 7),
            ["group"] = assembler.ForGroupMessage(GroupBuffer(), "@u", "hi", telegramMessageId: 7),
            ["group, process restarted while queued"] =
                assembler.ForGroupMessageColdStart(GroupBuffer(), "@u", "hi", telegramMessageId: 7),
            ["check-in"] = assembler.ForCheckIn(checkedBuffer, "Proactive check-in", "Anything pending?"),
            ["check-in, process restarted while queued"] =
                assembler.ForCheckInColdStart(checkedBuffer, "Proactive check-in", "Anything pending?"),
        };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AProviderThatKeepsTheConversation_GetsNoHistoryBlock_InAnyPromptForm(bool processWarm)
    {
        var forms = AllForms(Assembler(keepsConversation: true, processWarm));

        Assert.Equal(6, forms.Count);
        Assert.All(forms, form =>
        {
            Assert.DoesNotContain(DmHistory, form.Value);
            Assert.DoesNotContain(GroupHistory, form.Value);
            Assert.DoesNotContain("an earlier message", form.Value);
            Assert.DoesNotContain("an earlier answer", form.Value);
        });
    }

    /// <summary>What it gets instead is exactly the prompt a warm process gets today.</summary>
    [Fact]
    public void ItsPrompts_AreTheOnesAWarmProcessGets_WhetherOrNotItsProcessIsWarm()
    {
        var warmToday = AllForms(Assembler(keepsConversation: false, processWarm: true));

        Assert.Equal("[telegram_message_id: 7]\n[channel: dm chat_id=100 user=@u]\nhi", warmToday["dm"]);
        Assert.Equal(
            "[New message]\n[telegram_message_id: 7]\n[channel: group chat_id=-100 title=\"group A\"]\n[From: @u] hi",
            warmToday["group"]);

        foreach (var processWarm in new[] { false, true })
        {
            var forms = AllForms(Assembler(keepsConversation: true, processWarm));
            Assert.Equal(warmToday["dm"], forms["dm"]);
            Assert.Equal(warmToday["dm"], forms["dm, process restarted while queued"]);
            Assert.Equal(warmToday["group"], forms["group"]);
            Assert.Equal(warmToday["group"], forms["group, process restarted while queued"]);
            Assert.Equal(warmToday["check-in"], forms["check-in"]);
            Assert.Equal(warmToday["check-in"], forms["check-in, process restarted while queued"]);
        }
    }

    /// <summary>
    /// "New messages since last check-in" is unread tracking, not history: a check-in still shows
    /// the messages that arrived since the last one, and only those.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACheckIn_StillCarriesTheMessagesNotYetShown(bool processWarm)
    {
        var assembler = Assembler(keepsConversation: true, processWarm);
        var buffer = new GroupChatBuffer { ChatId = -100, ChatTitle = "group A" };
        buffer.Add("@u", "already seen", null, DateTimeOffset.UtcNow.AddMinutes(-10), telegramMessageId: 5);
        buffer.MarkChecked(DateTimeOffset.UtcNow.AddMinutes(-5));
        buffer.Add("@v", "not seen yet", null, DateTimeOffset.UtcNow, telegramMessageId: 6);

        foreach (var prompt in new[]
        {
            assembler.ForCheckIn(buffer, "All-messages check-in", "Review the conversation above."),
            assembler.ForCheckInColdStart(buffer, "All-messages check-in", "Review the conversation above."),
        })
        {
            Assert.Contains("[New messages since last check-in]\n[telegram_message_id: 6] @v: not seen yet", prompt);
            Assert.DoesNotContain("already seen", prompt);
            Assert.DoesNotContain(GroupHistory, prompt);
            Assert.Contains("[All-messages check-in]", prompt);
        }
    }

    /// <summary>
    /// A provider that keeps no conversation is unchanged: its history block is there, fed from the
    /// buffer in memory.
    /// </summary>
    [Fact]
    public void AProviderThatKeepsNoConversation_StillGetsItsHistoryBlock_FromTheBufferInMemory()
    {
        var forms = AllForms(Assembler(keepsConversation: false, processWarm: false));

        Assert.Equal(
            "[Recent conversation]\n[channel: dm chat_id=100 user=@u]\n"
            + "[telegram_message_id: 5] @u: an earlier message\n[telegram_message_id: 6] @agent: an earlier answer\n\n"
            + "[New message][telegram_message_id: 7]\n[channel: dm chat_id=100 user=@u]\nhi",
            forms["dm"]);
        Assert.StartsWith(
            "[Recent group conversation]\n[channel: group chat_id=-100 title=\"group A\"]\n"
            + "[telegram_message_id: 5] @u: an earlier message\n",
            forms["group"], StringComparison.Ordinal);
        Assert.Contains("[Recent group conversation]\n[telegram_message_id: 5] @u: an earlier message", forms["check-in"]);
    }

    /// <summary>Which provider is which. An executor that does not say keeps none.</summary>
    [Fact]
    public void ClaudeAndCodexKeepTheConversation_GeminiDoesNot()
    {
        var options = Options.Create(new AgentOptions
        {
            Name = "agent1", Role = "developer", WorkDir = Path.GetTempPath(), Model = "model-x",
        });
        var prompts = new PromptBuilder(options, NullLogger<PromptBuilder>.Instance);

        IAgentExecutor claude = new ClaudeExecutor(options, NullLogger<ClaudeExecutor>.Instance, prompts,
            new AgentHarness(options.Value, _ => null, NullLogger.Instance));
        IAgentExecutor codex = new CodexExecutor(options, Options.Create(new TelegramOptions()), prompts,
            NullLogger<CodexExecutor>.Instance, _ => null, _ => null);
        IAgentExecutor gemini = new GeminiExecutor(options, prompts, NullLogger<GeminiExecutor>.Instance);

        Assert.True(claude.KeepsConversation);
        Assert.True(codex.KeepsConversation);
        Assert.False(gemini.KeepsConversation);
        Assert.False(((IAgentExecutor)new Harness.ScriptedExecutor()).KeepsConversation);
    }

    [Fact]
    public void TheNewSessionLine_IsOneFixedLine_WithNoMessageContent()
    {
        Assert.Equal(
            "[session: new; earlier messages are not in this session's context; "
            + "if a request refers to them, read them with the journal tools]",
            PromptAssembler.NewSessionLine);
        Assert.DoesNotContain('\n', PromptAssembler.NewSessionLine);
        Assert.Equal(PromptAssembler.NewSessionLine + "\nthe task", PromptAssembler.WithNewSessionLine("the task"));
    }
}
