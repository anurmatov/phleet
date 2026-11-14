using Microsoft.Extensions.Logging;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Fleet.Agent.Services.TelegramShell;

/// <summary>
/// A reaction added to or removed from a message becomes a task for the agent, one per changed
/// emoji, under the same authorization as a message from that chat or user.
/// </summary>
internal sealed class TelegramReactionHandler(AllowlistHolder allowlist, TaskManager taskManager, ILogger logger)
{
    private readonly AllowlistHolder _allowlist = allowlist;
    private readonly TaskManager _taskManager = taskManager;
    private readonly ILogger _logger = logger;

    internal void Handle(MessageReactionUpdated reaction)
    {
        // Anonymous group admins have no User context (Telegram uses actor_chat instead).
        // We can't attribute the reaction to a specific user, so discard it.
        if (reaction.User is null)
        {
            _logger.LogWarning(
                "Ignoring reaction on message {MsgId} in chat {ChatId} — anonymous admin (no user context)",
                reaction.MessageId, reaction.Chat.Id);
            return;
        }

        var chatId = reaction.Chat.Id;
        var userId = reaction.User.Id;
        var messageId = reaction.MessageId;

        // Authorization: same rules as regular messages
        var isGroupChat = reaction.Chat.Type is ChatType.Group or ChatType.Supergroup;
        if (isGroupChat)
        {
            if (!_allowlist.IsGroupAllowed(chatId))
            {
                _logger.LogDebug("Ignoring reaction from unauthorized group {ChatId}", chatId);
                return;
            }
        }
        else
        {
            if (userId == 0 || !_allowlist.IsUserAllowed(userId))
            {
                _logger.LogDebug("Ignoring reaction from unauthorized user {UserId}", userId);
                return;
            }
        }

        var (added, removed) = DiffReactions(reaction.NewReaction, reaction.OldReaction);
        if (added.Count == 0 && removed.Count == 0) return; // no net change

        var channelAnchor = BuildChannelAnchorFromChat(reaction.Chat);
        foreach (var emoji in added)
        {
            var text = $"{channelAnchor}\n[reaction: {emoji} on message_id={messageId} from user_id={userId}]";
            _taskManager.StartTaskDetached(chatId, text, text, isSessionTask: true, userId: userId);
        }

        foreach (var emoji in removed)
        {
            var text = $"{channelAnchor}\n[reaction removed: {emoji} on message_id={messageId} from user_id={userId}]";
            _taskManager.StartTaskDetached(chatId, text, text, isSessionTask: true, userId: userId);
        }
    }

    /// <summary>
    /// Builds a <c>[channel: ...]</c> anchor string from a Telegram <see cref="Chat"/> object.
    /// Used for reaction events where the full Chat is available directly.
    /// </summary>
    internal static string BuildChannelAnchorFromChat(Chat chat)
    {
        var chatId = chat.Id;
        if (chatId < 0)
        {
            return chat.Title is { Length: > 0 }
                ? $"[channel: group chat_id={chatId} title=\"{chat.Title.Replace("\"", "\\\"")}\"]"
                : $"[channel: group chat_id={chatId}]";
        }
        // DM
        if (chat.Username is { Length: > 0 })
            return $"[channel: dm chat_id={chatId} user=@{chat.Username}]";
        if (chat.FirstName is { Length: > 0 })
            return $"[channel: dm chat_id={chatId} name=\"{chat.FirstName.Replace("\"", "\\\"")}\"]";
        return $"[channel: dm chat_id={chatId}]";
    }

    /// <summary>
    /// Diffs two reaction lists, returning only standard emoji (<see cref="ReactionTypeEmoji"/>)
    /// that were added or removed. Custom emoji and paid reactions are silently ignored.
    /// </summary>
    internal static (HashSet<string> added, HashSet<string> removed) DiffReactions(
        IEnumerable<ReactionType>? newReaction,
        IEnumerable<ReactionType>? oldReaction)
    {
        var newEmojis = (newReaction ?? []).OfType<ReactionTypeEmoji>().Select(r => r.Emoji).ToHashSet();
        var oldEmojis = (oldReaction ?? []).OfType<ReactionTypeEmoji>().Select(r => r.Emoji).ToHashSet();
        return ([.. newEmojis.Except(oldEmojis)], [.. oldEmojis.Except(newEmojis)]);
    }
}
