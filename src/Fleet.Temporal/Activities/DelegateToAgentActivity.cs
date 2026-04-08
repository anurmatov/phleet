using System.Text;
using System.Text.Json;
using Fleet.Temporal.Configuration;
using Fleet.Temporal.Models;
using Fleet.Temporal.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using Temporalio.Activities;

namespace Fleet.Temporal.Activities;

/// <summary>
/// Temporal activity that delegates a task to a fleet agent via RabbitMQ relay.
/// Publishes a directive RelayMessage with a TaskId, then awaits the agent's response
/// through the TaskCompletionRegistry (completed by TemporalRelayListener).
/// </summary>
public sealed class DelegateToAgentActivity
{
    /// <summary>
    /// The Sender field used in directive RelayMessages published by this bridge.
    /// Agents reply to "temporal-bridge" routing key which maps to our listener queue.
    /// </summary>
    private const string BridgeSender = "temporal-bridge";

    private readonly RabbitMqOptions _rabbitConfig;
    private readonly TemporalBridgeOptions _bridgeConfig;
    private readonly TaskCompletionRegistry _registry;
    private readonly ILogger<DelegateToAgentActivity> _logger;

    // RabbitMQ channel is created per-activity-invocation via the factory
    private readonly IConnectionFactory _connectionFactory;
    private readonly IHttpClientFactory _httpClientFactory;

    public DelegateToAgentActivity(
        IOptions<RabbitMqOptions> rabbitConfig,
        IOptions<TemporalBridgeOptions> bridgeConfig,
        TaskCompletionRegistry registry,
        IConnectionFactory connectionFactory,
        IHttpClientFactory httpClientFactory,
        ILogger<DelegateToAgentActivity> logger)
    {
        _rabbitConfig = rabbitConfig.Value;
        _bridgeConfig = bridgeConfig.Value;
        _registry = registry;
        _connectionFactory = connectionFactory;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Delegate a task to a named fleet agent and wait for its response.
    /// </summary>
    /// <param name="agentName">Short agent name.</param>
    /// <param name="instruction">The task instruction to send.</param>
    /// <param name="taskId">
    ///   Correlation ID for this delegation. Callers should pass a stable, unique ID
    ///   (e.g. workflowId/activityId) so Temporal can safely retry without re-registering.
    /// </param>
    /// <param name="retryOnIncomplete">
    ///   When true (default), automatically re-delegates with a continuation prompt if the
    ///   agent returns an incomplete response (context/turn limit hit). The final result's
    ///   Text is the concatenation of all partial responses.
    /// </param>
    /// <param name="maxIncompleteRetries">Maximum continuation attempts (default 3).</param>
    [Activity]
    public async Task<AgentTaskResult> DelegateToAgentAsync(
        string agentName,
        string instruction,
        string taskId,
        bool retryOnIncomplete = true,
        int maxIncompleteRetries = 3)
    {
        var ctx = ActivityExecutionContext.Current;
        _logger.LogInformation(
            "DelegateToAgent: agent={Agent}, taskId={TaskId}, workflowId={WorkflowId}",
            agentName, taskId, ctx.Info.WorkflowId);

        var timeout = TimeSpan.FromSeconds(_bridgeConfig.AgentTimeoutSeconds);

        // Register before publishing — avoids a race where response arrives before TCS is registered
        var tcs = _registry.Register(taskId);

        try
        {
            await PublishDirectiveAsync(agentName, instruction, taskId, _bridgeConfig.GroupChatId, ctx.CancellationToken);
        }
        catch (Exception ex)
        {
            _registry.TryCancel(taskId);
            _logger.LogError(ex, "Failed to publish directive to agent {Agent}", agentName);
            throw;
        }

        _logger.LogInformation("Directive published to {Agent}, awaiting response for taskId={TaskId}", agentName, taskId);

        try
        {
            var result = await WaitForResponseAsync(agentName, tcs, taskId, ctx, timeout, instruction);

            if (!retryOnIncomplete || !result.IsIncomplete)
                return result;

            var accumulatedText = result.Text;

            for (var attempt = 1; attempt <= maxIncompleteRetries; attempt++)
            {
                _logger.LogWarning(
                    "Agent {Agent} returned incomplete response (attempt {Attempt}/{Max}), retrying with continuation prompt. taskId={TaskId}",
                    agentName, attempt, maxIncompleteRetries, taskId);

                var continuationInstruction =
                    $"continue where you left off — your previous response was truncated due to context limits.\n\n" +
                    $"Previous incomplete response:\n{accumulatedText}\n\n" +
                    $"Resume and complete the task.";

                var retryTaskId = $"{taskId}/incomplete-retry-{attempt}";
                var retryTcs = _registry.Register(retryTaskId);

                try
                {
                    await PublishDirectiveAsync(agentName, continuationInstruction, retryTaskId, _bridgeConfig.GroupChatId, ctx.CancellationToken);
                }
                catch (Exception ex)
                {
                    _registry.TryCancel(retryTaskId);
                    _logger.LogError(ex, "Failed to publish continuation directive to agent {Agent} (attempt {Attempt})", agentName, attempt);
                    throw;
                }

                result = await WaitForResponseAsync(agentName, retryTcs, retryTaskId, ctx, timeout, continuationInstruction);
                accumulatedText += "\n" + result.Text;

                if (!result.IsIncomplete)
                    return result with { Text = accumulatedText };
            }

            _logger.LogWarning(
                "Agent {Agent} still incomplete after {Max} retries, returning accumulated text. taskId={TaskId}",
                agentName, maxIncompleteRetries, taskId);
            return result with { Text = accumulatedText };
        }
        catch (OperationCanceledException) when (ctx.CancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Activity cancelled for taskId={TaskId}, sending /cancel to agent {Agent}", taskId, agentName);
            await TrySendCancelAsync(agentName, taskId);
            throw;
        }
        catch (TimeoutException)
        {
            var msg = $"Agent {agentName} did not respond within {(int)timeout.TotalMinutes} minutes (taskId={taskId}, workflowId={ctx.Info.WorkflowId})";
            _logger.LogWarning("{Message}", msg);
            await TryReportTimeoutToActoAsync(agentName, taskId, ctx.Info.WorkflowId, timeout);
            throw new TimeoutException(msg);
        }
    }

    /// <summary>
    /// Waits for an agent response with heartbeating and periodic re-publication.
    /// Throws <see cref="TimeoutException"/> if the configured timeout elapses.
    /// Does NOT call TryReportTimeoutToActoAsync — that is the caller's responsibility
    /// so notification fires only once after all retries are exhausted.
    /// </summary>
    private async Task<AgentTaskResult> WaitForResponseAsync(
        string agentName,
        TaskCompletionSource<AgentTaskResult> tcs,
        string taskId,
        ActivityExecutionContext ctx,
        TimeSpan timeout,
        string instruction)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.CancellationToken);
        timeoutCts.CancelAfter(timeout);

