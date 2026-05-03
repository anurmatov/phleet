using System.Collections.Concurrent;

namespace Fleet.Memory.Services;

/// <summary>
/// In-process ACL denial counter keyed by (agent, tool).
/// Resets on container restart — no persistence by design.
/// Exposed via GET /internal/stats/acl-denied for rollout observability.
/// </summary>
public sealed class AclDeniedCounterService
{
    private readonly ConcurrentDictionary<string, long> _counts = new();

    /// <summary>Container start time — the "since" field in the stats snapshot.</summary>
    public DateTimeOffset Since { get; } = DateTimeOffset.UtcNow;

    /// <summary>Increment the denial counter for the given agent and tool.</summary>
    public void Increment(string agent, string tool)
    {
        var key = $"{agent}|{tool}";
        _counts.AddOrUpdate(key, 1, (_, v) => v + 1);
    }

    /// <summary>Returns a snapshot of all denial counts as a flat list.</summary>
    public IReadOnlyList<AclDeniedEntry> GetSnapshot() =>
        _counts
            .Select(kvp =>
            {
                var parts = kvp.Key.Split('|', 2);
                return new AclDeniedEntry(
                    Agent: parts.Length > 0 ? parts[0] : kvp.Key,
                    Tool: parts.Length > 1 ? parts[1] : "",
                    Count: kvp.Value);
            })
            .OrderByDescending(e => e.Count)
            .ToList();
}

public record AclDeniedEntry(string Agent, string Tool, long Count);
