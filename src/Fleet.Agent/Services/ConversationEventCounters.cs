using System.Collections.Concurrent;

namespace Fleet.Agent.Services;

/// <summary>
/// Observability for the conversation event seam (D20).
///
/// The important distinction here is <c>not_routed</c> versus <c>dropped</c>. Every Telegram and
/// relay turn publishes events that no adapter owns — that is the expected steady state, not a
/// fault. Counting it as a drop would make the drop counter the dominant metric in production and
/// indistinguishable from real misrouting, which trains everyone to ignore the signal. Only
/// oversize, serialize failure, queue-full, shutdown and unknown-conversation are drops.
/// </summary>
public sealed class ConversationEventCounters
{
    // conversation_events_dropped_total{reason}
    public const string ReasonProgressQueueFull = "progress_queue_full";
    public const string ReasonTerminalOutboxOverflow = "terminal_outbox_overflow";
    public const string ReasonOversize = "oversize";
    public const string ReasonSerializeFailed = "serialize_failed";
    public const string ReasonUnknownConversation = "unknown_conversation";
    public const string ReasonShutdown = "shutdown";

    // channel_adapter_delivery_failures_total{channelId,reason}
    public const string FailureThrew = "threw";
    public const string FailureTimeout = "timeout";

    private readonly ConcurrentDictionary<(string channelId, string kind), long> _published = new();
    private readonly ConcurrentDictionary<string, long> _notRouted = new();
    private readonly ConcurrentDictionary<string, long> _dropped = new();
    private readonly ConcurrentDictionary<(string channelId, string reason), long> _deliveryFailures = new();
    private readonly ConcurrentDictionary<string, long> _submissions = new();
    private readonly ConcurrentDictionary<string, long> _outcomeUnknown = new();

    public void Published(string channelId, string kind) => Bump(_published, (channelId, kind));
    public void NotRouted(string channelId) => Bump(_notRouted, channelId);
    public void Dropped(string reason) => Bump(_dropped, reason);
    public void DeliveryFailure(string channelId, string reason) => Bump(_deliveryFailures, (channelId, reason));
    public void Submission(string disposition) => Bump(_submissions, disposition);
    public void OutcomeUnknown(string reason) => Bump(_outcomeUnknown, reason);

    public long PublishedCount(string channelId, string kind) => Read(_published, (channelId, kind));
    public long NotRoutedCount(string channelId) => Read(_notRouted, channelId);
    public long DroppedCount(string reason) => Read(_dropped, reason);
    public long TotalDropped() => _dropped.Values.Sum();
    public long DeliveryFailureCount(string channelId, string reason) => Read(_deliveryFailures, (channelId, reason));
    public long SubmissionCount(string disposition) => Read(_submissions, disposition);
    public long OutcomeUnknownCount(string reason) => Read(_outcomeUnknown, reason);

    private static void Bump<TKey>(ConcurrentDictionary<TKey, long> map, TKey key) where TKey : notnull =>
        map.AddOrUpdate(key, 1L, (_, c) => c + 1L);

    private static long Read<TKey>(ConcurrentDictionary<TKey, long> map, TKey key) where TKey : notnull =>
        map.TryGetValue(key, out var value) ? value : 0L;
}
