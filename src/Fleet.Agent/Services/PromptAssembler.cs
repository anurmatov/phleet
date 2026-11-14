using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Services;

/// <summary>
/// Single source of truth for building task prompts with context.
/// Replaces scattered context building in GroupBehavior (BuildGroupTask, BuildDmTask,
/// RunGroupCheckInAsync inline, OnRelayMessage inline).
/// </summary>
public sealed class PromptAssembler
{
    /// <summary>
    /// Prompt-internal marker for text produced by speech-to-text (issue #245).
    ///
    /// Emitted alongside the [telegram_message_id: N] and [channel: ...] metadata and,
    /// like them, never sent to Telegram. It exists because a transcript is otherwise
    /// indistinguishable from typed text, and an agent that cannot tell them apart will
    /// over-trust a name or a number that whisper rendered wrong.
    ///
    /// No confidence score: the transcription service does not return one, and inventing
    /// a number would be worse than saying nothing.
    /// </summary>
    public const string VoiceTranscriptionMarker =
        "[voice_transcription: whisper; may_contain_errors=true]";

    private readonly IAgentExecutor _executor;

    public PromptAssembler(IAgentExecutor executor)
    {
        _executor = executor;
    }

    /// <summary>
    /// Build a prompt for a direct message (DM) task.
    /// </summary>
    /// <param name="isVoiceTranscription">
    /// True when <paramref name="taskText"/> came from speech-to-text; adds
    /// <see cref="VoiceTranscriptionMarker"/> to the prompt metadata.
    /// </param>
    public string ForDm(GroupChatBuffer buffer, string taskText,
        long? replyToTelegramMessageId = null, long telegramMessageId = 0,
        bool isVoiceTranscription = false) =>
        RenderDm(buffer, taskText, replyToTelegramMessageId, telegramMessageId, isVoiceTranscription,
            _executor.IsProcessWarm);

    /// <summary>
    /// <see cref="ForDm"/> as a provider process that holds no conversation needs it: with the
    /// recent history above the message, whatever the process's state is right now. A message that
    /// may wait in the queue is given both, because the process can be restarted while it waits.
    /// </summary>
    public string ForDmColdStart(GroupChatBuffer buffer, string taskText,
        long? replyToTelegramMessageId = null, long telegramMessageId = 0,
        bool isVoiceTranscription = false) =>
        RenderDm(buffer, taskText, replyToTelegramMessageId, telegramMessageId, isVoiceTranscription,
            processWarm: false);

    private static string RenderDm(GroupChatBuffer buffer, string taskText,
        long? replyToTelegramMessageId, long telegramMessageId, bool isVoiceTranscription, bool processWarm)
    {
        var channelAnchor = buffer.RenderHeader();
        var msgIdTag = telegramMessageId > 0 ? $"[telegram_message_id: {telegramMessageId}]" : "";
        var channelLine = channelAnchor is not null ? $"\n{channelAnchor}" : "";
        var voiceLine = isVoiceTranscription ? $"\n{VoiceTranscriptionMarker}" : "";
        var replyContext = replyToTelegramMessageId is > 0
            ? $"{(msgIdTag.Length > 0 ? " " : "")}[reply_to_message_id: {replyToTelegramMessageId}]"
            : "";

        if (processWarm)
        {
            var meta = string.Concat(msgIdTag, replyContext, channelLine, voiceLine);
            return meta.Length > 0 ? $"{meta}\n{taskText}" : taskText;
        }

        var context = buffer.FormatContext();
        var metaCold = string.Concat(msgIdTag, replyContext, channelLine, voiceLine);
        if (context.Length > 0)
        {
            var historySection = channelAnchor is not null ? $"{channelAnchor}\n{context}" : context;
            return $"[Recent conversation]\n{historySection}\n\n[New message]{metaCold}\n{taskText}";
        }

        return metaCold.Length > 0 ? $"[New message]{metaCold}\n{taskText}" : taskText;
    }

