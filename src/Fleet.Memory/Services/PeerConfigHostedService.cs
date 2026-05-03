using System.Text;
using System.Text.Json;
using Fleet.Shared;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Fleet.Memory.Services;

/// <summary>
/// Subscribes to RabbitMQ <c>config.changed</c> events and triggers an ACL cache refresh
/// on any change. fleet-memory does not need specific key values from the config broadcast —
/// any change may affect project-access rows, so every event triggers a refresh.
///
/// No PEER_CONFIG_KEYS are declared; this service connects directly to the exchange without
/// the key-filtering logic in PeerConfigClient (which only fires when subscribed keys match).
/// </summary>
public sealed class PeerConfigHostedService : IHostedService, IAsyncDisposable
{
    private readonly AclCacheService _aclCache;
    private readonly ILogger<PeerConfigHostedService> _logger;

    private IConnection? _conn;
    private IChannel? _channel;

    public PeerConfigHostedService(AclCacheService aclCache, ILogger<PeerConfigHostedService> logger)
    {
        _aclCache = aclCache;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var rabbitHost = ExtractRabbitHost(
            Environment.GetEnvironmentVariable("RABBITMQ_HOST") is { Length: > 0 } h ? h
            : (Environment.GetEnvironmentVariable("RABBITMQ_URL") ?? "amqp://rabbitmq:5672/"));

        if (string.IsNullOrEmpty(rabbitHost))
        {
            _logger.LogWarning("RabbitMQ not configured — config.changed subscription disabled (ACL relies on 5-minute TTL refresh only)");
            return;
        }

        try
        {
            var exchange = Environment.GetEnvironmentVariable("RABBITMQ_EXCHANGE") ?? "fleet.orchestrator";

            var factory = new ConnectionFactory
            {
                HostName = rabbitHost,
                ClientProvidedName = "fleet-memory-acl-sub",
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true,
                RequestedHeartbeat = TimeSpan.FromSeconds(30),
                NetworkRecoveryInterval = TimeSpan.FromSeconds(5),
            };

            _conn = await RabbitMqConnectionHelper.ConnectWithRetryAsync(factory, _logger, cancellationToken);
            _channel = await _conn.CreateChannelAsync(cancellationToken: cancellationToken);

            await _channel.ExchangeDeclareAsync(exchange, ExchangeType.Topic,
                durable: true, autoDelete: false, cancellationToken: cancellationToken);

            var queueName = $"fleet.memory-acl-sub.{Guid.NewGuid():N}";
            await _channel.QueueDeclareAsync(queueName, durable: false, exclusive: true,
                autoDelete: true, cancellationToken: cancellationToken);

            await _channel.QueueBindAsync(queueName, exchange, routingKey: "config.changed",
                cancellationToken: cancellationToken);

            var consumer = new AsyncEventingBasicConsumer(_channel);
            consumer.ReceivedAsync += OnConfigChanged;
            await _channel.BasicConsumeAsync(queueName, autoAck: true, consumer: consumer,
                cancellationToken: cancellationToken);

            _logger.LogInformation("Subscribed to config.changed on {Exchange} for ACL cache invalidation", exchange);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not subscribe to config.changed — ACL relies on 5-minute TTL refresh only");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private Task OnConfigChanged(object _, BasicDeliverEventArgs ea)
    {
        try
        {
            _logger.LogInformation("config.changed received — triggering ACL cache refresh");
            _aclCache.TriggerRefresh();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error handling config.changed for ACL refresh");
        }
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null) await _channel.DisposeAsync();
        if (_conn is not null) await _conn.DisposeAsync();
    }

    private static string ExtractRabbitHost(string amqpUrlOrHost)
    {
        if (!amqpUrlOrHost.Contains("://")) return amqpUrlOrHost;
        try { return new Uri(amqpUrlOrHost).Host; }
        catch { return "rabbitmq"; }
    }
}
