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
    /// <param name="chatId">The originating Telegram chat ID. Injected as <c>[Chat: {chatId}]</c> into the prompt so the agent has a deterministic reply target.</param>
    public string ForDm(long chatId, GroupChatBuffer buffer, string taskText,
        string? replyToText = null)
    {
        var chatTag = $"[Chat: {chatId}]";
        var replyContext = replyToText is not null
            ? $"\n[Replying to: \"{TruncateReplyText(replyToText, 300)}\"]"
            : "";

        if (_executor.IsProcessWarm)
            return replyContext.Length > 0
                ? $"{chatTag}{replyContext}\n{taskText}"
                : $"{chatTag}\n{taskText}";

        var context = buffer.FormatContext();
        if (context.Length > 0)
            return $"[Recent conversation]\n{context}\n\n{chatTag}\n[New message]{replyContext}\n{taskText}";

        return replyContext.Length > 0
            ? $"{chatTag}\n[New message]{replyContext}\n{taskText}"
            : $"{chatTag}\n{taskText}";
    }

    /// <summary>
    /// Build a prompt for a group message task (mention, reply, or /new).
    /// </summary>
    /// <param name="chatId">The originating Telegram chat ID. Injected as <c>[Chat: {chatId}]</c> into the prompt so the agent has a deterministic reply target.</param>
    public string ForGroupMessage(long chatId, GroupChatBuffer buffer, string sender, string taskText,
        string? replyToUsername = null, string? replyToText = null)
    {
        var chatTag = $"[Chat: {chatId}]";
        var replyContext = replyToUsername is not null && replyToText is not null
            ? $"\n[Replying to {replyToUsername}: \"{TruncateReplyText(replyToText, 300)}\"]"
            : replyToUsername is not null
                ? $"\n[Replying to {replyToUsername}]"
                : "";

        if (_executor.IsProcessWarm)
            return $"{chatTag}\n[New message]\n[From: {sender}]{replyContext} {taskText}";

        var context = buffer.FormatContext();

        var result = "";
        if (context.Length > 0)
            result += $"[Recent group conversation]\n{context}\n\n";

        result += $"{chatTag}\n[New message]\n[From: {sender}]{replyContext} {taskText}";
        return result;
    }

    private static string TruncateReplyText(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "...";

    /// <summary>
    /// Build a prompt for a relay directive from another agent.
    /// </summary>
    /// <param name="chatId">The originating Telegram chat ID. Injected as <c>[Chat: {chatId}]</c> into the prompt so the agent has a deterministic reply target.</param>
    public string ForRelayDirective(long chatId, GroupChatBuffer buffer, string sender, string text)
    {
        return $"""
            [Chat: {chatId}]
            [Directive from {sender}]
            {text}
            """;
    }

    /// <summary>
    /// Build a prompt for a periodic check-in (debounce, proactive, supervision).
    /// </summary>
    /// <param name="chatId">The originating Telegram chat ID. Injected as <c>[Chat: {chatId}]</c> into the prompt so the agent has a deterministic reply target.</param>
    public string ForCheckIn(long chatId, GroupChatBuffer buffer, string label, string instruction)
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

                [Chat: {chatId}]
                [{label}]
                {instruction}
                """;
        }

        return $"""
            [Chat: {chatId}]
            [{label}]
            {instruction}
            """;
    }
}
