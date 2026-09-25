using Fleet.Protocol;

namespace Fleet.Agent.Models;

/// <summary>
/// A conversational item that arrived while a session turn was already running.
/// Used either for live mid-turn injection or as the fallback turn-end inbox payload.
///
/// <see cref="Identity"/> rides along so routing identity survives injection and the turn-end
/// inbox drain. It is optional (trailing, defaulted) so every existing construction site is
/// unchanged; a null identity means "synthesize a Telegram identity from the chat key".
/// </summary>
public sealed record MidTurnMessage(
    string Task,
    string DisplayText,
    bool IsSessionTask,
    TaskSource Source,
    string? RelaySender,
    string? CorrelationId,
    string? TaskId,
    IReadOnlyList<MessageImage>? Images,
    IReadOnlyList<MessageDocument>? Documents,
    long UserId,
    DateTimeOffset ArrivedAt,
    ConversationIdentity? Identity = null);
