using Fleet.Conversations.Contracts;

namespace Fleet.Journal.Client;

/// <summary>
/// The publisher's classification step: Telegram's chat type in, a decision out, BEFORE anything
/// is written.
/// </summary>
/// <remarks>
/// The rules themselves are the shared pure function
/// <see cref="Conversations.Contracts.JournalClassifier.Classify"/>; this adds only the mapping
/// from Telegram's chat type and the reason strings the counters use. An excluded message writes
/// nothing — no spool file, no request — and only increments <c>journal_excluded{reason}</c>.
/// </remarks>
public static class JournalClassifier
{
    /// <summary>
    /// Maps Telegram's <c>Chat.Type</c> (<c>private</c>, <c>group</c>, <c>supergroup</c>). A channel
    /// or anything else has no journal kind and is never captured.
    /// </summary>
    public static JournalChatKind? ChatKind(string? telegramChatType) => telegramChatType?.ToLowerInvariant() switch
    {
        "private" => JournalChatKind.Private,
        "group" => JournalChatKind.Group,
        "supergroup" => JournalChatKind.Supergroup,
        _ => null,
    };

    /// <summary>Classifies, or returns an exclusion with <paramref name="reason"/> set.</summary>
    public static bool ShouldCapture(
        JournalDirection direction, long chatId, JournalChatKind? kind, JournalTaskOrigin origin,
        JournalAllowlist allowlist, IReadOnlySet<long> excludedChatIds, out string reason)
    {
        if (kind is null)
        {
            reason = "unsupported_chat";
            return false;
        }

        var decision = Conversations.Contracts.JournalClassifier.Classify(
            direction, chatId, kind.Value, origin, allowlist, excludedChatIds);

        reason = decision.Reason switch
        {
            null => "",
            JournalExclusion.NoChat => "no_chat",
            JournalExclusion.SystemOrigin => "system_origin",
            JournalExclusion.OperationalChat => "operational_chat",
            JournalExclusion.UnauthorizedChat => "unauthorized_chat",
            _ => "unknown",
        };

        return decision.Include;
    }
}
