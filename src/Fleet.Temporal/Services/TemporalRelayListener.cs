using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fleet.Shared;
using Fleet.Temporal.Configuration;
using Fleet.Temporal.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Fleet.Temporal.Mcp;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Temporalio.Exceptions;

namespace Fleet.Temporal.Services;

/// <summary>What became of one relayed workflow signal.</summary>
internal enum RelaySignalOutcome
{
    /// <summary>Temporal accepted the signal.</summary>
    Delivered,

    /// <summary>
    /// Nothing will ever deliver it: it is malformed, names an approver-only gate, or Temporal
    /// said the workflow does not exist or has closed. Retrying cannot change that.
    /// </summary>
    Dropped,

    /// <summary>Every attempt failed for a reason that could have passed. The signal is lost.</summary>
    GaveUp,

    /// <summary>The bridge is stopping. The message goes back to the queue for the next start.</summary>
    Interrupted,
}

/// <summary>
/// Subscribes to the temporal-bridge agent queue on RabbitMQ.
/// When an agent sends a response/partial-response with a TaskId,
/// routes it to TaskCompletionRegistry to complete the awaiting Temporal activity.
/// When an agent sends a workflow-signal message, delivers it to the target workflow via TemporalClientFactory.
/// </summary>
public sealed class TemporalRelayListener : IHostedService, IAsyncDisposable
{
    private const string QueueName = RelayBroker.TemporalBridgeQueue;
    private const string RoutingKey = RelayBroker.TemporalBridgeKey;

    private static readonly Regex StatusPrefixRegex =
        new(@"^\[status:\s*(\w+)\]\s*", RegexOptions.Compiled);

