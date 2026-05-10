using System.Text;
using System.Text.Json;
using Fleet.Orchestrator.Configuration;
using Fleet.Shared;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// Publishes config.update messages to running agent queues so AllowedUserIds and
/// AllowedGroupIds changes take effect without a reprovision.
/// Uses the default AMQP exchange (empty string) and the agent's well-known queue name
/// as the routing key, which guarantees delivery regardless of which named exchange
/// the agent declared at startup.
/// </summary>
public sealed class AgentConfigPublisherService : IAsyncDisposable
{
    private readonly RabbitMqOptions _rabbitConfig;
    private readonly ILogger<AgentConfigPublisherService> _logger;

    private IConnection? _connection;
    private IChannel? _channel;
    private bool _initialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    // The agent-direct exchange name (direct, durable) that agents bind their queues to.
    private const string AgentDirectExchange = "fleet.group";

    public AgentConfigPublisherService(
        IOptions<RabbitMqOptions> rabbitConfig,
        ILogger<AgentConfigPublisherService> logger)
    {
        _rabbitConfig = rabbitConfig.Value;
        _logger = logger;
    }

    public bool IsEnabled => !string.IsNullOrEmpty(_rabbitConfig.Host);

    /// <summary>
    /// Publish a config.update diff to the named agent's queue.
    /// No-ops gracefully when RabbitMQ is not configured or the connection fails.
    /// </summary>
    public async Task PublishAllowlistUpdateAsync(
        string agentShortName,
        IReadOnlyList<long> addedUserIds,
        IReadOnlyList<long> removedUserIds,
        IReadOnlyList<long> addedGroupIds,
        IReadOnlyList<long> removedGroupIds,
        CancellationToken ct = default)
    {
        if (!IsEnabled) return;
        if (addedUserIds.Count == 0 && removedUserIds.Count == 0 &&
            addedGroupIds.Count == 0 && removedGroupIds.Count == 0) return;

        try
        {
            await EnsureInitializedAsync(ct);
            if (_channel is null) return;

            var payload = new
            {
                schema_version = 1,
                target_agent   = agentShortName,
                added_user_ids    = addedUserIds,
                removed_user_ids  = removedUserIds,
                added_group_ids   = addedGroupIds,
                removed_group_ids = removedGroupIds,
            };

            // Wrap in the RelayMessage envelope that GroupRelayService.OnRelayMessageReceived
            // expects: {ChatId, Sender, Text, Timestamp, Type}.
            var envelope = new
            {
                ChatId    = 0L,
                Sender    = "orchestrator",
                Text      = JsonSerializer.Serialize(payload),
                Timestamp = DateTimeOffset.UtcNow,
                Type      = "config.update",
            };

            var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope));
            var props = new BasicProperties { DeliveryMode = DeliveryModes.Persistent };
            var routingKey = agentShortName.ToLowerInvariant();

            await _channel.BasicPublishAsync(
                exchange: AgentDirectExchange,
                routingKey: routingKey,
                mandatory: false,
                basicProperties: props,
                body: body,
                cancellationToken: ct);

            _logger.LogInformation(
                "config.update published to agent '{Agent}' (+{Added} / -{Removed} users, +{AddedG} / -{RemovedG} groups)",
                agentShortName, addedUserIds.Count, removedUserIds.Count,
                addedGroupIds.Count, removedGroupIds.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish config.update to agent '{Agent}'", agentShortName);
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;

            var factory = new ConnectionFactory
            {
                HostName = _rabbitConfig.Host,
                ClientProvidedName = "fleet-orchestrator-publisher",
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true,
                RequestedHeartbeat = TimeSpan.FromSeconds(30),
                NetworkRecoveryInterval = TimeSpan.FromSeconds(5),
            };

            _connection = await RabbitMqConnectionHelper.ConnectWithRetryAsync(factory, _logger, ct);
            _channel = await _connection.CreateChannelAsync(cancellationToken: ct);

            // Declare the agent-direct exchange so publish works even if agents haven't started yet.
            await _channel.ExchangeDeclareAsync(
                AgentDirectExchange, ExchangeType.Direct,
                durable: true, autoDelete: false, cancellationToken: ct);

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
            await _channel.CloseAsync();
        if (_connection is not null)
            await _connection.CloseAsync();
    }
}
