using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Fleet.Agent.Services;

/// <summary>
/// Publishes directed messages to other agents and consumes messages routed to this agent via RabbitMQ direct exchange.
/// Bridges the Telegram bot-to-bot gap where bots cannot see each other's messages.
/// </summary>
public sealed class GroupRelayService : IAsyncDisposable, IAgentBrokerConnection
{
    private readonly AgentOptions _agentConfig;
    private readonly RabbitMqOptions _rabbitConfig;
    private readonly ILogger<GroupRelayService> _logger;

    private IConnection? _connection;
    private IChannel? _publishChannel;
    private IChannel? _consumeChannel;
    private bool _initialized;

    /// <summary>
    /// The fleet.orchestrator topic exchange (same one used for heartbeats).
    /// Access requests are published here so the orchestrator — which knows FLEET_CTO_AGENT — can forward them.
    /// Agent containers must not know who the CTO is.
    /// </summary>
    private const string OrchestratorExchange = "fleet.orchestrator";

    private readonly Dictionary<string, DateTimeOffset> _lastPublishTime = new();
    public IReadOnlyDictionary<string, DateTimeOffset> LastPublishTime => _lastPublishTime;

    /// <summary>
    /// Fired when a message from another agent is received via the relay.
    /// Parameters: chatId, sender, text, type, correlationId, taskId, workflowId, signalName, repo.
    /// <c>repo</c> is the structured <c>RelayMessage.Repo</c> (#347 D9), passed through unvalidated —
    /// the project-context router validates it at intake.
    /// </summary>
    public event Action<long, string, string, string, string?, string?, string?, string?, string?>? MessageReceived;

    public GroupRelayService(
        IOptions<AgentOptions> agentConfig,
        IOptions<RabbitMqOptions> rabbitConfig,
        ILogger<GroupRelayService> logger)
    {
        _agentConfig = agentConfig.Value;
        _rabbitConfig = rabbitConfig.Value;
        _logger = logger;
    }

    public bool IsEnabled => _rabbitConfig.Host.Length > 0;

    /// <summary>
    /// The agent's broker connection, for anything that needs a channel on it (<see
    /// cref="IAgentBrokerConnection"/>).
    /// </summary>
    /// <remarks>
    /// Exposed rather than duplicated. This class is where the agent's one connection to this broker
    /// is made, so a second consumer of the same broker takes a channel here instead of carrying its
    /// own connection string. Nothing outside closes it: its lifetime is this service's.
    /// </remarks>
    public IConnection? Connection => _connection;

    /// <summary>True once a consumer has been started. Used by the wiring-order tests.</summary>
    internal bool IsInitializedForTesting => _initialized;

