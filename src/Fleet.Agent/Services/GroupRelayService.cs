using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
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
    private const string OrchestratorExchange = OrchestratorBroker.Exchange;

    // Written from whichever pool thread publishes, read by status reporting.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastPublishTime = new();
    public IReadOnlyDictionary<string, DateTimeOffset> LastPublishTime => _lastPublishTime;

    private readonly Lock _initGate = new();
    private Task? _retryLoop;
    private volatile bool _retrying;

    /// <summary>The relay's state for <c>/health</c> and for log lines.</summary>
    public static class States
    {
        /// <summary>No broker host is configured.</summary>
        public const string Disabled = "disabled";
        /// <summary>The first connection attempt has not finished.</summary>
        public const string Connecting = "connecting";
        /// <summary>The consumer is running. A dropped connection recovers on its own from here.</summary>
        public const string Connected = "connected";
        /// <summary>The first attempt failed; the service keeps trying in the background.</summary>
        public const string Retrying = "retrying";
    }

    /// <summary>
    /// One of <see cref="States"/>. Reported next to the health status, never instead of it: an
    /// agent with no relay still answers its chats, and a container that restarted on every
    /// broker outage would turn one fault into a fleet-wide restart loop.
    /// </summary>
    public string State =>
        !IsEnabled ? States.Disabled
        : _initialized ? States.Connected
        : _retrying ? States.Retrying
        : States.Connecting;

    /// <summary>
    /// Wait before background attempt <c>n</c> (1-based): 5 s doubling to a 60 s cap. A seam so a
    /// test does not wait for it.
    /// </summary>
    internal Func<int, TimeSpan> RetryDelay { get; init; } =
        attempt => TimeSpan.FromSeconds(Math.Min(60, 5 * Math.Pow(2, Math.Min(attempt - 1, 4))));

    /// <summary>
    /// Fired when a message from another agent is received via the relay.
    /// Parameters: chatId, sender, text, type, correlationId, taskId, workflowId, signalName.
    /// </summary>
    public event Action<long, string, string, string, string?, string?, string?, string?>? MessageReceived;

    public GroupRelayService(
        IOptions<AgentOptions> agentConfig,
        IOptions<RabbitMqOptions> rabbitConfig,
        ILogger<GroupRelayService> logger)
    {
        _agentConfig = agentConfig.Value;
        _rabbitConfig = rabbitConfig.Value;
        _logger = logger;
        Connect = (factory, ct) => RabbitMqConnectionHelper.ConnectWithRetryAsync(factory, _logger, ct);
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

    /// <summary>
    /// Opens the broker connection. A seam so a test can hand <see cref="InitializeAsync"/> a
    /// recording connection and assert the bindings it makes; production retries with backoff.
    /// </summary>
    internal Func<ConnectionFactory, CancellationToken, Task<IConnection>> Connect { get; init; }

    /// <summary>
    /// Whether this agent's queue is bound to the <c>fleet.relay</c> token-broadcast fanout. False
    /// for a Claude local-model agent (#340): it holds an
    /// OAuth credential, so a broadcast has nothing to update and must never be applied.
    /// </summary>
    internal static bool BindsTokenBroadcasts(AgentOptions agent) =>
        !ClaudeLocalModel.IsEnabled(agent.Provider, agent.AnthropicBaseUrl);

    private string NoBroadcastMode =>
        "Claude local model mode";

    /// <summary>
    /// Connects and starts consuming. If that fails the service does not give up: it keeps trying
    /// in the background until it is connected or the host stops, and this call returns so the
    /// host can finish starting.
    /// </summary>
    /// <remarks>
    /// It used to log "relay disabled" and stop. The agent then stayed up, answered its health
    /// check, and never received a workflow task or published an answer again until someone
    /// restarted it.
    /// </remarks>
    public async Task InitializeAsync(CancellationToken ct)
    {
        if (!IsEnabled || _initialized)
            return;

        if (await TryInitializeOnceAsync(attempt: 0, ct))
            return;

        lock (_initGate)
        {
            if (_retryLoop is not null) return;
            _retrying = true;
            _retryLoop = Task.Run(() => RetryInitializeAsync(ct), CancellationToken.None);
        }
    }

    private async Task RetryInitializeAsync(CancellationToken ct)
    {
        for (var attempt = 1; !ct.IsCancellationRequested; attempt++)
        {
            try
            {
                await Task.Delay(RetryDelay(attempt), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (await TryInitializeOnceAsync(attempt, ct))
            {
                _retrying = false;
                return;
            }
        }
    }

    /// <summary>One attempt. Leaves nothing half-open behind when it fails.</summary>
    private async Task<bool> TryInitializeOnceAsync(int attempt, CancellationToken ct)
    {
        IChannel? publishChannel = null;
        IChannel? consumeChannel = null;
        try
        {
            // The connection is made once and kept: other consumers take channels on it
            // (IAgentBrokerConnection), and it recovers by itself after a drop.
            if (_connection is null)
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

                var connection = await Connect(factory, ct);
                connection.ConnectionShutdownAsync += (_, args) =>
                {
                    _logger.LogWarning("RabbitMQ connection lost (reason: {Reason}) — auto-recovery in progress", args.ReplyText);
                    return Task.CompletedTask;
                };
                _connection = connection;
            }

            publishChannel = await _connection.CreateChannelAsync(cancellationToken: ct);
            consumeChannel = await _connection.CreateChannelAsync(cancellationToken: ct);

            // Declare the direct exchange (idempotent, durable to survive broker restarts)
            await publishChannel.ExchangeDeclareAsync(
                _rabbitConfig.Exchange, ExchangeType.Direct, durable: true, autoDelete: false,
                cancellationToken: ct);

            // Declare the orchestrator topic exchange so we can publish access-request messages to it.
            // The orchestrator's HeartbeatConsumerService already owns this exchange and resolves FLEET_CTO_AGENT.
            await publishChannel.ExchangeDeclareAsync(
                OrchestratorExchange, ExchangeType.Topic, durable: true, autoDelete: false,
                cancellationToken: ct);

            // Declare a named queue for this agent, bound with routing key = lowercased short name
            var shortName = _agentConfig.ShortName.ToLowerInvariant();
            var queueName = RelayBroker.AgentQueue(shortName);

            await consumeChannel.QueueDeclareAsync(
                queue: queueName, durable: true, exclusive: false, autoDelete: false,
                cancellationToken: ct);

            await consumeChannel.QueueBindAsync(queueName, _rabbitConfig.Exchange, routingKey: shortName,
                cancellationToken: ct);

            if (!BindsTokenBroadcasts(_agentConfig))
            {
                // #340 D3, #386 §4.6: fleet.relay carries only OAuth token broadcasts, and a Claude
                // local-model agent holds none. Never bound, not even transiently; a
                // binding from an earlier life is removed. Runs after the QueueDeclare above:
                // unbinding an undeclared queue is a 404.
                await UnbindTokenBroadcastsAsync(queueName, ct);
            }
            else
            {
                // Also bind to fleet.relay fanout so agents receive broadcast token updates
                await consumeChannel.ExchangeDeclareAsync(
                    RelayBroker.TokenBroadcastExchange, ExchangeType.Fanout, durable: true, autoDelete: false,
                    cancellationToken: ct);
                await consumeChannel.QueueBindAsync(queueName, RelayBroker.TokenBroadcastExchange, routingKey: "",
                    cancellationToken: ct);
            }

            // Start consuming.
            //
            // autoAck stays on, deliberately. Acknowledging by hand after the handler returns would
            // protect nothing that matters: the task is only in this process's memory either way,
            // so a crash loses it whether or not the broker was told. What manual acknowledgement
            // does add is redelivery, and a redelivery whose first copy already ran is a turn that
            // runs twice: an acknowledgement lost to a dropped connection, then a restart before the
            // broker redelivers, cannot be told from a message that was never handled. Acknowledging
            // only when the turn completes is not available either. A turn can outlast the broker's
            // delivery acknowledgement timeout, and a crash near its end would run it again from the
            // start. Durable hand-off of delegated work belongs in the bridge's protocol, not here.
            var consumer = new AsyncEventingBasicConsumer(consumeChannel);
            consumer.ReceivedAsync += OnRelayMessageReceived;
            await consumeChannel.BasicConsumeAsync(queueName, autoAck: true, consumer: consumer,
                cancellationToken: ct);

            _publishChannel = publishChannel;
            _consumeChannel = consumeChannel;
            _initialized = true;
            _logger.LogInformation(
                "GroupRelayService initialized on exchange {Exchange}, queue {Queue}, routing key {RoutingKey}",
                _rabbitConfig.Exchange, queueName, shortName);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await CloseQuietlyAsync(consumeChannel);
            await CloseQuietlyAsync(publishChannel);
            return false;
        }
        catch (Exception ex)
        {
            await CloseQuietlyAsync(consumeChannel);
            await CloseQuietlyAsync(publishChannel);
            if (attempt == 0)
                _logger.LogError(ex,
                    "Failed to initialize GroupRelayService — relay not connected; retrying in the background until it is");
            else
                _logger.LogWarning(ex,
                    "GroupRelayService is still not connected (background attempt {Attempt}); next attempt in {Delay}s",
                    attempt, (int)RetryDelay(attempt + 1).TotalSeconds);
            return false;
        }
    }

    private static async Task CloseQuietlyAsync(IChannel? channel)
    {
        if (channel is null) return;
        try { await channel.DisposeAsync(); }
        catch { /* A channel that failed half-way is already gone; there is nothing to release. */ }
    }

    /// <summary>
    /// Removes a durable <c>fleet.relay</c> → <paramref name="queueName"/> binding, idempotently.
    /// </summary>
    /// <remarks>
    /// On its own short-lived channel: a broker error closes the channel it happens on, and on the
    /// consume channel that would stop all relay delivery to this agent. A failure is a warning,
    /// not a startup failure — the token-update handler ignores claude broadcasts in local mode, and
    /// the local-mode handler rejects token broadcasts.
    /// </remarks>
    private async Task UnbindTokenBroadcastsAsync(string queueName, CancellationToken ct)
    {
        try
        {
            await using var channel = await _connection!.CreateChannelAsync(cancellationToken: ct);
            // Declared first so the unbind cannot 404 on a broker that has never seen the exchange.
            await channel.ExchangeDeclareAsync(
                RelayBroker.TokenBroadcastExchange, ExchangeType.Fanout, durable: true, autoDelete: false,
                cancellationToken: ct);
            await channel.QueueUnbindAsync(queueName, RelayBroker.TokenBroadcastExchange, routingKey: "",
                cancellationToken: ct);
            await channel.CloseAsync(ct);

            _logger.LogInformation(
                "{Mode}: {Queue} is not bound to fleet.relay (token broadcasts)", NoBroadcastMode, queueName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "{Mode}: could not remove a fleet.relay binding from {Queue}; "
                + "token broadcasts are still ignored by the handler", NoBroadcastMode, queueName);
        }
    }

    /// <summary>
    /// Publish a directed message to a specific agent's queue.
    /// </summary>
    public Task PublishToAgentAsync(string targetAgent, long chatId, string text,
        string type = RelayMessageType.Directive, string? correlationId = null, string? taskId = null,
        string? workflowId = null, string? signalName = null) =>
        TryPublishToAgentAsync(targetAgent, chatId, text, type, correlationId, taskId, workflowId, signalName);

    /// <summary>
    /// <see cref="PublishToAgentAsync"/> for a caller that needs to know: true only when the
    /// message was handed to the broker. Never throws.
    /// </summary>
    public async Task<bool> TryPublishToAgentAsync(string targetAgent, long chatId, string text,
        string type = RelayMessageType.Directive, string? correlationId = null, string? taskId = null,
        string? workflowId = null, string? signalName = null)
    {
        if (_publishChannel is null || !_initialized)
        {
            // Identifiers only. It used to return here without a word, so a reply published while
            // the relay was down was lost and nothing said so.
            _logger.LogWarning(
                "Relay message to {Target} not published: relay is {State} (type={Type}, chat={ChatId}, hasTaskId={HasTaskId}, hasCorrelationId={HasCorrelationId})",
                targetAgent, State, type, chatId, taskId is not null, correlationId is not null);
            return false;
        }

        try
        {
            var routingKey = targetAgent.ToLowerInvariant();
            var message = new RelayMessage(chatId, _agentConfig.ShortName, text, DateTimeOffset.UtcNow, type, correlationId, taskId, workflowId, signalName);
            var body = RelayWire.Serialize(message, RelayShape.Agent);

            var props = new BasicProperties { DeliveryMode = DeliveryModes.Persistent };
            await _publishChannel.BasicPublishAsync(
                _rabbitConfig.Exchange, routingKey: routingKey, mandatory: false, basicProperties: props, body: body);

            _lastPublishTime[targetAgent] = DateTimeOffset.UtcNow;

            _logger.LogInformation("Relay published to {Target} (type={Type}, chat={ChatId}, {Length} chars)",
                targetAgent, type, chatId, text.Length);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish relay message to {Target} (type={Type}, chat={ChatId})", targetAgent, type, chatId);
            return false;
        }
    }

    private Task OnRelayMessageReceived(object sender, BasicDeliverEventArgs ea)
    {
        try
        {
            var json = Encoding.UTF8.GetString(ea.Body.Span);
            var message = RelayWire.Deserialize(json);

            if (message is null)
                return Task.CompletedTask;

            // Skip messages from self
            if (message.Sender.Equals(_agentConfig.ShortName, StringComparison.OrdinalIgnoreCase))
                return Task.CompletedTask;

            _logger.LogInformation("Relay received from {Sender} (type={Type}, chat={ChatId}): {Text}",
                message.Sender, message.Type, message.ChatId, TruncateForLog(message.Text));

            MessageReceived?.Invoke(message.ChatId, message.Sender, message.Text, message.Type, message.CorrelationId, message.TaskId, message.WorkflowId, message.SignalName);
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
            var routingKey = OrchestratorBroker.AgentKey(
                OrchestratorBroker.AccessRequest, _agentConfig.ShortName.ToLowerInvariant());
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
            targetAgent: RelayBroker.TemporalBridgeKey,
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
}
