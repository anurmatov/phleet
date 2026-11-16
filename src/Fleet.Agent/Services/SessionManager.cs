using System.Collections.Concurrent;

namespace Fleet.Agent.Services;

/// <summary>
/// Remembers, per chat, the session id a session task last reported there.
/// </summary>
/// <remarks>
/// It does not decide what is resumed. One provider process, and so one provider session, serves
/// every chat of an agent; the executor owns that session's id and the session file
/// (<see cref="ProviderSessionFile"/>). This map is read in one place, the <c>Session:</c> line of
/// <c>/status</c>, and only for a provider that reports no session of its own
/// (<see cref="IAgentExecutor.SessionOrigin"/> null).
/// </remarks>
public sealed class SessionManager
{
    private readonly ConcurrentDictionary<long, string> _sessions = new();

    /// <summary>Store a session ID for a given chat.</summary>
    public void SetSession(long chatId, string sessionId) =>
        _sessions[chatId] = sessionId;

    /// <summary>Get the session ID for a chat, if one exists.</summary>
    public string? GetSession(long chatId) =>
        _sessions.TryGetValue(chatId, out var id) ? id : null;

    /// <summary>Clear the session for a chat (e.g., on /reset).</summary>
    public void ClearSession(long chatId) =>
        _sessions.TryRemove(chatId, out _);

    /// <summary>Clear all sessions across all chats (e.g., on /stop).</summary>
    public void ClearAllSessions() =>
        _sessions.Clear();
}
