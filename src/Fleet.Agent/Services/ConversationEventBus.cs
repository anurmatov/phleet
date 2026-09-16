using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Fleet.Agent.Abstractions;
using Fleet.Protocol;
using Microsoft.Extensions.Logging;

namespace Fleet.Agent.Services;

/// <summary>
/// The single <see cref="IConversationEventPublisher"/> implementation (D11, D15).
///
/// <para><b>Publication is non-blocking.</b> <see cref="Publish"/> assigns <c>seq</c> and
/// <c>eventId</c>, hands the event to a bounded structure, and returns. It never awaits
/// <c>DeliverAsync</c> and never blocks on a lock held across I/O, so the executor and the
/// Telegram send path can never be stalled by an adapter (Constraint 6).</para>
///
/// <para><b>Two structures, not one.</b> A chatty progress stream must never be able to evict a
/// terminal event, so terminal kinds use a per-conversation outbox and everything else uses a
/// single shared progress channel. This is also what makes the reaper safe: an earlier design had
/// the reaper publish through the same channel whose fullness it was meant to cover, which is
/// circular. There is no shared terminal channel to fill (Constraint 21).</para>
/// </summary>
public sealed class ConversationEventBus : IConversationEventPublisher
{
    /// <summary>Shared progress capacity. Oldest-wins is wrong here; a dropped write is counted.</summary>
    public const int ProgressChannelCapacity = 256;

    /// <summary>
    /// Per-conversation terminal capacity. A conversation runs one turn at a time and a turn
    /// produces one terminal event, so reaching 4 undelivered terminals for a single conversation
    /// is a defect, not load — hence the <c>Error</c> log rather than a warning.
    /// </summary>
    public const int TerminalOutboxCapacity = 4;

    private readonly IConversationRegistry _registry;
    private readonly ConversationEventCounters _counters;
    private readonly ILogger<ConversationEventBus> _logger;
    private readonly TimeProvider _timeProvider;

    private readonly Channel<PendingEvent> _progress = Channel.CreateBounded<PendingEvent>(
        new BoundedChannelOptions(ProgressChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });

    private readonly ConcurrentDictionary<long, Channel<PendingEvent>> _terminalOutboxes = new();
    private readonly ConcurrentDictionary<string, long> _seqByConversation = new(StringComparer.Ordinal);

    /// <summary>Signalled on every accepted publish so the pump can wake without polling.</summary>
    private readonly SemaphoreSlim _signal = new(0);

    internal record struct PendingEvent(long RuntimeKey, ConversationEvent Event);

