using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using Temporalio.Client;

namespace Fleet.Temporal.Mcp;

[McpServerToolType]
public sealed class TemporalWorkflowTools(TemporalClientFactory clientFactory, WorkflowTypeRegistry registry)
{
    private const string DefaultNamespace = "fleet";

    /// <summary>
    /// Signals that are exclusively for CEO approval and must never be sent by agents via the MCP tool.
    /// These gates must go through the fleet dashboard (orchestrator REST API), which is auth-gated.
    /// </summary>
    private static readonly HashSet<string> CeoOnlySignals = new(StringComparer.OrdinalIgnoreCase)
    {
        "merge-approval",
        "doc-review",
        "design-approval",
        "advisory-review"
    };

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
        "These gates must be sent from the fleet dashboard by the CEO.")]
    public async Task<string> SignalWorkflowAsync(
        [Description("Workflow ID to signal")] string workflow_id,
        [Description("Signal name (e.g. human-review, merge-approval)")] string signal_name,
        [Description("Signal payload as a JSON object string. For 'human-review': {\"Decision\":\"approved\"} or {\"Decision\":\"changes_requested\",\"Comment\":\"your feedback\"}. For 'merge-approval': omit or pass null.")] string? args = null,
        [Description("Temporal namespace. Default: fleet.")] string @namespace = DefaultNamespace)
    {
        if (CeoOnlySignals.Contains(signal_name))
        {
            return $"Error: '{signal_name}' is a CEO-only gate and cannot be sent via the MCP tool. " +
                   "Use the fleet dashboard to send this signal. " +
                   "CEO-only signals: merge-approval, doc-review, design-approval, advisory-review.";
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
