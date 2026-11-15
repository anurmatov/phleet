using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Services;

/// <summary>
/// <c>Usage:Enabled</c>: the orchestrator sets <c>Usage__Enabled=true</c> on an agent only when its own
/// usage integration is enabled and valid. Off, which is the default, no turn is measured and nothing
/// is queued or published.
/// </summary>
public sealed class AgentUsageOptions
{
    public const string Section = "Usage";
    public bool Enabled { get; set; }
}

/// <summary>
/// One started provider turn. Its id and start time are fixed before the provider sees the input; the
/// first <see cref="UsageReporter.Complete"/> freezes it and every later call is ignored.
/// </summary>
public sealed class UsageTurn
{
    private int _completed;

    internal UsageTurn(string id, DateTimeOffset startedAt)
    {
        Id = id;
        StartedAt = startedAt;
    }

    public string Id { get; }
    public DateTimeOffset StartedAt { get; }

    internal bool TryComplete() => Interlocked.Exchange(ref _completed, 1) == 0;
}

/// <summary>A frozen, serialized row waiting for the publisher, and when it was frozen.</summary>
public readonly record struct QueuedUsageRow(byte[] Body, DateTimeOffset FrozenAt);

/// <summary>
/// Turns a provider's terminal snapshot into one frozen <see cref="AgentTurnUsage"/> row and queues it
/// for <see cref="OrchestratorHeartbeatService"/>, which publishes it on its own channel.
/// </summary>
/// <remarks>
/// <para>
/// A cumulative snapshot is compared with the previous one for the same provider, process, session and
/// set of models (not the row's model label), component by component. With no comparable previous
/// snapshot, or when any counter went down, the row is marked <c>delta_reset</c>: the first known totals stand in as this
/// turn's share and a negative difference becomes 0, in the delta columns only. A component the
/// previous snapshot did not have stays null. The baseline moves on every completed turn, whether or
/// not the row is ever delivered.
/// </para>
/// <para>
/// Best effort: the queue holds <see cref="QueueCapacity"/> rows and a full queue or an oversized row
/// is dropped and counted. Nothing here may throw into an executor: a usage fault never changes a
/// reply, a cancellation or a session.
/// </para>
/// </remarks>
public sealed class UsageReporter
{
    public const int QueueCapacity = 128;

    private static readonly JsonSerializerOptions RowJson = new();

