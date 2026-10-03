using System.Collections.Concurrent;

namespace Fleet.Conversations.Journal;

/// <summary>
/// The in-process half of <c>GET /journal/v1/status</c>: refusals, the retention sweep, ingest
/// latency and read-tool calls since this process started.
/// </summary>
/// <remarks>
/// One instance per process, shared by the journal listener and the maintenance loop, so the status
/// route reports the sweep the loop actually ran. The metric counters are emitted here too, so a
/// count in the status body and a count on the meter can never disagree about what was recorded.
/// </remarks>
public sealed class JournalRuntimeStats(TimeProvider? time = null)
{
    private int _attachmentFetchInFlight;
    public int AttachmentFetchInFlight => Volatile.Read(ref _attachmentFetchInFlight);
    public void AttachmentFetchEntered() => Interlocked.Increment(ref _attachmentFetchInFlight);
    public void AttachmentFetchExited() => Interlocked.Decrement(ref _attachmentFetchInFlight);
    public void AttachmentFetchBytes(long bytes) => ConversationMetrics.JournalAttachmentFetchBytes.Add(bytes);
    public void RecordAttachmentFetch(string result) => ConversationMetrics.JournalAttachmentFetch.Add(1, new KeyValuePair<string, object?>("result", result));

    private static readonly TimeSpan LatencyWindow = TimeSpan.FromHours(1);

    /// <summary>Bound on retained samples, so a burst cannot grow the window without limit.</summary>
    private const int MaxLatencySamples = 20_000;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, long> _rejected = new(StringComparer.Ordinal);
    private readonly Queue<(DateTimeOffset At, double Milliseconds)> _latency = new();
    private readonly Queue<(DateTimeOffset At, double Milliseconds)> _uploadLatency = new();
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

    /// <summary>
    /// Records one upload's duration in its OWN window. An upload holds megabytes and a bucket
    /// round trip; folding it into the ingest window would make ingest p95 mean "whichever of the
    /// two was slower", which is not a number anyone can act on.
    /// </summary>
    public void RecordUploadDurationSample(double milliseconds)
    {
        ConversationMetrics.JournalUploadDuration.Record(milliseconds);

        var now = _time.GetUtcNow();
        lock (_gate)
        {
            _uploadLatency.Enqueue((now, milliseconds));
            Prune(_uploadLatency, now);
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

    // ── media (#388) ──────────────────────────────────────────────────────────

    private DateTimeOffset? _objectsLastSweepAt;
    private long _objectsAbandoned;
    private long _objectsRetired;
    private long _objectsOrphans;
    private long _objectSweepFailures;

    /// <summary>Counts one upload attempt by its fixed outcome code, on the status route and the meter.</summary>
    public void Upload(string result)
    {
        ConversationMetrics.JournalUpload.Add(1, new KeyValuePair<string, object?>("result", result));
    }

    public void RecordObjectSweep(int abandoned, int retired, int orphans, int failures)
    {
        lock (_gate)
        {
            _objectsLastSweepAt = _time.GetUtcNow();
            _objectsAbandoned += abandoned;
            _objectsRetired += retired;
            _objectsOrphans += orphans;
            _objectSweepFailures += failures;
        }
    }

    // ── read tools (#394) ─────────────────────────────────────────────────────

    private readonly ConcurrentDictionary<(string Tool, string Result), long> _reads = new();

    /// <summary>Counts one read-tool call by tool and fixed result code, on the status route and the meter.</summary>
    public void RecordRead(string tool, string result, double milliseconds)
    {
        _reads.AddOrUpdate((tool, result), 1, (_, n) => n + 1);

        ConversationMetrics.JournalRead.Add(1,
            new KeyValuePair<string, object?>("tool", tool),
            new KeyValuePair<string, object?>("result", result));
        ConversationMetrics.JournalReadDuration.Record(milliseconds,
            new KeyValuePair<string, object?>("tool", tool));
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

        /// <summary>The media sweep, and the upload latency window. Null sweep time means it has not run.</summary>
        public DateTimeOffset? ObjectsLastSweepAt { get; init; }
        public long ObjectsAbandonedDeleted { get; init; }
        public long ObjectsRetiredDeleted { get; init; }
        public long ObjectsOrphansDeleted { get; init; }
        public long ObjectSweepFailures { get; init; }
        public int UploadSamples { get; init; }
        /// <summary>Upload latency percentiles over the rolling hour. Null when no upload has been sampled.</summary>
        public double? UploadP50Milliseconds { get; init; }
        public double? UploadP95Milliseconds { get; init; }

        /// <summary>Read-tool calls since start: tool → result code → count, both levels sorted.</summary>
        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>> ReadsSinceStart { get; init; } =
            new SortedDictionary<string, IReadOnlyDictionary<string, long>>(StringComparer.Ordinal);
    }

    public Snapshot Read()
    {
        double[] samples;
        double[] uploadSamples;
        Snapshot partial;

        lock (_gate)
        {
            var now = _time.GetUtcNow();
            Prune(_latency, now);
            Prune(_uploadLatency, now);
            samples = _latency.Select(s => s.Milliseconds).ToArray();
            uploadSamples = _uploadLatency.Select(s => s.Milliseconds).ToArray();
            partial = new Snapshot
            {
                RejectedSinceStart = new SortedDictionary<string, long>(
                    _rejected.ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal),
                GcLastRunAt = _gcLastRunAt,
                GcDeletedMessages = _gcMessages,
                GcDeletedConversations = _gcConversations,
                GcFailures = _gcFailures,
                LatencySamples = samples.Length,
                ObjectsLastSweepAt = _objectsLastSweepAt,
                ObjectsAbandonedDeleted = _objectsAbandoned,
                ObjectsRetiredDeleted = _objectsRetired,
                ObjectsOrphansDeleted = _objectsOrphans,
                ObjectSweepFailures = _objectSweepFailures,
                UploadSamples = uploadSamples.Length,
                ReadsSinceStart = new SortedDictionary<string, IReadOnlyDictionary<string, long>>(
                    _reads.GroupBy(kv => kv.Key.Tool, StringComparer.Ordinal).ToDictionary(
                        tool => tool.Key,
                        tool => (IReadOnlyDictionary<string, long>)new SortedDictionary<string, long>(
                            tool.ToDictionary(kv => kv.Key.Result, kv => kv.Value), StringComparer.Ordinal),
                        StringComparer.Ordinal),
                    StringComparer.Ordinal),
            };
        }

        Array.Sort(samples);
        Array.Sort(uploadSamples);
        return partial with
        {
            IngestP50Milliseconds = Percentile(samples, 0.50),
            IngestP95Milliseconds = Percentile(samples, 0.95),
            UploadP50Milliseconds = Percentile(uploadSamples, 0.50),
            UploadP95Milliseconds = Percentile(uploadSamples, 0.95),
        };
    }

    private void Prune(DateTimeOffset now) => Prune(_latency, now);

    private static void Prune(
        Queue<(DateTimeOffset At, double Milliseconds)> queue, DateTimeOffset now)
    {
        while (queue.Count > 0
               && (queue.Count > MaxLatencySamples || now - queue.Peek().At > LatencyWindow))
        {
            queue.Dequeue();
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
