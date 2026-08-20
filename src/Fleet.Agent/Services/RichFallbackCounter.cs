using System.Collections.Concurrent;

namespace Fleet.Agent.Services;

/// <summary>
/// Tracks per-(agentName, failureType) counts of Rich→LegacyHtml and LegacyHtml→PlainText
/// fallbacks that occur during sendRichMessage sends. Singleton; injected into AgentTransport
/// so tests can assert specific counters without needing a real Telegram connection.
/// </summary>
public sealed class RichFallbackCounter
{
    private readonly ConcurrentDictionary<(string agent, string type), long> _counts = new();

    public void Increment(string agentName, string failureType) =>
        _counts.AddOrUpdate((agentName, failureType), 1L, (_, c) => c + 1L);

    public long GetCount(string agentName, string failureType) =>
        _counts.TryGetValue((agentName, failureType), out var v) ? v : 0L;
}
