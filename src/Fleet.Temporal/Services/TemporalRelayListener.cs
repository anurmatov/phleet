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
/// </summary>
public sealed class TemporalRelayListener : IHostedService, IAsyncDisposable
{
    private static readonly Regex StatusPrefixRegex =
        new(@"^\[status:\s*(\w+)\]\s*", RegexOptions.Compiled);

    private readonly RabbitMqOptions _rabbitConfig;
    private readonly TaskCompletionRegistry _registry;
    private readonly TemporalClientFactory _clientFactory;
    private readonly ILogger<TemporalRelayListener> _logger;

    private IConnection? _connection;
    private IChannel? _channel;

    public TemporalRelayListener(
        IOptions<RabbitMqOptions> rabbitConfig,
        TaskCompletionRegistry registry,
        TemporalClientFactory clientFactory,
        ILogger<TemporalRelayListener> logger)
    {
        _rabbitConfig = rabbitConfig.Value;
        _registry = registry;
        _clientFactory = clientFactory;
        _logger = logger;
    }

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
                await HandleWorkflowSignalAsync(message);
                await _channel!.BasicAckAsync(ea.DeliveryTag, multiple: false);
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
                _logger.LogWarning("Orphan response for TaskId={TaskId} — nacking without requeue", message.TaskId);
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
