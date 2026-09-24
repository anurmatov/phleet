using Fleet.Protocol;

namespace Fleet.Agent.Models;

/// <summary>
/// A conversational item that arrived while a session turn was already running.
/// Used either for live mid-turn injection or as the fallback turn-end inbox payload.
///
/// <see cref="Identity"/> rides along so routing identity survives injection and the turn-end
/// inbox drain. It is optional (trailing, defaulted) so every existing construction site is
/// unchanged; a null identity means "synthesize a Telegram identity from the chat key".
///
/// <see cref="ContextRequests"/> (#347) rides along the same way. <see cref="Task"/> is always the
/// ORIGINAL text — never a rendered project-context prefix — which is what lets a process-exit
/// redelivery re-render against the current ledger instead of replaying a stale attachment.
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
    ConversationIdentity? Identity = null,
    IReadOnlyList<ContextAttachmentRequest>? ContextRequests = null);
