using System.Text;
using System.Text.Json;
using Fleet.Agent.Configuration;
using Fleet.Journal.Client;
using Fleet.Shared;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Fleet.Agent.Services;

/// <summary>
/// Publishes heartbeat and registration messages to the fleet.orchestrator topic exchange
/// so the Fleet.Orchestrator service can track agent state in real time.
///
/// Routing keys:
///   registration.{shortName} — published once on startup
///   heartbeat.{shortName}    — published every 30 seconds
///
/// Gracefully disables itself when RabbitMQ is not configured or unreachable.
/// </summary>
public sealed class OrchestratorHeartbeatService : IHostedService, IAsyncDisposable
{
    private const string OrchestratorExchange = OrchestratorBroker.Exchange;
    private const int HeartbeatIntervalSeconds = 30;

    private readonly AgentOptions _agentConfig;
    private readonly RabbitMqOptions _rabbitConfig;
    private readonly TaskManager _taskManager;
    private readonly IFleetConnectionState _connectionState;
    private readonly ILogger<OrchestratorHeartbeatService> _logger;
    private readonly JournalDrainer? _journal;
    private readonly TurnBindingPublisher? _turnBindings;
    private readonly AgentHarness? _harness;

    private IConnection? _connection;
    private IChannel? _channel;
    private Timer? _heartbeatTimer;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    public OrchestratorHeartbeatService(
        IOptions<AgentOptions> agentConfig,
        IOptions<RabbitMqOptions> rabbitConfig,
        TaskManager taskManager,
        IFleetConnectionState connectionState,
        ILogger<OrchestratorHeartbeatService> logger,
        JournalDrainer? journal = null, TurnBindingPublisher? turnBindings = null,
        AgentHarness? harness = null)
    {
        _harness = harness;
        _journal = journal;
        _turnBindings = turnBindings;
        _agentConfig = agentConfig.Value;
        _rabbitConfig = rabbitConfig.Value;
        _taskManager = taskManager;
        _connectionState = connectionState;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_rabbitConfig.Host))
        {
            _logger.LogDebug("RabbitMQ not configured — orchestrator heartbeat disabled");
            return;
        }

        try
        {
            var factory = new ConnectionFactory
            {
                HostName = _rabbitConfig.Host,
                ClientProvidedName = $"{_agentConfig.Name}-heartbeat",
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true,
                RequestedHeartbeat = TimeSpan.FromSeconds(30),
                NetworkRecoveryInterval = TimeSpan.FromSeconds(5),
            };

            _connection = await RabbitMqConnectionHelper.ConnectWithRetryAsync(factory, _logger, ct);
            _connection.ConnectionShutdownAsync += (_, args) =>
            {
                _logger.LogWarning("Orchestrator heartbeat RabbitMQ connection lost (reason: {Reason})", args.ReplyText);
                return Task.CompletedTask;
            };

            _channel = await _connection.CreateChannelAsync(cancellationToken: ct);

            await _channel.ExchangeDeclareAsync(
                OrchestratorExchange,
                ExchangeType.Topic,
                durable: true,
                autoDelete: false,
                cancellationToken: ct);

            _logger.LogInformation("OrchestratorHeartbeatService connected to {Exchange}", OrchestratorExchange);

            // Registration message — announce once on startup
            await PublishAsync(OrchestratorBroker.Registration, ct);

            // Periodic heartbeat timer
            _heartbeatTimer = new Timer(
                _ => _ = PublishHeartbeatFireAndForgetAsync(),
                null,
                TimeSpan.FromSeconds(HeartbeatIntervalSeconds),
                TimeSpan.FromSeconds(HeartbeatIntervalSeconds));

            // Immediate heartbeat on state change (task started or completed)
            _taskManager.OnStatusChanged += () => _ = PublishHeartbeatFireAndForgetAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OrchestratorHeartbeatService failed to start — heartbeats disabled");
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_heartbeatTimer is not null)
            await _heartbeatTimer.DisposeAsync();

        await DisposeAsync();
    }

    private async Task PublishHeartbeatFireAndForgetAsync()
    {
        try
        {
            await PublishAsync(OrchestratorBroker.Heartbeat, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish heartbeat");
        }
    }

    private async Task PublishAsync(string messageType, CancellationToken ct)
    {
        if (_channel is null)
            return;

        var (status, currentTask, currentTaskId) = GetAgentStatus();
        var shortName = _agentConfig.ShortName.ToLowerInvariant();
        var buildCommit = Environment.GetEnvironmentVariable("FLEET_BUILD_COMMIT");

        var containerName = Environment.GetEnvironmentVariable("CONTAINER_NAME")
                         ?? Environment.GetEnvironmentVariable("HOSTNAME");

        var queueSnapshot = _taskManager.GetQueueSnapshot();
        var queuedMessages = queueSnapshot
            .Take(5)
            .Select(q => new QueuedMessageInfo(
                Preview: TaskManager.TruncateText(q.DisplayText, 80),
                Source: q.Source.ToString().ToLowerInvariant(),
                QueuedAt: q.QueuedAt,
                Priority: q.Priority == Models.TaskPriority.PrimaryHuman))
            .ToArray();

        var backgroundTasks = _taskManager.GetActiveBackgroundTasks()
            .Select(t => new BackgroundTaskSummary(
                TaskId: t.TaskId,
                Description: t.Description,
                TaskType: t.TaskType,
                ElapsedSeconds: t.ElapsedSeconds,
                Summary: t.Summary))
            .ToArray();

        var message = new AgentHeartbeat(
            AgentName: shortName,
            Status: status,
            Timestamp: DateTimeOffset.UtcNow,
            CurrentTask: currentTask,
            CurrentTaskId: currentTaskId,
            Version: buildCommit,
            ContainerName: containerName,
            Role: _agentConfig.Role,
            Model: _agentConfig.Model,
            Projects: _agentConfig.Projects,
            UptimeSeconds: (long)(DateTimeOffset.UtcNow - _startedAt).TotalSeconds,
            QueuedCount: queueSnapshot.Count,
            QueuedMessages: queuedMessages,
            BackgroundTasks: backgroundTasks.Length > 0 ? backgroundTasks : null,
            TelegramConnected: _connectionState.TelegramConnected,
            Journal: JournalFrom(_journal?.Snapshot(), _turnBindings?.BindingFailed == true),
            PriorityQueuedCount: queueSnapshot.Count(q => q.Priority == Models.TaskPriority.PrimaryHuman),
            PlanSha256: _harness?.Plan?.PlanSha256);

        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
        var props = new BasicProperties
        {
            DeliveryMode = DeliveryModes.Persistent,
            Expiration = OrchestratorBroker.HeartbeatExpirationMilliseconds,
        };

        var routingKey = OrchestratorBroker.AgentKey(messageType, shortName);
        await _channel.BasicPublishAsync(
            OrchestratorExchange,
            routingKey: routingKey,
            mandatory: false,
            basicProperties: props,
            body: body,
            cancellationToken: ct);

        _logger.LogDebug("Published {Type} to {Exchange}/{RoutingKey} (status={Status})",
            messageType, OrchestratorExchange, routingKey, status);
    }

    /// <summary>
    /// Derives current agent status from TaskManager.
    /// Returns ("idle", null) when no tasks are running, ("busy", taskSummary) otherwise.
    /// </summary>
    private (string Status, string? CurrentTask, string? CurrentTaskId) GetAgentStatus()
        => _taskManager.GetOrchestratorStatus();

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
            await _channel.CloseAsync();
        if (_connection is not null)
            await _connection.CloseAsync();
    }

    /// <summary>The journal block of a heartbeat, or null when the journal is off.</summary>
    private static JournalHeartbeat? JournalFrom(JournalHeartbeatSnapshot? s, bool bindingFailed) => s is null
        ? null
        : new(s.Enabled, s.SpoolDepth, s.OldestAgeSeconds, s.Dropped, s.Dead, s.AuthFailed ? 1 : 0, bindingFailed ? 1 : 0);
}
