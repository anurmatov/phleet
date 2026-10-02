namespace Fleet.Conversations.Contracts;

/// <summary>The runtime's desired current-turn scope. No message content or credentials.</summary>
public sealed record JournalTurnBinding(
    string Epoch, long Seq, string State,
    string? ChatKind = null, long? BotId = null, long? ChatId = null);
