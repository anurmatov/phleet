using System.ComponentModel;
using System.Text.Json;
using Fleet.Telegram.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;

namespace Fleet.Telegram.Tools;

[McpServerToolType]
public sealed class SetReactionTool(BotClientFactory factory, IHttpContextAccessor httpContextAccessor, ILogger<SetReactionTool> logger)
{
    /// <summary>
    /// Standard (non-premium) Telegram reaction emoji allowlist.
    /// Source: Telegram Bot API — ReactionTypeEmoji.emoji values.
    /// </summary>
    private static readonly HashSet<string> AllowedEmoji =
    [
        "👍", "👎", "❤", "🔥", "🥰", "👏", "😁", "🤔", "🤯", "😱",
        "🤬", "😢", "🎉", "🤩", "🤮", "💩", "🙏", "👌", "🕊", "🤡",
        "🥱", "🥴", "😍", "🐳", "❤‍🔥", "🌚", "🌭", "💯", "🤣", "⚡",
        "🍌", "🏆", "💔", "🤨", "😐", "🍓", "🍾", "💋", "🖕", "😈",
        "😴", "😭", "🤓", "👻", "👨‍💻", "👀", "🎃", "🙈", "😇", "😨",
        "🤝", "✍", "🤗", "🫡", "🎅", "🎄", "☃", "💅", "🤪", "🗿",
        "🆒", "💘", "🙉", "🦄", "😘", "💊", "🙊", "😎", "👾", "🤷‍♂",
        "🤷", "🤷‍♀", "😡"
    ];

    /// <summary>Returns true when <paramref name="emoji"/> is a standard (non-premium) Telegram reaction emoji.</summary>
    internal static bool IsAllowedEmoji(string emoji) => AllowedEmoji.Contains(emoji);

    [McpServerTool(Name = "set_reaction")]
    [Description("Set an emoji reaction on a Telegram message. Validates the emoji against the standard allowlist before calling the API. Returns {\"ok\":true} on success or {\"ok\":false,\"error\":\"...\"} on failure.")]
    public async Task<string> SetReactionAsync(
        [Description("Telegram chat ID as integer or string")] string chat_id,
        [Description("ID of the message to react to")] int message_id,
        [Description("Standard emoji to react with (e.g. \"👍\"). Must be a non-premium Telegram reaction emoji.")] string emoji,
        CancellationToken cancellationToken = default)
    {
        // Agent identity is resolved server-side from the ?agent= query parameter
        // baked into the MCP URL at provision time — never supplied by the caller.
        var agent_name = httpContextAccessor.HttpContext?.Request.Query["agent"].FirstOrDefault() ?? "";

        if (!long.TryParse(chat_id?.Trim(), out var chatIdLong))
            return JsonSerializer.Serialize(new { ok = false, error = $"Invalid chat_id '{chat_id}' — must be a numeric value" });

        // Validate emoji before any API call
        if (string.IsNullOrEmpty(emoji))
            return JsonSerializer.Serialize(new { ok = false, error = "emoji is required" });

        if (!IsAllowedEmoji(emoji))
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = $"Emoji '{emoji}' is not in the standard Telegram reaction allowlist. Use a non-premium reaction emoji."
            });

        var client = factory.GetClient(agent_name);
        if (client is null)
        {
            const string err = "No bot client available — notifier bot token not configured";
            logger.LogError(err);
            return JsonSerializer.Serialize(new { ok = false, error = err });
        }

        try
        {
            await client.SetMessageReaction(
                chatIdLong,
                message_id,
                [new ReactionTypeEmoji { Emoji = emoji }],
                cancellationToken: cancellationToken);

            return JsonSerializer.Serialize(new { ok = true });
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 429)
        {
            var retryAfter = ex.Parameters?.RetryAfter;
            var msg = retryAfter.HasValue
                ? $"Rate limited by Telegram: retry after {retryAfter.Value} seconds"
                : "Rate limited by Telegram";
            logger.LogWarning("Telegram 429 on set_reaction for chat {ChatId} message {MsgId}: {Msg}", chatIdLong, message_id, msg);
            return JsonSerializer.Serialize(new { ok = false, error = msg });
        }
        catch (ApiRequestException ex) when (ex.Message.Contains("message not found"))
        {
            return JsonSerializer.Serialize(new { ok = false, error = $"Message not found (id={message_id})" });
        }
        catch (ApiRequestException ex) when (ex.Message.Contains("can't be reacted") || ex.Message.Contains("MESSAGE_REACTION_INVALID"))
        {
            return JsonSerializer.Serialize(new { ok = false, error = "Message cannot receive reactions" });
        }
        catch (ApiRequestException ex) when (ex.Message.Contains("Forbidden") || ex.Message.Contains("bot is not a member"))
        {
            return JsonSerializer.Serialize(new { ok = false, error = "Bot lacks permission in this chat" });
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Network error calling Telegram setMessageReaction for chat {ChatId}", chatIdLong);
            return JsonSerializer.Serialize(new { ok = false, error = "Telegram API unreachable" });
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Timeout calling Telegram setMessageReaction for chat {ChatId}", chatIdLong);
            return JsonSerializer.Serialize(new { ok = false, error = "Telegram API unreachable" });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to set reaction on message {MsgId} in chat {ChatId}", message_id, chatIdLong);
            return JsonSerializer.Serialize(new { ok = false, error = ex.Message });
        }
    }
}
