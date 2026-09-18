using System.Text;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Fleet.Conversations;

/// <summary>
/// The broker topology this service owns, as the design's topology table fixes it.
/// </summary>
public static class ConversationBroker
{
    /// <summary>Commands to the agent. Direct; the routing key is the agent name.</summary>
    public const string CommandExchange = "fleet.conversations";

    /// <summary>Committed events. Direct; the routing key is the conversation id.</summary>
    public const string EventExchange = "fleet.conversations.events";

    /// <summary>The agent's durable inbound queue. One agent per deployment in this slice.</summary>
    public static string CommandQueue(string agentName) =>
        $"fleet.conversations.inbound.{agentName}";
}

/// <summary>
/// Publishes an outbox batch to RabbitMQ and reports back <b>only what the broker confirmed</b>.
/// </summary>
/// <remarks>
/// <para>
/// Publisher confirms are the entire point of this class. <c>OutboxPublisher</c> writes
/// <c>published_at</c> for the ids this returns and retries everything else, so returning an id the
/// broker never confirmed loses the message silently — the row then says it was delivered and there
/// is nothing left to retry.
/// </para>
/// <para>
/// Messages are published <b>persistent</b> to a <b>durable</b> exchange, and the command queue is
/// declared and bound <b>by this service</b> rather than by the consumer. A command published before
/// the agent has ever started must still be there when it first attaches; leaving the declaration to
/// the consumer means the exchange drops it on the floor and nothing anywhere records that it
/// happened.
/// </para>
/// </remarks>
public sealed class RabbitMqOutboxTransport : IOutboxTransport, IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly string _exchangeType;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IConnection? _connection;
    private IChannel? _channel;

    public RabbitMqOutboxTransport(
        string connectionString, string exchange, ILogger logger, string exchangeType = ExchangeType.Direct)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A broker connection string is required.", nameof(connectionString));

        _connectionString = connectionString;
        Exchange = exchange;
        _exchangeType = exchangeType;
        _logger = logger;
    }

    public string Exchange { get; }

    /// <summary>
    /// Declares the exchanges, and the agent's queue bound to the command exchange.
    /// </summary>
    /// <remarks>
    /// Idempotent, and deliberately done by the producer. AC21b is exactly this: a submission
    /// published to a queue no consumer has ever attached to is still there when one first does.
    /// </remarks>
    public async Task DeclareTopologyAsync(string agentName, CancellationToken ct)
    {
        var channel = await ChannelAsync(ct);

        await channel.ExchangeDeclareAsync(
            ConversationBroker.CommandExchange, ExchangeType.Direct,
            durable: true, autoDelete: false, cancellationToken: ct);

        await channel.ExchangeDeclareAsync(
            ConversationBroker.EventExchange, ExchangeType.Direct,
            durable: true, autoDelete: false, cancellationToken: ct);

        var queue = ConversationBroker.CommandQueue(agentName);

        // Quorum, because a command is a durable client submission: a classic queue on a restarted
        // node can lose one, and the submission row would then say it was dispatched forever.
        await channel.QueueDeclareAsync(
            queue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum" },
            cancellationToken: ct);

        await channel.QueueBindAsync(
            queue, ConversationBroker.CommandExchange, routingKey: agentName, cancellationToken: ct);
    }

    public async Task<IReadOnlySet<ulong>> PublishAsync(
        IReadOnlyList<OutboxMessage> batch, CancellationToken ct)
    {
        var channel = await ChannelAsync(ct);
        var confirmed = new HashSet<ulong>();

        foreach (var message in batch)
        {
            var properties = new BasicProperties
            {
                DeliveryMode = DeliveryModes.Persistent,

                // The consumer's deduplication key: `eventId` for an event, the message id for a
                // command. It is what makes a republished row a no-op rather than a second turn.
                MessageId = message.DedupeId,
                ContentType = "application/json",
            };

            try
            {
                // With publisher confirmations enabled on the channel, this call does not return
                // until the broker has confirmed — and throws if it nacks. That is what makes the
                // set below a record of confirmations rather than of attempts.
                await channel.BasicPublishAsync(
                    Exchange, message.RoutingKey, mandatory: false,
                    basicProperties: properties,
                    body: Encoding.UTF8.GetBytes(message.PayloadJson ?? "{}"),
                    cancellationToken: ct);

                confirmed.Add(message.Id);
            }
            catch (Exception e)
            {
                // Type only — a broker error message can carry a host and a virtual host. The row
                // stays pending and is republished; consumers deduplicate on the message id.
                _logger.LogWarning(
                    "outbox publish to {Exchange} was not confirmed for one row: {Error}",
                    Exchange, e.GetType().Name);

                // The channel may be poisoned after a nack, so the rest of the batch is left for the
                // next drain rather than published down a connection that may be gone.
                await ResetAsync();
                break;
            }
        }

        return confirmed;
    }

    private async Task<IChannel> ChannelAsync(CancellationToken ct)
    {
        if (_channel is { IsOpen: true }) return _channel;

        await _gate.WaitAsync(ct);
        try
        {
            if (_channel is { IsOpen: true }) return _channel;

            var factory = new ConnectionFactory
            {
                Uri = new Uri(_connectionString),
                AutomaticRecoveryEnabled = true,
            };

            _connection = await factory.CreateConnectionAsync(ct);

            // Confirmations AND tracking. Without tracking, BasicPublishAsync returns as soon as the
            // frame is written and every id would be reported confirmed — which is the one failure
            // this interface cannot detect.
            _channel = await _connection.CreateChannelAsync(
                new CreateChannelOptions(
                    publisherConfirmationsEnabled: true,
                    publisherConfirmationTrackingEnabled: true),
                ct);

            return _channel;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ResetAsync()
    {
        var channel = _channel;
        _channel = null;

        if (channel is not null)
        {
            try { await channel.DisposeAsync(); }
            catch (Exception) { /* already gone */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ResetAsync();

        if (_connection is not null)
        {
            try { await _connection.DisposeAsync(); }
            catch (Exception) { /* already gone */ }
        }

        _gate.Dispose();
    }
}
