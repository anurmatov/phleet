using Fleet.Agent.Abstractions;
using Fleet.Protocol;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fleet.Agent.Services;

/// <summary>
/// The single reader that moves published events to adapters (D11).
///
/// Running delivery here rather than at the publish site is the whole point of the seam: the
/// executor and the Telegram send path hand an event to a bounded queue and return, and every
/// adapter call happens on this loop under a bounded timeout. A hung adapter is abandoned and
/// counted; it can never reach a turn.
/// </summary>
public sealed class ConversationEventPump : BackgroundService
{
    /// <summary>Per-delivery timeout. A hung adapter is abandoned here, not waited on.</summary>
    public static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Total budget for the shutdown drain.</summary>
    public static readonly TimeSpan ShutdownDrainBudget = TimeSpan.FromSeconds(5);

    private readonly ConversationEventBus _bus;
    private readonly IConversationRegistry _registry;
    private readonly ConversationEventCounters _counters;
    private readonly ILogger<ConversationEventPump> _logger;
    private readonly Dictionary<string, IChannelAdapter> _adapters;

    public ConversationEventPump(
        ConversationEventBus bus,
        IConversationRegistry registry,
        ConversationEventCounters counters,
        IEnumerable<IChannelAdapter> adapters,
        ILogger<ConversationEventPump> logger)
    {
        _bus = bus;
        _registry = registry;
        _counters = counters;
        _logger = logger;
        _adapters = BuildAdapterMap(adapters);
    }

    /// <summary>
    /// Two adapters registered under one <c>ChannelId</c> is a STARTUP failure, not a runtime
    /// warning (D15). A warning would leave the process running with nondeterministic routing —
    /// which is the cross-route leak the registry-lookup design exists to make impossible.
    /// </summary>
    private static Dictionary<string, IChannelAdapter> BuildAdapterMap(IEnumerable<IChannelAdapter> adapters)
    {
        var map = new Dictionary<string, IChannelAdapter>(StringComparer.Ordinal);
        foreach (var adapter in adapters)
        {
            if (string.IsNullOrWhiteSpace(adapter.ChannelId))
                throw new InvalidOperationException($"Channel adapter {adapter.GetType().Name} has an empty ChannelId.");

            if (ChannelIds.IsRuntimeOwned(adapter.ChannelId))
            {
                throw new InvalidOperationException(
                    $"Channel adapter {adapter.GetType().Name} claims reserved ChannelId '{adapter.ChannelId}'. " +
                    "'telegram' and 'relay' are owned by the existing runtime paths.");
            }

            if (!map.TryAdd(adapter.ChannelId, adapter))
            {
                throw new InvalidOperationException(
                    $"Two channel adapters are registered with ChannelId '{adapter.ChannelId}': " +
                    $"{map[adapter.ChannelId].GetType().Name} and {adapter.GetType().Name}.");
            }
        }
        return map;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _bus.Signal.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await DrainOnceAsync(stoppingToken);
        }

        await DrainForShutdownAsync();
    }

    /// <summary>
    /// Terminal outboxes drain FIRST, every iteration. A terminal event is the only thing a
    /// client needs to stop waiting, so it must never sit behind a backlog of typing pings.
    /// </summary>
    internal async Task DrainOnceAsync(CancellationToken ct)
    {
        foreach (var reader in _bus.TerminalReaders.ToList())
        {
            while (reader.TryRead(out var pending))
                await DeliverAsync(pending, ct);
        }

        while (_bus.ProgressReader.TryRead(out var pending))
            await DeliverAsync(pending, ct);
    }

    private async Task DrainForShutdownAsync()
    {
        _bus.CompleteWriters();

        using var budget = new CancellationTokenSource(ShutdownDrainBudget);
        try
        {
            await DrainOnceAsync(budget.Token);
        }
        catch (OperationCanceledException)
        {
            // Fall through to the accounting below — an event we could not drain is counted,
            // never lost silently.
        }

        var undrained = 0;
        foreach (var reader in _bus.TerminalReaders.ToList())
            while (reader.TryRead(out _)) undrained++;
        while (_bus.ProgressReader.TryRead(out _)) undrained++;

        for (var i = 0; i < undrained; i++)
            _counters.Dropped(ConversationEventCounters.ReasonShutdown);

        if (undrained > 0)
            _logger.LogWarning("Conversation event pump stopped with {Count} undrained event(s)", undrained);
    }

    private async Task DeliverAsync(ConversationEventBus.PendingEvent pending, CancellationToken ct)
    {
        var reference = _registry.Lookup(pending.RuntimeKey);
        if (reference is null)
        {
            _counters.Dropped(ConversationEventCounters.ReasonUnknownConversation);
            return;
        }

        if (!_adapters.TryGetValue(reference.ChannelId, out var adapter))
        {
            // The conversation's adapter went away between publish and delivery, or was never
            // registered. Expected for runtime-owned channels, which never reach this method.
            _counters.NotRouted(reference.ChannelId);
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(DeliveryTimeout);

        try
        {
            await adapter.DeliverAsync(pending.Event, timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _counters.DeliveryFailure(reference.ChannelId, ConversationEventCounters.FailureTimeout);
            LogDeliveryFailure(reference.ChannelId, pending.Event, "timeout");
        }
        catch (Exception ex)
        {
            // An adapter can never fault a turn. The turn already completed; this is delivery.
            _counters.DeliveryFailure(reference.ChannelId, ConversationEventCounters.FailureThrew);
            LogDeliveryFailure(reference.ChannelId, pending.Event, ex.GetType().Name);
        }
    }

    /// <summary>
    /// D10 case 1: an adapter that failed is exactly the thing that is not working, so no
    /// <c>turn.outcome_unknown</c> is emitted to it — the failure is observable in metrics and
    /// logs instead. Payload text is never logged (D20).
    /// </summary>
    private void LogDeliveryFailure(string channelId, ConversationEvent evt, string reason)
    {
        _logger.LogWarning(
            "Channel adapter delivery failed: channelId={ChannelId} kind={Kind} conversationId={ConversationId} eventId={EventId} reason={Reason}",
            channelId, evt.Kind, evt.Identity.ConversationId, evt.EventId, reason);
    }
}
