using System.Collections.Concurrent;

namespace Fleet.Journal.Client;

/// <summary>
/// In-process journal counters, the same shape as the agent's other counters. Labels are fixed
/// codes only — never a chat id, a subject, a token or text.
/// </summary>
public sealed class JournalCounters
{
    private readonly ConcurrentDictionary<string, long> _values = new(StringComparer.Ordinal);

    public void Captured(string direction) => Add($"journal_captured{{direction={direction}}}");
    public void Excluded(string reason) => Add($"journal_excluded{{reason={reason}}}");
    public void Delivered() => Add("journal_delivered");
    public void Dead(string reason) => Add($"journal_dead{{reason={reason}}}");
    public void Dropped(string reason) => Add($"journal_dropped{{reason={reason}}}");
    public void SpoolFull() => Add("journal_spool_dropped{reason=full}");
    public void CaptureFailed() => Add("journal_capture_failed");
    public void MediaDisabled() => Add("journal_media_disabled");
    public void EndpointMissing() => Add("journal_endpoint_missing");

    /// <summary>Records lost on the agent before they reached the spool: spool full or a write error.</summary>
    public long LocallyDropped => Get("journal_spool_dropped{reason=full}") + Get("journal_capture_failed");

    public long Get(string name) => _values.TryGetValue(name, out var v) ? v : 0;

    public IReadOnlyDictionary<string, long> Snapshot() =>
        new SortedDictionary<string, long>(_values.ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal);

    private void Add(string name) => _values.AddOrUpdate(name, 1, (_, n) => n + 1);
}
