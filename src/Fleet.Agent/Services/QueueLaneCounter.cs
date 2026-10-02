using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace Fleet.Agent.Services;

public sealed class QueueLaneCounter
{
    private static readonly Meter Meter = new("Fleet.Agent.Queue");
    private static readonly Counter<long> Counter = Meter.CreateCounter<long>("fleet_agent_queue_lane_total");
    private readonly ConcurrentDictionary<string, long> _counts = new(StringComparer.Ordinal);
    public long Count(string result) => _counts.GetValueOrDefault(result);
    public void Increment(string result)
    {
        _counts.AddOrUpdate(result, 1, (_, n) => n + 1);
        Counter.Add(1, new KeyValuePair<string, object?>("result", result));
    }
}