    /// <summary>
    /// Build a prompt for a group message task (mention, reply, or /new).
    /// For media groups, <paramref name="telegramMessageId"/> is the first photo's message ID.
    /// </summary>
    public string ForGroupMessage(GroupChatBuffer buffer, string sender, string taskText,
        long? replyToTelegramMessageId = null, long telegramMessageId = 0,
        bool isVoiceTranscription = false) =>
        RenderGroupMessage(buffer, sender, taskText, replyToTelegramMessageId, telegramMessageId,
            isVoiceTranscription, _executor.IsProcessWarm);

    /// <summary><see cref="ForGroupMessage"/> for a cold provider process. See <see cref="ForDmColdStart"/>.</summary>
    public string ForGroupMessageColdStart(GroupChatBuffer buffer, string sender, string taskText,
        long? replyToTelegramMessageId = null, long telegramMessageId = 0,
        bool isVoiceTranscription = false) =>
        RenderGroupMessage(buffer, sender, taskText, replyToTelegramMessageId, telegramMessageId,
            isVoiceTranscription, processWarm: false);

    private static string RenderGroupMessage(GroupChatBuffer buffer, string sender, string taskText,
        long? replyToTelegramMessageId, long telegramMessageId, bool isVoiceTranscription, bool processWarm)
    {
        var channelAnchor = buffer.RenderHeader();
        var replyTag = replyToTelegramMessageId is > 0 ? $"[reply_to_message_id: {replyToTelegramMessageId}]" : "";
        var idTags = string.Join(" ", new[]
        {
            telegramMessageId > 0 ? $"[telegram_message_id: {telegramMessageId}]" : "", replyTag,
        }.Where(tag => tag.Length > 0));
        var msgIdLine = idTags.Length > 0 ? idTags + "\n" : "";
        var channelLine = channelAnchor is not null ? $"{channelAnchor}\n" : "";
        var voiceLine = isVoiceTranscription ? $"{VoiceTranscriptionMarker}\n" : "";
        var fromLine = $"[From: {sender}]";
        var header = $"{msgIdLine}{channelLine}{voiceLine}{fromLine}";

        if (processWarm)
            return $"[New message]\n{header} {taskText}";

        var context = buffer.FormatContext();

        var result = "";
        if (context.Length > 0)
        {
            var historySection = channelAnchor is not null ? $"{channelAnchor}\n{context}" : context;
            result += $"[Recent group conversation]\n{historySection}\n\n";
        }

        result += $"[New message]\n{header} {taskText}";
        return result;
    }

    /// <summary>
    /// Build a prompt for a relay directive from another agent.
    /// </summary>
    public string ForRelayDirective(GroupChatBuffer buffer, string sender, string text)
    {
        return $"""
            [channel: relay]
            [Directive from {sender}]
            {text}
            """;
    }

    /// <summary>
    /// Build a prompt for a periodic check-in (debounce, proactive, supervision).
    /// When the buffer is sourced from a real group/DM, the channel anchor identifies it;
    /// falls back to <c>[channel: relay]</c> for headless/workflow buffers (ChatId == 0).
    /// </summary>
    public string ForCheckIn(GroupChatBuffer buffer, string label, string instruction) =>
        RenderCheckIn(buffer, label, instruction, _executor.IsProcessWarm);

    /// <summary><see cref="ForCheckIn"/> for a cold provider process. See <see cref="ForDmColdStart"/>.</summary>
    public string ForCheckInColdStart(GroupChatBuffer buffer, string label, string instruction) =>
        RenderCheckIn(buffer, label, instruction, processWarm: false);

    private static string RenderCheckIn(GroupChatBuffer buffer, string label, string instruction, bool processWarm)
    {
        var channelAnchor = buffer.RenderHeader() ?? "[channel: relay]";
        var context = processWarm
            ? buffer.FormatNewMessages()
            : buffer.FormatContext();

        if (context.Length > 0)
        {
            var contextLabel = processWarm
                ? "New messages since last check-in"
                : "Recent group conversation";
            return $"""
                {channelAnchor}
                [{contextLabel}]
                {context}

                [{label}]
                {instruction}
                """;
        }

        return $"""
            {channelAnchor}
            [{label}]
            {instruction}
            """;
    }
}
