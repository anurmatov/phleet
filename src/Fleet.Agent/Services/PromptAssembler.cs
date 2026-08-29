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
        string? replyToText = null, long telegramMessageId = 0,
        bool isVoiceTranscription = false)
    {
        var channelAnchor = buffer.RenderHeader();
        var msgIdTag = telegramMessageId > 0 ? $"[telegram_message_id: {telegramMessageId}]" : "";
        var channelLine = channelAnchor is not null ? $"\n{channelAnchor}" : "";
        var voiceLine = isVoiceTranscription ? $"\n{VoiceTranscriptionMarker}" : "";
        var replyContext = replyToText is not null
            ? $"\n[Replying to: \"{TruncateReplyText(replyToText, 300)}\"]"
            : "";

        if (_executor.IsProcessWarm)
        {
            var meta = string.Concat(msgIdTag, channelLine, voiceLine, replyContext);
            return meta.Length > 0 ? $"{meta}\n{taskText}" : taskText;
        }

        var context = buffer.FormatContext();
        var metaCold = string.Concat(msgIdTag, channelLine, voiceLine, replyContext);
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
        string? replyToUsername = null, string? replyToText = null, long telegramMessageId = 0,
        bool isVoiceTranscription = false)
    {
        var channelAnchor = buffer.RenderHeader();
        var msgIdLine = telegramMessageId > 0 ? $"[telegram_message_id: {telegramMessageId}]\n" : "";
        var channelLine = channelAnchor is not null ? $"{channelAnchor}\n" : "";
        var voiceLine = isVoiceTranscription ? $"{VoiceTranscriptionMarker}\n" : "";
        var replyContext = replyToUsername is not null && replyToText is not null
            ? $" [Replying to {replyToUsername}: \"{TruncateReplyText(replyToText, 300)}\"]"
            : replyToUsername is not null
                ? $" [Replying to {replyToUsername}]"
                : "";

        // [telegram_message_id: N]         (optional)
        // [channel: group ...]             (optional)
        // [voice_transcription: whisper…]  (optional)
        // [From: sender][reply]
        var fromLine = $"[From: {sender}]{replyContext}";
        var header = $"{msgIdLine}{channelLine}{voiceLine}{fromLine}";

        if (_executor.IsProcessWarm)
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

    private static string TruncateReplyText(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "...";

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
    public string ForCheckIn(GroupChatBuffer buffer, string label, string instruction)
    {
        var channelAnchor = buffer.RenderHeader() ?? "[channel: relay]";
        var context = _executor.IsProcessWarm
            ? buffer.FormatNewMessages()
            : buffer.FormatContext();

        if (context.Length > 0)
        {
            var contextLabel = _executor.IsProcessWarm
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
