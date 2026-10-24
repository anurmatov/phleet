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

namespace Fleet.Temporal.Services;

/// <summary>
/// Subscribes to the temporal-bridge agent queue on RabbitMQ.
/// When an agent sends a response/partial-response with a TaskId,
/// routes it to TaskCompletionRegistry to complete the awaiting Temporal activity.
/// When an agent sends a workflow-signal message, delivers it to the target workflow via TemporalClientFactory.
///
/// <para>
/// #476: a reply whose waiter is already gone is, with
/// <see cref="TemporalBridgeOptions.WorkflowOutcomeLateCaptureEnabled"/>, handed to the durable late
/// queue first and acknowledged only after the broker confirmed that copy. When the handoff fails
/// the reply is NOT acknowledged: the consumer is cancelled, the reply returns to the relay queue,
/// and consumption resumes after a 5..60 s backoff. With capture off the legacy discard is unchanged.
/// </para>
/// </summary>
public sealed class TemporalRelayListener : IHostedService, IAsyncDisposable
{
    private static readonly Regex StatusPrefixRegex =
        new(@"^\[status:\s*(\w+)\]\s*", RegexOptions.Compiled);

    private readonly RabbitMqOptions _rabbitConfig;
    private readonly TemporalBridgeOptions _bridgeOptions;
    private readonly TaskCompletionRegistry _registry;
    private readonly TemporalClientFactory _clientFactory;
    private readonly IWorkflowOutcomeBroker? _outcomeBroker;
    private readonly TimeProvider _time;
    private readonly ILogger<TemporalRelayListener> _logger;
    private readonly WorkflowOutcomeBackoff _captureBackoff = new();
    private readonly CancellationTokenSource _stopping = new();

    private IConnection? _connection;
    private IChannel? _channel;
    private string? _consumerTag;
    private int _pausing;