        // When Temporal cancels the activity (workflow cancellation or explicit cancel request),
        // ctx.CancellationToken is signalled. Register a callback that immediately resolves the
        // TCS so Task.WhenAny wakes up and the OperationCanceledException propagates through the
        // existing catch chain, which calls TrySendCancelAsync to stop the agent.
        using var cancellationRegistration = ctx.CancellationToken.Register(
            () => tcs.TrySetCanceled(ctx.CancellationToken));

        try
        {
            var lastPublished = DateTimeOffset.UtcNow;
            var resendInterval = TimeSpan.FromMinutes(5);

            while (true)
            {
                var heartbeatDelay = Task.Delay(TimeSpan.FromSeconds(30), timeoutCts.Token);
                var completed = await Task.WhenAny(tcs.Task, heartbeatDelay);

                if (completed == tcs.Task)
                    return await tcs.Task;

                // Heartbeat so Temporal tracks activity liveness
                ctx.Heartbeat($"waiting for agent {agentName} response (taskId={taskId})");

                // Re-publish directive every 5 min in case agent restarted and lost context.
                // The agent deduplicates via taskId — if it's already running the task it ignores the re-send.
                if (DateTimeOffset.UtcNow - lastPublished >= resendInterval)
                {
                    _logger.LogInformation(
                        "Re-sending directive to {Agent} (taskId={TaskId}) — agent may have restarted",
                        agentName, taskId);
                    try
                    {
                        await PublishDirectiveAsync(agentName, instruction, taskId, _bridgeConfig.GroupChatId, timeoutCts.Token);
                        lastPublished = DateTimeOffset.UtcNow;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to re-send directive to agent {Agent} for taskId={TaskId}", agentName, taskId);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ctx.CancellationToken.IsCancellationRequested)
        {
            _registry.TryCancel(taskId);
            throw; // activity cancellation — caller sends /cancel to the agent
        }
        catch (OperationCanceledException)
        {
            // Timeout CTS fired — convert to TimeoutException so the caller can notify the escalation target once
            _registry.TryCancel(taskId);
            throw new TimeoutException($"Agent {agentName} timed out (taskId={taskId})");
        }
    }

    /// <summary>
    /// Best-effort: notifies the escalation target in the group chat that an agent timed out.
    /// Uses a fresh CancellationToken since the activity's own token is already cancelled.
    /// Swallows exceptions — this is fire-and-forget visibility only.
    /// </summary>
    private async Task TryReportTimeoutToActoAsync(string agentName, string taskId, string workflowId, TimeSpan timeout)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var escalationTarget = FleetWorkflowConfig.Instance.EscalationTarget;
            var report = $"[temporal] activity timeout: agent={agentName} did not respond within {(int)timeout.TotalMinutes}m. taskId={taskId}, workflowId={workflowId}. the workflow will fail this activity.";
            await PublishDirectiveAsync(escalationTarget, report, taskId + "/timeout-report", _bridgeConfig.GroupChatId, cts.Token);
            _logger.LogInformation("Timeout report sent to {EscalationTarget} for agent {Agent}, taskId={TaskId}", escalationTarget, agentName, taskId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send timeout report for agent {Agent}, taskId={TaskId}", agentName, taskId);
        }
    }

    /// <summary>
    /// Best-effort: cancels the specific task on the agent via the orchestrator HTTP API.
    /// Uses POST /api/agents/{name}/cancel/{taskId} which targets only this task,
    /// leaving other running/queued tasks on the agent unaffected.
    /// Falls back to a RabbitMQ /cancel broadcast if the orchestrator URL is not configured.
    /// Uses a fresh CancellationToken since the activity's own token is already cancelled.
    /// Swallows exceptions — the agent will time out on its own if this fails.
    /// </summary>
    private async Task TrySendCancelAsync(string agentName, string taskId)
    {
        // Prefer targeted HTTP cancel via orchestrator if URL is configured
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

        // Fallback: broadcast /cancel via RabbitMQ (cancels all tasks on the agent)
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await PublishDirectiveAsync(agentName, "/cancel all", taskId + "/cancel", _bridgeConfig.GroupChatId, cts.Token);
            _logger.LogInformation("Fallback cancel broadcast sent to agent {Agent} for taskId={TaskId}", agentName, taskId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send fallback cancel to agent {Agent} for taskId={TaskId}", agentName, taskId);
        }
    }

    private async Task PublishDirectiveAsync(string agentName, string instruction, string taskId, long chatId, CancellationToken ct)
    {
        // Prepend the workflow context tag so agents can verify this is a real Temporal delegation.
        // Applied here (rather than in DelegateToAgentAsync) so every publish — initial, re-send,
        // and retry continuations — always carries the tag.
        var workflowTag = string.Empty;
        var memoryHint = string.Empty;
        if (ActivityExecutionContext.Current is { } activityCtx)
        {
            workflowTag = $"[fleet-wf:{activityCtx.Info.WorkflowType}:{activityCtx.Info.WorkflowId}]\n";
            memoryHint = $"\n\nSearch fleet-memory for '{activityCtx.Info.WorkflowType}' to find any operational context before proceeding.";
        }
        instruction = workflowTag + instruction + memoryHint;

        await using var connection = await _connectionFactory.CreateConnectionAsync(ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);

        await channel.ExchangeDeclareAsync(
            _rabbitConfig.Exchange, ExchangeType.Direct, durable: true, autoDelete: false,
            cancellationToken: ct);

        var message = new RelayMessage(
            ChatId: chatId,
            Sender: BridgeSender,
            Text: instruction,
            Timestamp: DateTimeOffset.UtcNow,
            Type: RelayMessageType.Directive,
            TaskId: taskId);

        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
        var props = new BasicProperties { DeliveryMode = DeliveryModes.Persistent };
        var routingKey = agentName.ToLowerInvariant();

        await channel.BasicPublishAsync(
            _rabbitConfig.Exchange, routingKey: routingKey, mandatory: false,
            basicProperties: props, body: body, cancellationToken: ct);

        _logger.LogInformation("Published directive to {Agent} (taskId={TaskId})", agentName, taskId);
    }
}
