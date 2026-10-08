using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Fleet.Temporal.Configuration;
using ModelContextProtocol.Server;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;

namespace Fleet.Temporal.Mcp;

[McpServerToolType]
public sealed class TemporalWorkflowTools(
    ITemporalClientFactory clientFactory,
    WorkflowTypeRegistry registry,
    CtoAgentConfigService ctoAgentConfig,
    IWorkflowGateStateReader gateStateReader,
    IEpicGrantDecisionForwarder grantForwarder,
    IHttpContextAccessor httpContextAccessor,
    ILogger<TemporalWorkflowTools> logger)
{
    private const string DefaultNamespace = "fleet";
    private const string MergeApprovalSignal = "merge-approval";
    private const string DesignApprovalSignal = "design-approval";
    private const string DocReviewSignal = "doc-review";
    private const string ChangesRequestedDecision = "changes_requested";
    private const string ApprovedDecision = "approved";
    private const string GrantIdField = "GrantId";

    /// <summary>
    /// Gates on which the configured CTO agent may send <c>approved</c> with a <c>GrantId</c> (#436).
    /// This tool never signals such a payload: it forwards it to the orchestrator, which decides it
    /// under an active epic grant and sends the signal itself, or sends nothing.
    ///
    /// Like <see cref="FeedbackEligibleGates"/>, deliberately private to this MCP tool and NOT a
    /// change to <see cref="CeoGateSignals"/>. <c>advisory-review</c> is never delegable.
    /// </summary>
    private static readonly string[] DelegableGates = [MergeApprovalSignal, DesignApprovalSignal, DocReviewSignal];

    /// <summary>
    /// CEO-only gates that the configured CTO agent may return for revision through this tool, and
    /// only with the exact changes-requested payload (#259, #430).
    ///
    /// Deliberately private to this MCP tool and NOT a member of <see cref="CeoGateSignals"/>:
    /// definitions are agent-authored, so any engine path to a reserved signal would let an agent
    /// approve its own work (#280).
    /// </summary>
    private static readonly string[] FeedbackEligibleGates = [MergeApprovalSignal, DesignApprovalSignal];

    /// <summary>
    /// Signals that are exclusively for CEO approval, apart from the narrow CTO changes-requested exception.
    /// All other uses of these gates must go through the fleet dashboard (orchestrator REST API), which is auth-gated.
    ///
    /// The names live in <see cref="CeoGateSignals"/> because the workflow engine enforces the same
    /// list against <c>signal_workflow</c> steps — two copies of a security list is a list that
    /// drifts, and the drift would be silent until someone approved their own work through the
    /// half that was not updated (#280).
    /// </summary>
    private static bool IsCeoOnlySignal(string signalName) => CeoGateSignals.IsReserved(signalName);

    [McpServerTool(Name = "temporal_start_workflow")]
    [Description("Start a new Temporal workflow execution. Returns workflowId and runId. IMPORTANT: use 'input' (not 'args') to pass workflow arguments — 'args' is for temporal_signal_workflow only.")]
    public async Task<string> StartWorkflowAsync(
        [Description("Registered workflow type name (e.g. ConsensusReviewWorkflow, AuthTokenRefreshWorkflow)")] string workflow_type,
        [Description("Optional custom workflow ID. Auto-generated as '{type}-{timestamp}' if omitted.")] string? workflow_id = null,
        [Description("Workflow-specific input as a JSON object string. Schema depends on workflow type — use temporal_list_workflow_types to check. NOTE: this parameter is called 'input', not 'args'.")] string? input = null,
        [Description("Task queue name. Defaults to namespace name if omitted.")] string? task_queue = null,
        [Description("Temporal namespace. Default: fleet.")] string @namespace = DefaultNamespace)
    {
        task_queue ??= @namespace;
        var id = workflow_id ?? $"{workflow_type}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

        object? inputObj = null;
        if (!string.IsNullOrWhiteSpace(input))
        {
            try
            {
                inputObj = JsonSerializer.Deserialize<JsonElement>(input);
            }
            catch (JsonException ex)
            {
                return $"Error: invalid JSON in input — {ex.Message}";
            }
        }

        // Validate: if this workflow type is registered and has required fields, reject empty input early.
        if (inputObj is null)
        {
            var allTypes = await registry.GetAllAsync();
            var knownType = allTypes.FirstOrDefault(t =>
                string.Equals(t.Name, workflow_type, StringComparison.OrdinalIgnoreCase));
            if (knownType is not null)
            {
                var requiredFields = GetRequiredFields(knownType.InputSchema);
                if (requiredFields.Count > 0)
                {
                    return $"Error: workflow '{workflow_type}' requires input but none was provided. " +
                           $"Required fields: {string.Join(", ", requiredFields)}. " +
                           $"Pass workflow arguments via the 'input' parameter (not 'args' — that is for temporal_signal_workflow). " +
                           $"Use temporal_list_workflow_types to see the full input schema.";
                }
            }
        }

        try
        {
            var client = await clientFactory.GetClientAsync(@namespace);
            var handle = await client.StartWorkflowAsync(
                workflow_type,
                inputObj is null ? [] : [inputObj],
                new WorkflowOptions(id: id, taskQueue: task_queue));

            return JsonSerializer.Serialize(new
            {
                workflowId = handle.Id,
                runId = handle.ResultRunId,
                workflowType = workflow_type,
                @namespace,
                status = "started"
            });
        }
        catch (Exception ex)
        {
            return $"Error starting workflow: {ex.Message}";
        }
    }

    [McpServerTool(Name = "temporal_get_workflow_status")]
    [Description("Get current status and metadata of a workflow execution.")]
    public async Task<string> GetWorkflowStatusAsync(
        [Description("Workflow ID to query")] string workflow_id,
        [Description("Temporal namespace. Default: fleet.")] string @namespace = DefaultNamespace)
    {
        try
        {
            var client = await clientFactory.GetClientAsync(@namespace);
            var handle = client.GetWorkflowHandle(workflow_id);
            var desc = await handle.DescribeAsync();

            return JsonSerializer.Serialize(new
            {
                workflowId = desc.Id,
                runId = desc.RunId,
                workflowType = desc.WorkflowType,
                status = desc.Status.ToString(),
                startTime = desc.StartTime,
                closeTime = desc.CloseTime,
                historyLength = desc.HistoryLength
            });
        }
        catch (Exception ex)
        {
            return $"Error getting workflow status: {ex.Message}";
        }
    }

    [McpServerTool(Name = "temporal_list_workflows")]
    [Description("List workflow executions with optional filtering by status or Temporal visibility query.")]
    public async Task<string> ListWorkflowsAsync(
        [Description("Optional Temporal visibility query (e.g. WorkflowType=\"UwePrImplementationWorkflow\")")] string? query = null,
        [Description("Filter by status: running, completed, failed, cancelled, terminated")] string? status = null,
        [Description("Maximum number of results to return. Default: 20")] int limit = 20,
        [Description("Temporal namespace. Default: fleet.")] string @namespace = DefaultNamespace)
    {
        try
        {
            var client = await clientFactory.GetClientAsync(@namespace);
            var visibilityQuery = BuildVisibilityQuery(query, status);

            var results = new List<object>();
            await foreach (var wf in client.ListWorkflowsAsync(visibilityQuery))
            {
                results.Add(new
                {
                    workflowId = wf.Id,
                    runId = wf.RunId,
                    workflowType = wf.WorkflowType,
                    status = wf.Status.ToString(),
                    startTime = wf.StartTime
                });

                if (results.Count >= limit)
                    break;
            }

            return JsonSerializer.Serialize(results);
        }
        catch (Exception ex)
        {
            return $"Error listing workflows: {ex.Message}";
        }
    }

    [McpServerTool(Name = "temporal_signal_workflow")]
    [Description(
        "Send a signal to a running workflow. " +
        "Signal reference for UwePrImplementationWorkflow: " +
        "(1) 'human-review' — structured payload required: {\"Decision\":\"approved\"} or {\"Decision\":\"changes_requested\",\"Comment\":\"feedback\"} or {\"Decision\":\"rejected\",\"Comment\":\"reason\"}. " +
        "(2) 'escalation-decision' — {\"Decision\":\"retry|skip|continue\",\"UpdatedInstruction\":\"...\"}. " +
        "IMPORTANT — CEO-only signals are BLOCKED and cannot be sent via this tool: " +
        "'merge-approval', 'doc-review', 'design-approval', 'advisory-review'. " +
        "There are two exceptions, both only for the currently configured CTO agent resolved from the MCP request. " +
        "(a) Feedback: 'merge-approval' or 'design-approval' with Decision exactly 'changes_requested' and a nonblank Comment. " +
        "'design-approval' feedback is accepted only while the workflow is Running and waiting at its design-approval gate (Phase 'design-approval'); otherwise it is refused and nothing is sent. " +
        "(b) Delegated approval under an epic grant: 'merge-approval', 'design-approval' or 'doc-review' with {\"Decision\":\"approved\",\"GrantId\":\"<grant id>\",\"VisitId\":\"<GateVisit>\",\"ArtifactRef\":\"<ReviewRef>\",\"Evidence\":\"<https url>\"}. " +
        "This tool does not signal it: it is forwarded to the orchestrator, which decides it under an active epic grant and sends nothing on refusal. The tool returns status 'delegated' with the orchestrator's result and reason. " +
        "Approval without a GrantId, and every rejection, remain CEO-only; 'advisory-review' has no exception. All other CEO-only gate decisions must be sent from the fleet dashboard by the CEO.")]
    public async Task<string> SignalWorkflowAsync(
        [Description("Workflow ID to signal")] string workflow_id,
        [Description("Signal name (e.g. human-review, merge-approval)")] string signal_name,
        [Description("Signal payload as a JSON object string. For 'human-review': {\"Decision\":\"approved\"} or {\"Decision\":\"changes_requested\",\"Comment\":\"your feedback\"}. For the CTO 'merge-approval' or 'design-approval' exception: {\"Decision\":\"changes_requested\",\"Comment\":\"required feedback\"}. For a CTO delegated approval under an epic grant: {\"Decision\":\"approved\",\"GrantId\":\"<grant id>\",\"VisitId\":\"<GateVisit>\",\"ArtifactRef\":\"<ReviewRef>\",\"Evidence\":\"<https url>\"}.")] string? args = null,
        [Description("Temporal namespace. Default: fleet.")] string @namespace = DefaultNamespace)
    {
        // #436 delegated approval. Only the exact delegated shape enters this branch; every other
        // payload, including a CTO 'approved' without a GrantId, falls through to the code below
        // unchanged. Gate matched like the feedback gates: case-insensitive, untrimmed.
        var delegatedGate = Array.Find(
            DelegableGates,
            gate => string.Equals(signal_name, gate, StringComparison.OrdinalIgnoreCase));

        if (delegatedGate is not null && TryReadDelegatedApproval(args, out var delegatedArgs))
            return await ForwardDelegatedApprovalAsync(workflow_id, delegatedGate, delegatedArgs, @namespace);

        // Matched exactly as before: case-insensitive but untrimmed, so a padded name falls through
        // to the reserved-list refusal below instead of reaching the exception.
        var feedbackGate = Array.Find(
            FeedbackEligibleGates,
            gate => string.Equals(signal_name, gate, StringComparison.OrdinalIgnoreCase));

        if (feedbackGate is not null)
        {
            var caller = httpContextAccessor.HttpContext?.Request.Query["agent"].FirstOrDefault();
            var configuredCto = ctoAgentConfig.GetCtoAgent();
            JsonElement? argsElement = null;

            if (!string.IsNullOrWhiteSpace(args))
            {
                try
                {
                    argsElement = JsonSerializer.Deserialize<JsonElement>(args);
                }
                catch (JsonException ex)
                {
                    LogBlockedFeedback(feedbackGate, workflow_id, signal_name, "unavailable", caller);
                    return $"Error: invalid JSON in args — {ex.Message}";
                }
            }

            var decision = GetStringProperty(argsElement, "Decision");
            string? blockReason = null;

            if (string.IsNullOrWhiteSpace(configuredCto))
                blockReason = "the configured CTO agent is not configured";
            else if (string.IsNullOrWhiteSpace(caller))
                blockReason = "the caller identity is unresolved";
            else if (!string.Equals(caller, configuredCto, StringComparison.OrdinalIgnoreCase))
                blockReason = "the caller is not the configured CTO agent";
            else if (argsElement is not { ValueKind: JsonValueKind.Object })
                blockReason = "the payload must be a JSON object";
            // This comparison is deliberately case-sensitive. Widening it would expand the CEO-only exception.
            else if (!string.Equals(decision, ChangesRequestedDecision, StringComparison.Ordinal))
                blockReason = $"Decision must be exactly '{ChangesRequestedDecision}'";
            else if (string.IsNullOrWhiteSpace(GetStringProperty(argsElement, "Comment")))
                blockReason = "Comment must be a nonblank string";

            if (blockReason is not null)
            {
                LogBlockedFeedback(feedbackGate, workflow_id, signal_name, decision, caller);
                return FeedbackBlockedError(feedbackGate, blockReason);
            }

            try
            {
                var client = await clientFactory.GetClientAsync(@namespace);
                var handle = client.GetWorkflowHandle(workflow_id);

                // design-approval only. merge-approval gets no describe and stays byte-identical.
                if (feedbackGate == DesignApprovalSignal)
                {
                    var gateBlockReason = await GetDesignGateBlockReasonAsync(handle);
                    if (gateBlockReason is not null)
                    {
                        LogBlockedFeedback(feedbackGate, workflow_id, signal_name, decision, caller);
                        return FeedbackBlockedError(feedbackGate, gateBlockReason);
                    }
                }

                LogAllowedFeedback(feedbackGate, workflow_id, signal_name, decision, caller);
                await handle.SignalAsync(feedbackGate, [argsElement.GetValueOrDefault()]);

                return JsonSerializer.Serialize(new
                {
                    workflowId = workflow_id,
                    signalName = feedbackGate,
                    status = "signalled"
                });
            }
            catch (Exception ex)
            {
                return $"Error signalling workflow: {ex.Message}";
            }
        }

        if (IsCeoOnlySignal(signal_name))
        {
            return $"Error: '{signal_name}' is a CEO-only gate and cannot be sent via the MCP tool. " +
                   "Use the fleet dashboard to send this signal. " +
                   $"CEO-only signals: {CeoGateSignals.Joined}.";
        }

        try
        {
            var client = await clientFactory.GetClientAsync(@namespace);
            var handle = client.GetWorkflowHandle(workflow_id);

            object? argsObj = null;
            if (!string.IsNullOrWhiteSpace(args))
            {
                try
                {
                    argsObj = JsonSerializer.Deserialize<JsonElement>(args);
                }
                catch (JsonException ex)
                {
                    return $"Error: invalid JSON in args — {ex.Message}";
                }
            }

            await handle.SignalAsync(signal_name, argsObj is null ? [] : [argsObj]);

            return JsonSerializer.Serialize(new
            {
                workflowId = workflow_id,
                signalName = signal_name,
                status = "signalled"
            });
        }
        catch (Exception ex)
        {
            return $"Error signalling workflow: {ex.Message}";
        }
    }

    private static string? GetStringProperty(JsonElement? element, string propertyName)
    {
        if (element is not { ValueKind: JsonValueKind.Object } value ||
            !value.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return property.GetString();
    }

    private static string FeedbackBlockedError(string gate, string blockReason) =>
        $"Error: '{gate}' remains a CEO-only gate because {blockReason}. " +
        $"Only the configured CTO agent may send Decision '{ChangesRequestedDecision}' with a nonblank Comment via this tool.";

    /// <summary>
    /// Returns null only when the workflow is Running and reports <c>Phase == "design-approval"</c>.
    /// Any lookup failure blocks: a feedback sent while the workflow is not parked at the gate is
    /// buffered by the engine and consumed at the next gate entry, before the CEO is notified (#430).
    /// The exception message is never echoed, only its type name.
    /// </summary>
    private async Task<string?> GetDesignGateBlockReasonAsync(WorkflowHandle handle)
    {
        WorkflowExecutionStatus status;
        string? phase;
        try
        {
            (status, phase) = await gateStateReader.ReadAsync(handle);
        }
        catch (Exception ex)
        {
            return $"the workflow gate could not be verified ({ex.GetType().Name})";
        }

        if (status == WorkflowExecutionStatus.Running &&
            string.Equals(phase, DesignApprovalSignal, StringComparison.Ordinal))
        {
            return null;
        }

        return $"the workflow is not waiting at the design-approval gate " +
               $"(status={status}, phase={(string.IsNullOrEmpty(phase) ? "missing" : phase)})";
    }

    // Two literal templates per log line, selected by gate, so both the rendered text and the
    // structured template for merge-approval stay exactly as they were before #430.
    private void LogBlockedFeedback(
        string gate,
        string workflowId,
        string signalName,
        string? decision,
        string? caller)
    {
        var loggedDecision = decision ?? "unavailable";
        var loggedCaller = string.IsNullOrWhiteSpace(caller) ? "unresolved" : caller;

        if (gate == MergeApprovalSignal)
        {
            logger.LogWarning(
                "Blocked merge-approval signal for workflow {WorkflowId}; signal={Signal}; decision={Decision}; caller={Caller}",
                workflowId,
                signalName,
                loggedDecision,
                loggedCaller);
        }
        else
        {
            logger.LogWarning(
                "Blocked design-approval signal for workflow {WorkflowId}; signal={Signal}; decision={Decision}; caller={Caller}",
                workflowId,
                signalName,
                loggedDecision,
                loggedCaller);
        }
    }

    private void LogAllowedFeedback(
        string gate,
        string workflowId,
        string signalName,
        string? decision,
        string? caller)
    {
        if (gate == MergeApprovalSignal)
        {
            logger.LogInformation(
                "Allowed merge-approval signal for workflow {WorkflowId}; signal={Signal}; decision={Decision}; caller={Caller}",
                workflowId,
                signalName,
                decision,
                caller);
        }
        else
        {
            logger.LogInformation(
                "Allowed design-approval signal for workflow {WorkflowId}; signal={Signal}; decision={Decision}; caller={Caller}",
                workflowId,
                signalName,
                decision,
                caller);
        }
    }

    // ── #436 delegated approval ───────────────────────────────────────────────────

    /// <summary>
    /// True only for the delegated shape: <paramref name="args"/> parses to a JSON object whose
    /// <c>Decision</c> is the string <c>approved</c> (ordinal) and which has a property named
    /// <c>GrantId</c> in any casing, whatever its value. Pure: no logging, no side effects, so a
    /// payload that is not delegated reaches the existing code exactly as before.
    /// </summary>
    private static bool TryReadDelegatedApproval(string? args, out JsonElement argsObject)
    {
        argsObject = default;
        if (string.IsNullOrWhiteSpace(args)) return false;

        JsonElement parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<JsonElement>(args);
        }
        catch (JsonException)
        {
            return false;
        }

        if (parsed.ValueKind != JsonValueKind.Object) return false;
        if (!string.Equals(GetStringProperty(parsed, "Decision"), ApprovedDecision, StringComparison.Ordinal)) return false;
        if (!parsed.EnumerateObject().Any(p => string.Equals(p.Name, GrantIdField, StringComparison.OrdinalIgnoreCase)))
            return false;

        argsObject = parsed;
        return true;
    }

    /// <summary>
    /// Checks the caller exactly like the feedback exception (CTO configured, caller resolved,
    /// equal ignoring case), then a nonblank string GrantId, then forwards once. The bridge never
    /// signals the workflow on this path — only the orchestrator does, after its own checks.
    /// </summary>
    private async Task<string> ForwardDelegatedApprovalAsync(
        string workflowId,
        string gate,
        JsonElement args,
        string @namespace)
    {
        var caller = httpContextAccessor.HttpContext?.Request.Query["agent"].FirstOrDefault();
        var configuredCto = ctoAgentConfig.GetCtoAgent();
        var grantId = GetStringPropertyIgnoreCase(args, GrantIdField);

        string? blockReason = null;
        if (string.IsNullOrWhiteSpace(configuredCto))
            blockReason = "the configured CTO agent is not configured";
        else if (string.IsNullOrWhiteSpace(caller))
            blockReason = "the caller identity is unresolved";
        else if (!string.Equals(caller, configuredCto, StringComparison.OrdinalIgnoreCase))
            blockReason = "the caller is not the configured CTO agent";
        else if (string.IsNullOrWhiteSpace(grantId))
            blockReason = "GrantId must be a nonblank string";

        if (blockReason is not null)
        {
            LogDelegatedApproval(LogLevel.Warning, gate, workflowId, caller, grantId, "blocked", blockReason);
            return DelegatedBlockedError(gate, blockReason);
        }

        var request = new EpicGrantDecisionRequest(
            Namespace: @namespace,
            WorkflowId: workflowId,
            Gate: gate,
            Decision: ApprovedDecision,
            VisitId: GetStringPropertyIgnoreCase(args, "VisitId") ?? "",
            ArtifactRef: GetStringPropertyIgnoreCase(args, "ArtifactRef") ?? "",
            Evidence: GetStringPropertyIgnoreCase(args, "Evidence") ?? "",
            Caller: caller!);

        EpicGrantDecisionResult outcome;
        try
        {
            outcome = await grantForwarder.ForwardAsync(grantId!, request);
        }
        catch (Exception ex)
        {
            outcome = EpicGrantDecisionResult.Failed($"the forward failed ({ex.GetType().Name})");
        }

        if (outcome.Error is not null)
        {
            LogDelegatedApproval(LogLevel.Warning, gate, workflowId, caller, grantId, "forward_failed", outcome.Error);
            return $"Error: the delegated '{gate}' approval could not be forwarded to the orchestrator because " +
                   $"{outcome.Error}. Nothing was sent; the gate is unchanged.";
        }

        LogDelegatedApproval(LogLevel.Information, gate, workflowId, caller, grantId, outcome.Result, outcome.Reason);
        return JsonSerializer.Serialize(new
        {
            workflowId,
            signalName = gate,
            status = "delegated",
            result = outcome.Result,
            reason = outcome.Reason
        });
    }

    /// <summary>
    /// The exact property first, otherwise the first property with that name in any casing.
    /// Null unless the value is a JSON string.
    /// </summary>
    private static string? GetStringPropertyIgnoreCase(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        if (element.TryGetProperty(propertyName, out var exact))
            return exact.ValueKind == JsonValueKind.String ? exact.GetString() : null;

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
        }

        return null;
    }

    private static string DelegatedBlockedError(string gate, string blockReason) =>
        $"Error: the delegated '{gate}' approval was refused because {blockReason}. " +
        $"Only the configured CTO agent may send Decision '{ApprovedDecision}' with a nonblank {GrantIdField} via this tool; " +
        "it is forwarded to the orchestrator, which decides it under an active epic grant. Nothing was sent.";

    /// <summary>
    /// One line per delegated attempt. Evidence, VisitId and ArtifactRef are never logged; the
    /// orchestrator records the decision itself.
    /// </summary>
    private void LogDelegatedApproval(
        LogLevel level,
        string gate,
        string workflowId,
        string? caller,
        string? grantId,
        string? outcome,
        string? reason)
    {
        logger.Log(
            level,
            "Delegated {Gate} approval for workflow {WorkflowId}; decision={Decision}; caller={Caller}; grant={GrantId}; outcome={Outcome}; reason={Reason}",
            gate,
            workflowId,
            ApprovedDecision,
            string.IsNullOrWhiteSpace(caller) ? "unresolved" : caller,
            string.IsNullOrWhiteSpace(grantId) ? "missing" : grantId,
            outcome ?? "unknown",
            reason ?? "none");
    }

    [McpServerTool(Name = "temporal_cancel_workflow")]
    [Description("Request graceful cancellation of a running workflow.")]
    public async Task<string> CancelWorkflowAsync(
        [Description("Workflow ID to cancel")] string workflow_id,
        [Description("Temporal namespace. Default: fleet.")] string @namespace = DefaultNamespace)
    {
        try
        {
            var client = await clientFactory.GetClientAsync(@namespace);
            var handle = client.GetWorkflowHandle(workflow_id);
            await handle.CancelAsync();

            return JsonSerializer.Serialize(new
            {
                workflowId = workflow_id,
                status = "cancel_requested"
            });
        }
        catch (Exception ex)
        {
            return $"Error cancelling workflow: {ex.Message}";
        }
    }

    [McpServerTool(Name = "temporal_terminate_workflow")]
    [Description("Forcefully terminate a workflow execution. Unlike cancel, this is immediate and does not allow cleanup.")]
    public async Task<string> TerminateWorkflowAsync(
        [Description("Workflow ID to terminate")] string workflow_id,
        [Description("Reason for termination (optional)")] string? reason = null,
        [Description("Temporal namespace. Default: fleet.")] string @namespace = DefaultNamespace)
    {
        try
        {
            var client = await clientFactory.GetClientAsync(@namespace);
            var handle = client.GetWorkflowHandle(workflow_id);
            await handle.TerminateAsync(reason ?? "Terminated via MCP tool");

            return JsonSerializer.Serialize(new
            {
                workflowId = workflow_id,
                status = "terminated"
            });
        }
        catch (Exception ex)
        {
            return $"Error terminating workflow: {ex.Message}";
        }
    }

    [McpServerTool(Name = "temporal_get_workflow_result")]
    [Description("Get the result/output of a completed workflow. Returns an error if still running or failed.")]
    public async Task<string> GetWorkflowResultAsync(
        [Description("Workflow ID to get result from")] string workflow_id,
        [Description("Temporal namespace. Default: fleet.")] string @namespace = DefaultNamespace)
    {
        try
        {
            var client = await clientFactory.GetClientAsync(@namespace);
            var handle = client.GetWorkflowHandle(workflow_id);
            var result = await handle.GetResultAsync<JsonElement>();
            return JsonSerializer.Serialize(result);
        }
        catch (Exception ex)
        {
            return $"Error getting workflow result: {ex.Message}";
        }
    }

    [McpServerTool(Name = "temporal_list_workflow_types")]
    [Description("List all registered workflow types with descriptions, input schemas, and namespaces.")]
    public async Task<string> ListWorkflowTypesAsync()
    {
        var allTypes = await registry.GetAllAsync();
        var sb = new StringBuilder();
        sb.AppendLine($"Registered workflow types ({allTypes.Count} total):");
        sb.AppendLine();

        foreach (var t in allTypes)
        {
            sb.AppendLine($"## {t.Name}");
            sb.AppendLine($"Namespace: {t.Namespace}");
            sb.AppendLine(t.Description);
            sb.AppendLine();
            sb.AppendLine("Input schema:");
            sb.AppendLine(t.InputSchema);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "temporal_create_schedule")]
    [Description("Create a cron schedule that starts a workflow on the given cadence.")]
    public async Task<string> CreateScheduleAsync(
        [Description("Unique schedule identifier (e.g. health-check-6h)")] string schedule_id,
        [Description("Registered workflow type name")] string workflow_type,
        [Description("Standard cron expression (e.g. '0 */6 * * *'). Supports CRON_TZ= prefix for timezone.")] string cron_expression,
        [Description("Workflow input as a JSON string (optional)")] string? input = null,
        [Description("Task queue name. Defaults to namespace name if omitted.")] string? task_queue = null,
        [Description("Human-readable description of the schedule (optional)")] string? note = null,
        [Description("Temporal namespace. Default: fleet.")] string @namespace = DefaultNamespace)
    {
        task_queue ??= @namespace;
        object? inputObj = null;
        if (!string.IsNullOrWhiteSpace(input))
        {
            try
            {
                inputObj = JsonSerializer.Deserialize<JsonElement>(input);
            }
            catch (JsonException ex)
            {
                return $"Error: invalid JSON in input — {ex.Message}";
            }
        }

        try
        {
            var client = await clientFactory.GetClientAsync(@namespace);
            var handle = await client.CreateScheduleAsync(
                schedule_id,
                new Temporalio.Client.Schedules.Schedule(
                    Action: Temporalio.Client.Schedules.ScheduleActionStartWorkflow.Create(
                        workflow_type,
                        inputObj is null ? [] : [inputObj],
                        new WorkflowOptions(id: $"{workflow_type}-{schedule_id}", taskQueue: task_queue)),
                    Spec: new Temporalio.Client.Schedules.ScheduleSpec
                    {
                        CronExpressions = [cron_expression]
                    })
                {
                    State = new Temporalio.Client.Schedules.ScheduleState { Note = note ?? string.Empty }
                });

            return JsonSerializer.Serialize(new
            {
                scheduleId = handle.Id,
                workflowType = workflow_type,
                cronExpression = cron_expression,
                @namespace,
                status = "created"
            });
        }
        catch (Exception ex)
        {
            return $"Error creating schedule: {ex.Message}";
        }
    }

    [McpServerTool(Name = "temporal_list_schedules")]
    [Description("List all active Temporal schedules.")]
    public async Task<string> ListSchedulesAsync(
        [Description("Maximum number of results. Default: 20")] int limit = 20,
        [Description("Temporal namespace. Default: fleet.")] string @namespace = DefaultNamespace)
    {
        try
        {
            var client = await clientFactory.GetClientAsync(@namespace);
            var results = new List<object>();
            await foreach (var s in client.ListSchedulesAsync())
            {
                var spec = s.Schedule?.Spec;
                var action = s.Schedule?.Action as Temporalio.Client.Schedules.ScheduleListActionStartWorkflow;
                results.Add(new
                {
                    scheduleId = s.Id,
                    workflowType = action?.Workflow,
                    cronExpression = spec?.CronExpressions.FirstOrDefault(),
                    paused = s.Schedule?.State?.Paused ?? false,
                    note = s.Schedule?.State?.Note
                });

                if (results.Count >= limit)
                    break;
            }

            return JsonSerializer.Serialize(results);
        }
        catch (Exception ex)
        {
            return $"Error listing schedules: {ex.Message}";
        }
    }

    [McpServerTool(Name = "temporal_describe_schedule")]
    [Description("Get details of a specific schedule including next run times and recent runs.")]
    public async Task<string> DescribeScheduleAsync(
        [Description("Schedule ID to describe")] string schedule_id,
        [Description("Temporal namespace. Default: fleet.")] string @namespace = DefaultNamespace)
    {
        try
        {
            var client = await clientFactory.GetClientAsync(@namespace);
            var handle = client.GetScheduleHandle(schedule_id);
            var desc = await handle.DescribeAsync();

            var action = desc.Schedule.Action as Temporalio.Client.Schedules.ScheduleActionStartWorkflow;
            var spec = desc.Schedule.Spec;

            return JsonSerializer.Serialize(new
            {
                scheduleId = desc.Id,
                workflowType = action?.Workflow,
                cronExpression = spec?.CronExpressions.FirstOrDefault(),
                paused = desc.Schedule.State?.Paused ?? false,
                note = desc.Schedule.State?.Note,
                nextActionTimes = desc.Info?.NextActionTimes,
                recentActions = desc.Info?.RecentActions?.Select(a => new
                {
                    scheduledAt = a.ScheduledAt,
                    startedAt = a.StartedAt,
                    workflowId = (a.Action as Temporalio.Client.Schedules.ScheduleActionExecutionStartWorkflow)?.WorkflowId
                })
            });
        }
        catch (Exception ex)
        {
            return $"Error describing schedule: {ex.Message}";
        }
    }

    [McpServerTool(Name = "temporal_delete_schedule")]
    [Description("Delete a schedule. Does not affect already-running workflow executions.")]
    public async Task<string> DeleteScheduleAsync(
        [Description("Schedule ID to delete")] string schedule_id,
        [Description("Temporal namespace. Default: fleet.")] string @namespace = DefaultNamespace)
    {
        try
        {
            var client = await clientFactory.GetClientAsync(@namespace);
            var handle = client.GetScheduleHandle(schedule_id);
            await handle.DeleteAsync();

            return JsonSerializer.Serialize(new
            {
                scheduleId = schedule_id,
                status = "deleted"
            });
        }
        catch (Exception ex)
        {
            return $"Error deleting schedule: {ex.Message}";
        }
    }

    [McpServerTool(Name = "request_memory_store")]
    [Description(
        "Submit a memory store request for review. " +
        "Starts a MemoryStoreRequestWorkflow (namespace: fleet, task queue: fleet) and returns the workflow ID. " +
        "Use this to persist learnings, decisions, task results, or reference information. " +
        "A reviewer will approve, reject, or edit the request before it is written to memory.")]
    public async Task<string> RequestMemoryStoreAsync(
        [Description("Memory type. Use: 'learning' for discovered knowledge, 'decision' for architectural/product decisions, 'task_result' for task outcomes, 'reference' for external resources, 'conversation_summary' for session digests.")] string type,
        [Description("Short descriptive title (5-10 words). Used for retrieval — be specific.")] string title,
        [Description("Full memory content. Include: what was learned/decided, why it matters, and how to apply it.")] string content,
        [Description("Project this memory relates to (e.g. 'my-project').")] string project,
        [Description("Your agent name (e.g. 'my-agent'). Used to identify the requester.")] string agent,
        [Description("Optional comma-separated tags for categorization (e.g. 'testing,ci,dotnet').")] string tags = "",
        [Description("Optional source context (e.g. workflow ID, PR number, issue number).")] string source = "")
    {
        var workflowId = $"MemoryStoreRequestWorkflow-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        var input = new
        {
            Type = type,
            Title = title,
            Content = content,
            Project = project,
            Agent = agent,
            Tags = tags,
            Source = source
        };

        try
        {
            var client = await clientFactory.GetClientAsync("fleet");
            var inputJson = JsonSerializer.SerializeToElement(input);
            var handle = await client.StartWorkflowAsync(
                "MemoryStoreRequestWorkflow",
                [inputJson],
                new WorkflowOptions(id: workflowId, taskQueue: "fleet"));

            return JsonSerializer.Serialize(new
            {
                workflowId = handle.Id,
                status = "submitted",
                message = "Memory request submitted. The CTO agent will review and approve/reject. You do not need to wait for the result."
            });
        }
        catch (Exception ex)
        {
            return $"Error submitting memory request: {ex.Message}";
        }
    }

    private static string BuildVisibilityQuery(string? query, string? status)
    {
        if (!string.IsNullOrWhiteSpace(query))
            return query;

        if (!string.IsNullOrWhiteSpace(status))
        {
            var temporalStatus = status.ToLowerInvariant() switch
            {
                "running" => "Running",
                "completed" => "Completed",
                "failed" => "Failed",
                "cancelled" => "Cancelled",
                "terminated" => "Terminated",
                _ => status
            };
            return $"ExecutionStatus=\"{temporalStatus}\"";
        }

        return string.Empty;
    }

    private static List<string> GetRequiredFields(string inputSchema)
    {
        try
        {
            var doc = JsonSerializer.Deserialize<JsonElement>(inputSchema);
            if (doc.TryGetProperty("required", out var required) &&
                required.ValueKind == JsonValueKind.Array)
            {
                return required.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .ToList();
            }
        }
        catch (JsonException)
        {
            // Unparseable schema — skip validation
        }
        return [];
    }
}
