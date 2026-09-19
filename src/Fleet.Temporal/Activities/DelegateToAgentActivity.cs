using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Fleet.Temporal.Configuration;
using Fleet.Temporal.Models;
using Fleet.Temporal.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using Temporalio.Activities;
using Temporalio.Exceptions;

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

    // Returns the effective group chat ID: runtime value from PeerConfigClient takes precedence
    // over the compose-env value baked into TemporalBridgeOptions at startup.
    private long EffectiveGroupChatId =>
        FleetWorkflowConfig.GroupChatId != 0 ? FleetWorkflowConfig.GroupChatId : _bridgeConfig.GroupChatId;
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
    /// <param name="agentBudgetSeconds">
    ///   How long the agent may take, in seconds. When greater than zero this is the authoritative
    ///   budget AND the activity verifies at start that it was scheduled with a
    ///   <c>StartToCloseTimeout</c> of at least budget + <see cref="AgentDelegationBudget.StartToCloseMargin"/>,
    ///   failing non-retryably if not.
    ///
    ///   Zero (the default) keeps the historical behaviour for callers that do not supply one:
    ///   the budget comes from <c>TemporalBridge:AgentTimeoutSeconds</c> and no ordering check is
    ///   made. Those callers — notably UWE delegate steps, which size StartToClose from their own
    ///   per-step <c>timeoutMinutes</c> — would otherwise be failed by a guard comparing their
    ///   container against a deployment-wide constant they never asked for.
    /// </param>
    [Activity]
    public async Task<AgentTaskResult> DelegateToAgentAsync(
        string agentName,
        string instruction,
        string taskId,
        bool retryOnIncomplete = true,
        int maxIncompleteRetries = 3,
        int agentBudgetSeconds = 0)
    {
        var ctx = ActivityExecutionContext.Current;
        _logger.LogInformation(
            "DelegateToAgent: agent={Agent}, taskId={TaskId}, workflowId={WorkflowId}, budgetSeconds={Budget}",
            agentName, taskId, ctx.Info.WorkflowId, agentBudgetSeconds);

        var budgetIsExplicit = agentBudgetSeconds > 0;
        var timeout = budgetIsExplicit
            ? TimeSpan.FromSeconds(agentBudgetSeconds)
            : TimeSpan.FromSeconds(_bridgeConfig.AgentTimeoutSeconds);

        if (budgetIsExplicit)
            GuardTimerOrdering(ctx, timeout, agentName, taskId);

        // Register before publishing — avoids a race where response arrives before TCS is registered
        var tcs = _registry.Register(taskId);

        try
        {
            await PublishDirectiveAsync(agentName, instruction, taskId, EffectiveGroupChatId, ctx.CancellationToken);
        }
        catch (Exception ex)
        {
            _registry.TryCancel(taskId, tcs);
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
                    await PublishDirectiveAsync(agentName, continuationInstruction, retryTaskId, EffectiveGroupChatId, ctx.CancellationToken);
                }
                catch (Exception ex)
                {
                    _registry.TryCancel(retryTaskId, retryTcs);
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
        catch (TimeoutException ex)
        {
            // The message is built where the elapsed time is actually measured, so the number
            // reported is a measurement rather than a restatement of the configured budget.
            _logger.LogWarning("{Message}", ex.Message);
            await TryReportTimeoutToActoAsync(agentName, taskId, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Fails non-retryably when the activity was scheduled inside a container shorter than the
    /// budget it is being asked to enforce.
    ///
    /// Without this, the two timers can be ordered backwards — Temporal kills the attempt while
    /// the agent is still working, the activity never reaches its own timeout path, and nothing
    /// in the failure says why. A grep over workflow files would not catch a JSON-defined caller;
    /// checking the container the SDK actually reports does.
    /// </summary>
    private void GuardTimerOrdering(ActivityExecutionContext ctx, TimeSpan budget, string agentName, string taskId)
    {
        var required = AgentDelegationBudget.StartToCloseFor(budget);
        var scheduled = ctx.Info.StartToCloseTimeout;

        if (scheduled is null)
        {
            // Only ScheduleToClose was set. Nothing to compare against, and refusing would break a
            // legitimate caller, so record it and continue.
            _logger.LogWarning(
                "DelegateToAgent scheduled without a StartToCloseTimeout (agent={Agent}, taskId={TaskId}) — cannot verify it outlasts the {Budget} agent budget",
                agentName, taskId, budget);
            return;
        }

        if (scheduled >= required) return;

        throw new ApplicationFailureException(
            $"DelegateToAgent for agent {agentName} was scheduled with StartToCloseTimeout {scheduled.Value} " +
            $"but was given a {budget} agent budget, which needs at least {required} " +
            $"(budget + {AgentDelegationBudget.StartToCloseMargin} margin). Temporal would kill this attempt " +
            $"while the agent is still working. Fix the caller's ActivityOptions, not this budget. " +
            $"(taskId={taskId}, workflowId={ctx.Info.WorkflowId})",
            errorType: "MisorderedAgentTimeouts",
            nonRetryable: true);
    }

    /// <summary>
    /// Waits for an agent response with heartbeating and periodic re-publication.
    /// Throws <see cref="TimeoutException"/> if the agent budget elapses, carrying the MEASURED
    /// elapsed time. Does NOT call TryReportTimeoutToActoAsync — that is the caller's
    /// responsibility so notification fires only once after all retries are exhausted.
    /// </summary>
    private async Task<AgentTaskResult> WaitForResponseAsync(
        string agentName,
        TaskCompletionSource<AgentTaskResult> tcs,
        string taskId,
        ActivityExecutionContext ctx,
        TimeSpan timeout,
        string instruction)
    {
        var started = Stopwatch.StartNew();

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

                // The delay lost the race, which means either the heartbeat interval elapsed or
                // the token was cancelled. Task.WhenAny does NOT throw on a cancelled task, so
                // this check is what distinguishes the two.
                //
                // Without it, a fired timeout leaves Task.Delay returning an already-cancelled
                // task on every subsequent iteration and the loop spins with no delay at all —
                // burning a core until the 5-minute re-publish branch below finally throws. The
                // timeout was still reported eventually, so results stayed correct while a hot
                // loop ran for up to five minutes per timed-out activity (issue #251).
                //
                // Throwing here routes into the existing catch chain: activity cancellation is
                // caught by the ctx.CancellationToken filter, a bare timeout by the one after it
                // that converts to TimeoutException.
                timeoutCts.Token.ThrowIfCancellationRequested();

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
                        await PublishDirectiveAsync(agentName, instruction, taskId, EffectiveGroupChatId, timeoutCts.Token);
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
            _registry.TryCancel(taskId, tcs);
            throw; // activity cancellation — caller sends /cancel to the agent
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            // Our own budget elapsed — convert to TimeoutException so the caller can notify the
            // escalation target once. The elapsed value is measured here; the budget is labelled
            // as a budget so the two can never be read as the same thing.
            _registry.TryCancel(taskId, tcs);
            throw new TimeoutException(
                $"Agent {agentName} did not respond — elapsed {Format(started.Elapsed)} " +
                $"(budget {Format(timeout)}, taskId={taskId}, workflowId={ctx.Info.WorkflowId})");
        }
        catch (OperationCanceledException)
        {
            // Neither this activity's cancellation nor its own budget: the completion source was
            // cancelled by something else — registry shutdown, or (before compare-and-remove
            // landed) a stale attempt's cleanup taking down its successor. Reporting a timeout
            // here is what produced "did not respond within 90 minutes" about thirty seconds into
            // an attempt, and sent the first investigation of #321 the wrong way.
            _registry.TryCancel(taskId, tcs);
            throw new ApplicationFailureException(
                $"Delegation to agent {agentName} was aborted before any response — elapsed " +
                $"{Format(started.Elapsed)}, well inside its {Format(timeout)} budget. This is not a " +
                $"timeout: the pending registration was cancelled by something other than this " +
                $"activity. (taskId={taskId}, workflowId={ctx.Info.WorkflowId})",
                errorType: "DelegationAborted");
        }
    }

    /// <summary>Compact, honest duration rendering — sub-minute values keep their seconds.</summary>
    private static string Format(TimeSpan span) =>
        span.TotalMinutes >= 1
            ? $"{span.TotalMinutes:0.0}m"
            : $"{span.TotalSeconds:0.0}s";

    /// <summary>
    /// Best-effort: notifies the escalation target in the group chat that an agent timed out.
    /// Uses a fresh CancellationToken since the activity's own token is already cancelled.
    /// Swallows exceptions — this is fire-and-forget visibility only.
    /// </summary>
    /// <param name="detail">
    /// The timeout exception's own message, so the elapsed time the escalation target reads is
    /// the same measured number the workflow failure carries.
    /// </param>
    private async Task TryReportTimeoutToActoAsync(string agentName, string taskId, string detail)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var escalationTarget = FleetWorkflowConfig.Instance.EscalationTarget;
            var report = $"[temporal] activity timeout: {detail}. the workflow will fail this activity.";
            await PublishDirectiveAsync(escalationTarget, report, taskId + "/timeout-report", EffectiveGroupChatId, cts.Token);
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
            await PublishDirectiveAsync(agentName, "/cancel all", taskId + "/cancel", EffectiveGroupChatId, cts.Token);
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
