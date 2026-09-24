using System.Collections.Concurrent;
using ModelContextProtocol.AspNetCore;
using Microsoft.Extensions.Options;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// Which MCP sessions were created on <c>/mcp/context</c>, and for which agent (#347 D6).
/// </summary>
/// <remarks>
/// <para>
/// The SDK keeps one session table for every <c>MapMcp</c> route, and a session's tool collection is
/// fixed when it is created. So the route a request arrives on says nothing about the session it
/// names: a session made on <c>/mcp</c> keeps every admin tool when replayed on
/// <c>/mcp/context</c>, and a context session sees whatever <c>?agent=</c> the current request
/// carries. This registry is the other half of that fact — <see cref="ContextMcpSessionGuard"/>
/// binds each context session to its route and agent here, and rejects any later request that
/// disagrees.
/// </para>
/// <para>
/// In memory on purpose. An orchestrator restart empties both this and the SDK's session table, and
/// clients re-initialize on the 404 an unbound id gets — nothing is lost.
/// </para>
/// <para>
/// Lifetime follows the transport's own limits, read from <see cref="HttpServerTransportOptions"/>
/// rather than restated: an entry idle longer than <c>IdleTimeout</c> is pruned (every 60 s, by
/// <see cref="ContextSessionPruneService"/>), and the table never holds more than
/// <c>MaxIdleSessionCount</c> entries, evicting the least recently seen first.
/// </para>
/// </remarks>
public sealed class ContextSessionRegistry(IOptionsMonitor<HttpServerTransportOptions> transportOptions, TimeProvider time)
{
    private readonly ConcurrentDictionary<string, Entry> _sessions = new(StringComparer.Ordinal);

    private sealed record Entry(string Agent, DateTimeOffset LastSeen);

    public int Count => _sessions.Count;

    /// <summary>Binds <paramref name="sessionId"/> to <paramref name="agent"/>, then enforces the cap.</summary>
    public void Bind(string sessionId, string agent)
    {
        _sessions[sessionId] = new Entry(agent, time.GetUtcNow());
        EnforceCap();
    }

    public bool TryGetAgent(string sessionId, out string agent)
    {
        if (_sessions.TryGetValue(sessionId, out var entry))
        {
            agent = entry.Agent;
            return true;
        }

        agent = "";
        return false;
    }

    public bool Contains(string sessionId) => _sessions.ContainsKey(sessionId);

    /// <summary>Refreshes <c>lastSeen</c> for a bound session. A no-op for an unknown id.</summary>
    public void Touch(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var entry))
            _sessions.TryUpdate(sessionId, entry with { LastSeen = time.GetUtcNow() }, entry);
    }

    public bool Remove(string sessionId) => _sessions.TryRemove(sessionId, out _);

    /// <summary>
    /// Drops entries idle longer than the transport <c>IdleTimeout</c>, then enforces the cap.
    /// Returns how many were removed.
    /// </summary>
    public int Prune()
    {
        var removed = 0;
        var idleTimeout = transportOptions.CurrentValue.IdleTimeout;

        // A non-positive timeout (e.g. Timeout.InfiniteTimeSpan) means the SDK never idles a
        // session out, so neither does the binding.
        if (idleTimeout > TimeSpan.Zero)
        {
            var cutoff = time.GetUtcNow() - idleTimeout;
            foreach (var (id, entry) in _sessions)
            {
                if (entry.LastSeen < cutoff && _sessions.TryRemove(new KeyValuePair<string, Entry>(id, entry)))
                    removed++;
            }
        }

        return removed + EnforceCap();
    }

    private int EnforceCap()
    {
        var cap = transportOptions.CurrentValue.MaxIdleSessionCount;
        var excess = _sessions.Count - Math.Max(cap, 0);
        if (excess <= 0) return 0;

        var removed = 0;
        foreach (var (id, _) in _sessions.OrderBy(kv => kv.Value.LastSeen).Take(excess).ToList())
        {
            if (_sessions.TryRemove(id, out _))
                removed++;
        }

        return removed;
    }
}

/// <summary>Prunes <see cref="ContextSessionRegistry"/> every 60 s.</summary>
public sealed class ContextSessionPruneService(
    ContextSessionRegistry registry,
    TimeProvider time,
    ILogger<ContextSessionPruneService> logger) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var removed = registry.Prune();
                if (removed > 0)
                    logger.LogInformation(
                        "ContextMcpGuard pruned {Removed} binding(s), {Remaining} remaining", removed, registry.Count);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutdown
        }
    }
}
