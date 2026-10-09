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
/// Consumes what <c>fleet-telegram</c> reports it delivered through a Telegram MCP tool for this
/// agent's own bot, and journals every delivered message (#394, #439). Registered only when
/// <c>Journal__IngestToken</c> is set.
/// </summary>
/// <remarks>
/// <para>
/// The queue is <c>fleet.journal.tool-sends.&lt;name&gt;</c>: durable, bound to the direct exchange
/// with the agent's name, limited by <see cref="ToolSendReceipts.QueueArguments"/>. It is consumed
/// on the agent's one broker connection with manual ack.
/// </para>
/// <para>
/// <b>Delivery is the admission rule.</b> A receipt is captured when it parses as <c>v: 1</c>, names
/// this agent, carries this agent's own bot id and every message id is positive. Turn origin, turn
/// outcome, timing and provider lifecycle are never consulted. The classifier still decides where
/// capture is allowed (excluded chats, the allowlist, chat kind). Each receipt is decided on
/// arrival and acked only after its spool write returns, so a receipt redelivered after a crash is
/// captured again and Comms answers <c>duplicate</c>.
/// </para>
/// <para>
/// ⚠️ Nothing here can fail or slow a Telegram send or a turn: the tool already returned when the
/// receipt was published, and capture never throws. Logs carry fixed codes and counts only — never
/// text, never an id.
/// </para>
/// </remarks>
public sealed class ToolSendReceiptConsumer : BackgroundService
{
    /// <summary>Unacked receipts on the channel. A receipt is at most 1 MiB, so at most 32 MiB in flight.</summary>
    public const int Prefetch = 32;

    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(1);

    private readonly string _agentName;
    private readonly IAgentBrokerConnection _broker;
    private readonly JournalCapture _capture;
    private readonly JournalCounters _counters;
    private readonly Func<long?> _ownBotId;
    private readonly ILogger<ToolSendReceiptConsumer> _logger;
    private readonly TimeProvider _time;

    private IChannel? _channel;
    private string? _consumerTag;

    /// <param name="ownBotId">
    /// This agent's own bot id, or null while it is unknown. The transport derives it from
    /// <c>Telegram:BotToken</c> when it is constructed, before any hosted service starts, so a
    /// receipt queued across a restart meets a known id. A receipt from any other bot — the
    /// notifier fallback bot included — is dropped.
    /// </param>
    public ToolSendReceiptConsumer(
        IOptions<AgentOptions> agent,
        IAgentBrokerConnection broker,
        JournalCapture capture,
        JournalCounters counters,
        Func<long?> ownBotId,
        ILogger<ToolSendReceiptConsumer> logger,
        TimeProvider? time = null)
    {
        _agentName = agent.Value.Name;
        _broker = broker;
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
                return;
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

        await channel.BasicQosAsync(0, Prefetch, global: false, cancellationToken: ct);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, ea) =>
        {
            try
            {
                // The body buffer is only valid during this callback; HandleAsync parses it before
                // its first await.
                await HandleAsync(ea.Body, ea.DeliveryTag, channel);
            }
            catch (Exception e)
            {
                // Unacked: redelivered when the channel closes, then captured again as a duplicate.
                _logger.LogWarning("tool-send receipt handler failed ({Error})", e.GetType().Name);
            }
        };

        _consumerTag = await channel.BasicConsumeAsync(QueueName, autoAck: false, consumer: consumer, cancellationToken: ct);
        _logger.LogInformation("tool-send receipt consumer attached to {Queue}", QueueName);
    }

    // ── one receipt ──────────────────────────────────────────────────────────

    /// <summary>
    /// Decides one delivery, writes what it keeps to the spool, logs one line, then acks. The ack
    /// never precedes the spool write.
    /// </summary>
    internal Task HandleAsync(ReadOnlyMemory<byte> body, ulong deliveryTag, IChannel? channel = null)
    {
        var outcome = Decide(body.Span);

        // Fixed codes and counts only.
        _logger.LogInformation(
            "tool-send receipt handled (result={Result}, reason={Reason}, parts={Parts}, kept={Kept}, spooled={Spooled})",
            outcome.Result, outcome.Reason, outcome.Parts, outcome.Kept, outcome.Spooled);

        return AckAsync(channel, deliveryTag);
    }

    private Outcome Decide(ReadOnlySpan<byte> body)
    {
        if (!ToolSendReceipts.TryParse(body, out var receipt, out var reason))
            return Invalid(reason ?? "malformed", parts: 0);

        var parts = receipt!.Messages.Count;

        // fleet-telegram stamps the trusted endpoint attribution, lower-cased.
        if (!string.Equals(receipt.Agent, ToolSendReceipts.RoutingKey(_agentName), StringComparison.Ordinal))
            return Invalid("agent", parts);

        if (receipt.Messages.Any(m => m.MessageId <= 0))
            return Invalid("message_id", parts);

        // Only this agent's own bot writes to its conversations. A fallback-bot send, or anything
        // another broker client published here, never becomes a row.
        if (_ownBotId() is not { } own || receipt.BotId != own)
        {
            _counters.ToolSendForeignBot();
            return new Outcome("foreign_bot", "-", parts, 0, 0);
        }

        var (kept, spooled) = Capture(receipt);
        _counters.ToolSendCaptured();
        return new Outcome("captured", "-", parts, kept, spooled);
    }

    private Outcome Invalid(string reason, int parts)
    {
        _counters.ReceiptInvalid();
        return new Outcome("invalid", reason, parts, 0, 0);
    }

    /// <summary>
    /// The existing capture path with <c>origin: agent_tool</c>: rules 1, 3 and 4 and the allowlist
    /// apply unchanged, and a multi-message send shares one <c>sendGroup</c>. Never throws.
    /// </summary>
    private (int Kept, int Spooled) Capture(ToolSendReceipt receipt)
    {
        JournalCapture.OutboundBatch? batch = null;
        try
        {
            batch = _capture.Outbound(OutboundOrigin.Human, JournalRecordOrigin.AgentTool);
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

        return (batch?.Kept ?? 0, batch?.Spooled ?? 0);
    }

    private readonly record struct Outcome(string Result, string Reason, int Parts, int Kept, int Spooled);

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
            // The channel went away: the receipt is redelivered and captured again as a duplicate.
            _logger.LogWarning("tool-send receipt ack failed ({Error})", e.GetType().Name);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // A receipt not yet acked is redelivered to the next start and captured then.
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
