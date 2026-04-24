using System.Collections.Concurrent;

namespace Fleet.Memory.Services;

/// <summary>
/// In-process read counter for memory_get calls.
/// Resets to empty on container restart — no persistence by design.
/// Agent attribution comes from the ?agent= query parameter on the MCP URL.
/// Missing attribution buckets to "unknown" rather than dropping the read.
/// </summary>
public sealed class ReadCounterService
{
    private readonly ConcurrentDictionary<string, MemoryReadStats> _stats = new();

    /// <summary>Container start time — the "since" field in the stats snapshot.</summary>
    public DateTimeOffset Since { get; } = DateTimeOffset.UtcNow;

    /// <summary>Record a read of <paramref name="memoryId"/> by <paramref name="agentName"/>.</summary>
    public void RecordRead(string memoryId, string agentName)
    {
        if (string.IsNullOrWhiteSpace(agentName))
            agentName = "unknown";

        var stats = _stats.GetOrAdd(memoryId, _ => new MemoryReadStats(memoryId));
        stats.RecordRead(agentName);
    }

    /// <summary>Returns all per-memory stats sorted by total read count descending.</summary>
    public IReadOnlyList<MemoryReadStatsDto> GetSnapshot() =>
        _stats.Values
            .Select(s => s.ToDto())
            .OrderByDescending(s => s.Total)
            .ToList();
}

/// <summary>Mutable per-memory read stats (thread-safe via Interlocked).</summary>
public sealed class MemoryReadStats(string memoryId)
{
    private int _total;
    private readonly ConcurrentDictionary<string, AgentReadStats> _byAgent = new();

    public string MemoryId { get; } = memoryId;

    public void RecordRead(string agentName)
    {
        Interlocked.Increment(ref _total);
        _byAgent.GetOrAdd(agentName, _ => new AgentReadStats()).RecordRead();
    }

    public MemoryReadStatsDto ToDto() => new(
        MemoryId,
        _total,
        _byAgent.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToDto()),
        _byAgent.Values.Count == 0 ? DateTimeOffset.MinValue : _byAgent.Values.Max(a => a.LastReadAt));
}

/// <summary>Mutable per-agent stats (thread-safe via Interlocked).</summary>
public sealed class AgentReadStats
{
    private int _count;
    private DateTimeOffset _lastReadAt = DateTimeOffset.UtcNow;

    public DateTimeOffset LastReadAt => _lastReadAt;

    public void RecordRead()
    {
        Interlocked.Increment(ref _count);
        _lastReadAt = DateTimeOffset.UtcNow;
    }

    public AgentReadStatsDto ToDto() => new(_count, _lastReadAt);
}

public record MemoryReadStatsDto(
    string MemoryId,
    int Total,
    Dictionary<string, AgentReadStatsDto> ByAgent,
    DateTimeOffset LastReadAt);

public record AgentReadStatsDto(int Count, DateTimeOffset LastReadAt);
