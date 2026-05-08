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
    private readonly IAgentExecutor _executor;

    public PromptAssembler(IAgentExecutor executor)
    {
        _executor = executor;
    }

    /// <summary>
    /// Build a prompt for a direct message (DM) task.
    /// </summary>
    public string ForDm(GroupChatBuffer buffer, string taskText,
        string? replyToText = null, long telegramMessageId = 0)
    {
        var channelAnchor = buffer.RenderHeader();
        var msgIdTag = telegramMessageId > 0 ? $"[telegram_message_id: {telegramMessageId}]" : "";
        var channelLine = channelAnchor is not null ? $"\n{channelAnchor}" : "";
        var replyContext = replyToText is not null
            ? $"\n[Replying to: \"{TruncateReplyText(replyToText, 300)}\"]"
            : "";

        if (_executor.IsProcessWarm)
        {
            var meta = string.Concat(msgIdTag, channelLine, replyContext);
            return meta.Length > 0 ? $"{meta}\n{taskText}" : taskText;
        }

        var context = buffer.FormatContext();
        var metaCold = string.Concat(msgIdTag, channelLine, replyContext);
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
        string? replyToUsername = null, string? replyToText = null, long telegramMessageId = 0)
    {
        var channelAnchor = buffer.RenderHeader();
        var msgIdLine = telegramMessageId > 0 ? $"[telegram_message_id: {telegramMessageId}]\n" : "";
        var channelLine = channelAnchor is not null ? $"{channelAnchor}\n" : "";
        var replyContext = replyToUsername is not null && replyToText is not null
            ? $" [Replying to {replyToUsername}: \"{TruncateReplyText(replyToText, 300)}\"]"
            : replyToUsername is not null
                ? $" [Replying to {replyToUsername}]"
                : "";

        // [telegram_message_id: N]  (optional)
        // [channel: group ...]      (optional)
        // [From: sender][reply]
        var fromLine = $"[From: {sender}]{replyContext}";
        var header = $"{msgIdLine}{channelLine}{fromLine}";

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
    /// Check-ins are proactively initiated by the agent — no associated Telegram chat.
    /// </summary>
    public string ForCheckIn(GroupChatBuffer buffer, string label, string instruction)
    {
        var context = _executor.IsProcessWarm
            ? buffer.FormatNewMessages()
            : buffer.FormatContext();

        if (context.Length > 0)
        {
            var contextLabel = _executor.IsProcessWarm
                ? "New messages since last check-in"
                : "Recent group conversation";
            return $"""
                [channel: relay]
                [{contextLabel}]
                {context}

                [{label}]
                {instruction}
                """;
        }

        return $"""
            [channel: relay]
            [{label}]
            {instruction}
            """;
    }
}
