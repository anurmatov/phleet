using System.Globalization;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Conversations.Contracts;
using Fleet.Journal.Client;
using Fleet.Shared.Journal;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Fleet.Agent.Services;

/// <summary>
/// Consumes what <c>fleet-telegram</c> reports it sent through a Telegram MCP tool for this agent,
/// and journals the sends the <see cref="TurnOriginLedger"/> proves answered a human (#394).
/// Registered only when <c>Journal__IngestToken</c> is set.
/// </summary>
/// <remarks>
/// <para>
/// The queue is <c>fleet.journal.tool-sends.&lt;name&gt;</c>: durable, bound to the direct exchange
/// with the agent's name, limited by <see cref="ToolSendReceipts.QueueArguments"/>. It is consumed
/// on the agent's one broker connection with manual ack.
/// </para>
/// <para>
/// <b>A receipt is decided only after its window has been observed.</b> It waits, unacked, until
/// the agent clock passes <c>requestedAt + 2 s + 0.25 s</c>, in a list bounded at
/// <see cref="MaxDeferred"/>; the ack follows the decision. A receipt redelivered after a restart
/// meets an empty ledger and is excluded.
/// </para>
/// <para>
/// ⚠️ Nothing here can fail or slow a Telegram send or a turn: the tool already returned when the
/// receipt was published, and capture never throws. Logs carry reason codes only — never text,
/// never a chat id.
/// </para>
/// </remarks>
public sealed class ToolSendReceiptConsumer : BackgroundService
{
    /// <summary>How long after <c>requestedAt</c> a receipt waits before it is decided.</summary>
    public static readonly TimeSpan DecisionDelay = TurnOriginLedger.ClockSkew + TimeSpan.FromMilliseconds(250);

    /// <summary>Receipts waiting for their decision. Also the broker prefetch, so the list is the backpressure.</summary>
    public const int MaxDeferred = 1_000;

    private static readonly TimeSpan DecisionTick = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(1);

    private readonly string _agentName;
    private readonly IAgentBrokerConnection _broker;
    private readonly TurnOriginLedger _ledger;
    private readonly JournalCapture _capture;
    private readonly JournalCounters _counters;
    private readonly Func<long?> _ownBotId;
    private readonly ILogger<ToolSendReceiptConsumer> _logger;
    private readonly TimeProvider _time;
    private readonly List<Deferred> _deferred = [];

    private IChannel? _channel;
    private string? _consumerTag;

    /// <param name="ownBotId">
    /// This agent's own bot id, or null while it is unknown. A receipt from any other bot — the
    /// notifier fallback bot included — is dropped.
    /// </param>
    public ToolSendReceiptConsumer(
        IOptions<AgentOptions> agent,
        IAgentBrokerConnection broker,
        TurnOriginLedger ledger,
        JournalCapture capture,
        JournalCounters counters,
        Func<long?> ownBotId,
        ILogger<ToolSendReceiptConsumer> logger,
        TimeProvider? time = null)
    {
        _agentName = agent.Value.Name;
        _broker = broker;
        _ledger = ledger;
        _capture = capture;
        _counters = counters;
        _ownBotId = ownBotId;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    private string QueueName => ToolSendReceipts.QueueName(_agentName);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_broker is GroupRelayService { IsEnabled: false })
        {
            _logger.LogInformation("tool-send receipts are not consumed: no broker is configured");
            return;
        }

        var delay = InitialBackoff;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await AttachAsync(stoppingToken);
                break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // Type only: a broker error can carry a host name. The queue keeps receipts while
                // the agent retries (24 h, 10,000, drop-head).
                _logger.LogWarning(
                    "tool-send receipt consumer could not attach ({Error}); retrying in {Delay}",
                    e.GetType().Name, delay);

                try { await Task.Delay(delay, _time, stoppingToken); }
                catch (OperationCanceledException) { return; }