    public ConversationEventBus(
        IConversationRegistry registry,
        ConversationEventCounters counters,
        ILogger<ConversationEventBus> logger,
        TimeProvider? timeProvider = null)
    {
        _registry = registry;
        _counters = counters;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal SemaphoreSlim Signal => _signal;
    internal ChannelReader<PendingEvent> ProgressReader => _progress.Reader;

    internal IEnumerable<ChannelReader<PendingEvent>> TerminalReaders =>
        _terminalOutboxes.Values.Select(c => c.Reader);

    /// <inheritdoc/>
    public bool Publish<TPayload>(long runtimeConversationKey, string kind, ConversationIdentity identity, TPayload? payload)
        where TPayload : class
    {
        ConversationEvent evt;
        try
        {
            // seq is assigned here, at the publish moment, so it reflects EMISSION order rather
            // than delivery order. A terminal event may overtake still-queued progress events on
            // the way out; seq is what makes that detectable rather than invisible.
            var seq = NextSeq(identity.ConversationId);
            evt = ConversationEvent.Create(
                kind, identity, NewEventId(), seq, _timeProvider.GetUtcNow(), payload);
        }
        catch (Exception ex)
        {
            // Serialization is the only thing that can fail here, and it must not reach the turn.
            _logger.LogWarning(ex, "Failed to serialize conversation event kind={Kind}", kind);
            _counters.Dropped(ConversationEventCounters.ReasonSerializeFailed);
            return false;
        }

        return PublishCore(runtimeConversationKey, evt);
    }

    /// <inheritdoc/>
    public void Publish(ConversationEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        // Best-effort reverse lookup; an unregistered conversation is dropped and counted.
        PublishCore(ResolveKey(evt.Identity), evt);
    }

    private long ResolveKey(ConversationIdentity identity)
    {
        // Telegram and relay conversation ids are the runtime key rendered as a string.
        if (long.TryParse(identity.ConversationId, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        return long.MinValue;
    }

    private bool PublishCore(long runtimeKey, ConversationEvent evt)
    {
        var reference = _registry.Lookup(runtimeKey);
        var channelId = reference?.ChannelId;

        if (channelId is null)
        {
            _counters.Dropped(ConversationEventCounters.ReasonUnknownConversation);
            _logger.LogDebug("Conversation event kind={Kind} dropped: conversation not registered", evt.Kind);
            return false;
        }

        // The 128 KiB hard cap is defence in depth behind D18's per-field truncation. Measuring it
        // requires serializing, which is also where a contract defect would surface.
        int serializedBytes;
        try
        {
            serializedBytes = System.Text.Encoding.UTF8.GetByteCount(
                JsonSerializer.Serialize(evt, FleetProtocolJson.Options));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to measure conversation event kind={Kind}", evt.Kind);
            _counters.Dropped(ConversationEventCounters.ReasonSerializeFailed);
            return false;
        }

        if (serializedBytes > ProtocolLimits.MaxSerializedEventBytes)
        {
            _counters.Dropped(ConversationEventCounters.ReasonOversize);
            _logger.LogWarning(
                "Conversation event kind={Kind} exceeded the {Cap}-byte hard cap ({Actual}) and was dropped",
                evt.Kind, ProtocolLimits.MaxSerializedEventBytes, serializedBytes);

            if (evt.IsTerminal && evt.Kind != ConversationEventKind.TurnOutcomeUnknown)
            {
                // D10 case 3: the submission must still terminate, just indeterminately.
                return Publish(runtimeKey, ConversationEventKind.TurnOutcomeUnknown, evt.Identity,
                    new TurnOutcomeUnknownPayload { Reason = OutcomeUnknownReason.TerminalEventOversize });
            }
            return false;
        }

        _counters.Published(channelId, evt.Kind);

        // Runtime-owned channels have no adapter by design. This is EXPECTED traffic — counted
        // separately from drops so the drop counter stays a real signal (D15, D20).
        if (ChannelIds.IsRuntimeOwned(channelId))
        {
            _counters.NotRouted(channelId);
            return true;
        }

        var accepted = evt.IsTerminal
            ? WriteTerminal(runtimeKey, evt)
            : WriteProgress(runtimeKey, evt);

        if (accepted)
            _signal.Release();

        return accepted;
    }

    private bool WriteProgress(long runtimeKey, ConversationEvent evt)
    {
        if (_progress.Writer.TryWrite(new PendingEvent(runtimeKey, evt)))
            return true;

        _counters.Dropped(ConversationEventCounters.ReasonProgressQueueFull);
        _logger.LogDebug("Progress channel full — dropped conversation event kind={Kind}", evt.Kind);
        return false;
    }

    private bool WriteTerminal(long runtimeKey, ConversationEvent evt)
    {
        var outbox = _terminalOutboxes.GetOrAdd(runtimeKey, _ => Channel.CreateBounded<PendingEvent>(
            new BoundedChannelOptions(TerminalOutboxCapacity)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
            }));

        // TryWrite: never blocking, never waiting. This is exactly what lets the reaper publish
        // unconditionally from a finally block without any risk of hanging a turn's teardown.
        if (outbox.Writer.TryWrite(new PendingEvent(runtimeKey, evt)))
            return true;

        _counters.Dropped(ConversationEventCounters.ReasonTerminalOutboxOverflow);
        _logger.LogError(
            "Terminal outbox overflow for conversation {ConversationId} (kind={Kind}) — this indicates a defect, not load",
            evt.Identity.ConversationId, evt.Kind);
        return false;
    }

    /// <summary>Free a conversation's outbox so the dictionary does not grow without bound (D11).</summary>
    public void ReleaseConversation(long runtimeKey)
    {
        if (_terminalOutboxes.TryGetValue(runtimeKey, out var outbox)
            && outbox.Reader.Count == 0
            && _terminalOutboxes.TryRemove(runtimeKey, out var removed))
        {
            removed.Writer.TryComplete();
        }
    }

    /// <summary>Complete all writers so the pump can drain to exhaustion on shutdown.</summary>
    internal void CompleteWriters()
    {
        _progress.Writer.TryComplete();
        foreach (var outbox in _terminalOutboxes.Values)
            outbox.Writer.TryComplete();
    }

    private long NextSeq(string conversationId) =>
        _seqByConversation.AddOrUpdate(conversationId, 1L, (_, current) => current + 1L);

    private static string NewEventId() => Guid.NewGuid().ToString("N");
}