    public async Task InitializeAsync(CancellationToken ct)
    {
        if (!IsEnabled || _initialized)
            return;

        try
        {
            var factory = new ConnectionFactory
            {
                HostName = _rabbitConfig.Host,
                ClientProvidedName = _agentConfig.Name,
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true,
                RequestedHeartbeat = TimeSpan.FromSeconds(30),
                NetworkRecoveryInterval = TimeSpan.FromSeconds(5),
            };

            _connection = await RabbitMqConnectionHelper.ConnectWithRetryAsync(factory, _logger, ct);
            _connection.ConnectionShutdownAsync += (_, args) =>
            {
                _logger.LogWarning("RabbitMQ connection lost (reason: {Reason}) — auto-recovery in progress", args.ReplyText);
                return Task.CompletedTask;
            };

            _publishChannel = await _connection.CreateChannelAsync(cancellationToken: ct);
            _consumeChannel = await _connection.CreateChannelAsync(cancellationToken: ct);

            // Declare the direct exchange (idempotent, durable to survive broker restarts)
            await _publishChannel.ExchangeDeclareAsync(
                _rabbitConfig.Exchange, ExchangeType.Direct, durable: true, autoDelete: false,
                cancellationToken: ct);

            // Declare the orchestrator topic exchange so we can publish access-request messages to it.
            // The orchestrator's HeartbeatConsumerService already owns this exchange and resolves FLEET_CTO_AGENT.
            await _publishChannel.ExchangeDeclareAsync(
                OrchestratorExchange, ExchangeType.Topic, durable: true, autoDelete: false,
                cancellationToken: ct);

            // Declare a named queue for this agent, bound with routing key = lowercased short name
            var shortName = _agentConfig.ShortName.ToLowerInvariant();
            var queueName = $"fleet.agent.{shortName}";

            await _consumeChannel.QueueDeclareAsync(
                queue: queueName, durable: true, exclusive: false, autoDelete: false,
                cancellationToken: ct);

            await _consumeChannel.QueueBindAsync(queueName, _rabbitConfig.Exchange, routingKey: shortName,
                cancellationToken: ct);

            if (ClaudeLocalModel.IsEnabled(_agentConfig.Provider, _agentConfig.AnthropicBaseUrl))
            {
                // #340 D3: fleet.relay carries only OAuth token broadcasts, and a local-model agent
                // holds none. Never bound, not even transiently; a binding from an earlier life is
                // removed. Runs after the QueueDeclare above: unbinding an undeclared queue is a 404.
                await UnbindTokenBroadcastsAsync(queueName, ct);
            }
            else
            {
                // Also bind to fleet.relay fanout so agents receive broadcast token updates
                await _consumeChannel.ExchangeDeclareAsync(
                    "fleet.relay", ExchangeType.Fanout, durable: true, autoDelete: false,
                    cancellationToken: ct);
                await _consumeChannel.QueueBindAsync(queueName, "fleet.relay", routingKey: "",
                    cancellationToken: ct);
            }

            // Start consuming
            var consumer = new AsyncEventingBasicConsumer(_consumeChannel);
            consumer.ReceivedAsync += OnRelayMessageReceived;
            await _consumeChannel.BasicConsumeAsync(queueName, autoAck: true, consumer: consumer,
                cancellationToken: ct);

            _initialized = true;
            _logger.LogInformation(
                "GroupRelayService initialized on exchange {Exchange}, queue {Queue}, routing key {RoutingKey}",
                _rabbitConfig.Exchange, queueName, shortName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize GroupRelayService — relay disabled");
        }
    }

    /// <summary>
    /// Removes a durable <c>fleet.relay</c> → <paramref name="queueName"/> binding, idempotently.
    /// </summary>
    /// <remarks>
    /// On its own short-lived channel: a broker error closes the channel it happens on, and on the
    /// consume channel that would stop all relay delivery to this agent. A failure is a warning,
    /// not a startup failure — the token-update handler ignores claude broadcasts in local mode.
    /// </remarks>
    private async Task UnbindTokenBroadcastsAsync(string queueName, CancellationToken ct)
    {
        try
        {
            await using var channel = await _connection!.CreateChannelAsync(cancellationToken: ct);
            // Declared first so the unbind cannot 404 on a broker that has never seen the exchange.
            await channel.ExchangeDeclareAsync(
                "fleet.relay", ExchangeType.Fanout, durable: true, autoDelete: false,
                cancellationToken: ct);
            await channel.QueueUnbindAsync(queueName, "fleet.relay", routingKey: "",
                cancellationToken: ct);
            await channel.CloseAsync(ct);

            _logger.LogInformation(
                "Claude local model mode: {Queue} is not bound to fleet.relay (token broadcasts)", queueName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Claude local model mode: could not remove a fleet.relay binding from {Queue}; "
                + "claude token broadcasts are still ignored by the handler", queueName);
        }
    }

    /// <summary>
    /// Publish a directed message to a specific agent's queue.
    /// </summary>
    public async Task PublishToAgentAsync(string targetAgent, long chatId, string text,
        string type = RelayMessageType.Directive, string? correlationId = null, string? taskId = null,
        string? workflowId = null, string? signalName = null)
    {
        if (_publishChannel is null || !_initialized)
            return;

        try
        {
            var routingKey = targetAgent.ToLowerInvariant();
            var message = new RelayMessage(chatId, _agentConfig.ShortName, text, DateTimeOffset.UtcNow, type, correlationId, taskId, workflowId, signalName);
            var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));

            var props = new BasicProperties { DeliveryMode = DeliveryModes.Persistent };
            await _publishChannel.BasicPublishAsync(
                _rabbitConfig.Exchange, routingKey: routingKey, mandatory: false, basicProperties: props, body: body);

            _lastPublishTime[targetAgent] = DateTimeOffset.UtcNow;

            _logger.LogInformation("Relay published to {Target} (type={Type}, chat={ChatId}, {Length} chars)",
                targetAgent, type, chatId, text.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish relay message to {Target}", targetAgent);
        }
    }