    /// <summary>
    /// Waits between attempts to deliver one relayed signal after a failure that may pass: seven
    /// attempts over about a minute, which covers a Temporal server restart.
    ///
    /// <para>
    /// The message stays unacknowledged for the whole time, and the queue is consumed one message
    /// at a time, so nothing behind it is handled until it is settled. That is deliberate: it keeps
    /// one agent's signal and its later reply in the order they were sent, and it is why the total
    /// is bounded — an unbounded retry would hold every delegation reply behind one signal.
    /// </para>
    /// </summary>
    internal static readonly TimeSpan[] DefaultSignalRetryDelays =
    [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30),
    ];

    /// <summary>
    /// Waits between attempts to start consuming once the first attempt has failed. The last value
    /// repeats: the listener never stops trying, because a bridge that is up without it accepts
    /// delegations whose replies it cannot receive.
    /// </summary>
    internal static readonly TimeSpan[] DefaultStartRetryDelays =
    [
        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(60),
    ];

    private enum Phase { NotStarted, Disabled, Starting, Consuming, Retrying, Stopped }

    private readonly RabbitMqOptions _rabbitConfig;
    private readonly TaskCompletionRegistry _registry;
    private readonly ITemporalClientFactory _clientFactory;
    private readonly ILogger<TemporalRelayListener> _logger;
    private readonly DurableDelegationService? _durable;
    private readonly CancellationTokenSource _stopping = new();

    private IConnection? _connection;
    private IChannel? _channel;
    private Task? _startRetry;
    private volatile Phase _phase = Phase.NotStarted;

    public TemporalRelayListener(
        IOptions<RabbitMqOptions> rabbitConfig,
        TaskCompletionRegistry registry,
        ITemporalClientFactory clientFactory,
        ILogger<TemporalRelayListener> logger,
        DurableDelegationService? durable = null)
    {
        _rabbitConfig = rabbitConfig.Value;
        _registry = registry;
        _clientFactory = clientFactory;
        _logger = logger;
        _durable = durable;
        ConnectAsync = ConnectToBrokerAsync;
    }

    /// <summary>Opens the broker connection. Replaced in tests, which have no broker.</summary>
    internal Func<CancellationToken, Task<IConnection>> ConnectAsync { get; set; }

    /// <summary>The wait between attempts. Replaced in tests so they do not sleep.</summary>
    internal Func<TimeSpan, CancellationToken, Task> DelayAsync { get; set; } = Task.Delay;

    internal IReadOnlyList<TimeSpan> SignalRetryDelays { get; set; } = DefaultSignalRetryDelays;

    internal IReadOnlyList<TimeSpan> StartRetryDelays { get; set; } = DefaultStartRetryDelays;

    /// <summary>
    /// Where the listener is, for <c>/health</c>: <c>disabled</c> (no broker host configured),
    /// <c>starting</c>, <c>consuming</c>, <c>retrying</c> (it has never consumed and is still
    /// trying), <c>recovering</c> (it was consuming and the channel is closed; the client library
    /// is reconnecting) or <c>stopped</c>. Only <c>consuming</c> receives agent replies.
    /// </summary>
    public string State => _phase switch
    {
        Phase.Disabled => "disabled",
        Phase.Consuming => _channel is { IsOpen: true } ? "consuming" : "recovering",
        Phase.Retrying => "retrying",
        Phase.Stopped => "stopped",
        _ => "starting",
    };

    public async Task StartAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_rabbitConfig.Host))
        {
            _logger.LogWarning("RabbitMq:Host not configured — relay listener disabled");
            _phase = Phase.Disabled;
            return;
        }

        _phase = Phase.Starting;

        // The first attempt is awaited, as it always was, so that in the ordinary case the queue
        // is declared and consumed before the workers that publish delegations start.
        if (await TryStartConsumingAsync(attempt: 1, ct))
            return;

        // It used to stop here: one log line, and a bridge that answered /health while every
        // delegation it accepted ran to its timeout. Host startup is not held up any longer, but
        // the listener keeps trying for as long as the bridge runs.
        _phase = Phase.Retrying;
        _startRetry = Task.Run(() => RetryStartAsync(_stopping.Token), CancellationToken.None);
    }

    private async Task RetryStartAsync(CancellationToken ct)
    {
        for (var attempt = 2; !ct.IsCancellationRequested; attempt++)
        {
            var delay = StartRetryDelays[Math.Min(attempt - 2, StartRetryDelays.Count - 1)];
            _logger.LogWarning(
                "TemporalRelayListener is not consuming — agent replies are not being received. Next start attempt {Attempt} in {Delay}s",
                attempt, delay.TotalSeconds);

            try { await DelayAsync(delay, ct); }
            catch (OperationCanceledException) { return; }

            if (await TryStartConsumingAsync(attempt, ct))
                return;
        }
    }

    /// <summary>
    /// One attempt to connect, declare the topology and start consuming. On failure everything
    /// the attempt opened is closed again, so a retry never leaves a second connection behind.
    /// </summary>
    private async Task<bool> TryStartConsumingAsync(int attempt, CancellationToken ct)
    {
        IConnection? connection = null;
        IChannel? channel = null;
        try
        {
            connection = await ConnectAsync(ct);
            connection.ConnectionShutdownAsync += (_, args) =>
            {
                _logger.LogWarning("RabbitMQ connection lost (reason: {Reason}) — auto-recovery in progress", args.ReplyText);
                return Task.CompletedTask;
            };

            channel = await connection.CreateChannelAsync(cancellationToken: ct);

            // Idempotent exchange declare — must match what agents declare
            await channel.ExchangeDeclareAsync(
                _rabbitConfig.Exchange, ExchangeType.Direct, durable: true, autoDelete: false,
                cancellationToken: ct);

            // Dedicated queue for the temporal bridge
            await channel.QueueDeclareAsync(
                queue: QueueName, durable: true, exclusive: false, autoDelete: false,
                cancellationToken: ct);

            await channel.QueueBindAsync(QueueName, _rabbitConfig.Exchange, routingKey: RoutingKey,
                cancellationToken: ct);

            // One message at a time: a signal and the reply that follows it are handled in the
            // order the agent sent them.
            await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false,
                cancellationToken: ct);

            // Published before the consumer exists, so the first delivery finds them set.
            _connection = connection;
            _channel = channel;

            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += OnMessageReceivedAsync;
            await channel.BasicConsumeAsync(QueueName, autoAck: false, consumer: consumer,
                cancellationToken: ct);

            _phase = Phase.Consuming;
            _logger.LogInformation(
                "TemporalRelayListener started on exchange={Exchange}, queue={Queue} (attempt {Attempt})",
                _rabbitConfig.Exchange, QueueName, attempt);
            return true;
        }
        catch (Exception ex)
        {
            _channel = null;
            _connection = null;
            _logger.LogError(ex, "Failed to start TemporalRelayListener (attempt {Attempt})", attempt);
            await CloseQuietlyAsync(channel, connection);
            return false;
        }
    }

    private Task<IConnection> ConnectToBrokerAsync(CancellationToken ct)
    {
        var factory = new ConnectionFactory
        {
            HostName = _rabbitConfig.Host,
            ClientProvidedName = "fleet-temporal-bridge",
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            RequestedHeartbeat = TimeSpan.FromSeconds(30),
            NetworkRecoveryInterval = TimeSpan.FromSeconds(5),
        };

        return RabbitMqConnectionHelper.ConnectWithRetryAsync(factory, _logger, ct);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        // First, so a start attempt or a signal waiting out a retry lets go instead of holding
        // shutdown for the rest of its backoff.
        await _stopping.CancelAsync();
        if (_startRetry is not null)
        {
            try { await _startRetry.WaitAsync(ct); }
            catch { /* the loop logs its own failures; shutdown goes on */ }
        }

        _phase = Phase.Stopped;
        _registry.CancelAll();
        await DisposeAsync();
    }

    private async Task OnMessageReceivedAsync(object sender, BasicDeliverEventArgs ea)
    {
        try
        {
            var json = Encoding.UTF8.GetString(ea.Body.Span);
            var message = RelayWire.Deserialize(json);

            if (message is null)
            {
                await _channel!.BasicAckAsync(ea.DeliveryTag, multiple: false);
                return;
            }

            _logger.LogInformation(
                "Relay received from {Sender} (type={Type}, taskId={TaskId}, workflowId={WorkflowId})",
                message.Sender, message.Type, message.TaskId ?? "none", message.WorkflowId ?? "none");

            if (message.Type == RelayMessageType.WorkflowSignal)
            {
                switch (await HandleWorkflowSignalAsync(message, _stopping.Token))
                {
                    case RelaySignalOutcome.Interrupted:
                        // Back to the queue: the next start delivers it. The bridge is stopping,
                        // so this cannot turn into a redelivery loop.
                        await _channel!.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: true);
                        break;
                    case RelaySignalOutcome.GaveUp:
                        await _channel!.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false);
                        break;
                    default:
                        await _channel!.BasicAckAsync(ea.DeliveryTag, multiple: false);
                        break;
                }
                return;
            }

            // Only process responses that carry a TaskId
            if (message.TaskId is null || (message.Type != RelayMessageType.Response && message.Type != RelayMessageType.PartialResponse))
            {
                await _channel!.BasicAckAsync(ea.DeliveryTag, multiple: false);
                return;
            }

            var matched = await TryRouteReplyAsync(message, _stopping.Token);

            if (matched)
            {
                // TCS fulfilled — safe to ack
                await _channel!.BasicAckAsync(ea.DeliveryTag, multiple: false);
            }
            else
            {
                // Orphan response: no pending TCS (already completed or expired) — nack without requeue
                _logger.LogWarning(
                    "Orphan response for TaskId={TaskId} from {Sender} (type={Type}) — no delegation is waiting for it, nacking without requeue",
                    message.TaskId, message.Sender, message.Type);
                await _channel!.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false);
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested && _durable is { Enabled: true })
        {
            // The bridge is stopping in the middle of a reply to a durable delegation. Back to the
            // queue: the next start handles it, and a record that was already updated answers the
            // redelivery with "nothing changed". With durable delegation off this never applies
            // and the catch below handles everything, as before.
            try { await _channel!.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: true); }
            catch { /* channel may be closing; an unacked message is requeued by the broker anyway */ }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing relay message — nacking without requeue");
            try { await _channel!.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false); }
            catch { /* channel may be closing */ }
        }
    }

    /// <summary>
    /// Hands an agent's reply to whatever is waiting for it. True when something was.
    ///
    /// <para>
    /// A durable delegation (only when that is switched on) is asked first, because its record
    /// outlives this process and an in-memory registration does not. Anything but "not pending"
    /// from it settles the message: completed, continued, recorded for a later pass, or a repeat
    /// that changes nothing. Otherwise the in-memory registry is asked, as it always was.
    /// </para>
    /// </summary>
    internal async Task<bool> TryRouteReplyAsync(RelayMessage message, CancellationToken ct = default)
    {
        var result = ParseAgentResult(message.Text, message.Type);

        if (_durable is { Enabled: true }
            && await _durable.TryHandleReplyAsync(message.TaskId!, result, ct) != DurableReplyOutcome.NotPending)
        {
            return true;
        }

        return _registry.TryComplete(message.TaskId!, result);
    }

    /// <summary>
    /// Delivers a workflow-signal message to the target workflow via Temporal.
    /// Expects WorkflowId and SignalName to be populated; Text is the signal payload.
    ///
    /// <para>
    /// A failure used to be logged and the message acknowledged, so a wakeup an agent had sent was
    /// gone the moment Temporal was briefly unreachable. Now a failure that can pass is retried on
    /// <see cref="SignalRetryDelays"/> before the signal is given up, and only a failure that
    /// cannot pass (<see cref="IsPermanent"/>) is dropped at once.
    /// </para>
    ///
    /// <para>
    /// Delivery is at least once: an attempt Temporal applied but answered too late is sent
    /// again. Never logs the signal's text.
    /// </para>
    /// </summary>
    internal async Task<RelaySignalOutcome> HandleWorkflowSignalAsync(RelayMessage message, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(message.WorkflowId) || string.IsNullOrEmpty(message.SignalName))
        {
            _logger.LogWarning(
                "workflow-signal from {Sender} missing WorkflowId or SignalName — dropping",
                message.Sender);
            return RelaySignalOutcome.Dropped;
        }

        if (CeoGateSignals.IsReserved(message.SignalName))
        {
            _logger.LogWarning("Reserved workflow signal refused on agent relay: {SignalName}", message.SignalName);
            return RelaySignalOutcome.Dropped;
        }

        var attempts = SignalRetryDelays.Count + 1;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var client = await _clientFactory.GetClientAsync("fleet");
                var handle = client.GetWorkflowHandle(message.WorkflowId);
                await handle.SignalAsync(message.SignalName, new[] { message.Text });

                _logger.LogInformation(
                    "Signal '{SignalName}' delivered to workflow {WorkflowId} from {Sender} (attempt {Attempt})",
                    message.SignalName, message.WorkflowId, message.Sender, attempt);
                return RelaySignalOutcome.Delivered;
            }
            catch (Exception ex) when (IsPermanent(ex))
            {
                _logger.LogWarning(ex,
                    "Signal '{SignalName}' from {Sender} cannot be delivered to workflow {WorkflowId} — dropping (no such workflow, or it has closed)",
                    message.SignalName, message.Sender, message.WorkflowId);
                return RelaySignalOutcome.Dropped;
            }
            catch (Exception ex) when (attempt >= attempts)
            {
                _logger.LogError(ex,
                    "Failed to deliver signal '{SignalName}' from {Sender} to workflow {WorkflowId} after {Attempts} attempts — giving up, the signal is lost",
                    message.SignalName, message.Sender, message.WorkflowId, attempts);
                return RelaySignalOutcome.GaveUp;
            }
            catch (Exception ex)
            {
                var delay = SignalRetryDelays[attempt - 1];
                _logger.LogWarning(ex,
                    "Failed to deliver signal '{SignalName}' to workflow {WorkflowId} (attempt {Attempt}/{Attempts}) — retrying in {Delay}s",
                    message.SignalName, message.WorkflowId, attempt, attempts, delay.TotalSeconds);

                try { await DelayAsync(delay, ct); }
                catch (OperationCanceledException) { return RelaySignalOutcome.Interrupted; }
            }
        }
    }

    /// <summary>
    /// True when Temporal's answer will be the same however often it is asked: the workflow does
    /// not exist or has already closed (<c>NotFound</c>), or the request itself is invalid.
    /// Everything else — the server unreachable, a deadline, a client that could not connect — is
    /// treated as able to pass.
    /// </summary>
    internal static bool IsPermanent(Exception ex) =>
        ex is RpcException { Code: RpcException.StatusCode.NotFound or RpcException.StatusCode.InvalidArgument };

    /// <summary>
    /// Parses the [status: X] prefix that agents inject when TaskId is set (TelegramTransport.FormatTaskResponse).
    /// Falls back to inferring status from message type.
    /// </summary>
    private static AgentTaskResult ParseAgentResult(string text, string messageType)
    {
        var match = StatusPrefixRegex.Match(text);
        if (match.Success)
        {
            var status = match.Groups[1].Value.ToLowerInvariant();
            var body = text[match.Length..];
            return new AgentTaskResult(body, status);
        }

        // Fallback: partial-response without prefix → incomplete
        var inferredStatus = messageType == RelayMessageType.PartialResponse ? "incomplete" : "completed";
        return new AgentTaskResult(text, inferredStatus);
    }

    public async ValueTask DisposeAsync()
    {
        var channel = _channel;
        var connection = _connection;
        _channel = null;
        _connection = null;
        await CloseQuietlyAsync(channel, connection);
    }

    private static async Task CloseQuietlyAsync(IChannel? channel, IConnection? connection)
    {
        if (channel is not null)
        {
            try { await channel.CloseAsync(); }
            catch { /* already closed, or the connection under it is gone */ }
        }

        if (connection is not null)
        {
            try { await connection.CloseAsync(); }
            catch { /* already closed */ }
        }
    }
}
