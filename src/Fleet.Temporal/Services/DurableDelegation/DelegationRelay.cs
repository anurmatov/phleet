using System.Text;
using System.Text.Json;
using Fleet.Temporal.Configuration;
using Fleet.Temporal.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Fleet.Temporal.Services;

/// <summary>What is known about a cancel when the call that sent it has ended.</summary>
public enum CancelDelivery
{
    /// <summary>
    /// The orchestrator answered, and its answer says nothing is still on its way: it passed the
    /// cancel to the agent and the agent answered (the orchestrator's route awaits the agent's,
    /// and the agent's applies the cancel before it answers), or it does not know the agent.
    /// </summary>
    Settled,

    /// <summary>
    /// The cancel may still arrive: the call failed or was stopped part-way, the orchestrator
    /// could not say what the agent did, or the broadcast fallback was used, which the agent
    /// reads from its queue whenever it gets to it.
    /// </summary>
    Unknown,
}

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

    /// <summary>
    /// Best effort: tells the agent to stop the task it is working on. Never throws. The agent
    /// matches a cancel by task id and nothing else, so the answer matters to the caller: until a
    /// cancel is <see cref="CancelDelivery.Settled"/> it can still reach whatever is running under
    /// that task id. Cancelling <paramref name="ct"/> stops the call and sends nothing further.
    /// </summary>
    Task<CancelDelivery> SendCancelAsync(PendingDelegation delegation, CancellationToken ct = default);

    /// <summary>Best effort: tells the escalation target that the agent ran out of budget. Never throws.</summary>
    Task ReportTimeoutAsync(PendingDelegation delegation, string detail, CancellationToken ct = default);
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
            delegation.Repo, ct, ExecutionIdOf(delegation));

    /// <summary>Null for a record that has none, so nothing is written for it.</summary>
    private static string? ExecutionIdOf(PendingDelegation delegation) =>
        string.IsNullOrEmpty(delegation.ExecutionId) ? null : delegation.ExecutionId;

    public async Task<CancelDelivery> SendCancelAsync(PendingDelegation delegation, CancellationToken ct = default)
    {
        var agentName = delegation.Agent;
        var taskId = delegation.CurrentTaskId;
        var executionId = ExecutionIdOf(delegation);

        if (!string.IsNullOrEmpty(_bridgeConfig.OrchestratorUrl))
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                var client = _httpClientFactory.CreateClient("orchestrator-cancel");
                var url = $"{_bridgeConfig.OrchestratorUrl.TrimEnd('/')}/api/agents/{agentName}/cancel/{Uri.EscapeDataString(taskId)}";
                // The orchestrator passes this on to the agent, which then stops only the task
                // that carries the same id. A query parameter, so an orchestrator or an agent
                // that does not know it ignores it and matches by task id as before.
                if (executionId is not null) url += $"?{ExecutionIdParameter}={Uri.EscapeDataString(executionId)}";
                var request = new HttpRequestMessage(HttpMethod.Post, url);
                if (!string.IsNullOrEmpty(_bridgeConfig.OrchestratorAuthToken))
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _bridgeConfig.OrchestratorAuthToken);
                var response = await client.SendAsync(request, cts.Token);
                _logger.LogInformation(
                    "Targeted cancel sent to orchestrator for agent {Agent}, taskId={TaskId}, status={Status}",
                    agentName, taskId, (int)response.StatusCode);
                if (executionId is not null && response.IsSuccessStatusCode)
                    await LogExecutionAnswerAsync(response, agentName, taskId, executionId, cts.Token);
                // 2xx: the agent answered the orchestrator, so it has applied the cancel. 404: the
                // orchestrator knows no such agent and sent nothing. Anything else leaves open
                // what reached the agent.
                return response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound
                    ? CancelDelivery.Settled
                    : CancelDelivery.Unknown;
            }
            catch (Exception ex) when (ct.IsCancellationRequested)
            {
                // The caller has stopped waiting. Nothing more is sent on its behalf: a broadcast
                // started now could arrive at any later time.
                _logger.LogWarning(
                    "Orchestrator targeted cancel for agent {Agent}, taskId={TaskId} was stopped before it finished: {Error}",
                    agentName, taskId, ex.GetType().Name);
                return CancelDelivery.Unknown;
            }
            catch (Exception ex) when (executionId is not null)
            {
                _logger.LogWarning(
                    "Orchestrator targeted cancel failed for agent {Agent}, taskId={TaskId}: {Error}. No broadcast is sent for a delegation with an execution id",
                    agentName, taskId, ex.GetType().Name);
                return CancelDelivery.Unknown;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Orchestrator targeted cancel failed for agent {Agent}, taskId={TaskId} — falling back to RabbitMQ broadcast", agentName, taskId);
            }
        }

        if (executionId is not null)
        {
            // The fallback tells the agent to stop EVERYTHING it is doing, whenever it reads the
            // message. That cannot be held to one execution, which is what the execution id is
            // for, so a delegation that has one is cancelled through the orchestrator or not at
            // all. The agent then finishes the work and its answer finds nothing waiting.
            if (string.IsNullOrEmpty(_bridgeConfig.OrchestratorUrl))
                _logger.LogWarning(
                    "Cancel for agent {Agent}, taskId={TaskId} was not sent: TemporalBridge:OrchestratorUrl is not set, and no broadcast is sent for a delegation with an execution id",
                    agentName, taskId);
            return CancelDelivery.Unknown;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            await PublishAsync(delegation, agentName, "/cancel all", taskId + "/cancel", repo: null, cts.Token);
            _logger.LogInformation("Fallback cancel broadcast sent to agent {Agent} for taskId={TaskId}", agentName, taskId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send fallback cancel to agent {Agent} for taskId={TaskId}", agentName, taskId);
        }

        return CancelDelivery.Unknown;
    }

    public async Task ReportTimeoutAsync(PendingDelegation delegation, string detail, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
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

    /// <summary>The name of the query parameter that carries the execution id on a cancel.</summary>
    public const string ExecutionIdParameter = "executionId";

    /// <summary>
    /// What the agent did with a cancel that named an execution, for the log: <c>cancelled</c>,
    /// <c>not_found</c> (it had finished, or never arrived), <c>other_execution</c> (the task id
    /// is now another delegation's) or <c>no_execution_id</c>. Nothing when the answer does not
    /// say, which is an orchestrator or an agent from before the id: the cancel was then matched
    /// by task id. Identifiers only, and the outcome of the cancel never depends on this.
    /// </summary>
    private async Task LogExecutionAnswerAsync(
        HttpResponseMessage response, string agentName, string taskId, string executionId, CancellationToken ct)
    {
        string? outcome = null;
        try
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (body.RootElement.ValueKind == JsonValueKind.Object
                && body.RootElement.TryGetProperty("executionId", out var echoed)
                && echoed.ValueKind == JsonValueKind.String
                && string.Equals(echoed.GetString(), executionId, StringComparison.Ordinal)
                && body.RootElement.TryGetProperty("outcome", out var said)
                && said.ValueKind == JsonValueKind.String)
            {
                outcome = said.GetString();
            }
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or IOException or OperationCanceledException)
        {
            // An answer that cannot be read says nothing.
        }

        if (outcome is null)
            _logger.LogWarning(
                "Cancel for agent {Agent}, taskId={TaskId} was answered without the execution id: the orchestrator or the agent predates it and matched by task id",
                agentName, taskId);
        else
            _logger.LogInformation(
                "Cancel for agent {Agent}, taskId={TaskId}, execution {ExecutionId}: {Outcome}",
                agentName, taskId, executionId, outcome is "cancelled" or "not_found" or "other_execution" or "no_execution_id" ? outcome : "unrecognised");
    }

    private async Task PublishAsync(
        PendingDelegation delegation, string agentName, string instruction, string taskId, string? repo, CancellationToken ct,
        string? executionId = null)
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
            Repo: repo,
            ExecutionId: executionId);

        var body = RelayWire.Serialize(message, RelayShape.Delegation);
        var props = new BasicProperties { DeliveryMode = DeliveryModes.Persistent };
        var routingKey = agentName.ToLowerInvariant();

        await channel.BasicPublishAsync(
            _rabbitConfig.Exchange, routingKey: routingKey, mandatory: false,
            basicProperties: props, body: body, cancellationToken: ct);

        _logger.LogInformation("Published directive to {Agent} (taskId={TaskId})", agentName, taskId);
    }
}
