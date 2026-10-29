using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Fleet.Bridge.Configuration;
using Fleet.Shared;
using Fleet.Shared.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Fleet.Bridge.Services;

/// <summary>
/// Manages RabbitMQ connection for bridge request-response communication with fleet agents.
/// Publishes BridgeRequest messages and awaits BridgeResponse via correlation-keyed TaskCompletionSources.
/// </summary>
public sealed class BridgeRelayService : IAsyncDisposable
{
    private readonly StockBrokerIdentity? _stockBroker;
    private readonly RabbitMqOptions _rabbitConfig;
    private readonly ILogger<BridgeRelayService> _logger;

    private volatile string _targetAgent = "";
    private long _bridgeChatId;

    /// <summary>Target agent routing key — updatable at runtime via peer config.</summary>
    public string TargetAgent
    {
        get => _targetAgent;
        set => _targetAgent = value;
    }

    /// <summary>Bridge chat ID — updatable at runtime via peer config.</summary>
    public long BridgeChatId
    {
        get => Interlocked.Read(ref _bridgeChatId);
        set => Interlocked.Exchange(ref _bridgeChatId, value);
    }

    private readonly int _timeoutSeconds;

    private IConnection? _connection;
    private IChannel? _publishChannel;
    private IChannel? _consumeChannel;
    private bool _initialized;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pendingRequests = new();

    public BridgeRelayService(
        IOptions<BridgeOptions> bridgeConfig,
        IOptions<RabbitMqOptions> rabbitConfig,
        ILogger<BridgeRelayService> logger, IConfiguration? configuration = null)
        : this(bridgeConfig, rabbitConfig, logger, configuration is null ? null
            : StockBrokerIdentity.Optional(configuration["StockOpenShell:AuthorityFile"],
                configuration["StockOpenShell:BrokerPasswordFile"], "control"))
    {
        var directory = configuration?["StockOpenShell:AuthorityDirectory"];
        if (string.IsNullOrEmpty(directory)) return;
        // Managed lanes keep this ordinary connection for ordinary targets; a process-wide single-file
        // stock identity has no ordinary connection, so the two modes never combine.
        if (_stockBroker is not null || !Path.IsPathFullyQualified(directory)) throw new StockIdentityDenied("broker_configuration");
        Lanes = new BridgeControlLanes(directory, bridgeConfig.Value.TimeoutSeconds, _rabbitConfig.Exchange, logger)
        { ForwardWorkflowReply = ForwardWorkflowReplyAsync };
    }

    /// <summary>One relay bound to an explicit epoch identity: the single-file process or a managed lane.</summary>
    internal BridgeRelayService(IOptions<BridgeOptions> bridgeConfig, IOptions<RabbitMqOptions> rabbitConfig,
        ILogger<BridgeRelayService> logger, StockBrokerIdentity? stockBroker)
    {
        _stockBroker = stockBroker;
        var opts = bridgeConfig.Value;
        TargetAgent = opts.TargetAgent;
        BridgeChatId = opts.BridgeChatId;
        _timeoutSeconds = opts.TimeoutSeconds;
        _rabbitConfig = rabbitConfig.Value;
        _logger = logger;
        Connect = (factory, ct) => _stockBroker is null
            ? RabbitMqConnectionHelper.ConnectWithRetryAsync(factory, _logger, ct) : factory.CreateConnectionAsync(ct);
    }

    internal Func<ConnectionFactory, CancellationToken, Task<IConnection>> Connect { get; init; }

    /// <summary>
    /// Managed lane only (#492): forwards an admitted typed workflow reply to the existing Temporal receiver
    /// route on the ordinary connection. Null on the ordinary and single-file relays.
    /// </summary>
    internal Func<RelayMessage, Task>? ForwardWorkflowReply { get; init; }

    private const string ResponseType = "response";
    private const string PartialResponseType = "partial-response";

    /// <summary>Per-agent managed control lanes; null unless <c>StockOpenShell:AuthorityDirectory</c> is set.</summary>
    internal BridgeControlLanes? Lanes { get; }

