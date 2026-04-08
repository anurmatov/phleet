using Fleet.Orchestrator.Configuration;
using Microsoft.Extensions.Options;
using Temporalio.Client;
using Temporalio.Common;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// Singleton that holds per-namespace Temporal clients, shared between the poller and REST handlers.
/// </summary>
public sealed class TemporalClientRegistry(
    IOptions<TemporalOptions> opts,
    ILogger<TemporalClientRegistry> logger)
{
    private readonly TemporalOptions _opts = opts.Value;
    private Dictionary<string, ITemporalClient>? _clients;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_opts.Address);

    public async Task<IReadOnlyDictionary<string, ITemporalClient>> GetClientsAsync(CancellationToken ct = default)
    {
        if (_clients is not null) return _clients;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_clients is not null) return _clients;

            var clients = new Dictionary<string, ITemporalClient>(StringComparer.OrdinalIgnoreCase);
            foreach (var ns in _opts.Namespaces)
            {
                try
                {
                    clients[ns] = await TemporalClient.ConnectAsync(new TemporalClientConnectOptions(_opts.Address)
                    {
                        Namespace = ns
                    });
                    logger.LogDebug("Connected to Temporal namespace '{Namespace}'", ns);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to connect to Temporal namespace '{Namespace}' — skipping", ns);
                }
            }

            _clients = clients;
            return _clients;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<ITemporalClient?> GetClientAsync(string @namespace, CancellationToken ct = default)
    {
        var clients = await GetClientsAsync(ct);
        return clients.TryGetValue(@namespace, out var client) ? client : null;
    }

    /// <summary>Returns the list of known namespaces (connected clients if initialized, configured list otherwise).</summary>
    public string[] GetNamespaces() =>
        _clients is not null ? [.. _clients.Keys] : [.. _opts.Namespaces];

    public async Task<bool> CancelWorkflowAsync(string @namespace, string workflowId, string? runId, CancellationToken ct = default)
    {
        var client = await GetClientAsync(@namespace, ct);
        if (client is null)
        {
            logger.LogWarning("No client for namespace '{Namespace}' — cannot cancel workflow", @namespace);
            return false;
        }

        var handle = client.GetWorkflowHandle(workflowId, runId: runId);
        await handle.CancelAsync();
        logger.LogInformation("Cancelled workflow {WorkflowId} (run {RunId}) in namespace {Namespace}", workflowId, runId, @namespace);
        return true;
    }

    public async Task<bool> TerminateWorkflowAsync(string @namespace, string workflowId, string? runId, string reason = "Terminated via Fleet Dashboard", CancellationToken ct = default)
    {
        var client = await GetClientAsync(@namespace, ct);
        if (client is null)
        {
            logger.LogWarning("No client for namespace '{Namespace}' — cannot terminate workflow", @namespace);
            return false;
        }

        var handle = client.GetWorkflowHandle(workflowId, runId: runId);
        await handle.TerminateAsync(reason);
        logger.LogInformation("Terminated workflow {WorkflowId} (run {RunId}) in namespace {Namespace}", workflowId, runId, @namespace);
        return true;
    }

    /// <summary>
    /// Returns workflows that closed with Failed, Terminated, or Canceled status within the given window.
    /// Returns empty list if Temporal is not configured or the query fails.
    /// </summary>
    public async Task<List<Models.WorkflowSummary>> ListRecentFailuresAsync(TimeSpan window, CancellationToken ct = default)
    {
        var clients = await GetClientsAsync(ct);
        var results = new List<Models.WorkflowSummary>();
        var since = DateTimeOffset.UtcNow - window;
        var sinceStr = since.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var query = $"(ExecutionStatus=\"Failed\" OR ExecutionStatus=\"Terminated\" OR ExecutionStatus=\"Canceled\") AND CloseTime >= \"{sinceStr}\"";

        foreach (var (ns, client) in clients)
        {
            try
            {
                await foreach (var wf in client.ListWorkflowsAsync(query).WithCancellation(ct))
                {
                    results.Add(new Models.WorkflowSummary(
                        WorkflowId:   wf.Id,
                        RunId:        wf.RunId ?? "",
                        WorkflowType: wf.WorkflowType ?? "",
                        Namespace:    ns,
                        TaskQueue:    wf.TaskQueue,
                        Status:       wf.Status.ToString(),
                        StartTime:    wf.StartTime));
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error querying failed workflows in namespace '{Namespace}'", ns);
            }
        }

        return results;
    }

    /// <summary>
    /// Returns all recently closed workflows (Completed, Failed, Canceled, Terminated) within the given window.
    /// Includes search attributes and CloseTime. Results are ordered by CloseTime descending, capped at 50.
    /// Returns empty list if Temporal is not configured or the query fails.
    /// </summary>
    public async Task<List<Models.WorkflowSummary>> ListRecentlyClosedAsync(TimeSpan window, CancellationToken ct = default)
    {
        var clients = await GetClientsAsync(ct);
        var results = new List<Models.WorkflowSummary>();
        var since = DateTimeOffset.UtcNow - window;
        var sinceStr = since.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var query = $"ExecutionStatus != \"Running\" AND CloseTime >= \"{sinceStr}\"";

        foreach (var (ns, client) in clients)
        {
            try
            {
                await foreach (var wf in client.ListWorkflowsAsync(query).WithCancellation(ct))
                {
                    int? issueNum = null;
                    int? prNum = null;
                    string? repo = null;
                    string? docPrs = null;
                    string? phase = null;
                    try
                    {
                        var attrs = wf.TypedSearchAttributes;
                        if (attrs.TryGetValue(SearchAttributeKey.CreateLong("IssueNumber"), out var iv))
                            issueNum = (int)iv;
                        if (attrs.TryGetValue(SearchAttributeKey.CreateLong("PrNumber"), out var pv))
                            prNum = (int)pv;
                        if (attrs.TryGetValue(SearchAttributeKey.CreateKeyword("Repo"), out var rv))
                            repo = rv;
                        if (attrs.TryGetValue(SearchAttributeKey.CreateKeyword("DocPrs"), out var dv))
                            docPrs = dv;
                        if (attrs.TryGetValue(SearchAttributeKey.CreateKeyword("Phase"), out var phv))
                            phase = phv;
                    }
                    catch
                    {
                        // Search attributes unavailable — non-fatal
                    }

                    results.Add(new Models.WorkflowSummary(
                        WorkflowId:   wf.Id,
                        RunId:        wf.RunId ?? "",
                        WorkflowType: wf.WorkflowType ?? "",
                        Namespace:    ns,
                        TaskQueue:    wf.TaskQueue,
                        Status:       wf.Status.ToString(),
                        StartTime:    wf.StartTime,
                        CloseTime:    wf.CloseTime,
                        IssueNumber:  issueNum,
                        PrNumber:     prNum,
                        Repo:         repo,
                        DocPrs:       docPrs,
                        Phase:        phase));
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error querying closed workflows in namespace '{Namespace}'", ns);
            }
        }

        return results
            .OrderByDescending(w => w.CloseTime)
            .Take(50)
            .ToList();
    }

    /// <summary>
    /// Restarts a workflow by reading its original type, task queue, and input from history,
    /// then starting a new execution. Optionally terminates the old execution first.
    /// Returns the new workflow ID and run ID.
    /// </summary>
    public async Task<(string NewWorkflowId, string NewRunId)> RestartWorkflowAsync(
        string @namespace, string workflowId, string? runId,
        bool terminateExisting, CancellationToken ct = default)
    {
        var client = await GetClientAsync(@namespace, ct);
        if (client is null)
            throw new InvalidOperationException($"No Temporal client for namespace '{@namespace}'");

        var handle = client.GetWorkflowHandle(workflowId, runId: runId);

        // Fetch history to extract original workflow type, task queue, and input
        var history = await handle.FetchHistoryAsync();
        var startEvent = history.Events.FirstOrDefault(e =>
            e.EventType == Temporalio.Api.Enums.V1.EventType.WorkflowExecutionStarted);

        if (startEvent?.WorkflowExecutionStartedEventAttributes is not { } attrs)
            throw new InvalidOperationException("WorkflowExecutionStarted event not found in history");

        var wfType = attrs.WorkflowType.Name;
        var taskQueue = attrs.TaskQueue.Name;

        // Decode JSON payloads from the original input
        object?[] args = [];
        if (attrs.Input?.Payloads_ is { Count: > 0 } payloads)
        {
            args = payloads.Select(p =>
            {
                var encoding = p.Metadata.TryGetValue("encoding", out var enc)
                    ? System.Text.Encoding.UTF8.GetString(enc.ToByteArray())
                    : "";
                if (encoding.StartsWith("json/", StringComparison.OrdinalIgnoreCase))
                {
                    var json = p.Data.ToStringUtf8();
                    return (object?)System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json);
                }
                return (object?)null;
            }).ToArray();
        }

        // Optionally terminate the old execution
        if (terminateExisting)
        {
            try { await handle.TerminateAsync("Restarted via Fleet Dashboard"); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not terminate old workflow {WorkflowId} before restart — may already be closed", workflowId);
            }
        }

        // Start a fresh execution with an auto-generated ID
        var newId = $"{wfType}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        var newHandle = await client.StartWorkflowAsync(
            wfType, args,
            new WorkflowOptions(id: newId, taskQueue: taskQueue));

        logger.LogInformation("Restarted workflow {OldId} as {NewId} in namespace {Namespace}", workflowId, newId, @namespace);
        return (newHandle.Id, newHandle.ResultRunId ?? "");
    }

    /// <summary>
    /// Returns a simplified event list for a workflow execution.
    /// Filters to: WorkflowStarted, WorkflowCompleted, WorkflowFailed, WorkflowCanceled,
    ///             ActivityScheduled, ActivityCompleted, ActivityFailed, SignalReceived.
    /// All other event types are skipped. Capped at 300 events.
    /// </summary>
    public async Task<List<Models.WorkflowEventSummary>> FetchWorkflowEventsAsync(
        string @namespace, string workflowId, string? runId = null, CancellationToken ct = default)
    {
        var client = await GetClientAsync(@namespace, ct);
        if (client is null)
            throw new InvalidOperationException($"No Temporal client for namespace '{@namespace}'");

        var handle = client.GetWorkflowHandle(workflowId, runId: runId);
        var history = await handle.FetchHistoryAsync();

        var events = new List<Models.WorkflowEventSummary>();
        // Map from scheduledEventId → (ActivityType, InputSummary) for correlating Completed/Failed events
        var activityTypeMap  = new Dictionary<long, string>();
        var activityInputMap = new Dictionary<long, string>();
        var agentMap         = new Dictionary<long, string?>();

        foreach (var e in history.Events)
        {
            if (events.Count >= 300) break;

            switch (e.EventType)
            {
                case Temporalio.Api.Enums.V1.EventType.WorkflowExecutionStarted:
                {
                    var attrs = e.WorkflowExecutionStartedEventAttributes;
                    events.Add(new Models.WorkflowEventSummary(
                        EventId: e.EventId,
                        EventType: "WorkflowStarted",
                        Timestamp: e.EventTime?.ToDateTimeOffset() ?? DateTimeOffset.MinValue,
                        ActivityType: null,
                        Agent: null,
                        InputSummary: DecodePayloads(attrs?.Input?.Payloads_),
                        OutputSummary: null,
                        SignalName: null,
                        FailureMessage: null));
                    break;
                }
                case Temporalio.Api.Enums.V1.EventType.WorkflowExecutionCompleted:
                {
                    var attrs = e.WorkflowExecutionCompletedEventAttributes;
                    events.Add(new Models.WorkflowEventSummary(
                        EventId: e.EventId,
                        EventType: "WorkflowCompleted",
                        Timestamp: e.EventTime?.ToDateTimeOffset() ?? DateTimeOffset.MinValue,
                        ActivityType: null,
                        Agent: null,
                        InputSummary: null,
                        OutputSummary: DecodePayloads(attrs?.Result?.Payloads_),
                        SignalName: null,
                        FailureMessage: null));
                    break;
                }
                case Temporalio.Api.Enums.V1.EventType.WorkflowExecutionFailed:
                {
                    var attrs = e.WorkflowExecutionFailedEventAttributes;
                    events.Add(new Models.WorkflowEventSummary(
                        EventId: e.EventId,
                        EventType: "WorkflowFailed",
                        Timestamp: e.EventTime?.ToDateTimeOffset() ?? DateTimeOffset.MinValue,
                        ActivityType: null,
                        Agent: null,
                        InputSummary: null,
                        OutputSummary: null,
                        SignalName: null,
                        FailureMessage: attrs?.Failure?.Message));
                    break;
                }
                case Temporalio.Api.Enums.V1.EventType.WorkflowExecutionCanceled:
                {
                    events.Add(new Models.WorkflowEventSummary(
                        EventId: e.EventId,
                        EventType: "WorkflowCanceled",
                        Timestamp: e.EventTime?.ToDateTimeOffset() ?? DateTimeOffset.MinValue,
                        ActivityType: null,
                        Agent: null,
                        InputSummary: null,
                        OutputSummary: null,
                        SignalName: null,
                        FailureMessage: null));
                    break;
                }
                case Temporalio.Api.Enums.V1.EventType.ActivityTaskScheduled:
                {
                    var attrs = e.ActivityTaskScheduledEventAttributes;
                    var actType  = attrs?.ActivityType?.Name ?? "";
                    var inputStr = DecodePayloads(attrs?.Input?.Payloads_);
                    activityTypeMap[e.EventId]  = actType;
                    activityInputMap[e.EventId] = inputStr ?? "";

                    // Extract agent name from DelegateToAgentActivity: first string payload
                    string? agent = null;
                    if (actType == "DelegateToAgent" && attrs?.Input?.Payloads_ is { Count: > 0 } p)
                    {
                        var firstPayload = DecodePayload(p[0]);
                        // firstPayload is a JSON string like "\"developer\"" — unquote it
                        if (firstPayload?.StartsWith("\"") == true)
                        {
                            try { agent = System.Text.Json.JsonSerializer.Deserialize<string>(firstPayload); }
                            catch { /* ignore */ }
                        }
                    }

                    if (agent != null) agentMap[e.EventId] = agent;

                    events.Add(new Models.WorkflowEventSummary(
                        EventId: e.EventId,
                        EventType: "ActivityScheduled",
                        Timestamp: e.EventTime?.ToDateTimeOffset() ?? DateTimeOffset.MinValue,
                        ActivityType: actType,
                        Agent: agent,
                        InputSummary: inputStr,
                        OutputSummary: null,
                        SignalName: null,
                        FailureMessage: null));
                    break;
                }
                case Temporalio.Api.Enums.V1.EventType.ActivityTaskCompleted:
                {
                    var attrs = e.ActivityTaskCompletedEventAttributes;
                    var scheduledId = attrs?.ScheduledEventId ?? 0;
                    activityTypeMap.TryGetValue(scheduledId, out var actType);
                    activityInputMap.TryGetValue(scheduledId, out var inputStr);

                    events.Add(new Models.WorkflowEventSummary(
                        EventId: e.EventId,
                        EventType: "ActivityCompleted",
                        Timestamp: e.EventTime?.ToDateTimeOffset() ?? DateTimeOffset.MinValue,
                        ActivityType: actType,
                        Agent: agentMap.GetValueOrDefault(scheduledId),
                        InputSummary: inputStr,
                        OutputSummary: DecodePayloads(attrs?.Result?.Payloads_),
                        SignalName: null,
                        FailureMessage: null));
                    break;
                }
                case Temporalio.Api.Enums.V1.EventType.ActivityTaskFailed:
                {
                    var attrs = e.ActivityTaskFailedEventAttributes;
                    var scheduledId = attrs?.ScheduledEventId ?? 0;
                    activityTypeMap.TryGetValue(scheduledId, out var actType);

                    events.Add(new Models.WorkflowEventSummary(
                        EventId: e.EventId,
                        EventType: "ActivityFailed",
                        Timestamp: e.EventTime?.ToDateTimeOffset() ?? DateTimeOffset.MinValue,
                        ActivityType: actType,
                        Agent: agentMap.GetValueOrDefault(scheduledId),
                        InputSummary: null,
                        OutputSummary: null,
                        SignalName: null,
                        FailureMessage: attrs?.Failure?.Message));
                    break;
                }
                case Temporalio.Api.Enums.V1.EventType.WorkflowExecutionSignaled:
                {
                    var attrs = e.WorkflowExecutionSignaledEventAttributes;
                    events.Add(new Models.WorkflowEventSummary(
                        EventId: e.EventId,
                        EventType: "SignalReceived",
                        Timestamp: e.EventTime?.ToDateTimeOffset() ?? DateTimeOffset.MinValue,
                        ActivityType: null,
                        Agent: null,
                        InputSummary: DecodePayloads(attrs?.Input?.Payloads_),
                        OutputSummary: null,
                        SignalName: attrs?.SignalName,
                        FailureMessage: null));
                    break;
                }
            }
        }

        return events;
    }

    /// Decodes all payloads in a list, joins with newline. Returns null if empty.
    private static string? DecodePayloads(Google.Protobuf.Collections.RepeatedField<Temporalio.Api.Common.V1.Payload>? payloads)
    {
        if (payloads is null || payloads.Count == 0) return null;
        var parts = payloads.Select(DecodePayload).Where(s => s is not null).ToList();
        if (parts.Count == 0) return null;
        var joined = string.Join("\n", parts);
        return joined.Length > 2000 ? joined[..2000] + "…" : joined;
    }

    /// Decodes a single Temporal payload to string. Returns null for binary or unrecognized encodings.
    private static string? DecodePayload(Temporalio.Api.Common.V1.Payload payload)
    {
        if (!payload.Metadata.TryGetValue("encoding", out var encBytes)) return null;
        var encoding = System.Text.Encoding.UTF8.GetString(encBytes.ToByteArray());
        if (!encoding.StartsWith("json/", StringComparison.OrdinalIgnoreCase))
            return null;
        return payload.Data.ToStringUtf8();
    }

    // ─── Schedule helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Lists all schedules in the given namespace. Returns cheap fields only (no per-schedule describe calls).
    /// </summary>
    public async Task<List<object>> ListSchedulesAsync(string @namespace, CancellationToken ct = default)
    {
        var client = await GetClientAsync(@namespace, ct);
        if (client is null)
            throw new InvalidOperationException($"No Temporal client for namespace '{@namespace}'");

        var results = new List<object>();
        await foreach (var s in client.ListSchedulesAsync().WithCancellation(ct))
        {
            var spec   = s.Schedule?.Spec;
            var action = s.Schedule?.Action as Temporalio.Client.Schedules.ScheduleListActionStartWorkflow;
            results.Add(new
            {
                scheduleId     = s.Id,
                @namespace,
                workflowType   = action?.Workflow,
                cronExpression = spec?.CronExpressions.FirstOrDefault(),
                paused         = s.Schedule?.State?.Paused ?? false,
                memo           = s.Schedule?.State?.Note
            });
        }
        return results;
    }

    /// <summary>
    /// Describes a single schedule in full detail, including run-time fields (next/last run times).
    /// </summary>
    public async Task<object> DescribeScheduleAsync(string @namespace, string scheduleId, CancellationToken ct = default)
    {
        var client = await GetClientAsync(@namespace, ct);
        if (client is null)
            throw new InvalidOperationException($"No Temporal client for namespace '{@namespace}'");

        var handle = client.GetScheduleHandle(scheduleId);
        var desc   = await handle.DescribeAsync();

        var action     = desc.Schedule.Action as Temporalio.Client.Schedules.ScheduleActionStartWorkflow;
        var spec       = desc.Schedule.Spec;
        var lastAction = desc.Info?.RecentActions?.LastOrDefault();

        return new
        {
            scheduleId        = desc.Id,
            @namespace,
            workflowType      = action?.Workflow,
            cronExpression    = spec?.CronExpressions.FirstOrDefault(),
            paused            = desc.Schedule.State?.Paused ?? false,
            memo              = desc.Schedule.State?.Note,
            nextRunTime       = desc.Info?.NextActionTimes?.FirstOrDefault(),
            lastRunTime       = lastAction?.ScheduledAt,
            lastRunWorkflowId = (lastAction?.Action as Temporalio.Client.Schedules.ScheduleActionExecutionStartWorkflow)?.WorkflowId,
            input             = action?.Args.FirstOrDefault()
        };
    }

    /// <summary>
    /// Creates a new schedule.
    /// </summary>
    public async Task<string> CreateScheduleAsync(
        string @namespace, string scheduleId, string workflowType, string taskQueue,
        string cronExpression, object? input, string? memo, bool paused,
        CancellationToken ct = default)
    {
        var client = await GetClientAsync(@namespace, ct);
        if (client is null)
            throw new InvalidOperationException($"No Temporal client for namespace '{@namespace}'");

        var handle = await client.CreateScheduleAsync(
            scheduleId,
            new Temporalio.Client.Schedules.Schedule(
                Action: Temporalio.Client.Schedules.ScheduleActionStartWorkflow.Create(
                    workflowType,
                    input is null ? [] : [input],
                    new WorkflowOptions(id: $"{workflowType}-{scheduleId}", taskQueue: taskQueue)),
                Spec: new Temporalio.Client.Schedules.ScheduleSpec
                {
                    CronExpressions = [cronExpression]
                })
            {
                State = new Temporalio.Client.Schedules.ScheduleState
                {
                    Note   = memo ?? string.Empty,
                    Paused = paused
                }
            });

        logger.LogInformation("Created schedule {ScheduleId} in namespace {Namespace}", scheduleId, @namespace);
        return handle.Id;
    }

    /// <summary>Pauses a schedule.</summary>
    public async Task PauseScheduleAsync(string @namespace, string scheduleId, CancellationToken ct = default)
    {
        var client = await GetClientAsync(@namespace, ct);
        if (client is null)
            throw new InvalidOperationException($"No Temporal client for namespace '{@namespace}'");

        var handle = client.GetScheduleHandle(scheduleId);
        await handle.PauseAsync();
        logger.LogInformation("Paused schedule {ScheduleId} in namespace {Namespace}", scheduleId, @namespace);
    }

    /// <summary>Unpauses a schedule.</summary>
    public async Task UnpauseScheduleAsync(string @namespace, string scheduleId, CancellationToken ct = default)
    {
        var client = await GetClientAsync(@namespace, ct);
        if (client is null)
            throw new InvalidOperationException($"No Temporal client for namespace '{@namespace}'");

        var handle = client.GetScheduleHandle(scheduleId);
        await handle.UnpauseAsync();
        logger.LogInformation("Unpaused schedule {ScheduleId} in namespace {Namespace}", scheduleId, @namespace);
    }

    /// <summary>Triggers an immediate execution of a schedule.</summary>
    public async Task TriggerScheduleAsync(string @namespace, string scheduleId, CancellationToken ct = default)
    {
        var client = await GetClientAsync(@namespace, ct);
        if (client is null)
            throw new InvalidOperationException($"No Temporal client for namespace '{@namespace}'");

        var handle = client.GetScheduleHandle(scheduleId);
        await handle.TriggerAsync();
        logger.LogInformation("Triggered schedule {ScheduleId} in namespace {Namespace}", scheduleId, @namespace);
    }

    /// <summary>Deletes a schedule.</summary>
    public async Task DeleteScheduleAsync(string @namespace, string scheduleId, CancellationToken ct = default)
    {
        var client = await GetClientAsync(@namespace, ct);
        if (client is null)
            throw new InvalidOperationException($"No Temporal client for namespace '{@namespace}'");

        var handle = client.GetScheduleHandle(scheduleId);
        await handle.DeleteAsync();
        logger.LogInformation("Deleted schedule {ScheduleId} in namespace {Namespace}", scheduleId, @namespace);
    }

    /// <summary>
    /// Starts a new workflow execution with the given type, ID, namespace, task queue, and optional JSON input.
    /// Returns the new workflow ID.
    /// </summary>
    public async Task<string> StartWorkflowAsync(
        string workflowType, string workflowId, string @namespace, string taskQueue,
        string? inputJson, CancellationToken ct = default)
    {
        var client = await GetClientAsync(@namespace, ct);
        if (client is null)
            throw new InvalidOperationException($"No Temporal client for namespace '{@namespace}'");

        object?[] args = [];
        if (!string.IsNullOrWhiteSpace(inputJson))
        {
            var element = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(inputJson);
            args = [element];
        }

        var handle = await client.StartWorkflowAsync(
            workflowType, args,
            new WorkflowOptions(id: workflowId, taskQueue: taskQueue));

        logger.LogInformation("Started workflow {Type} as {Id} in namespace {Namespace}", workflowType, workflowId, @namespace);
        return handle.Id;
    }

    public async Task<bool> SignalWorkflowAsync(
        string @namespace, string workflowId, string? runId,
        string signalName, string jsonPayload,
        CancellationToken ct = default)
    {
        var client = await GetClientAsync(@namespace, ct);
        if (client is null)
        {
            logger.LogWarning("No client for namespace '{Namespace}' — cannot signal workflow", @namespace);
            return false;
        }

        var handle = client.GetWorkflowHandle(workflowId, runId: runId);
        var payload = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(jsonPayload);
        await handle.SignalAsync(signalName, new object?[] { (object?)payload });
        logger.LogInformation("Sent signal '{Signal}' to workflow {WorkflowId} in namespace {Namespace}", signalName, workflowId, @namespace);
        return true;
    }
}
