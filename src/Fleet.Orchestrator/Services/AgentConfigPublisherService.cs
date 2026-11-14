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
/// Publishes to the <c>fleet.group</c> direct exchange with routing key = agentShortName,
/// which matches the binding that GroupRelayService declares for each agent queue at startup.
/// </summary>
public sealed class AgentConfigPublisherService : IAsyncDisposable
{
    private readonly RabbitMqOptions _rabbitConfig;
    private readonly ILogger<AgentConfigPublisherService> _logger;

    private IConnection? _connection;
    private IChannel? _channel;
    private volatile bool _initialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    /// <summary>
    /// Sends one message to an agent's routing key on <see cref="AgentDirectExchange"/>. Replaced
    /// in tests; null means the RabbitMQ channel.
    /// </summary>
    internal Func<string, byte[], CancellationToken, Task>? Send { get; init; }

    // fleet.group is the direct exchange agents bind their individual queues to.
    // Each agent queue has a binding with routing key = agentShortName.ToLowerInvariant().
    private const string AgentDirectExchange = RelayBroker.Exchange;

    public AgentConfigPublisherService(
        IOptions<RabbitMqOptions> rabbitConfig,
        ILogger<AgentConfigPublisherService> logger)
    {
        _rabbitConfig = rabbitConfig.Value;
        _logger = logger;
    }

    public bool IsEnabled => Send is not null || !string.IsNullOrEmpty(_rabbitConfig.Host);

    /// <summary>
    /// Publish a config.update diff to the named agent's queue.
    /// </summary>
    /// <returns>
    /// False when the diff could not be handed to RabbitMQ. The change it describes is already
    /// committed, and the running agent has not been told: the caller must say so, not report
    /// success (<see cref="LiveDelivery"/>). True when it was published, when there was nothing to
    /// publish, and when RabbitMQ is not configured at all, where no agent is listening either.
    /// </returns>
    public async Task<bool> PublishAllowlistUpdateAsync(
        string agentShortName,
        IReadOnlyList<AddedUserInfo> addedUsers,
        IReadOnlyList<long> removedUserIds,
        IReadOnlyList<long> addedGroupIds,
        IReadOnlyList<long> removedGroupIds,
        CancellationToken ct = default)
    {
        if (!IsEnabled) return true;
        if (addedUsers.Count == 0 && removedUserIds.Count == 0 &&
            addedGroupIds.Count == 0 && removedGroupIds.Count == 0) return true;

        try
        {
            var payload = new ConfigUpdateMessage
            {
                TargetAgent = agentShortName,
                AddedUsers = addedUsers,
                RemovedUserIds = removedUserIds,
                AddedGroupIds = addedGroupIds,
                RemovedGroupIds = removedGroupIds,
            };

            var envelope = new RelayMessage(
                ChatId: 0,
                Sender: RelaySender.Orchestrator,
                Text: JsonSerializer.Serialize(payload),
                Timestamp: DateTimeOffset.UtcNow,
                Type: RelayMessageType.ConfigUpdate);

            var body = RelayWire.Serialize(envelope, RelayShape.Envelope);
            var routingKey = agentShortName.ToLowerInvariant();

            if (Send is not null)
            {
                await Send(routingKey, body, ct);
            }
            else
            {
                await EnsureInitializedAsync(ct);
                if (_channel is null)
                {
                    _logger.LogError("Failed to publish config.update to agent '{Agent}': no RabbitMQ channel", agentShortName);
                    return false;
                }

                var props = new BasicProperties { DeliveryMode = DeliveryModes.Persistent };
                await _channel.BasicPublishAsync(
                    exchange: AgentDirectExchange,
                    routingKey: routingKey,
                    mandatory: false,
                    basicProperties: props,
                    body: body,
                    cancellationToken: ct);
            }

            _logger.LogInformation(
                "config.update published to agent '{Agent}' (+{Added} / -{Removed} users, +{AddedG} / -{RemovedG} groups)",
                agentShortName, addedUsers.Count, removedUserIds.Count,
                addedGroupIds.Count, removedGroupIds.Count);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish config.update to agent '{Agent}'", agentShortName);
            return false;
        }
    }

    /// <summary>
    /// Publish a relay directive to a specific agent's queue on fleet.group.
    /// Used by the orchestrator to forward access-request notifications to the CTO agent.
    /// </summary>
    public async Task PublishDirectiveToAgentAsync(
        string agentShortName,
        string text,
        string type = RelayMessageType.Directive,
        CancellationToken ct = default)
    {
        if (!IsEnabled) return;

        try
        {
            await EnsureInitializedAsync(ct);
            if (_channel is null) return;

            var envelope = new RelayMessage(
                ChatId: 0,
                Sender: RelaySender.Orchestrator,
                Text: text,
                Timestamp: DateTimeOffset.UtcNow,
                Type: type);

            var body = RelayWire.Serialize(envelope, RelayShape.Envelope);
            var props = new BasicProperties { DeliveryMode = DeliveryModes.Persistent };
            var routingKey = agentShortName.ToLowerInvariant();

            await _channel.BasicPublishAsync(
                exchange: AgentDirectExchange,
                routingKey: routingKey,
                mandatory: false,
                basicProperties: props,
                body: body,
                cancellationToken: ct);

            _logger.LogInformation("Directive published to agent '{Agent}' ({Length} chars)", agentShortName, text.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish directive to agent '{Agent}'", agentShortName);
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
