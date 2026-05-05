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
        var msgIdTag = telegramMessageId > 0 ? $"[telegram_message_id: {telegramMessageId}]" : "";
        var replyContext = replyToText is not null
            ? $"\n[Replying to: \"{TruncateReplyText(replyToText, 300)}\"]"
            : "";

        if (_executor.IsProcessWarm)
        {
            var meta = string.Concat(msgIdTag, replyContext);
            return meta.Length > 0 ? $"{meta}\n{taskText}" : taskText;
        }

        var context = buffer.FormatContext();
        var metaCold = string.Concat(msgIdTag, replyContext);
        if (context.Length > 0)
            return $"[Recent conversation]\n{context}\n\n[New message]{metaCold}\n{taskText}";

        return metaCold.Length > 0 ? $"[New message]{metaCold}\n{taskText}" : taskText;
    }

    /// <summary>
    /// Build a prompt for a group message task (mention, reply, or /new).
    /// For media groups, <paramref name="telegramMessageId"/> is the first photo's message ID.
    /// </summary>
    public string ForGroupMessage(GroupChatBuffer buffer, string sender, string taskText,
        string? replyToUsername = null, string? replyToText = null, long telegramMessageId = 0)
    {
        var msgIdTag = telegramMessageId > 0 ? $"[telegram_message_id: {telegramMessageId}]" : "";
        var replyContext = replyToUsername is not null && replyToText is not null
            ? $"\n[Replying to {replyToUsername}: \"{TruncateReplyText(replyToText, 300)}\"]"
            : replyToUsername is not null
                ? $"\n[Replying to {replyToUsername}]"
                : "";

        var fromLine = msgIdTag.Length > 0
            ? string.Concat($"[From: {sender}] ", msgIdTag, replyContext)
            : string.Concat($"[From: {sender}]", replyContext);

        if (_executor.IsProcessWarm)
            return $"[New message]\n{fromLine} {taskText}";

        var context = buffer.FormatContext();

        var result = "";
        if (context.Length > 0)
            result += $"[Recent group conversation]\n{context}\n\n";

        result += $"[New message]\n{fromLine} {taskText}";
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
            [Directive from {sender}]
            {text}
            """;
    }

    /// <summary>
    /// Build a prompt for a periodic check-in (debounce, proactive, supervision).
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
                [{contextLabel}]
                {context}

                [{label}]
                {instruction}
                """;
        }

        return $"""
            [{label}]
            {instruction}
            """;
    }
}