    public TemporalRelayListener(
        IOptions<RabbitMqOptions> rabbitConfig,
        TaskCompletionRegistry registry,
        TemporalClientFactory clientFactory,
        ILogger<TemporalRelayListener> logger,
        IOptions<TemporalBridgeOptions>? bridgeOptions = null,
        IWorkflowOutcomeBroker? outcomeBroker = null,
        TimeProvider? timeProvider = null)
    {
        _rabbitConfig = rabbitConfig.Value;
        _bridgeOptions = bridgeOptions?.Value ?? new TemporalBridgeOptions();
        _registry = registry;
        _clientFactory = clientFactory;
        _outcomeBroker = outcomeBroker;
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    private bool CaptureEnabled => _bridgeOptions.WorkflowOutcomeLateCaptureEnabled;

    /// <summary>Test seam: the real-broker suite points the listener at its own throwaway broker.</summary>
    internal ConnectionFactory? ConnectionFactoryOverride { get; init; }

    public async Task StartAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_rabbitConfig.Host))
        {
            _logger.LogWarning("RabbitMq:Host not configured — relay listener disabled");
            return;
        }

        try
        {
            var factory = ConnectionFactoryOverride ?? new ConnectionFactory
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

            // Activation is checked up front so a broker without publisher confirms (or a queue
            // declared with other arguments) is reported now, not at the first late reply.
            if (CaptureEnabled)
            {
                string? blocked;
                try
                {
                    blocked = _outcomeBroker is null ? "capture_not_configured" : await _outcomeBroker.ActivateAsync(ct);
                }
                catch (Exception ex)
                {
                    // Never let capture keep the relay consumer from starting: matched replies must flow.
                    blocked = ex.GetType().Name;
                }

                if (blocked is not null)
                {
                    _logger.LogError(
                        "Workflow outcome late-result capture activation blocked ({Reason}). Replies whose waiter is gone will be held unacknowledged with the broker until capture activates; they are never acknowledged and discarded",
                        blocked);
                }
            }

            await OpenRelayConsumerAsync(ct);

            _logger.LogInformation(
                "TemporalRelayListener started on exchange={Exchange}, queue={Queue}",
                _rabbitConfig.Exchange, TemporalBridgeOptions.RelayQueue);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start TemporalRelayListener");
        }
    }

    /// <summary>The relay channel and consumer exactly as they always were; reused after a pause.</summary>
    private async Task OpenRelayConsumerAsync(CancellationToken ct)
    {
        var channel = await _connection!.CreateChannelAsync(cancellationToken: ct);
        _channel = channel;

        // Idempotent exchange declare — must match what agents declare
        await channel.ExchangeDeclareAsync(
            _rabbitConfig.Exchange, ExchangeType.Direct, durable: true, autoDelete: false,
            cancellationToken: ct);

        // Dedicated queue for the temporal bridge
        const string queueName = TemporalBridgeOptions.RelayQueue;
        const string routingKey = "temporal-bridge";

        await channel.QueueDeclareAsync(
            queue: queueName, durable: true, exclusive: false, autoDelete: false,
            cancellationToken: ct);

        await channel.QueueBindAsync(queueName, _rabbitConfig.Exchange, routingKey: routingKey,
            cancellationToken: ct);

        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false,
            cancellationToken: ct);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += OnMessageReceivedAsync;
        _consumerTag = await channel.BasicConsumeAsync(queueName, autoAck: false, consumer: consumer,
            cancellationToken: ct);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _stopping.Cancel();
        _registry.CancelAll();
        await DisposeAsync();
    }

    private Task OnMessageReceivedAsync(object sender, BasicDeliverEventArgs ea)
    {
        var channel = ((AsyncEventingBasicConsumer)sender).Channel;
        return HandleDeliveryAsync(
            ea.Body, ea.BasicProperties, new RabbitMqOutcomeDelivery(channel, ea.DeliveryTag, (backoff, reason) => Pause(channel, backoff, reason)));
    }

    /// <summary>
    /// One relay delivery. Matched replies, signals and everything else are settled exactly as
    /// before; only a waiter miss with capture enabled takes the confirmed handoff path.
    /// </summary>
    internal async Task HandleDeliveryAsync(
        ReadOnlyMemory<byte> body, IReadOnlyBasicProperties? properties, IWorkflowOutcomeDelivery delivery)
    {
        try
        {
            var json = Encoding.UTF8.GetString(body.Span);
            var message = JsonSerializer.Deserialize<RelayMessage>(json);

            if (message is null)
            {
                await delivery.AckAsync();
                return;
            }

            _logger.LogInformation(
                "Relay received from {Sender} (type={Type}, taskId={TaskId}, workflowId={WorkflowId})",
                message.Sender, message.Type, message.TaskId ?? "none", message.WorkflowId ?? "none");

            if (message.Type == RelayMessageType.WorkflowSignal)
            {
                await HandleWorkflowSignalAsync(message);
                await delivery.AckAsync();
                return;
            }

            // Only process responses that carry a TaskId
            if (message.TaskId is null || (message.Type != RelayMessageType.Response && message.Type != RelayMessageType.PartialResponse))
            {
                await delivery.AckAsync();
                return;
            }

            var result = ParseAgentResult(message.Text, message.Type);
            var matched = _registry.TryComplete(message.TaskId, result);

            if (matched)
            {
                // TCS fulfilled — safe to ack
                await delivery.AckAsync();
            }
            else if (CaptureEnabled)
            {
                await CaptureLateReplyAsync(message.TaskId, body, properties, delivery);
            }
            else
            {
                // Orphan response: no pending TCS (already completed or expired) — nack without requeue
                _logger.LogWarning("Orphan response for TaskId={TaskId} — nacking without requeue", message.TaskId);
                await delivery.RejectWithoutRequeueAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing relay message — nacking without requeue");
            try { await delivery.RejectWithoutRequeueAsync(); }
            catch { /* channel may be closing */ }
        }
    }

    /// <summary>
    /// Publishes the ORIGINAL bytes to the late queue and acknowledges the relay delivery only
    /// once the broker confirmed that copy. Anything else — return, nack (including the
    /// reject-publish quota), unknown confirm, capture unavailable — pauses without acknowledging.
    /// </summary>
    private async Task CaptureLateReplyAsync(
        string taskId, ReadOnlyMemory<byte> body, IReadOnlyBasicProperties? properties, IWorkflowOutcomeDelivery delivery)
    {
        var messageId = WorkflowOutcomeLateEnvelope.StableMessageId(properties?.MessageId, body.Span);
        var now = _time.GetUtcNow();

        WorkflowOutcomePublishResult published;
        try
        {
            published = _outcomeBroker is null
                ? new(WorkflowOutcomePublishStatus.Unavailable, "capture_not_configured")
                : await _outcomeBroker.PublishAsync(
                    _bridgeOptions.WorkflowOutcomeLateQueue,
                    body,
                    messageId,
                    WorkflowOutcomeLateEnvelope.LateHeaders(attempt: 0, dueAt: now, receivedAt: now, taskId),
                    _stopping.Token);
        }
        catch (Exception ex)
        {
            published = new(WorkflowOutcomePublishStatus.Unknown, ex.GetType().Name);
        }

        if (published.IsConfirmed)
        {
            _captureBackoff.Reset();
            _logger.LogInformation(
                "Late reply for TaskId={TaskId} captured to {Queue} (messageId={MessageId})",
                taskId, _bridgeOptions.WorkflowOutcomeLateQueue, messageId);
            await delivery.AckAsync();
            return;
        }

        var backoff = _captureBackoff.Next();
        _logger.LogWarning(
            "Late reply for TaskId={TaskId} not captured ({Status}: {Reason}) — left unacknowledged; relay consumer paused for {Backoff}s",
            taskId, published.Status, published.Reason, backoff.TotalSeconds);
        delivery.Pause(backoff, $"{published.Status}_{published.Reason}");
    }

    /// <summary>
    /// Cancels the relay consumer and closes its channel, which returns the unacknowledged reply to
    /// the queue, then reopens it after <paramref name="backoff"/>. Runs off the consumer callback,
    /// and only once at a time.
    /// </summary>
    private void Pause(IChannel channel, TimeSpan backoff, string reason)
    {
        if (Interlocked.CompareExchange(ref _pausing, 1, 0) != 0) return;
        _ = Task.Run(() => PauseAndResumeAsync(channel, backoff, reason));
    }

    private async Task PauseAndResumeAsync(IChannel channel, TimeSpan backoff, string reason)
    {
        try
        {
            try { if (_consumerTag is not null) await channel.BasicCancelAsync(_consumerTag); } catch { /* closing anyway */ }
            try { await channel.CloseAsync(); } catch { /* already closed */ }

            while (!_stopping.IsCancellationRequested)
            {
                await Task.Delay(backoff, _time, _stopping.Token);
                try
                {
                    await OpenRelayConsumerAsync(_stopping.Token);
                    _logger.LogInformation("Relay consumer resumed after a late-result handoff pause ({Reason})", reason);
                    return;
                }
                catch (Exception ex) when (!_stopping.IsCancellationRequested)
                {
                    backoff = _captureBackoff.Next();
                    _logger.LogWarning(
                        "Relay consumer could not resume ({Error}); retrying in {Backoff}s", ex.GetType().Name, backoff.TotalSeconds);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        finally
        {
            Interlocked.Exchange(ref _pausing, 0);
        }
    }

    /// <summary>
    /// Delivers a workflow-signal message to the target workflow via Temporal.
    /// Expects WorkflowId and SignalName to be populated; Text is the signal payload.
    /// </summary>
    private async Task HandleWorkflowSignalAsync(RelayMessage message)
    {
        if (string.IsNullOrEmpty(message.WorkflowId) || string.IsNullOrEmpty(message.SignalName))
        {
            _logger.LogWarning(
                "workflow-signal from {Sender} missing WorkflowId or SignalName — dropping",
                message.Sender);
            return;
        }

        try
        {
            var client = await _clientFactory.GetClientAsync("fleet");
            var handle = client.GetWorkflowHandle(message.WorkflowId);
            await handle.SignalAsync(message.SignalName, new[] { message.Text });

            _logger.LogInformation(
                "Signal '{SignalName}' delivered to workflow {WorkflowId} from {Sender}",
                message.SignalName, message.WorkflowId, message.Sender);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to deliver signal '{SignalName}' to workflow {WorkflowId}",
                message.SignalName, message.WorkflowId);
        }
    }

    /// <summary>
    /// Parses the [status: X] prefix that agents inject when TaskId is set (TelegramTransport.FormatTaskResponse).
    /// Falls back to inferring status from message type.
    /// </summary>
    internal static AgentTaskResult ParseAgentResult(string text, string messageType)
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
        // A capture pause may already have closed the relay channel.
        if (_channel is { IsOpen: true })
            await _channel.CloseAsync();
        if (_connection is not null)
            await _connection.CloseAsync();
    }
}