                delay = delay * 2 > MaxBackoff ? MaxBackoff : delay * 2;
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(DecisionTick, _time, stoppingToken); }
            catch (OperationCanceledException) { return; }

            try
            {
                await DecideDueAsync();
            }
            catch (Exception e)
            {
                // A fault escaping a BackgroundService stops the host; the journal must never do
                // that. What was not acked is redelivered and excluded.
                _logger.LogWarning("tool-send receipt decision failed ({Error})", e.GetType().Name);
            }
        }
    }

    private async Task AttachAsync(CancellationToken ct)
    {
        // The agent's own connection; null until GroupRelayService has connected.
        var connection = _broker.Connection
            ?? throw new InvalidOperationException("the agent's broker connection is not open yet");

        if (_channel is not null)
        {
            try { await _channel.DisposeAsync(); } catch { /* a failed attach already closed it */ }
            _channel = null;
        }

        var channel = _channel = await connection.CreateChannelAsync(cancellationToken: ct);

        // Declared here as well as by the publisher, so the binding cannot 404 when the agent starts
        // first. Same shape as every other fleet exchange: direct, durable.
        await channel.ExchangeDeclareAsync(
            ToolSendReceipts.Exchange, ExchangeType.Direct, durable: true, autoDelete: false,
            cancellationToken: ct);
        await channel.QueueDeclareAsync(
            QueueName, durable: true, exclusive: false, autoDelete: false,
            arguments: ToolSendReceipts.QueueArguments(), cancellationToken: ct);
        await channel.QueueBindAsync(
            QueueName, ToolSendReceipts.Exchange, ToolSendReceipts.RoutingKey(_agentName),
            cancellationToken: ct);

        // Unacked receipts are exactly the deferred ones, so the prefetch bounds the list.
        await channel.BasicQosAsync(0, MaxDeferred, global: false, cancellationToken: ct);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, ea) =>
        {
            try
            {
                // The body buffer is only valid during this callback; Admit parses it synchronously.
                await ReceiveAsync(ea.Body, ea.DeliveryTag, channel);
            }
            catch (Exception e)
            {
                // Unacked: redelivered when the channel closes, and then excluded by the ledger.
                _logger.LogWarning("tool-send receipt handler failed ({Error})", e.GetType().Name);
            }
        };

        _consumerTag = await channel.BasicConsumeAsync(QueueName, autoAck: false, consumer: consumer, cancellationToken: ct);
        _logger.LogInformation("tool-send receipt consumer attached to {Queue}", QueueName);
    }

    // ── one receipt ──────────────────────────────────────────────────────────

    /// <summary>
    /// A delivery arrived. Dropped at once when it cannot be journaled whatever the ledger says;
    /// otherwise deferred until its window has been observed.
    /// </summary>
    internal Task ReceiveAsync(ReadOnlyMemory<byte> body, ulong deliveryTag, IChannel? channel = null) =>
        Admit(body.Span, deliveryTag, channel) ? Task.CompletedTask : AckAsync(channel, deliveryTag);

    /// <summary>True when deferred; false when dropped and counted, and the caller acks.</summary>
    private bool Admit(ReadOnlySpan<byte> body, ulong deliveryTag, IChannel? channel)
    {
        var arrivedAt = _time.GetUtcNow();

        if (!ToolSendReceipts.TryParse(body, out var receipt, out var reason))
        {
            _counters.ReceiptInvalid();
            _logger.LogDebug("tool-send receipt dropped (receipt_invalid, {Reason})", reason);
            return false;
        }

        // Only this agent's own bot writes to its conversations. A fallback-bot send, or anything
        // another broker client published here, never becomes a row.
        if (_ownBotId() is not { } own || receipt!.BotId != own)
        {
            _counters.ToolSendForeignBot();
            _logger.LogDebug("tool-send receipt dropped (tool_send_foreign_bot)");
            return false;
        }

        // A stamp later than its own arrival: the publisher's clock is ahead, and the window
        // cannot be placed on this agent's clock.
        if (receipt.RequestedAt > arrivedAt)
        {
            _counters.ReceiptClockSkew();
            _logger.LogDebug("tool-send receipt dropped (receipt_clock_skew)");
            return false;
        }

        lock (_deferred)
        {
            if (_deferred.Count >= MaxDeferred)
            {
                _counters.ReceiptDeferredOverflow();
                _logger.LogDebug("tool-send receipt dropped (receipt_deferred_overflow)");
                return false;
            }

            _deferred.Add(new Deferred(receipt, deliveryTag, channel, receipt.RequestedAt + DecisionDelay));
        }

        return true;
    }

    /// <summary>
    /// Decides and acks every receipt whose window has been observed. Runs on the consumer's tick;
    /// tests call it after moving the clock.
    /// </summary>
    internal async Task<int> DecideDueAsync()
    {
        var now = _time.GetUtcNow();
        List<Deferred> due;
        lock (_deferred)
        {
            due = _deferred.Where(d => now >= d.DueAt).OrderBy(d => d.DueAt).ToList();
            _deferred.RemoveAll(d => now >= d.DueAt);
        }

        foreach (var entry in due)
        {
            Decide(entry.Receipt);
            await AckAsync(entry.Channel, entry.DeliveryTag);
        }

        // Eviction and the stuck warning run even when no receipt arrives.
        _ledger.Sweep();
        return due.Count;
    }

    internal int DeferredCountForTests
    {
        get { lock (_deferred) return _deferred.Count; }
    }

    private void Decide(ToolSendReceipt receipt)
    {
        switch (_ledger.Attribute(receipt.RequestedAt))
        {
            case ToolSendAttribution.Human:
                _counters.ToolSendCaptured();
                Capture(receipt);
                break;

            case ToolSendAttribution.NonHuman:
                _counters.ToolSendNonHuman();
                _logger.LogDebug("tool-send receipt excluded (tool_send_non_human)");
                break;

            default:
                _counters.ToolSendUnattributed();
                _logger.LogDebug("tool-send receipt excluded (tool_send_unattributed)");
                break;
        }
    }

    /// <summary>
    /// The existing capture path with <c>origin: agent_tool</c>: rules 1–4 and the allowlist apply
    /// unchanged, and a multi-message send shares one <c>sendGroup</c>. Never throws.
    /// </summary>
    private void Capture(ToolSendReceipt receipt)
    {
        try
        {
            var batch = _capture.Outbound(OutboundOrigin.Human, JournalRecordOrigin.AgentTool);
            foreach (var message in receipt.Messages)
            {
                batch.Add(new JournalMessage
                {
                    BotId = receipt.BotId,
                    ChatId = receipt.Chat.Id,
                    ChatType = receipt.Chat.Type,
                    ChatTitle = receipt.Chat.Title,
                    MessageId = message.MessageId,
                    ReplyToMessageId = message.ReplyToMessageId,
                    Date = message.Date,
                    SenderKind = JournalSenderKind.Agent,
                    SenderId = receipt.BotId.ToString(CultureInfo.InvariantCulture),
                    Text = message.Text,
                    TextFormat = message.TextFormat switch
                    {
                        ToolSendTextFormats.Html => JournalTextFormat.Html,
                        ToolSendTextFormats.Rich => JournalTextFormat.Rich,
                        _ => JournalTextFormat.Plain,
                    },
                });
            }

            batch.Flush();
        }
        catch (Exception e)
        {
            _counters.CaptureFailed();
            _logger.LogWarning("tool-send capture failed ({Error}); the record is lost", e.GetType().Name);
        }
    }

    // ── broker acknowledgement ───────────────────────────────────────────────

    /// <summary>Stands in for the broker ack in tests. Never set outside tests.</summary>
    internal Func<ulong, Task>? AckHook { get; set; }

    private async Task AckAsync(IChannel? channel, ulong deliveryTag)
    {
        if (AckHook is { } hook)
        {
            await hook(deliveryTag);
            return;
        }

        if (channel is null || !channel.IsOpen) return;

        try
        {
            await channel.BasicAckAsync(deliveryTag, multiple: false);
        }
        catch (Exception e)
        {
            // The channel went away: the receipt is redelivered and meets the ledger again.
            _logger.LogWarning("tool-send receipt ack failed ({Error})", e.GetType().Name);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Deferred receipts stay unacked on purpose: the broker redelivers them to the next start,
        // whose empty ledger excludes them.
        var channel = _channel;
        if (channel is { IsOpen: true } && _consumerTag is not null)
        {
            try { await channel.BasicCancelAsync(_consumerTag, noWait: false, cancellationToken); }
            catch (Exception e) { _logger.LogDebug("tool-send receipt consumer cancel failed ({Error})", e.GetType().Name); }
        }

        await base.StopAsync(cancellationToken);

        // The channel only; the connection is the agent's.
        if (channel is not null)
        {
            try { await channel.DisposeAsync(); } catch { /* shutting down */ }
        }
    }

    private sealed record Deferred(ToolSendReceipt Receipt, ulong DeliveryTag, IChannel? Channel, DateTimeOffset DueAt);
}

