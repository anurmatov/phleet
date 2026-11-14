using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Temporalio.Activities;

namespace Fleet.Temporal.Activities;

/// <summary>
/// Temporal activity that broadcasts a refreshed OAuth token to all agents
/// via the fleet.relay fanout exchange.
/// Supports Claude and Codex providers via the provider field.
/// </summary>
public sealed class BroadcastTokenUpdateActivity
{
    private const string RelayFanoutExchange = RelayBroker.TokenBroadcastExchange;

    private readonly IConnectionFactory _connectionFactory;
    private readonly ILogger<BroadcastTokenUpdateActivity> _logger;

    public BroadcastTokenUpdateActivity(
        IConnectionFactory connectionFactory,
        ILogger<BroadcastTokenUpdateActivity> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    /// <summary>
    /// Broadcast the new token to all agents via fleet.relay fanout exchange.
    /// The provider field lets agents route to the correct credentials file.
    /// </summary>
    [Activity]
    public async Task BroadcastAsync(
        string provider,
        string accessToken,
        string refreshToken,
        long expiresAt,
        string[]? scopes = null,
        string? subscriptionType = null,
        string? rateLimitTier = null,
        string? idToken = null,
        string? accountId = null)
    {
        _logger.LogInformation(
            "Broadcasting {Provider} token update to all agents via fleet.relay fanout",
            provider);

        await using var connection = await _connectionFactory.CreateConnectionAsync(
            ActivityExecutionContext.Current.CancellationToken);
        await using var channel = await connection.CreateChannelAsync(
            cancellationToken: ActivityExecutionContext.Current.CancellationToken);

        var ct = ActivityExecutionContext.Current.CancellationToken;

        // Declare exchange (idempotent — already exists in prod)
        await channel.ExchangeDeclareAsync(
            RelayFanoutExchange, ExchangeType.Fanout,
            durable: true, autoDelete: false,
            cancellationToken: ct);

        var props = new BasicProperties { DeliveryMode = DeliveryModes.Persistent };

        // Build provider-aware token payload
        var tokenJson = JsonSerializer.Serialize(new TokenUpdatePayload(
            provider, accessToken, refreshToken, expiresAt, scopes, subscriptionType, rateLimitTier,
            idToken, accountId));

        // Broadcast to all agents via fleet.relay fanout. Agents filter by provider to decide
        // whether to apply.
        var agentMessage = new RelayMessage(
            ChatId: 0,
            Sender: RelaySender.TemporalBridge,
            Text: tokenJson,
            Timestamp: DateTimeOffset.UtcNow,
            Type: RelayMessageType.TokenUpdate);

        await channel.BasicPublishAsync(
            RelayFanoutExchange, routingKey: "", mandatory: false,
            basicProperties: props, body: RelayWire.Serialize(agentMessage, RelayShape.Envelope),
            cancellationToken: ct);

        _logger.LogInformation(
            "Published {Provider} token update to all agents via {Exchange}", provider, RelayFanoutExchange);
    }
}