    private readonly Channel<QueuedUsageRow> _queue = Channel.CreateBounded<QueuedUsageRow>(
        new BoundedChannelOptions(QueueCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    private readonly object _baselineLock = new();
    private readonly ILogger<UsageReporter> _logger;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<string> _newId;
    private readonly string _agent;

    private (string Provider, long Generation, string? Session, string ModelSet)? _baselineKey;
    private ProviderUsageSnapshot? _baseline;
    private long _dropped;

    public UsageReporter(
        IOptions<AgentUsageOptions> usage, IOptions<AgentOptions> agent, ILogger<UsageReporter> logger)
        : this(usage.Value.Enabled, agent.Value.ShortName, logger, () => DateTimeOffset.UtcNow, () => Guid.NewGuid().ToString())
    {
    }

    internal UsageReporter(
        bool enabled, string shortName, ILogger<UsageReporter> logger,
        Func<DateTimeOffset> clock, Func<string> newId)
    {
        Enabled = enabled;
        // Exactly the heartbeat's AgentName, so a row and a heartbeat name the same agent.
        _agent = shortName.ToLowerInvariant();
        _logger = logger;
        _clock = clock;
        _newId = newId;
    }

    public bool Enabled { get; }

    /// <summary>What <see cref="OrchestratorHeartbeatService"/> publishes.</summary>
    public ChannelReader<QueuedUsageRow> Rows => _queue.Reader;

    /// <summary>Rows dropped so far: oversized, queue full, or failed to publish.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>A new turn, or null when usage is off. Call before the provider is given the input.</summary>
    public UsageTurn? BeginTurn() => Enabled ? new UsageTurn(_newId(), _clock()) : null;

    /// <summary>
    /// Freezes <paramref name="turn"/> with <paramref name="snapshot"/> and queues the row. A null turn
    /// (usage off) or one already completed does nothing.
    /// </summary>
    public void Complete(UsageTurn? turn, ProviderUsageSnapshot snapshot)
    {
        if (turn is null || !turn.TryComplete())
            return;

        try
        {
            var endedAt = _clock();
            var row = BuildRow(turn, snapshot, endedAt < turn.StartedAt ? turn.StartedAt : endedAt);
            var body = JsonSerializer.SerializeToUtf8Bytes(row, RowJson);
            if (body.Length > AgentTurnUsage.MaxBytes)
            {
                RecordDrop("row_too_large");
                return;
            }

            if (!_queue.Writer.TryWrite(new QueuedUsageRow(body, endedAt)))
                RecordDrop("queue_full");
        }
        catch (Exception ex)
        {
            RecordDrop(ex.GetType().Name);
        }
    }

    /// <summary>Counts and logs one dropped row by reason only: never a value from the row.</summary>
    public void RecordDrop(string reason)
    {
        var dropped = Interlocked.Increment(ref _dropped);
        _logger.LogWarning("usage_row_dropped: {Reason} (dropped so far: {Dropped})", reason, dropped);
    }

    internal AgentTurnUsage BuildRow(UsageTurn turn, ProviderUsageSnapshot s, DateTimeOffset endedAt)
    {
        long? input = s.InputTokens, output = s.OutputTokens, cacheRead = s.CacheReadTokens, cacheCreation = s.CacheCreationTokens;
        decimal? cost = s.CostUsd;
        var reset = false;

        if (s.IsCumulative)
        {
            // The model set, not the label: two different sets are both labelled "multiple".
            var key = (s.Provider, s.ProcessGeneration, s.SessionId, s.ModelSet ?? s.Model);
            ProviderUsageSnapshot? previous;
            lock (_baselineLock)
            {
                previous = _baselineKey == key ? _baseline : null;
                _baselineKey = key;
                _baseline = s;
            }

            if (previous is null)
            {
                reset = true;
            }
            else
            {
                input = Delta(s.InputTokens, previous.InputTokens, ref reset);
                output = Delta(s.OutputTokens, previous.OutputTokens, ref reset);
                cacheRead = Delta(s.CacheReadTokens, previous.CacheReadTokens, ref reset);
                cacheCreation = Delta(s.CacheCreationTokens, previous.CacheCreationTokens, ref reset);
                cost = Delta(s.CostUsd, previous.CostUsd, ref reset);
            }
        }

        return new AgentTurnUsage
        {
            TurnId = turn.Id,
            Agent = _agent,
            Provider = s.Provider,
            Model = s.Model,
            SessionId = s.SessionId,
            InputTokens = input,
            OutputTokens = output,
            CacheReadTokens = cacheRead,
            CacheCreationTokens = cacheCreation,
            CostUsd = cost,
            CumInputTokens = s.IsCumulative ? s.InputTokens : null,
            CumOutputTokens = s.IsCumulative ? s.OutputTokens : null,
            CumCacheReadTokens = s.IsCumulative ? s.CacheReadTokens : null,
            CumCacheCreationTokens = s.IsCumulative ? s.CacheCreationTokens : null,
            CumCostUsd = s.IsCumulative ? s.CostUsd : null,
            DeltaReset = reset,
            ContextWindow = s.ContextWindow,
            NumTurns = s.NumTurns,
            DurationMs = s.DurationMs,
            TurnStartedAt = Format(turn.StartedAt),
            TurnEndedAt = Format(endedAt),
        };
    }

    private static long? Delta(long? current, long? previous, ref bool reset)
    {
        if (current is not { } c || previous is not { } p) return null;
        if (c >= p) return c - p;
        reset = true;
        return 0;
    }

    private static decimal? Delta(decimal? current, decimal? previous, ref bool reset)
    {
        if (current is not { } c || previous is not { } p) return null;
        if (c >= p) return c - p;
        reset = true;
        return 0;
    }

    private static string Format(DateTimeOffset at) =>
        at.UtcDateTime.ToString(AgentTurnUsage.TimestampFormat, CultureInfo.InvariantCulture);
}
