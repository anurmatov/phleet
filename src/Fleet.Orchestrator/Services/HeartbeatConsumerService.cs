using System.Text;
using System.Text.Json;
using Fleet.Orchestrator.Configuration;
using Fleet.Orchestrator.Models;
using Fleet.Shared;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// Hosted service that consumes heartbeat and registration messages
/// from the fleet.orchestrator topic exchange on RabbitMQ.
/// </summary>
public sealed class HeartbeatConsumerService : IHostedService, IAsyncDisposable, IRabbitMqStatus, IConfigChangedPublisher
{
    private readonly RabbitMqOptions _rabbitConfig;
    private readonly AgentRegistry _registry;
    private readonly ILogger<HeartbeatConsumerService> _logger;

    private IConnection? _connection;
    private IChannel? _channel;

    /// <inheritdoc />
    public bool IsConnected { get; private set; }

    public HeartbeatConsumerService(
        IOptions<RabbitMqOptions> rabbitConfig,
        AgentRegistry registry,
        ILogger<HeartbeatConsumerService> logger)
    {
        _rabbitConfig = rabbitConfig.Value;
        _registry = registry;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_rabbitConfig.Host))
        {
            _logger.LogWarning("RabbitMQ host not configured — heartbeat consumer disabled");
            return;
        }

        var factory = new ConnectionFactory
        {
            HostName = _rabbitConfig.Host,
            ClientProvidedName = "fleet-orchestrator",
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            RequestedHeartbeat = TimeSpan.FromSeconds(30),
            NetworkRecoveryInterval = TimeSpan.FromSeconds(5),
        };

        _connection = await RabbitMqConnectionHelper.ConnectWithRetryAsync(factory, _logger, ct);
        IsConnected = true;
        _connection.ConnectionShutdownAsync += (_, args) =>
        {
            IsConnected = false;
            _logger.LogWarning("RabbitMQ connection lost (reason: {Reason}) — auto-recovery in progress", args.ReplyText);
            return Task.CompletedTask;
        };
        _connection.RecoverySucceededAsync += (_, _) =>
        {
            IsConnected = true;
            _logger.LogInformation("RabbitMQ connection recovered");
            return Task.CompletedTask;
        };

        _channel = await _connection.CreateChannelAsync(cancellationToken: ct);

        await _channel.ExchangeDeclareAsync(
            _rabbitConfig.Exchange,
            ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            cancellationToken: ct);

        const string queueName = "fleet.orchestrator.heartbeats";
        await _channel.QueueDeclareAsync(
            queue: queueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            cancellationToken: ct);

        // Bind to all routing keys on this exchange (heartbeat.*, registration.*)
        await _channel.QueueBindAsync(queueName, _rabbitConfig.Exchange, routingKey: "#", cancellationToken: ct);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += OnMessageReceived;

        await _channel.BasicConsumeAsync(queueName, autoAck: true, consumer: consumer, cancellationToken: ct);

        _logger.LogInformation("HeartbeatConsumerService started, listening on exchange {Exchange}", _rabbitConfig.Exchange);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await DisposeAsync();
    }

    private Task OnMessageReceived(object sender, BasicDeliverEventArgs ea)
    {
        try
        {
            var json = Encoding.UTF8.GetString(ea.Body.Span);
            var heartbeat = JsonSerializer.Deserialize<AgentHeartbeat>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (heartbeat is null)
            {
                _logger.LogWarning("Received null or unparseable heartbeat message");
                return Task.CompletedTask;
            }

            _logger.LogDebug("Heartbeat from {Agent} via key={RoutingKey}", heartbeat.AgentName, ea.RoutingKey);
            _registry.UpdateAgent(heartbeat);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing heartbeat message (routingKey={RoutingKey})", ea.RoutingKey);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task TryPublishAsync(string? agentName = null, CancellationToken ct = default)
    {
        if (_channel is null)
        {
            _logger.LogWarning("Cannot publish config.changed — RabbitMQ channel not initialized");
            return;
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            @event = "config.changed",
            scope = agentName is null ? "global" : "agent",
            agentName,
        });

        // Retry with backoff: 3 attempts, delays [1s, 3s, 10s].
        // Peers that miss the event will pick up fresh config on their next reconnect.
        var delays = new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10) };
        Exception? lastEx = null;

        for (var attempt = 0; attempt <= delays.Length; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(delays[attempt - 1], ct);

            try
            {
                await _channel.BasicPublishAsync(
                    exchange: _rabbitConfig.Exchange,
                    routingKey: "config.changed",
                    body: payload,
                    cancellationToken: ct);

                _logger.LogDebug("Published config.changed event (agentName={AgentName})", agentName ?? "(global)");
                return;
            }
            catch (Exception ex)
            {
                lastEx = ex;
                if (attempt < delays.Length)
                    _logger.LogWarning(ex, "config.changed publish attempt {Attempt} failed — retrying", attempt + 1);
            }
        }

        _logger.LogError(lastEx,
            "CONFIG_CHANGED_PUBLISH_EXHAUSTED: all {Attempts} publish attempts failed (agentName={AgentName}). " +
            "Peers will pick up fresh config on their next reconnect.",
            delays.Length + 1, agentName ?? "(global)");
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
            await _channel.CloseAsync();
        if (_connection is not null)
            await _connection.CloseAsync();
    }
}