    public bool IsEnabled => _rabbitConfig.Host.Length > 0;

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
                ClientProvidedName = "fleet-bridge",
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true,
                RequestedHeartbeat = TimeSpan.FromSeconds(30),
                NetworkRecoveryInterval = TimeSpan.FromSeconds(5),
            };

            _stockBroker?.Configure(factory);
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

            await _publishChannel.ExchangeDeclareAsync(
                _rabbitConfig.Exchange, ExchangeType.Direct, durable: true, autoDelete: false,
                cancellationToken: ct);

            // Declare bridge queue for receiving responses
            const string queueName = "fleet.agent.bridge";
            await _consumeChannel.QueueDeclareAsync(
                queue: queueName, durable: true, exclusive: false, autoDelete: false,
                cancellationToken: ct);
            await _consumeChannel.QueueBindAsync(queueName, _rabbitConfig.Exchange, routingKey: "bridge",
                cancellationToken: ct);

            var consumer = new AsyncEventingBasicConsumer(_consumeChannel);
            consumer.ReceivedAsync += OnMessageReceived;
            await _consumeChannel.BasicConsumeAsync(queueName, autoAck: true, consumer: consumer,
                cancellationToken: ct);

            _initialized = true;
            _logger.LogInformation("BridgeRelayService initialized on exchange {Exchange}", _rabbitConfig.Exchange);
            // Managed lanes are the sole consumers of their epoch reply queues; open the current ones so a
            // workflow reply is read without waiting for a bridge ask to that agent.
            if (Lanes is not null) await Lanes.OpenActiveAsync(ct);
        }
        catch (Exception) when (_stockBroker is not null)
        {
            throw new StockIdentityDenied("broker_startup_denied");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize BridgeRelayService");
        }
    }

    /// <summary>
    /// Send a question to Acto and wait synchronously for the response.
    /// </summary>
    public async Task<string> AskAsync(string question, string agentName, string? project, int? timeoutSeconds, CancellationToken ct)
    {
        if (Lanes is not null)
        {
            // agentName only labels the caller; the destination (and so the lane) is TargetAgent.
            // A managed target never falls back to this ordinary connection.
            BridgeControlLanes.Route route;
            try { route = await Lanes.RouteAsync(TargetAgent, connect: true, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return "Request cancelled."; }
            if (route.Managed)
            {
                if (route.Lane is null) return BridgeControlLanes.Denied;
                route.Lane.BridgeChatId = BridgeChatId;
                return await route.Lane.AskAsync(question, agentName, project, timeoutSeconds, ct);
            }
        }

        if (!_initialized || _publishChannel is null)
            return "Error: Bridge relay service is not connected to RabbitMQ.";

        var correlationId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[correlationId] = tcs;

        try
        {
            var sender = string.IsNullOrEmpty(project)
                ? $"bridge:{agentName}"
                : $"bridge:{agentName}:{project}";

            var message = new RelayMessage(
                ChatId: BridgeChatId,
                Sender: sender,
                Text: question,
                Timestamp: DateTimeOffset.UtcNow,
                Type: "bridge-request",
                CorrelationId: correlationId);

            var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
            var props = _stockBroker?.Properties() ?? new BasicProperties { DeliveryMode = DeliveryModes.Persistent };

            var target = TargetAgent.ToLowerInvariant();
            if (_stockBroker is not null && target != _stockBroker.Agent) throw new StockIdentityDenied("broker_target");
            await _publishChannel.BasicPublishAsync(
                _rabbitConfig.Exchange, routingKey: target, mandatory: false,
                basicProperties: props, body: body, cancellationToken: ct);

            _logger.LogInformation("Bridge request published (correlationId={CorrelationId}, agent={Agent}, target={Target})",
                correlationId, agentName, target);

            var timeout = TimeSpan.FromSeconds(timeoutSeconds ?? _timeoutSeconds);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, timeoutCts.Token));

            if (completedTask == tcs.Task)
                return await tcs.Task;

            if (_stockBroker is not null) return "Error: stock control outcome unconfirmed; manual reconciliation required.";
            return $"Timeout: Acto did not respond within {(int)timeout.TotalSeconds} seconds. He may be busy with another task.";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return "Request cancelled.";
        }
        catch (OperationCanceledException)
        {
            if (_stockBroker is not null) return "Error: stock control outcome unconfirmed; manual reconciliation required.";
            return $"Timeout: Acto did not respond within the configured timeout.";
        }
        catch (Exception) when (_stockBroker is not null)
        {
            return "Error: stock control admission denied.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending bridge request");
            return $"Error: {ex.Message}";
        }
        finally
        {
            _pendingRequests.TryRemove(correlationId, out _);
        }
    }

    /// <summary>
    /// Check if the target agent is idle or busy.
    /// </summary>
    public async Task<string> CheckStatusAsync(CancellationToken ct)
    {
        if (_stockBroker is not null) return "Error: stock status route is not enrolled.";
        if (Lanes is not null && (await Lanes.RouteAsync(TargetAgent, connect: false, ct)).Managed)
            return "Error: stock status route is not enrolled.";
        if (!_initialized || _publishChannel is null)
            return "Error: Bridge relay service is not connected to RabbitMQ.";

        var correlationId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[correlationId] = tcs;

        try
        {
            // Use status-check type — handled by GroupBehavior.OnRelayMessage directly
            var message = new RelayMessage(
                ChatId: BridgeChatId,
                Sender: "bridge",
                Text: "",
                Timestamp: DateTimeOffset.UtcNow,
                Type: "status-check",
                CorrelationId: null);

            var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
            var props = new BasicProperties { DeliveryMode = DeliveryModes.Persistent };

            var target = TargetAgent.ToLowerInvariant();
            await _publishChannel.BasicPublishAsync(
                _rabbitConfig.Exchange, routingKey: target, mandatory: false,
                basicProperties: props, body: body, cancellationToken: ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, timeoutCts.Token));
            if (completedTask == tcs.Task)
                return await tcs.Task;

            return "unknown (timeout — agent may be down)";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking agent status");
            return $"Error: {ex.Message}";
        }
        finally
        {
            _pendingRequests.TryRemove(correlationId, out _);
        }
    }

    private async Task OnMessageReceived(object sender, BasicDeliverEventArgs ea)
    {
        try
        {
            if (_stockBroker is not null)
            {
                _stockBroker.RequireDelivery(ea.BasicProperties.UserId, "workload", ea.Exchange, _rabbitConfig.Exchange,
                    ea.RoutingKey, "bridge");
                if (ea.Body.Length > 262144) throw new StockIdentityDenied("broker_size");
            }
            var json = Encoding.UTF8.GetString(ea.Body.Span);
            var message = JsonSerializer.Deserialize<RelayMessage>(json);
            if (message is null)
                return;
            if (_stockBroker is not null && message.Type is ResponseType or PartialResponseType)
            {
                // Typed workflow reply (#492): validated workload user-id, fleet.group/bridge route, this lane's
                // agent, sandbox and epoch, a session nonce and a TaskId; Temporal checks the live correlation.
                if (ForwardWorkflowReply is null || message.Sender != _stockBroker.Agent || message.Epoch != _stockBroker.Epoch
                    || message.Sandbox != _stockBroker.Sandbox || !Guid.TryParseExact(message.CorrelationId, "N", out _)
                    || message.TaskId is not { Length: > 0 and <= 512 })
                    throw new StockIdentityDenied("broker_route_denied");
                await ForwardWorkflowReply(message);
                _logger.LogInformation("Verified stock workflow reply forwarded");
                return;
            }
            if (_stockBroker is not null && (message.Sender != _stockBroker.Agent || message.Type != "bridge-response"
                || !Guid.TryParseExact(message.CorrelationId, "N", out _)))
                throw new StockIdentityDenied("broker_route_denied");

            _logger.LogInformation("Bridge received message type={Type}, correlationId={CorrelationId}, from={Sender}",
                message.Type, message.CorrelationId, message.Sender);

            if (message.Type == "bridge-response" && message.CorrelationId is not null)
            {
                if (_pendingRequests.TryRemove(message.CorrelationId, out var tcs))
                    tcs.TrySetResult(message.Text);
                else
                    _logger.LogWarning("No pending request for correlationId={CorrelationId}", message.CorrelationId);
            }
            else if (message.Type == "status-response")
            {
                // Status responses don't have correlationId — resolve the first pending status check
                foreach (var kvp in _pendingRequests)
                {
                    if (_pendingRequests.TryRemove(kvp.Key, out var statusTcs))
                    {
                        statusTcs.TrySetResult(message.Text);
                        break;
                    }
                }
            }
        }
        catch (Exception) when (_stockBroker is not null)
        {
            _logger.LogWarning("Stock broker completion denied");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing bridge response");
        }
    }

    /// <summary>Publishes an admitted managed workflow reply to <c>temporal-bridge</c> on the ordinary connection.</summary>
    private async Task ForwardWorkflowReplyAsync(RelayMessage reply)
    {
        if (!_initialized || _publishChannel is null) throw new StockIdentityDenied("broker_forward_unavailable");
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(reply));
        await _publishChannel.BasicPublishAsync(_rabbitConfig.Exchange, routingKey: "temporal-bridge", mandatory: false,
            basicProperties: new BasicProperties { DeliveryMode = DeliveryModes.Persistent }, body: body);
    }

    public async ValueTask DisposeAsync()
    {
        if (Lanes is not null) await Lanes.DisposeAsync();

        // Cancel all pending requests
        foreach (var (_, tcs) in _pendingRequests)
            tcs.TrySetCanceled();
        _pendingRequests.Clear();

        if (_consumeChannel is not null)
            await _consumeChannel.CloseAsync();
        if (_publishChannel is not null)
            await _publishChannel.CloseAsync();
        if (_connection is not null)
            await _connection.CloseAsync();
    }

    internal sealed record RelayMessage(long ChatId, string Sender, string Text, DateTimeOffset Timestamp,
        string Type = "directive", string? CorrelationId = null, string? TaskId = null,
        Guid? Epoch = null, string? Sandbox = null);
}
