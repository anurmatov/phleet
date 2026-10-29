using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fleet.Temporal.Configuration;
using Fleet.Temporal.Models;
using Fleet.Temporal.Services;
using Fleet.Shared.Security;
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
    /// The activity type name. Workflows that schedule this activity by name use it so the
    /// argument list they send is spelled out rather than filled in by the compiler (see
    /// <see cref="DelegateToAgentAsync"/>'s <c>repo</c> parameter).
    /// </summary>
    public const string ActivityName = "DelegateToAgent";

    /// <summary>
    /// The Sender field used in directive RelayMessages published by this bridge.
    /// Agents reply to "temporal-bridge" routing key which maps to our listener queue.
    /// </summary>
    private const string BridgeSender = "temporal-bridge";

    /// <summary>
    /// <c>owner/name</c>, the same rule the orchestrator applies to <c>repo</c> routes (#347).
    /// Matched against the trimmed value, so the <c>$</c>-before-final-newline quirk cannot let a
    /// trailing line break through.
    /// </summary>
    private static readonly Regex RepoPattern = new(
        "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant);

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
    /// <param name="repo">
    ///   Optional <c>owner/name</c> of the repository this delegation is about (#347). It is put on
    ///   every directive of this delegation — the first publish, re-sends and continuation retries —
    ///   as <see cref="RelayMessage.Repo"/>, so the agent can route project context by it. Blank
    ///   means "no repo signal". A value that is not <c>owner/name</c> is dropped with a Warning and
    ///   the delegation still goes out: a typo in a definition must not fail the step.
    ///
    ///   Only UWE <c>delegate</c> steps that set <c>repo</c> pass it, and they append it (after an
    ///   explicit <c>agentBudgetSeconds</c> of 0) only when it resolves non-empty. Every other
    ///   caller schedules by name with an explicit argument list, because an expression-tree call
    ///   would have the compiler append this parameter's default and change the activity input of
    ///   workflows that never asked for it.
    /// </param>
    [Activity(ActivityName)]
    public async Task<AgentTaskResult> DelegateToAgentAsync(
        string agentName,
        string instruction,
        string taskId,
        bool retryOnIncomplete = true,
        int maxIncompleteRetries = 3,
        int agentBudgetSeconds = 0,
        string? repo = null)
    {
        var ctx = ActivityExecutionContext.Current;
        repo = ValidateRepo(repo, agentName, taskId);
        _logger.LogInformation(
            "DelegateToAgent: agent={Agent}, taskId={TaskId}, workflowId={WorkflowId}, budgetSeconds={Budget}, repo={Repo}",
            agentName, taskId, ctx.Info.WorkflowId, agentBudgetSeconds, repo ?? "-");

        var budgetIsExplicit = agentBudgetSeconds > 0;
        var timeout = budgetIsExplicit
            ? TimeSpan.FromSeconds(agentBudgetSeconds)
            : TimeSpan.FromSeconds(_bridgeConfig.AgentTimeoutSeconds);

        if (budgetIsExplicit)
            GuardTimerOrdering(ctx, timeout, agentName, taskId);

        var managed = await GuardManagedTargetAsync(agentName, taskId, ctx.CancellationToken);

        // Register before publishing — avoids a race where response arrives before TCS is registered
        var tcs = _registry.Register(taskId, managed?.Correlation);

        try
        {
            await PublishDirectiveAsync(agentName, instruction, taskId, EffectiveGroupChatId, repo, ctx.CancellationToken, managed);
        }
        catch (Exception ex)
        {
            // A managed session is deliberately retained: the directive may have left before the failure was
            // reported, and the retry must register for the same session to accept its reply (#492 R5).
            _registry.TryCancel(taskId, tcs);
            _logger.LogError(ex, "Failed to publish directive to agent {Agent}", agentName);
            throw;
        }

        _logger.LogInformation("Directive published to {Agent}, awaiting response for taskId={TaskId}", agentName, taskId);

        try
        {
            var result = await WaitForResponseAsync(agentName, tcs, taskId, ctx, timeout, instruction, repo, managed);

            if (!retryOnIncomplete || !result.IsIncomplete)
                return Released(result);

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
                var retryTcs = _registry.Register(retryTaskId, managed?.Correlation);

                try
                {
                    await PublishDirectiveAsync(agentName, continuationInstruction, retryTaskId, EffectiveGroupChatId, repo, ctx.CancellationToken, managed);
                }
                catch (Exception ex)
                {
                    _registry.TryCancel(retryTaskId, retryTcs);
                    _logger.LogError(ex, "Failed to publish continuation directive to agent {Agent} (attempt {Attempt})", agentName, attempt);
                    throw;
                }

                result = await WaitForResponseAsync(agentName, retryTcs, retryTaskId, ctx, timeout, continuationInstruction, repo, managed);
                accumulatedText += "\n" + result.Text;

                if (!result.IsIncomplete)
                    return Released(result with { Text = accumulatedText });
            }

            _logger.LogWarning(
                "Agent {Agent} still incomplete after {Max} retries, returning accumulated text. taskId={TaskId}",
                agentName, maxIncompleteRetries, taskId);
            return Released(result with { Text = accumulatedText });
        }
        catch (OperationCanceledException) when (ctx.CancellationToken.IsCancellationRequested)
        {
            ReleaseSession();
            _logger.LogWarning("Activity cancelled for taskId={TaskId}, sending /cancel to agent {Agent}", taskId, agentName);
            await TrySendCancelAsync(agentName, taskId, managed);
            throw;
        }
        catch (TimeoutException ex)
        {
            ReleaseSession();
            // The message is built where the elapsed time is actually measured, so the number
            // reported is a measurement rather than a restatement of the configured budget.
            _logger.LogWarning("{Message}", ex.Message);
            await TryReportTimeoutToActoAsync(agentName, taskId, ex.Message);
            throw;
        }
        catch (ApplicationFailureException ex) when (managed is not null && IsManagedRefusal(ex))
        {
            // A refused resend (epoch changed or revoked) is final: the activity fails non-retryably.
            ReleaseSession();
            throw;
        }

        // Completion, cancellation, timeout and a refused resend end the managed session; compare-and-release leaves a session a
        // later attempt re-selected for a different epoch alone.
        void ReleaseSession()
        {
            if (managed is not null) _registry.ReleaseSession(taskId, managed.Correlation);
        }

        AgentTaskResult Released(AgentTaskResult final)
        {
            ReleaseSession();
            return final;
        }
    }

    /// <summary>
    /// Returns the repo to put on the relay message, or null. Blank is simply "no repo signal"
    /// and stays silent; anything else that is not <c>owner/name</c> is dropped with a Warning.
    /// </summary>
    private string? ValidateRepo(string? repo, string agentName, string taskId)
    {
        if (string.IsNullOrWhiteSpace(repo)) return null;

        var trimmed = repo.Trim();
        if (RepoPattern.IsMatch(trimmed)) return trimmed;

        _logger.LogWarning(
            "DelegateToAgent: ignoring malformed repo {Repo} (expected owner/name) — the directive goes out without a repo signal. agent={Agent}, taskId={TaskId}",
            trimmed.Length > 80 ? trimmed[..80] : trimmed, agentName, taskId);
        return null;
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

    /// <summary>Named refusal when a managed agent's selected authority record is stale, revoked, foreign or unreadable.</summary>
    public const string ManagedTargetDenied = "managed_route_denied:workflow_delegation";

    /// <summary>Named refusal when the managed-runtime check cannot tell what the target runs on.</summary>
    public const string ManagedTargetUnknown = "managed_route_unknown:workflow_delegation";

    /// <summary>Named refusal when managed mode is enabled but Temporal has no usable authority directory (#492 W3).</summary>
    public const string ManagedAuthorityUnconfigured = "managed_route_unconfigured:workflow_delegation";

    /// <summary>The selected epoch control identity and expected reply correlation for a managed delegation (#492).</summary>
    internal sealed record ManagedTarget(StockBrokerIdentity Identity, ManagedCorrelation Correlation);

    /// <summary>Opens the producer-only epoch control connection; replaceable by tests.</summary>
    internal Func<ConnectionFactory, CancellationToken, Task<IConnection>> ControlConnect { get; set; } =
        (factory, ct) => factory.CreateConnectionAsync(ct);

    /// <summary>Heartbeat cadence while waiting for a reply; replaceable by tests.</summary>
    internal TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long after the last publish the directive is re-sent; replaceable by tests.</summary>
    internal TimeSpan ResendInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>True for the named non-retryable refusal of a managed control lane whose epoch changed or was revoked.</summary>
    private static bool IsManagedRefusal(ApplicationFailureException ex) =>
        ex.NonRetryable && ex.ErrorType == "ManagedAgentContextDenied";

    /// <summary>
    /// Selects the route for a workflow directive (#492). Managed mode is off unless
    /// <c>StockOpenShell:ManagedEnabled</c> is true: then nothing here runs, the orchestrator is never
    /// consulted and an orchestrator outage cannot fail an ordinary delegation. With managed mode on, a managed
    /// target needs a usable <c>StockOpenShell:AuthorityDirectory</c>; without one the attempt is refused by name
    /// (<see cref="ManagedAuthorityUnconfigured"/>), never published on the ordinary broker.
    ///
    /// With managed mode on the orchestrator's applied runtime decides, failing closed: an unreachable
    /// orchestrator, a non-success status, an invalid body or an unknown runtime refuses this attempt
    /// (retryably) instead of publishing on the ordinary broker. <c>docker</c> keeps the ordinary path.
    /// <c>openshell</c> selects the agent's active authority record and its epoch <c>control</c> credential
    /// (never admin or observer); the directive then goes over that epoch's existing control lane, and the
    /// reply comes back through the Bridge's managed lane, the sole consumer of the epoch reply queue, to the
    /// ordinary Temporal receiver, where only a reply carrying the registered agent, sandbox, epoch and
    /// session completes the TaskId. A missing, revoked, ambiguous or unreadable record refuses
    /// non-retryably, never falling back to the ordinary broker.
    /// </summary>
    private async Task<ManagedTarget?> GuardManagedTargetAsync(string agentName, string taskId, CancellationToken ct)
    {
        if (!_bridgeConfig.StockManagedEnabled) return null;
        var directory = _bridgeConfig.StockAuthorityDirectory;
        string? applied = null;
        string? fault = null;
        if (string.IsNullOrEmpty(_bridgeConfig.OrchestratorUrl)) fault = "no orchestrator";
        else
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                var client = _httpClientFactory.CreateClient("orchestrator-cancel");
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    $"{_bridgeConfig.OrchestratorUrl.TrimEnd('/')}/api/agents/{Uri.EscapeDataString(agentName)}");
                if (!string.IsNullOrEmpty(_bridgeConfig.OrchestratorAuthToken))
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _bridgeConfig.OrchestratorAuthToken);
                using var response = await client.SendAsync(request, cts.Token);
                applied = response.IsSuccessStatusCode
                    ? AppliedRuntime(await response.Content.ReadAsStringAsync(cts.Token))
                    : null;
                if (!response.IsSuccessStatusCode) fault = $"status {(int)response.StatusCode}";
                else if (applied is null) fault = "invalid agent state";
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException)
            {
                applied = null;
                fault = ex.GetType().Name;
            }

        if (applied == "docker") return null;
        if (applied == "openshell")
        {
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory) || !Directory.Exists(directory))
            {
                _logger.LogError("DelegateToAgent refused: managed mode is enabled but StockOpenShell:AuthorityDirectory is " +
                    "missing or invalid ({Code}), agent={Agent}, taskId={TaskId}", ManagedAuthorityUnconfigured, agentName, taskId);
                throw new ApplicationFailureException(
                    $"{ManagedAuthorityUnconfigured}: agent {agentName} runs on the managed stock OpenShell runtime but this " +
                    $"Temporal bridge has no usable StockOpenShell:AuthorityDirectory; the directive was not published. (taskId={taskId})",
                    errorType: "ManagedAuthorityUnconfigured",
                    nonRetryable: true);
            }
            return SelectManagedTarget(directory, agentName, taskId);
        }

        fault ??= $"applied runtime '{Truncate(applied, 16)}'";
        _logger.LogError("DelegateToAgent refused: runtime of {Agent} could not be determined ({Fault}, {Code}), taskId={TaskId}",
            agentName, fault, ManagedTargetUnknown, taskId);
        // Retryable: an orchestrator outage is transient, and a retry repeats this check before any publish.
        throw new ApplicationFailureException(
            $"{ManagedTargetUnknown}: the runtime of agent {agentName} could not be determined ({fault}); the directive was " +
            $"not published on the ordinary broker. (taskId={taskId})",
            errorType: "ManagedAgentRuntimeUnknown");
    }

    private ManagedTarget SelectManagedTarget(string directory, string agentName, string taskId)
    {
        var agent = agentName.ToLowerInvariant();
        try
        {
            if (!StockAuthoritySource.ValidAgent(agent)) throw new StockIdentityDenied("authority_mapping_invalid");
            var record = StockAuthoritySource.Active(directory).GetValueOrDefault(agent)
                ?? throw new StockIdentityDenied("authority_mapping_missing");
            var identity = new StockBrokerIdentity(record.Path, StockAuthoritySource.RolePassword(directory, agent, "control"), "control");
            if (identity.Agent != agent) throw new StockIdentityDenied("authority_mapping_invalid");
            // A retry of this TaskId on the same epoch reuses the retained session (#492 R5).
            return new(identity, _registry.SessionFor(taskId, identity.Agent, identity.Sandbox, identity.Epoch));
        }
        catch (StockIdentityDenied)
        {
            _logger.LogError("DelegateToAgent refused: no current managed authority for {Agent} ({Code}), taskId={TaskId}",
                agentName, ManagedTargetDenied, taskId);
            throw new ApplicationFailureException(
                $"{ManagedTargetDenied}: agent {agentName} runs on the managed stock OpenShell runtime but has no current " +
                $"authority record or control credential; the directive was not published. (taskId={taskId})",
                errorType: "ManagedAgentContextDenied",
                nonRetryable: true);
        }
    }

    /// <summary><c>appliedRuntimeKind</c> of an orchestrator agent body, or null when the body is not a valid agent state.</summary>
    private static string? AppliedRuntime(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            string? applied = null;
            foreach (var property in document.RootElement.EnumerateObject())
                if (string.Equals(property.Name, "appliedRuntimeKind", StringComparison.OrdinalIgnoreCase))
                {
                    if (applied is not null || property.Value.ValueKind != JsonValueKind.String) return null; // duplicate or non-string
                    applied = property.Value.GetString();
                }
            return applied;
        }
        catch (JsonException) { return null; }
    }

    private static string Truncate(string? value, int max) =>
        value is null ? "" : value.Length <= max ? value : value[..max];

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
        string instruction,
        string? repo,
        ManagedTarget? managed)
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
            var resendInterval = ResendInterval;

            while (true)
            {
                var heartbeatDelay = Task.Delay(HeartbeatInterval, timeoutCts.Token);
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
                        await PublishDirectiveAsync(agentName, instruction, taskId, EffectiveGroupChatId, repo, timeoutCts.Token, managed);
                        lastPublished = DateTimeOffset.UtcNow;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (ApplicationFailureException ex) when (managed is not null && IsManagedRefusal(ex))
                    {
                        // The record's epoch changed or was revoked: no reply can arrive on this lane, so the
                        // named refusal fails the activity now instead of waiting silently for the timeout.
                        _registry.TryCancel(taskId, tcs);
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
            await PublishDirectiveAsync(escalationTarget, report, taskId + "/timeout-report", EffectiveGroupChatId, repo: null, cts.Token);
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
    private async Task TrySendCancelAsync(string agentName, string taskId, ManagedTarget? managed)
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

        // A managed agent never reads the ordinary broker; its cancel is the orchestrator's managed loopback cancel.
        if (managed is not null)
        {
            _logger.LogWarning("No ordinary cancel fallback for managed agent {Agent}, taskId={TaskId}", agentName, taskId);
            return;
        }

        // Fallback: broadcast /cancel via RabbitMQ (cancels all tasks on the agent)
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await PublishDirectiveAsync(agentName, "/cancel all", taskId + "/cancel", EffectiveGroupChatId, repo: null, cts.Token);
            _logger.LogInformation("Fallback cancel broadcast sent to agent {Agent} for taskId={TaskId}", agentName, taskId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send fallback cancel to agent {Agent} for taskId={TaskId}", agentName, taskId);
        }
    }

    /// <param name="repo">
    /// Already validated by <see cref="ValidateRepo"/>. Only the delegation's own directives carry
    /// it; the timeout report and the cancel fallback are not about the repository and pass null.
    /// </param>
    private async Task PublishDirectiveAsync(
        string agentName, string instruction, string taskId, long chatId, string? repo, CancellationToken ct,
        ManagedTarget? managed = null)
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

        if (managed is not null)
        {
            await PublishManagedDirectiveAsync(managed, instruction, taskId, chatId, repo, ct);
            return;
        }

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

    /// <summary>
    /// Publishes a typed workflow directive on the selected epoch's control lane (#492): a producer-only
    /// connection as the epoch <c>control</c> principal (validated user-id) to <c>fleet.group</c>, routed to the
    /// agent, carrying agent (sender route), sandbox, epoch, TaskId and the session nonce. Declares nothing.
    /// </summary>
    private async Task PublishManagedDirectiveAsync(
        ManagedTarget managed, string instruction, string taskId, long chatId, string? repo, CancellationToken ct)
    {
        var identity = managed.Identity;
        var factory = new ConnectionFactory { HostName = identity.BrokerHost, ClientProvidedName = "fleet-temporal-control" };
        BasicProperties props;
        try
        {
            identity.Configure(factory); // rechecks the current record; a changed or revoked epoch refuses here
            props = identity.Properties();
        }
        catch (StockIdentityDenied)
        {
            _logger.LogError("Managed workflow directive refused: control identity for {Agent} is not current ({Code}), taskId={TaskId}",
                identity.Agent, ManagedTargetDenied, taskId);
            throw new ApplicationFailureException(
                $"{ManagedTargetDenied}: the control lane of agent {identity.Agent} is stale, revoked or has no usable control " +
                $"credential; the directive was not published. (taskId={taskId})",
                errorType: "ManagedAgentContextDenied",
                nonRetryable: true);
        }
        await using var connection = await ControlConnect(factory, ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);

        // The activity's own StartToClose deadline (#492 X4): the agent keeps the reply session until then even
        // when no resend can be published (a broker or publish outage), since only this activity's end makes
        // the reply unwanted. Every publish (initial, resend, continuation) carries the same deadline.
        DateTimeOffset? deadline = ActivityExecutionContext.HasCurrent
            && ActivityExecutionContext.Current.Info.StartToCloseTimeout is { } startToClose
            ? new DateTimeOffset(DateTime.SpecifyKind(ActivityExecutionContext.Current.Info.StartedTime, DateTimeKind.Utc)) + startToClose
            : null;

        var message = new RelayMessage(
            ChatId: chatId,
            Sender: BridgeSender,
            Text: instruction,
            Timestamp: DateTimeOffset.UtcNow,
            Type: RelayMessageType.WorkflowDirective,
            CorrelationId: managed.Correlation.Session,
            TaskId: taskId,
            Repo: repo,
            Epoch: identity.Epoch,
            Sandbox: identity.Sandbox,
            Deadline: deadline);

        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
        await channel.BasicPublishAsync(
            _rabbitConfig.Exchange, routingKey: identity.Agent, mandatory: false,
            basicProperties: props, body: body, cancellationToken: ct);

        _logger.LogInformation("Published managed workflow directive to {Agent} (taskId={TaskId})", identity.Agent, taskId);
    }
}
