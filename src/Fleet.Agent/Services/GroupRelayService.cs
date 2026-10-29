using System.Text;
using System.Text.Json;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Shared;
using Fleet.Shared.Security;
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
    private readonly StockBrokerIdentity? _stockBroker;
    private readonly IServiceProvider? _services;
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
    // Managed workflow directives (#492) admitted on the control lane: TaskId -> session nonce for the reply.
    // Bounded (#492 W6): only a successfully published final reply frees its entry (#492 R5) — a failed
    // publication or a partial response keeps it, so a later final reply (for example from the turn a re-sent
    // directive runs) still carries the context; nothing republishes a failed reply by itself. At the bound the oldest unreplied session is dropped, so unreplied (cancelled or timed-out) tasks
    // can never fill the map and refuse every later directive.
    // Cancel (Temporal's per-task cancel) and timeout also free it. A session expires at the later of
    // WorkflowSessionIdleTimeout after its last (re-)admitted directive and the delegating activity's own
    // StartToClose deadline carried in that directive (#492 X4). While Temporal waits it re-sends the directive
    // every five minutes, but a broker or publish outage stops those resends while the activity still accepts
    // the reply, so idleness alone must not drop a live workflow's session: only the activity's end makes the
    // reply unwanted. Without a deadline (an older sender) the idle bound applies. The deadline is capped at
    // MaxWorkflowSessionLifetime after admission, and the count bound above still holds.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Session, long Order, DateTimeOffset Expires)> _workflowSessions = new(StringComparer.Ordinal);
    private long _workflowSessionOrder;
    internal const int MaxWorkflowSessions = 256;
    internal static readonly TimeSpan WorkflowSessionIdleTimeout = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan MaxWorkflowSessionLifetime = TimeSpan.FromDays(7);
    internal int WorkflowSessionCount => _workflowSessions.Count;
    /// <summary>Stands in for the wall clock in tests.</summary>
    internal Func<DateTimeOffset> WorkflowClock { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>Named refusal for a workflow signal from a managed agent: the managed lane carries none (#492 W6).</summary>
    public const string ManagedWorkflowSignalUnsupported = "workflow_signal_unsupported:managed_lane";
    private const string WorkflowDirectiveType = "workflow-directive";
    private const string TemporalBridgeRoute = "temporal-bridge";
    public IReadOnlyDictionary<string, DateTimeOffset> LastPublishTime => _lastPublishTime;

    /// <summary>
    /// Fired when a message from another agent is received via the relay.
    /// Parameters: chatId, sender, text, type, correlationId, taskId, workflowId, signalName.
    /// </summary>
    public event Action<long, string, string, string, string?, string?, string?, string?>? MessageReceived;

    public GroupRelayService(
        IOptions<AgentOptions> agentConfig,
        IOptions<RabbitMqOptions> rabbitConfig,
        ILogger<GroupRelayService> logger, StockBrokerIdentity? stockBroker = null, IServiceProvider? services = null)
    {
        _stockBroker = stockBroker;
        _services = services;
        _agentConfig = agentConfig.Value;
        _rabbitConfig = rabbitConfig.Value;
        _logger = logger;
        Connect = (factory, ct) => _stockBroker is null
            ? RabbitMqConnectionHelper.ConnectWithRetryAsync(factory, _logger, ct) : factory.CreateConnectionAsync(ct);
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

    public async Task InitializeAsync(CancellationToken ct)
    {
        if (_stockBroker is not null && !IsEnabled) throw new StockIdentityDenied("broker_target");
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

            _stockBroker?.Configure(factory);
            if (_stockBroker is not null && _stockBroker.Agent != _agentConfig.ShortName)
                throw new StockIdentityDenied("broker_agent_mismatch");
            _connection = await Connect(factory, ct);
            _connection.ConnectionShutdownAsync += (_, args) =>
            {
                if (_stockBroker is null)
                    _logger.LogWarning("RabbitMQ connection lost (reason: {Reason}) — auto-recovery in progress", args.ReplyText);
                else _logger.LogWarning("Stock broker connection lost; manual reconciliation required");
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

            if (_stockBroker is not null)
            {
                // A fresh epoch vhost has no legacy broadcast binding to remove.
                // Only the single verified control queue is consumed in this mode.
            }
            else if (!BindsTokenBroadcasts(_agentConfig))
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
                await _consumeChannel.ExchangeDeclareAsync(
                    "fleet.relay", ExchangeType.Fanout, durable: true, autoDelete: false,
                    cancellationToken: ct);
                await _consumeChannel.QueueBindAsync(queueName, "fleet.relay", routingKey: "",
                    cancellationToken: ct);
            }

            // Start consuming
            var consumer = new AsyncEventingBasicConsumer(_consumeChannel);
            // Stock (#492): manual ack, so an admitted receipt is acked only after its spool write.
            if (_stockBroker is null) consumer.ReceivedAsync += OnRelayMessageReceived;
            else consumer.ReceivedAsync += OnStockDeliveryAsync;
            await _consumeChannel.BasicConsumeAsync(queueName, autoAck: _stockBroker is null, consumer: consumer,
                cancellationToken: ct);

            _initialized = true;
            _logger.LogInformation(
                "GroupRelayService initialized on exchange {Exchange}, queue {Queue}, routing key {RoutingKey}",
                _rabbitConfig.Exchange, queueName, shortName);
        }
        catch (Exception) when (_stockBroker is not null)
        {
            throw new StockIdentityDenied("broker_startup_denied");
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
                "fleet.relay", ExchangeType.Fanout, durable: true, autoDelete: false,
                cancellationToken: ct);
            await channel.QueueUnbindAsync(queueName, "fleet.relay", routingKey: "",
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
            string? workflowSession = null;
            if (_stockBroker is not null) ExpireIdleWorkflowSessions(); // a timed-out workflow no longer takes the reply
            if (_stockBroker is not null && targetAgent == TemporalBridgeRoute
                && type is RelayMessageType.Response or RelayMessageType.PartialResponse
                && taskId is not null && workflowId is null && signalName is null && _workflowSessions.TryGetValue(taskId, out var admitted))
            {
                // Read, not consumed (#492 R5): the session is freed only once the final reply is published.
                var session = workflowSession = admitted.Session;
                // Typed workflow reply (#492): the existing bridge reply route as the workload principal,
                // carrying agent (sender), sandbox, epoch, session and TaskId for the Bridge and Temporal to check.
                routingKey = "bridge";
                message = message with { CorrelationId = session, Epoch = _stockBroker.Epoch, Sandbox = _stockBroker.Sandbox };
            }
            else if (_stockBroker is not null && (targetAgent != "bridge" || type != RelayMessageType.BridgeResponse
                || !Guid.TryParseExact(correlationId, "N", out _)))
                throw new StockIdentityDenied("broker_route_denied");
            var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));

            var props = _stockBroker?.Properties() ?? new BasicProperties { DeliveryMode = DeliveryModes.Persistent };
            await _publishChannel.BasicPublishAsync(
                _rabbitConfig.Exchange, routingKey: routingKey, mandatory: false, basicProperties: props, body: body);

            if (workflowSession is not null && type == RelayMessageType.Response)
                ForgetWorkflowSession(taskId!, workflowSession);

            _lastPublishTime[targetAgent] = DateTimeOffset.UtcNow;

            _logger.LogInformation("Relay published to {Target} (type={Type}, chat={ChatId}, {Length} chars)",
                targetAgent, type, chatId, text.Length);
        }
        catch (Exception) when (_stockBroker is not null)
        {
            _logger.LogWarning("Stock broker publication denied");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish relay message to {Target}", targetAgent);
        }
    }

    /// <summary>
    /// Managed receipt capture (#492): set by <see cref="ToolSendReceiptConsumer"/> when the journal is
    /// on. Takes the body, the pin check and the ack to run after the spool write.
    /// </summary>
    internal Func<ReadOnlyMemory<byte>, Func<Fleet.Shared.Journal.ToolSendReceipt, bool>, Func<Task>, Task>? ManagedReceiptSink { get; set; }

    /// <summary>Stands in for the broker ack in tests.</summary>
    internal Func<ulong, Task>? StockAckHook { get; set; }

    private async Task OnStockDeliveryAsync(object sender, BasicDeliverEventArgs ea)
    {
        if (ea.BasicProperties.Type == Fleet.Shared.Journal.ToolSendReceipts.ManagedMessageType)
        {
            await AdmitManagedReceiptAsync(ea);
            return;
        }
        await OnRelayMessageReceived(sender, ea);
        await StockAckAsync(ea.DeliveryTag); // handled or refused; a refused delivery is never requeued
    }

    /// <summary>
    /// A typed tool-send receipt on this workload's own queue: admitted only from the epoch control
    /// principal on <c>fleet.group</c> routed to this agent, with a pinned agent/sandbox/epoch that is
    /// this still-current enrollment. Anything else is refused (acked, not requeued, never retargeted).
    /// </summary>
    private async Task AdmitManagedReceiptAsync(BasicDeliverEventArgs ea)
    {
        var acked = false;
        Task Ack() { acked = true; return StockAckAsync(ea.DeliveryTag); }
        try
        {
            if (ea.Body.Length > Fleet.Shared.Journal.ToolSendReceipts.MaxBytes) throw new StockIdentityDenied("broker_size");
            if (ea.Exchange != "fleet.group") throw new StockIdentityDenied("broker_identity_denied");
            _stockBroker!.RequireDelivery(ea.BasicProperties.UserId, "control", ea.Exchange, _rabbitConfig.Exchange,
                ea.RoutingKey, _agentConfig.ShortName);
            // Resolved at first delivery, so a receipt arriving before hosted services finish starting is
            // still captured; with the journal off there is no consumer and the receipt is refused.
            if (ManagedReceiptSink is null && _services?.GetService(typeof(ToolSendReceiptConsumer)) is ToolSendReceiptConsumer receipts)
                ManagedReceiptSink = receipts.HandleManagedAsync;
            if (ManagedReceiptSink is not { } sink) throw new StockIdentityDenied("receipt_capture_off");
            var stock = _stockBroker;
            await sink(ea.Body, r => r.Managed is { } c && stock.Pins(r.Agent, c.Sandbox, c.Epoch), Ack);
            _logger.LogInformation("Verified stock control receipt handled");
        }
        catch (Exception)
        {
            _logger.LogWarning("Stock broker receipt denied");
        }
        if (!acked) await StockAckAsync(ea.DeliveryTag);
    }

    private async Task StockAckAsync(ulong deliveryTag)
    {
        if (StockAckHook is { } hook) { await hook(deliveryTag); return; }
        if (_consumeChannel is not { IsOpen: true } channel) return;
        try { await channel.BasicAckAsync(deliveryTag, multiple: false); }
        catch (Exception) { _logger.LogWarning("Stock broker ack failed"); }
    }

    private Task OnRelayMessageReceived(object sender, BasicDeliverEventArgs ea)
    {
        try
        {
            if (_stockBroker is not null && ea.Body.Length > 262144) throw new StockIdentityDenied("broker_size");
            var json = Encoding.UTF8.GetString(ea.Body.Span);
            var message = JsonSerializer.Deserialize<RelayMessage>(json);

            if (message is null)
                return Task.CompletedTask;

            if (_stockBroker is not null)
            {
                _stockBroker.RequireDelivery(ea.BasicProperties.UserId, "control", ea.Exchange, _rabbitConfig.Exchange,
                    ea.RoutingKey, _agentConfig.ShortName);
                if (message.Type == WorkflowDirectiveType) message = AdmitWorkflowDirective(message);
                else if (message.Type != RelayMessageType.BridgeRequest || !message.Sender.StartsWith("bridge:", StringComparison.Ordinal)
                    || !Guid.TryParseExact(message.CorrelationId, "N", out _) || message.TaskId is not null
                    || message.WorkflowId is not null || message.SignalName is not null || message.Epoch is not null
                    || message.Sandbox is not null)
                    throw new StockIdentityDenied("broker_route_denied");
            }

            // Skip messages from self
            if (message.Sender.Equals(_agentConfig.ShortName, StringComparison.OrdinalIgnoreCase))
                return Task.CompletedTask;

            if (_stockBroker is null)
                _logger.LogInformation("Relay received from {Sender} (type={Type}, chat={ChatId}): {Text}",
                    message.Sender, message.Type, message.ChatId, TruncateForLog(message.Text));
            else _logger.LogInformation("Verified stock control delivery admitted");

            MessageReceived?.Invoke(message.ChatId, message.Sender, message.Text, message.Type, message.CorrelationId, message.TaskId, message.WorkflowId, message.SignalName);
        }
        catch (Exception) when (_stockBroker is not null)
        {
            _logger.LogWarning("Stock broker delivery denied");
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
        if (_stockBroker is not null) return; // Not a verified route in this bounded enrollment.
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
    /// <remarks>
    /// A managed stock agent has no workflow-signal route on its lanes, so this refuses by name with
    /// <see cref="ManagedWorkflowSignalUnsupported"/> instead of being swallowed as a denied publication (#492 W6).
    /// </remarks>
    public Task PublishWorkflowSignalAsync(string workflowId, string signalName, string payload)
    {
        if (_stockBroker is not null)
        {
            _logger.LogError("Workflow signal refused ({Code}): a managed agent has no workflow-signal route", ManagedWorkflowSignalUnsupported);
            throw new InvalidOperationException(
                $"{ManagedWorkflowSignalUnsupported}: a managed stock agent cannot publish workflow signals; nothing was published.");
        }
        return PublishToAgentAsync(
            targetAgent: "temporal-bridge",
            chatId: 0,
            text: payload,
            type: RelayMessageType.WorkflowSignal,
            workflowId: workflowId,
            signalName: signalName);
    }

    /// <summary>
    /// Admits a typed workflow directive (#492) already verified as the epoch control principal on
    /// <c>fleet.group</c> routed to this agent: the pinned sandbox and epoch must match this enrollment, and it
    /// must carry a TaskId and session nonce. It then runs as an ordinary Temporal directive; its reply goes
    /// out as a typed workflow reply on the bridge reply route.
    /// </summary>
    private RelayMessage AdmitWorkflowDirective(RelayMessage message)
    {
        if (message.Sender != TemporalBridgeRoute || message.Epoch != _stockBroker!.Epoch || message.Sandbox != _stockBroker.Sandbox
            || !Guid.TryParseExact(message.CorrelationId, "N", out _) || message.TaskId is not { Length: > 0 and <= 512 }
            || message.WorkflowId is not null || message.SignalName is not null)
            throw new StockIdentityDenied("broker_route_denied");
        ExpireIdleWorkflowSessions();
        var now = WorkflowClock();
        var expires = now + WorkflowSessionIdleTimeout;
        if (message.Deadline is { } deadline)
        {
            var capped = deadline < now + MaxWorkflowSessionLifetime ? deadline : now + MaxWorkflowSessionLifetime;
            if (capped > expires) expires = capped;
        }
        // A resend carries the same session and refreshes it; it never shortens a later expiry already held.
        if (_workflowSessions.TryGetValue(message.TaskId, out var held) && held.Session == message.CorrelationId && held.Expires > expires)
            expires = held.Expires;
        _workflowSessions[message.TaskId] = (message.CorrelationId!, Interlocked.Increment(ref _workflowSessionOrder), expires);
        var excess = _workflowSessions.Count - MaxWorkflowSessions;
        if (excess > 0)
        {
            foreach (var oldest in _workflowSessions.Where(e => e.Key != message.TaskId).OrderBy(e => e.Value.Order).Take(excess).ToList())
                _workflowSessions.TryRemove(oldest);
            _logger.LogWarning("Dropped {Count} oldest unreplied managed workflow sessions (bound {Bound})", excess, MaxWorkflowSessions);
        }
        return message with { Type = RelayMessageType.Directive, CorrelationId = null, Epoch = null, Sandbox = null, Deadline = null };
    }

    /// <summary>
    /// Frees the workflow session for <paramref name="taskId"/> only while it still holds
    /// <paramref name="session"/>, so a concurrent re-admission with a different session is left alone.
    /// </summary>
    private void ForgetWorkflowSession(string taskId, string session)
    {
        while (_workflowSessions.TryGetValue(taskId, out var current) && current.Session == session)
            if (_workflowSessions.TryRemove(new KeyValuePair<string, (string Session, long Order, DateTimeOffset Expires)>(taskId, current))) return;
    }

    /// <summary>
    /// Cancel (#492): frees the workflow session for <paramref name="taskId"/> when Temporal cancels that task,
    /// so the cancelled turn's reply is not published as a workflow reply and the entry does not linger.
    /// </summary>
    public bool ForgetWorkflowSession(string taskId)
    {
        if (!_workflowSessions.TryRemove(taskId, out _)) return false;
        _logger.LogInformation("Freed the managed workflow session of a cancelled task");
        return true;
    }

    /// <summary>
    /// Timeout (#492): drops sessions past their expiry — the later of <see cref="WorkflowSessionIdleTimeout"/>
    /// after the last (re-)admitted directive and the activity deadline it carried (#492 X4); returns how many.
    /// </summary>
    private int ExpireIdleWorkflowSessions()
    {
        if (_workflowSessions.IsEmpty) return 0;
        var now = WorkflowClock();
        var expired = 0;
        foreach (var entry in _workflowSessions)
            if (entry.Value.Expires <= now && _workflowSessions.TryRemove(entry)) expired++;
        if (expired > 0)
            _logger.LogWarning("Dropped {Count} managed workflow sessions past their activity deadline or idle bound {Timeout} (cancelled or timed out)",
                expired, WorkflowSessionIdleTimeout);
        return expired;
    }

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

    private sealed record RelayMessage(long ChatId, string Sender, string Text, DateTimeOffset Timestamp,
        string Type = RelayMessageType.Directive, string? CorrelationId = null, string? TaskId = null,
        string? WorkflowId = null, string? SignalName = null, Guid? Epoch = null, string? Sandbox = null,
        DateTimeOffset? Deadline = null);
}
