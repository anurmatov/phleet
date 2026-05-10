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
    private volatile bool _initialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    // fleet.group is the direct exchange agents bind their individual queues to.
    // Each agent queue has a binding with routing key = agentShortName.ToLowerInvariant().
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
        IReadOnlyList<AddedUserInfo> addedUsers,
        IReadOnlyList<long> removedUserIds,
        IReadOnlyList<long> addedGroupIds,
        IReadOnlyList<long> removedGroupIds,
        CancellationToken ct = default)
    {
        if (!IsEnabled) return;
        if (addedUsers.Count == 0 && removedUserIds.Count == 0 &&
            addedGroupIds.Count == 0 && removedGroupIds.Count == 0) return;

        try
        {
            await EnsureInitializedAsync(ct);
            if (_channel is null) return;

            var payload = new
            {
                schema_version = 1,
                target_agent   = agentShortName,
                added_users       = addedUsers,
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
                agentShortName, addedUsers.Count, removedUserIds.Count,
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

/// <summary>
/// Per-user info carried in a config.update message so the receiving agent can
/// include the username/first_name in the welcome DM without a Telegram lookup.
/// Must match the shape of Fleet.Agent.Models.AddedUserInfo (JSON property names).
/// </summary>
public sealed class AddedUserInfo
{
    [System.Text.Json.Serialization.JsonPropertyName("user_id")]
    public long UserId { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("username")]
    public string? Username { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("first_name")]
    public string? FirstName { get; init; }
}
