namespace Fleet.Conversations.Contracts;

/// <summary>Which way a journaled message travelled, seen from the observing bot.</summary>
public enum JournalDirection { Inbound, Outbound }

/// <summary>The platform's chat type, copied from the update. Never inferred from the chat id's sign.</summary>
public enum JournalChatKind { Private, Group, Supergroup }

/// <summary>
/// The PUBLISHER-side task origin: the kind of work that produced an outbound message.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ This is NOT the wire record's <c>origin</c> field (<see cref="JournalRecordOrigin"/>), which
/// says which runtime observed the message. This one says why the message exists.
/// </para>
/// <list type="bullet">
///   <item><see cref="Relay"/>: work delegated by another agent or a workflow engine.</item>
///   <item><see cref="Bridge"/>: a request from an external agent through the consultation bridge.</item>
///   <item><see cref="Human"/>: everything else — a direct human message, a command, a scheduled
///   check-in, a batched group message.</item>
/// </list>
/// <para>
/// The capture slice maps the agent's existing task source to it: its <c>Relay</c> and
/// <c>Bridge</c> values map one to one, and every other value maps to <see cref="Human"/>. The enum
/// deliberately does not reference the agent assembly, so this project stays BCL-only.
/// </para>
/// </remarks>
public enum JournalTaskOrigin { Human, Relay, Bridge }

/// <summary>
/// The chats a publisher is allowed to journal.
/// </summary>
/// <param name="UserIds">Private-chat user ids.</param>
/// <param name="GroupIds">Group and supergroup chat ids.</param>
public sealed record JournalAllowlist(IReadOnlySet<long> UserIds, IReadOnlySet<long> GroupIds);

/// <summary>Why a message is not journaled.</summary>
public enum JournalExclusion { NoChat, SystemOrigin, OperationalChat, UnauthorizedChat }

/// <summary>The classifier's answer. <see cref="Reason"/> is null exactly when <see cref="Include"/> is true.</summary>
public readonly record struct JournalDecision(bool Include, JournalExclusion? Reason);

/// <summary>
/// Decides whether a message may be journaled. Pure and BCL-only, so a publisher can call it
/// without referencing the store.
/// </summary>
/// <remarks>
/// <para>
/// <b>Classification comes before persistence.</b> A publisher classifies first and writes nothing
/// for an excluded message — no local file, no request. Deleting afterwards is too late: the row
/// would already be in a backup.
/// </para>
/// <para>
/// The ingest endpoint calls the same function with <see cref="JournalTaskOrigin.Human"/> and a
/// null allowlist, so only rules 1 and 3 can apply there.
/// </para>
/// </remarks>
public static class JournalClassifier
{
    /// <summary>
    /// Applies the rules in order and stops at the first match:
    /// <list type="number">
    ///   <item><paramref name="chatId"/> is 0 → <see cref="JournalExclusion.NoChat"/>;</item>
    ///   <item>outbound, and <paramref name="origin"/> is relay or bridge work →
    ///   <see cref="JournalExclusion.SystemOrigin"/>;</item>
    ///   <item>the chat is in <paramref name="excludedChatIds"/> →
    ///   <see cref="JournalExclusion.OperationalChat"/>;</item>
    ///   <item>an allowlist is given and does not hold the chat →
    ///   <see cref="JournalExclusion.UnauthorizedChat"/>;</item>
    ///   <item>otherwise the message is included.</item>
    /// </list>
    /// </summary>
    /// <param name="origin">Ignored for <see cref="JournalDirection.Inbound"/>.</param>
    /// <param name="allowlist">Null means "no allowlist available", and rule 4 is skipped.</param>
    public static JournalDecision Classify(
        JournalDirection direction, long chatId, JournalChatKind chatKind,
        JournalTaskOrigin origin, JournalAllowlist? allowlist, IReadOnlySet<long> excludedChatIds)
    {
        ArgumentNullException.ThrowIfNull(excludedChatIds);

        if (chatId == 0)
            return new JournalDecision(false, JournalExclusion.NoChat);

        if (direction == JournalDirection.Outbound
            && origin is JournalTaskOrigin.Relay or JournalTaskOrigin.Bridge)
            return new JournalDecision(false, JournalExclusion.SystemOrigin);

        if (excludedChatIds.Contains(chatId))
            return new JournalDecision(false, JournalExclusion.OperationalChat);

        if (allowlist is not null)
        {
            var allowed = chatKind == JournalChatKind.Private
                ? allowlist.UserIds.Contains(chatId)
                : allowlist.GroupIds.Contains(chatId);

            if (!allowed)
                return new JournalDecision(false, JournalExclusion.UnauthorizedChat);
        }

        return new JournalDecision(true, null);
    }
}
