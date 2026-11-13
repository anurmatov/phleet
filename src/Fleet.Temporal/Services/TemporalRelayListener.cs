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

    private readonly RabbitMqOptions _rabbitConfig;
    private readonly TaskCompletionRegistry _registry;
    private readonly ITemporalClientFactory _clientFactory;
    private readonly ILogger<TemporalRelayListener> _logger;
    private readonly CancellationTokenSource _stopping = new();

    private IConnection? _connection;
    private IChannel? _channel;

    public TemporalRelayListener(
        IOptions<RabbitMqOptions> rabbitConfig,
        TaskCompletionRegistry registry,
        ITemporalClientFactory clientFactory,
        ILogger<TemporalRelayListener> logger)
    {
        _rabbitConfig = rabbitConfig.Value;
        _registry = registry;
        _clientFactory = clientFactory;
        _logger = logger;
    }

    /// <summary>The wait between attempts. Replaced in tests so they do not sleep.</summary>
    internal Func<TimeSpan, CancellationToken, Task> DelayAsync { get; set; } = Task.Delay;

    internal IReadOnlyList<TimeSpan> SignalRetryDelays { get; set; } = DefaultSignalRetryDelays;

    public async Task StartAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_rabbitConfig.Host))
        {
            _logger.LogWarning("RabbitMq:Host not configured — relay listener disabled");
            return;
        }

        try
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

            _connection = await RabbitMqConnectionHelper.ConnectWithRetryAsync(factory, _logger, ct);
            _connection.ConnectionShutdownAsync += (_, args) =>
            {
                _logger.LogWarning("RabbitMQ connection lost (reason: {Reason}) — auto-recovery in progress", args.ReplyText);
                return Task.CompletedTask;
            };

            _channel = await _connection.CreateChannelAsync(cancellationToken: ct);

            // Idempotent exchange declare — must match what agents declare
            await _channel.ExchangeDeclareAsync(
                _rabbitConfig.Exchange, ExchangeType.Direct, durable: true, autoDelete: false,
                cancellationToken: ct);

            // Dedicated queue for the temporal bridge
            const string queueName = "fleet.agent.temporal-bridge";
            const string routingKey = "temporal-bridge";

            await _channel.QueueDeclareAsync(
                queue: queueName, durable: true, exclusive: false, autoDelete: false,
                cancellationToken: ct);

            await _channel.QueueBindAsync(queueName, _rabbitConfig.Exchange, routingKey: routingKey,
                cancellationToken: ct);

            await _channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false,
                cancellationToken: ct);

            var consumer = new AsyncEventingBasicConsumer(_channel);
            consumer.ReceivedAsync += OnMessageReceivedAsync;
            await _channel.BasicConsumeAsync(queueName, autoAck: false, consumer: consumer,
                cancellationToken: ct);

            _logger.LogInformation(
                "TemporalRelayListener started on exchange={Exchange}, queue={Queue}",
                _rabbitConfig.Exchange, queueName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start TemporalRelayListener");
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        // First, so a signal waiting out a retry lets go instead of holding shutdown for the rest
        // of its backoff.
        await _stopping.CancelAsync();
        _registry.CancelAll();
        await DisposeAsync();
    }

    private async Task OnMessageReceivedAsync(object sender, BasicDeliverEventArgs ea)
    {
        try
        {
            var json = Encoding.UTF8.GetString(ea.Body.Span);
            var message = JsonSerializer.Deserialize<RelayMessage>(json);

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

            var result = ParseAgentResult(message.Text, message.Type);
            var matched = _registry.TryComplete(message.TaskId, result);

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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing relay message — nacking without requeue");
            try { await _channel!.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false); }
            catch { /* channel may be closing */ }
        }
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
        if (_channel is not null)
            await _channel.CloseAsync();
        if (_connection is not null)
            await _connection.CloseAsync();
    }
}