    /// <summary>Test seam: one broker delivery through the real deserialize-and-dispatch path.</summary>
    internal Task HandleDeliveryForTestsAsync(BasicDeliverEventArgs ea) => OnRelayMessageReceived(this, ea);

    private Task OnRelayMessageReceived(object sender, BasicDeliverEventArgs ea)
    {
        try
        {
            var json = Encoding.UTF8.GetString(ea.Body.Span);
            var message = JsonSerializer.Deserialize<RelayMessage>(json);

            if (message is null)
                return Task.CompletedTask;

            // Skip messages from self
            if (message.Sender.Equals(_agentConfig.ShortName, StringComparison.OrdinalIgnoreCase))
                return Task.CompletedTask;

            _logger.LogInformation("Relay received from {Sender} (type={Type}, chat={ChatId}): {Text}",
                message.Sender, message.Type, message.ChatId, TruncateForLog(message.Text));

            MessageReceived?.Invoke(message.ChatId, message.Sender, message.Text, message.Type, message.CorrelationId, message.TaskId, message.WorkflowId, message.SignalName, message.Repo);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing relay message");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Publish an access-request payload to the fleet.orchestrator topic exchange.
    /// The orchestrator resolves FLEET_CTO_AGENT and forwards a directive to the CTO agent.
    /// Routing key: access-request.{this-agent-shortname}.
    /// </summary>
    public async Task PublishAccessRequestAsync(AccessRequestPayload payload)
    {
        if (_publishChannel is null || !_initialized)
        {
            _logger.LogWarning("PublishAccessRequestAsync called before relay initialized — access request dropped");
            return;
        }

        try
        {
            var routingKey = $"access-request.{_agentConfig.ShortName.ToLowerInvariant()}";
            var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
            var props = new BasicProperties { DeliveryMode = DeliveryModes.Persistent };
            await _publishChannel.BasicPublishAsync(
                OrchestratorExchange, routingKey: routingKey, mandatory: false,
                basicProperties: props, body: body);

            _logger.LogInformation(
                "Access request from user {UserId} published to orchestrator (routing_key={RoutingKey}, request_id={RequestId})",
                payload.UserId, routingKey, payload.RequestId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish access request to orchestrator for user {UserId}", payload.UserId);
        }
    }

    /// <summary>
    /// Publish a workflow-signal message to the temporal-bridge queue so Temporal can deliver it as a signal.
    /// </summary>
    public Task PublishWorkflowSignalAsync(string workflowId, string signalName, string payload)
        => PublishToAgentAsync(
            targetAgent: "temporal-bridge",
            chatId: 0,
            text: payload,
            type: RelayMessageType.WorkflowSignal,
            workflowId: workflowId,
            signalName: signalName);

    private static string TruncateForLog(string text) =>
        text.Length <= 80 ? text : text[..80] + "...";

    public async ValueTask DisposeAsync()
    {
        if (_consumeChannel is not null)
            await _consumeChannel.CloseAsync();
        if (_publishChannel is not null)
            await _publishChannel.CloseAsync();
        if (_connection is not null)
            await _connection.CloseAsync();
    }

    /// <remarks>
    /// <c>Repo</c> (#347 D9) mirrors <c>Fleet.Temporal.Models.RelayMessage.Repo</c>: set from the
    /// workflow delegation, trailing and optional so older payloads deserialize with null. It is
    /// never parsed out of <c>Text</c>. The agent never sets it on what it publishes, and a null is
    /// not written, so outbound relay payloads are byte-identical to before.
    /// </remarks>
    private sealed record RelayMessage(long ChatId, string Sender, string Text, DateTimeOffset Timestamp,
        string Type = RelayMessageType.Directive, string? CorrelationId = null, string? TaskId = null,
        string? WorkflowId = null, string? SignalName = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Repo = null);
}
