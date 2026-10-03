using Fleet.Conversations.Contracts;
using Fleet.Conversations.Journal;

namespace Fleet.Comms.Routes;

/// <summary>A binding only narrows the caller's observed/all scope, never replaces it.</summary>
public sealed class JournalBindingScope(JournalTurnBindings bindings, IReadOnlySet<long> excluded)
{
    public JournalTurnBinding? Get(string subject) => bindings.Get(subject);
    public bool IsExcluded(long chatId) => excluded.Contains(chatId);
    public (string? Key, string? Reason) Resolve(string subject)
    {
        var bound = bindings.Get(subject);
        if (bound is null) return (null, "no_bound_conversation");
        if (excluded.Contains(bound.ChatId!.Value)) return (null, "conversation_not_journaled");
        if (!JournalWire.TryParse<JournalChatKind>(bound.ChatKind, out var kind))
            throw new InvalidOperationException("Turn binding contains an invalid chat kind.");
        return (JournalKeys.ConversationKey(new JournalTelegramRef
        { BotId = bound.BotId!.Value, ChatId = bound.ChatId.Value, ChatKind = kind, MessageId = 0 }), null);
    }
}
