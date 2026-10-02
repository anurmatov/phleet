using Fleet.Conversations.Contracts;
using Fleet.Conversations.Journal;

namespace Fleet.Comms.Routes;

/// <summary>A binding only narrows the caller's observed/all scope, never replaces it.</summary>
public sealed class JournalBindingScope(JournalTurnBindings bindings, IReadOnlySet<long> excluded)
{
    public (string? Key, string? Reason) Resolve(string subject)
    {
        var bound = bindings.Get(subject);
        if (bound is null) return (null, "no_bound_conversation");
        if (excluded.Contains(bound.ChatId!.Value)) return (null, "conversation_not_journaled");
        JournalWire.TryParse<JournalChatKind>(bound.ChatKind, out var kind);
        return (JournalKeys.ConversationKey(new JournalTelegramRef
        { BotId = bound.BotId!.Value, ChatId = bound.ChatId.Value, ChatKind = kind, MessageId = 0 }), null);
    }
}
