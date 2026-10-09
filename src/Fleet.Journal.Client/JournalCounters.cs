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

    /// <summary>
    /// An attachment whose bytes did not reach the store, counted by the reason the record carries.
    /// <paramref name="reason"/> is one of the six fixed wire codes, never a caller-built string.
    /// </summary>
    public void MediaReason(string reason) => Add($"journal_media_reason{{reason={reason}}}");

    /// <summary>One upload attempt and how it ended. <paramref name="result"/> is a fixed code.</summary>
    public void Upload(string result) => Add($"journal_upload{{result={result}}}");
    public void EndpointMissing() => Add("journal_endpoint_missing");

    // Tool-send receipts (#394, #439), one per receipt: what the agent decided about it.
    public void ToolSendCaptured() => Add("tool_send_captured");

    /// <summary>A captured receipt attributed to a relay (workflow) turn; a subset of <c>tool_send_captured</c>.</summary>
    public void ToolSendCapturedRelay() => Add("tool_send_captured_relay");

    /// <summary>A captured receipt whose window was partly interval-free next to a normally ended or open turn.</summary>
    public void ToolSendIdleEdge() => Add("tool_send_idle_edge");
    public void ToolSendUnattributed() => Add("tool_send_unattributed");
    public void ToolSendExcludedOrigin() => Add("tool_send_excluded_origin");
    public void ToolSendForeignBot() => Add("tool_send_foreign_bot");
    public void ReceiptClockSkew() => Add("receipt_clock_skew");
    public void ReceiptDeferredOverflow() => Add("receipt_deferred_overflow");
    public void ReceiptInvalid() => Add("receipt_invalid");

    /// <summary>Records lost on the agent before they reached the spool: spool full or a write error.</summary>
    public long LocallyDropped => Get("journal_spool_dropped{reason=full}") + Get("journal_capture_failed");

    public long Get(string name) => _values.TryGetValue(name, out var v) ? v : 0;

    public IReadOnlyDictionary<string, long> Snapshot() =>
        new SortedDictionary<string, long>(_values.ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal);

    private void Add(string name) => _values.AddOrUpdate(name, 1, (_, n) => n + 1);
}