/// <summary>
/// With the journal off, deletes this agent's receipt queue if an earlier journal-on life left one
/// behind (#394). Declares nothing — no queue, no exchange — and does nothing without a broker.
/// </summary>
internal sealed class ToolSendReceiptQueueCleanup(
    GroupRelayService relay,
    IOptions<AgentOptions> agent,
    ILogger<ToolSendReceiptQueueCleanup> logger) : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private const int Attempts = 12;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!relay.IsEnabled) return;

        var queue = ToolSendReceipts.QueueName(agent.Value.Name);
        for (var attempt = 1; attempt <= Attempts && !stoppingToken.IsCancellationRequested; attempt++)
        {
            if (relay.Connection is { } connection)
            {
                try
                {
                    // A channel of its own: RabbitMQ answers a missing queue with delete-ok, but a
                    // broker that answers 404 closes only this channel.
                    await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
                    var dropped = await channel.QueueDeleteAsync(queue, ifUnused: false, ifEmpty: false, cancellationToken: stoppingToken);
                    if (dropped > 0)
                        logger.LogInformation("journal is off: deleted leftover {Queue} ({Count} receipts)", queue, dropped);
                    await channel.CloseAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                }
                catch (Exception e)
                {
                    logger.LogDebug("journal is off: could not delete {Queue} ({Error})", queue, e.GetType().Name);
                }

                return;
            }

            try { await Task.Delay(RetryDelay, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
