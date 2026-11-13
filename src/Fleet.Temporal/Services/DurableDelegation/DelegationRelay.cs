using System.Text;
using System.Text.Json;
using Fleet.Temporal.Configuration;
using Fleet.Temporal.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Fleet.Temporal.Services;

/// <summary>What a pending delegation sends outside the process.</summary>
public interface IDelegationRelay
{
    /// <summary>
    /// False until the relay knows where to send: the bridge has its configuration from the
    /// orchestrator and the escalation target is set. Nothing is sent before then. A record found
    /// at start-up can owe a timeout notice before the bridge has learnt who the escalation target
    /// is, and a notice published to an empty target is lost without a trace.
    /// </summary>
    bool Ready => true;

    /// <summary>Publishes the directive the delegation is waiting on. Throws when it could not.</summary>
    Task PublishDirectiveAsync(PendingDelegation delegation, CancellationToken ct);

    /// <summary>Best effort: tells the agent to stop the task it is working on. Never throws.</summary>
    Task SendCancelAsync(PendingDelegation delegation);

    /// <summary>Best effort: tells the escalation target that the agent ran out of budget. Never throws.</summary>
    Task ReportTimeoutAsync(PendingDelegation delegation, string detail);
}

/// <summary>
/// The outbound half of a durable delegation: the same directive, cancel and timeout report that
/// <see cref="Activities.DelegateToAgentActivity"/> sends from inside an activity, sent from a
/// record instead. The message shapes are deliberately identical to the activity's
/// (<c>DelegationRelayParityTests</c> compares them), down to the workflow tag and memory hint the
/// activity puts on everything it publishes.
///
/// <para>
/// The two are separate on purpose: the activity's own publishing code is untouched, so the
/// switch-off path cannot have changed. They fold into one when the in-memory path is removed.
/// </para>
/// </summary>
public sealed class DelegationRelay : IDelegationRelay
{
    private const string BridgeSender = "temporal-bridge";

    private readonly RabbitMqOptions _rabbitConfig;
    private readonly TemporalBridgeOptions _bridgeConfig;
    private readonly IConnectionFactory _connectionFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DelegationRelay> _logger;

    public DelegationRelay(
        IOptions<RabbitMqOptions> rabbitConfig,
        IOptions<TemporalBridgeOptions> bridgeConfig,
        IConnectionFactory connectionFactory,
        IHttpClientFactory httpClientFactory,
        ILogger<DelegationRelay> logger)
    {
        _rabbitConfig = rabbitConfig.Value;
        _bridgeConfig = bridgeConfig.Value;
        _connectionFactory = connectionFactory;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private long EffectiveGroupChatId =>
        FleetWorkflowConfig.GroupChatId != 0 ? FleetWorkflowConfig.GroupChatId : _bridgeConfig.GroupChatId;

    public bool Ready
    {
        get
        {
            if (!FleetWorkflowConfig.PeerConfigLoaded) return false;
            try
            {
                return !string.IsNullOrWhiteSpace(FleetWorkflowConfig.Instance.EscalationTarget);
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    public Task PublishDirectiveAsync(PendingDelegation delegation, CancellationToken ct) =>
        PublishAsync(
            delegation, delegation.Agent, delegation.CurrentInstruction, delegation.CurrentTaskId,
            delegation.Repo, ct);

    public async Task SendCancelAsync(PendingDelegation delegation)
    {
        var agentName = delegation.Agent;
        var taskId = delegation.CurrentTaskId;

        if (!string.IsNullOrEmpty(_bridgeConfig.OrchestratorUrl))
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var client = _httpClientFactory.CreateClient("orchestrator-cancel");
                var url = $"{_bridgeConfig.OrchestratorUrl.TrimEnd('/')}/api/agents/{agentName}/cancel/{Uri.EscapeDataString(taskId)}";
                var request = new HttpRequestMessage(HttpMethod.Post, url);
                if (!string.IsNullOrEmpty(_bridgeConfig.OrchestratorAuthToken))
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _bridgeConfig.OrchestratorAuthToken);
                var response = await client.SendAsync(request, cts.Token);
                _logger.LogInformation(
                    "Targeted cancel sent to orchestrator for agent {Agent}, taskId={TaskId}, status={Status}",
                    agentName, taskId, (int)response.StatusCode);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Orchestrator targeted cancel failed for agent {Agent}, taskId={TaskId} — falling back to RabbitMQ broadcast", agentName, taskId);
            }
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await PublishAsync(delegation, agentName, "/cancel all", taskId + "/cancel", repo: null, cts.Token);
            _logger.LogInformation("Fallback cancel broadcast sent to agent {Agent} for taskId={TaskId}", agentName, taskId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send fallback cancel to agent {Agent} for taskId={TaskId}", agentName, taskId);
        }
    }

    public async Task ReportTimeoutAsync(PendingDelegation delegation, string detail)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var escalationTarget = FleetWorkflowConfig.Instance.EscalationTarget;
            var report = $"[temporal] activity timeout: {detail}. the workflow will fail this activity.";
            await PublishAsync(delegation, escalationTarget, report, delegation.TaskId + "/timeout-report", repo: null, cts.Token);
            _logger.LogInformation(
                "Timeout report sent to {EscalationTarget} for agent {Agent}, taskId={TaskId}",
                escalationTarget, delegation.Agent, delegation.TaskId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send timeout report for agent {Agent}, taskId={TaskId}", delegation.Agent, delegation.TaskId);
        }
    }

    private async Task PublishAsync(
        PendingDelegation delegation, string agentName, string instruction, string taskId, string? repo, CancellationToken ct)
    {
        instruction =
            $"[fleet-wf:{delegation.WorkflowType}:{delegation.WorkflowId}]\n" +
            instruction +
            $"\n\nSearch fleet-memory for '{delegation.WorkflowType}' to find any operational context before proceeding.";

        await using var connection = await _connectionFactory.CreateConnectionAsync(ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);

        await channel.ExchangeDeclareAsync(
            _rabbitConfig.Exchange, ExchangeType.Direct, durable: true, autoDelete: false,
            cancellationToken: ct);

        var message = new RelayMessage(
            ChatId: EffectiveGroupChatId,
            Sender: BridgeSender,
            Text: instruction,
            Timestamp: DateTimeOffset.UtcNow,
            Type: RelayMessageType.Directive,
            TaskId: taskId,
            Repo: repo);

        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
        var props = new BasicProperties { DeliveryMode = DeliveryModes.Persistent };
        var routingKey = agentName.ToLowerInvariant();

        await channel.BasicPublishAsync(
            _rabbitConfig.Exchange, routingKey: routingKey, mandatory: false,
            basicProperties: props, body: body, cancellationToken: ct);

        _logger.LogInformation("Published directive to {Agent} (taskId={TaskId})", agentName, taskId);
    }
}
