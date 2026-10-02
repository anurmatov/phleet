using Fleet.Agent.Models;
using Fleet.Agent.Services;
using NSubstitute;

namespace Fleet.Agent.Tests;

public sealed class PromptReplyMetadataTests
{
    public static IEnumerable<object[]> Cases =>
        from isGroup in new[] { false, true }
        from warm in new[] { false, true }
        from reply in new[] { false, true }
        from voice in new[] { false, true }
        select new object[] { isGroup, warm, reply, voice };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Assemble_ReplyMetadata_ContainsOnlyIds(bool group, bool warm, bool reply, bool voice)
    {
        var executor = Substitute.For<IAgentExecutor>();
        executor.IsProcessWarm.Returns(warm);
        var assembler = new PromptAssembler(executor);
        var buffer = new GroupChatBuffer { ChatId = group ? -100 : 100, ChatTitle = group ? "group A" : null, ChatLabel = "user=@u" };
        buffer.LoadEntries([new SerializedEntry("@u", "history", "untrusted_sender", DateTimeOffset.UtcNow)]);
        var prompt = group
            ? assembler.ForGroupMessage(buffer, "@u", "hi", replyToTelegramMessageId: reply ? 5 : null,
                telegramMessageId: 7, isVoiceTranscription: voice)
            : assembler.ForDm(buffer, "hi", replyToTelegramMessageId: reply ? 5 : null,
                telegramMessageId: 7, isVoiceTranscription: voice);
        Assert.Contains("[telegram_message_id: 7]", prompt);
        Assert.Equal(reply, prompt.Contains("[reply_to_message_id: 5]"));
        Assert.Equal(voice, prompt.Contains(PromptAssembler.VoiceTranscriptionMarker));
        Assert.Contains(group ? "[channel: group chat_id=-100 title=\"group A\"]" : "[channel: dm chat_id=100 user=@u]", prompt);
        if (group) Assert.Contains("[From: @u]", prompt);
        Assert.DoesNotContain("untrusted_sender", prompt);
        Assert.DoesNotContain("Replying to", prompt);
    }
}
