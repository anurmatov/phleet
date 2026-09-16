using System.Collections.Concurrent;

namespace Fleet.Agent.Services;

/// <summary>
/// <c>sink_suppressed_total{reason}</c> and <c>startup_telegram_state</c> (#277 §9).
///
/// The distinction this exists to make is "correctly not sent to Telegram" versus "the sink was
/// missing". Before <see cref="NullMessageSink"/> the two were indistinguishable: a first-party
/// conversation legitimately produces no Telegram output, and so does a process whose sink was
/// never wired, and neither left a trace.
///
/// Reason <see cref="ReasonNullSink"/> is emitted by <see cref="NullMessageSink"/>. It is the
/// expected steady state for a Telegram-free host, NOT a fault — a non-zero count in a process
/// that does have a bot token is the interesting reading.
/// </summary>
public sealed class SinkSuppressionCounter
{
    /// <summary>No sink is attached — the holder is serving <see cref="NullMessageSink"/>.</summary>
    public const string ReasonNullSink = "null_sink";

    // startup_telegram_state values.
    public const string TelegramAbsent = "absent";
    public const string TelegramMalformed = "malformed";
    public const string TelegramConfigured = "configured";

    private readonly ConcurrentDictionary<string, long> _suppressed = new();

    /// <summary>
    /// <c>startup_telegram_state</c>. Makes S1–S3 of #277 §4.2 visible in production instead of
    /// inferred from a log line. Null until <c>AgentTransport</c> is constructed; a host that
    /// never registers the transport (S4) leaves it null, which is itself the reading.
    /// </summary>
    public string? StartupTelegramState { get; private set; }

    public void Suppressed(string reason) =>
        _suppressed.AddOrUpdate(reason, 1L, (_, c) => c + 1L);

    public long GetSuppressed(string reason) =>
        _suppressed.TryGetValue(reason, out var v) ? v : 0L;

    public void SetStartupTelegramState(string state) => StartupTelegramState = state;
}
