using System.Collections.Concurrent;

namespace Fleet.Conversations.Journal;

/// <summary>
/// The in-process half of <c>GET /journal/v1/status</c>: refusals, the retention sweep and ingest
/// latency since this process started.
/// </summary>
/// <remarks>
/// One instance per process, shared by the journal listener and the maintenance loop, so the status
/// route reports the sweep the loop actually ran. The metric counters are emitted here too, so a
/// count in the status body and a count on the meter can never disagree about what was recorded.
/// </remarks>
public sealed class JournalRuntimeStats(TimeProvider? time = null)
{
    private static readonly TimeSpan LatencyWindow = TimeSpan.FromHours(1);

    /// <summary>Bound on retained samples, so a burst cannot grow the window without limit.</summary>
    private const int MaxLatencySamples = 20_000;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, long> _rejected = new(StringComparer.Ordinal);
    private readonly Queue<(DateTimeOffset At, double Milliseconds)> _latency = new();
    private readonly Lock _gate = new();

    private DateTimeOffset? _gcLastRunAt;
    private long _gcMessages;
    private long _gcConversations;
    private long _gcFailures;

    /// <summary>Counts one refused request by its error code.</summary>
    public void Rejected(string reason)
    {
        _rejected.AddOrUpdate(reason, 1, (_, n) => n + 1);
        ConversationMetrics.JournalRejected.Add(1, new KeyValuePair<string, object?>("reason", reason));
    }

    /// <summary>Counts one ingest outcome.</summary>
    public static void Ingest(string result) =>
        ConversationMetrics.JournalIngest.Add(1, new KeyValuePair<string, object?>("result", result));

    public void RecordIngestDuration(double milliseconds)
    {
        ConversationMetrics.JournalIngestDuration.Record(milliseconds);

        var now = _time.GetUtcNow();
        lock (_gate)
        {
            _latency.Enqueue((now, milliseconds));
            Prune(now);
        }
    }

    public void RecordSweep(int messages, int conversations)
    {
        lock (_gate)
        {
            _gcLastRunAt = _time.GetUtcNow();
            _gcMessages += messages;
            _gcConversations += conversations;
        }

        if (messages > 0)
            ConversationMetrics.JournalGcDeleted.Add(messages, new KeyValuePair<string, object?>("kind", "message"));

        if (conversations > 0)
            ConversationMetrics.JournalGcDeleted.Add(conversations, new KeyValuePair<string, object?>("kind", "conversation"));
    }

    public void RecordSweepFailure()
    {
        lock (_gate) _gcFailures++;
    }

    public sealed record Snapshot
    {
        public required IReadOnlyDictionary<string, long> RejectedSinceStart { get; init; }
        public DateTimeOffset? GcLastRunAt { get; init; }
        public required long GcDeletedMessages { get; init; }
        public required long GcDeletedConversations { get; init; }
        public required long GcFailures { get; init; }
        public required int LatencySamples { get; init; }
        public double? IngestP50Milliseconds { get; init; }
        public double? IngestP95Milliseconds { get; init; }
    }

    public Snapshot Read()
    {
        double[] samples;
        Snapshot partial;

        lock (_gate)
        {
            Prune(_time.GetUtcNow());
            samples = _latency.Select(s => s.Milliseconds).ToArray();
            partial = new Snapshot
            {
                RejectedSinceStart = new SortedDictionary<string, long>(
                    _rejected.ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal),
                GcLastRunAt = _gcLastRunAt,
                GcDeletedMessages = _gcMessages,
                GcDeletedConversations = _gcConversations,
                GcFailures = _gcFailures,
                LatencySamples = samples.Length,
            };
        }

        Array.Sort(samples);
        return partial with
        {
            IngestP50Milliseconds = Percentile(samples, 0.50),
            IngestP95Milliseconds = Percentile(samples, 0.95),
        };
    }

    private void Prune(DateTimeOffset now)
    {
        while (_latency.Count > 0
               && (_latency.Count > MaxLatencySamples || now - _latency.Peek().At > LatencyWindow))
        {
            _latency.Dequeue();
        }
    }

    /// <summary>Nearest-rank percentile over sorted samples; null when there are none.</summary>
    private static double? Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return null;

        var rank = (int)Math.Ceiling(p * sorted.Length);
        return sorted[Math.Clamp(rank, 1, sorted.Length) - 1];
    }
}
