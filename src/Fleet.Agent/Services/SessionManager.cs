using System.Collections.Concurrent;

namespace Fleet.Agent.Services;

/// <summary>
/// Tracks Claude session IDs per Telegram chat so follow-up messages
/// can use --resume to continue context.
/// </summary>
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
