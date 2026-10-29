using Fleet.Orchestrator.Helpers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Models;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// Tracks agent states and broadcasts updates to connected WebSocket clients
/// only when the effective status, current task, or reported fields change.
/// Also detects task transitions and persists them to TaskHistoryStore.
/// </summary>
public sealed class AgentRegistry
{
    private readonly ConcurrentDictionary<string, AgentState> _agents = new(StringComparer.OrdinalIgnoreCase);
    // Track last-broadcast effective status + task + queue count + bg task count per agent to avoid duplicate pushes
    private readonly ConcurrentDictionary<string, BroadcastKey> _lastBroadcast = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentBag<WebSocket> _sockets = [];
    private readonly TaskHistoryStore _taskHistory;
    private readonly ILogger<AgentRegistry> _logger;
    private readonly string? _backupDir;
    // #492 review F11/F12: live overlays applied on every status read, each source re-read at most once per window.
    private readonly ConcurrentDictionary<string, (string SandboxId, ManagedContainers Seen)> _observedContainers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _backupGate = new();
    private (DateTimeOffset? LastSuccess, string State) _backupStamp = (null, "unknown");
    private DateTimeOffset _backupReadAt = DateTimeOffset.MinValue;

    /// <summary>Longest a cached gateway backup stamp read is served before the status path re-reads it.</summary>
    internal TimeSpan ManagedRefreshWindow { get; init; } = TimeSpan.FromSeconds(30);
    internal Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;

    public AgentRegistry(TaskHistoryStore taskHistory, ILogger<AgentRegistry> logger, IConfiguration? config = null)
    {
        _taskHistory = taskHistory;
        _logger = logger;
        _backupDir = config?["StockOpenShell:BackupDir"];
    }

    /// <summary>
    /// Pre-populates an agent entry from DB config. Only runs if the agent isn't already tracked.
    /// The agent appears as "offline" until a real heartbeat arrives.
    /// </summary>
    public void PreloadFromDbConfig(Agent dbAgent)
    {
        var now = DateTimeOffset.UtcNow;
        var state = new AgentState
        {
            AgentName     = dbAgent.Name,
            ReportedStatus = "offline",
            Role          = dbAgent.Role,
            Model         = dbAgent.Model,
            ContainerName = dbAgent.ContainerName,
            HostPort      = dbAgent.HostPort,
            LastSeen      = DateTimeOffset.MinValue,
            RegisteredAt  = now,
            IsDbOnly      = true,
            RuntimeKind = dbAgent.RuntimeKind,
            AppliedRuntimeKind = dbAgent.AppliedRuntimeKind,
            RuntimePending = AgentPatchHelpers.RuntimePending(dbAgent),
        };
        (state.ManagedState, state.ManagedErrors, state.Managed) = ManagedView(dbAgent.AppliedRuntimeBinding);
        ApplyManagedLive(state);
        // Only add if not already present (heartbeat may have arrived first)
        if (_agents.TryAdd(dbAgent.Name, state))
            BroadcastIfChanged(state, force: true);
        else UpdateRuntimeSelection(dbAgent);
    }

    public void UpdateRuntimeSelection(Agent agent)
    {
        if (!_agents.TryGetValue(agent.Name, out var state)) return;
        state.RuntimeKind = agent.RuntimeKind;
        state.AppliedRuntimeKind = agent.AppliedRuntimeKind;
        state.RuntimePending = AgentPatchHelpers.RuntimePending(agent);
        (state.ManagedState, state.ManagedErrors, state.Managed) = ManagedView(agent.AppliedRuntimeBinding);
        ApplyManagedLive(state);
        BroadcastIfChanged(state, force: true);
    }

    /// <summary>
    /// Records a container observation made by the periodic status refresh (not a lifecycle operation).
    /// It overlays the persisted identities only for the same stock sandbox and only while newer.
    /// </summary>
    public void ObserveManagedContainers(string agentName, string sandboxId, ManagedContainers observed)
    {
        _observedContainers[agentName] = (sandboxId, observed);
        if (_agents.TryGetValue(agentName, out var state) && ApplyManagedLive(state))
            BroadcastIfChanged(state, force: true);
    }

    /// <summary>Status read path: refreshes backup stamps (cached) and container overlays on every managed entry.</summary>
    private void RefreshManagedLive()
    {
        foreach (var state in _agents.Values)
            if (state.Managed is not null && ApplyManagedLive(state))
                BroadcastIfChanged(state, force: true);
    }

    private (DateTimeOffset? LastSuccess, string State) BackupStamp()
    {
        lock (_backupGate)
        {
            var now = Clock();
            if (now - _backupReadAt >= ManagedRefreshWindow || now < _backupReadAt)
            {
                _backupStamp = ManagedBackupStamp.Read(_backupDir);
                _backupReadAt = now;
            }
            return _backupStamp;
        }
    }

    /// <summary>Applies the live overlays to one entry; true when a visible field changed.</summary>
    private bool ApplyManagedLive(AgentState state)
    {
        if (state.Managed is not { } view) return false;
        var backup = BackupStamp();
        var next = view with { BackupLastSuccessAt = backup.LastSuccess, BackupState = backup.State };
        if (_observedContainers.TryGetValue(state.AgentName, out var live) && view.AppliedSandboxId is not null
            && live.SandboxId == view.AppliedSandboxId && live.Seen is var seen
            && (view.ContainersObservedAt is not { } persisted || seen.ObservedAt > persisted))
            next = next with
            {
                WorkloadContainerId = seen.WorkloadId,
                SupervisorContainerId = seen.SupervisorId,
                ContainerStatus = seen.Status,
                ContainersObservedAt = seen.ObservedAt,
            };
        if (next == view) return false;
        state.Managed = next;
        return true;
    }

    public void UpdateAgent(AgentHeartbeat heartbeat)
    {
        var isNew = !_agents.ContainsKey(heartbeat.AgentName);

        var state = _agents.AddOrUpdate(
            heartbeat.AgentName,
            _ =>
            {
                var s = new AgentState
                {
                    AgentName        = heartbeat.AgentName,
                    ReportedStatus   = heartbeat.Status,
                    CurrentTask      = heartbeat.CurrentTask,
                    CurrentTaskId    = heartbeat.CurrentTaskId,
                    Version          = heartbeat.Version,
                    Endpoint         = heartbeat.Endpoint,
                    Role             = heartbeat.Role,
                    Model            = heartbeat.Model,
                    ContainerName    = heartbeat.ContainerName,
                    Capabilities     = heartbeat.Capabilities,
                    LastSeen         = heartbeat.Timestamp,
                    RegisteredAt     = heartbeat.Timestamp,
                    QueuedCount      = heartbeat.QueuedCount,
                    PriorityQueuedCount = heartbeat.PriorityQueuedCount,
                    QueuedMessages   = heartbeat.QueuedMessages,
                    BackgroundTasks  = heartbeat.BackgroundTasks,
                    Journal          = heartbeat.Journal,
                    StartedPlanSha256 = heartbeat.PlanSha256,
                };
                // New agent already doing a task — record start time
                if (heartbeat.CurrentTask is not null)
                    s.TaskStartedAt = heartbeat.Timestamp;
                return s;
            },
            (_, existing) =>
            {
                var oldTask = existing.CurrentTask;
                var newTask = heartbeat.CurrentTask;

                // Detect task transition
                if (oldTask != newTask)
                {
                    // Completed task
                    if (oldTask is not null && existing.TaskStartedAt.HasValue)
                    {
                        var record = new TaskRecord(
                            AgentName:  existing.AgentName,
                            TaskText:   oldTask,
                            StartedAt:  existing.TaskStartedAt.Value,
                            EndedAt:    heartbeat.Timestamp);
                        _taskHistory.Record(record);
                        _logger.LogInformation("Task completed for {Agent}: duration={Duration:F1}s",
                            existing.AgentName, record.DurationSeconds);
                    }

                    // New task started
                    existing.TaskStartedAt = newTask is not null ? heartbeat.Timestamp : null;
                }

                existing.IsDbOnly       = false;
                existing.ReportedStatus = heartbeat.Status;
                existing.CurrentTask    = newTask;
                existing.CurrentTaskId  = heartbeat.CurrentTaskId;
                existing.Version        = heartbeat.Version;
                existing.LastSeen       = heartbeat.Timestamp;
                existing.QueuedCount    = heartbeat.QueuedCount;
                existing.PriorityQueuedCount = heartbeat.PriorityQueuedCount;
                existing.QueuedMessages = heartbeat.QueuedMessages;
                existing.BackgroundTasks = heartbeat.BackgroundTasks;
                existing.Journal         = heartbeat.Journal;
                // #460: every report carries it, so an image without it (or a rollback onto one) reads null.
                existing.StartedPlanSha256 = heartbeat.PlanSha256;
                // Update registration fields only when provided (registration messages)
                if (heartbeat.Endpoint      is not null) existing.Endpoint      = heartbeat.Endpoint;
                if (heartbeat.Role          is not null) existing.Role          = heartbeat.Role;
                if (heartbeat.Model         is not null) existing.Model         = heartbeat.Model;
                if (heartbeat.ContainerName is not null) existing.ContainerName = heartbeat.ContainerName;
                if (heartbeat.Capabilities  is not null) existing.Capabilities  = heartbeat.Capabilities;
                return existing;
            });

        _logger.LogDebug("Agent {Agent} updated: reportedStatus={Status}", state.AgentName, state.ReportedStatus);
        BroadcastIfChanged(state, force: isNew);
    }

    /// <summary>Called by the state monitor when staleness is detected.</summary>
    public void BroadcastStateIfChanged(AgentState state)
        => BroadcastIfChanged(state, force: false);

    /// <summary>Updates the container start time for an agent. No-ops if agent not found.</summary>
    public void UpdateContainerStartedAt(string agentName, DateTimeOffset? startedAt)
    {
        if (_agents.TryGetValue(agentName, out var state))
            state.ContainerStartedAt = startedAt;
    }

    public IReadOnlyCollection<AgentState> GetAll()
    {
        RefreshManagedLive();
        return _agents.Values.OrderBy(a => a.AgentName).ToList();
    }

    public AgentState? Get(string agentName)
    {
        if (!_agents.TryGetValue(agentName, out var state)) return null;
        if (ApplyManagedLive(state)) BroadcastIfChanged(state, force: true);
        return state;
    }

    /// <summary>Removes an agent from the in-memory registry. Called after DB deletion to prevent ghost entries.</summary>
    public void Remove(string agentName)
    {
        _agents.TryRemove(agentName, out _);
        _observedContainers.TryRemove(agentName, out _);
    }

    public void AddSocket(WebSocket socket) => _sockets.Add(socket);

    /// <summary>
    /// Sends the current state of every known agent to a single newly connected socket.
    /// Called immediately after AddSocket so new/reconnected clients don't wait for individual
    /// per-agent heartbeat broadcasts to populate their agent cards.
    /// Errors are swallowed — a failed send must not crash the WS handler.
    /// </summary>
    public async Task BroadcastAllToSocket(WebSocket socket)
    {
        foreach (var state in _agents.Values)
        {
            if (socket.State != WebSocketState.Open)
                return;

            try
            {
                var json = JsonSerializer.Serialize(state, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                var bytes = Encoding.UTF8.GetBytes(json);
                await socket.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    CancellationToken.None);
            }
            catch
            {
                // Best-effort — ignore send errors for individual agent states
            }
        }
    }

    /// <summary>Pushes a typed workflow update to all connected WebSocket clients.</summary>
    public void BroadcastWorkflows(IReadOnlyList<WorkflowSummary> workflows)
    {
        var envelope = new { type = "workflows", data = workflows };
        var json = JsonSerializer.Serialize(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        _ = BroadcastRawAsync(json);
    }

    private void BroadcastIfChanged(AgentState state, bool force)
    {
        var effective = state.EffectiveStatus;
        var task = state.CurrentTask;
        var queued = state.QueuedCount;
        var bgCount = state.BackgroundTasks?.Length ?? 0;
        var current = new BroadcastKey(
            effective, task, queued, bgCount,
            state.Journal?.Enabled,
            state.Journal?.SpoolDepth,
            state.Journal?.OldestAgeSeconds,
            state.Journal?.Dropped,
            state.Journal?.Dead,
            state.Journal?.AuthFailed,
            state.Journal?.BindingFailed, state.PriorityQueuedCount);
        var prev = _lastBroadcast.GetOrAdd(state.AgentName, _ => default);

        // Dedup is by count only, not content — summary/elapsed changes within a stable-count
        // window will not trigger a push and will instead wait for the next heartbeat that
        // changes the count (or status/task).
        if (!force && prev == current)
            return;

        _lastBroadcast[state.AgentName] = current;
        _ = BroadcastAsync(state);
    }

    private Task BroadcastAsync(AgentState state)
    {
        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return BroadcastRawAsync(json);
    }

    private async Task BroadcastRawAsync(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var segment = new ArraySegment<byte>(bytes);
        var dead = new List<WebSocket>();

        foreach (var socket in _sockets)
        {
            if (socket.State != WebSocketState.Open)
            {
                dead.Add(socket);
                continue;
            }

            try
            {
                await socket.SendAsync(segment, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send to WebSocket client, marking for removal");
                dead.Add(socket);
            }
        }

        if (dead.Count > 0)
            _logger.LogDebug("Skipped {Count} closed WebSocket connection(s)", dead.Count);
    }

    private readonly record struct BroadcastKey(
        string? Status,
        string? Task,
        int QueuedCount,
        int BgTaskCount,
        bool? JournalEnabled,
        long? JournalSpoolDepth,
        long? JournalOldestAgeSeconds,
        long? JournalDropped,
        long? JournalDead,
        int? JournalAuthFailed,
        int? JournalBindingFailed,
        int PriorityQueuedCount);

    /// <summary>Content-free view of the managed record: state and error codes only.</summary>
    internal static (string? State, IReadOnlyList<string>? Errors, ManagedStatusView? Status) ManagedView(string? binding, string? backupDir = null)
    {
        if (string.IsNullOrEmpty(binding)) return (null, null, null);
        try
        {
            var record = ManagedRecord.Parse(binding);
            return (record.Operation is not null ? "busy" : record.State, record.Errors, ManagedStatus(record, backupDir));
        }
        catch (System.Text.Json.JsonException) { return ("unreadable", null, null); }
    }

    /// <summary>Applied-binding/lifecycle summary. Operation is reduced to its kind (no owner instance).</summary>
    internal static ManagedStatusView ManagedStatus(ManagedRecord record, string? backupDir = null)
    {
        var operation = record.Operation?.Split(':')[0];
        var admission = operation switch
        {
            "revoke" => "revoking",
            "stop" => "stopping",
            "start" or "apply" or "restart_hold" => "applying",
            not null => "busy",
            _ => record.State switch
            {
                "applied" => "active",
                "none" => "none",
                _ => record.State,
            },
        };
        var applied = record.Applied;
        var backup = ManagedBackupStamp.Read(backupDir);
        return new ManagedStatusView(
            record.State, record.Errors, admission,
            // Pending means an operation is in flight: a held candidate or a stale "applying" with no
            // operation is an unresolved hold (shown by its state/errors), not a permanent spinner.
            Pending: operation is not null,
            Operation: operation,
            AppliedSandboxId: applied?.SandboxId,
            AppliedSandboxName: applied?.SandboxName,
            Epoch: applied?.Epoch,
            PolicyVersionId: applied?.PolicyVersionId,
            PolicySha256: applied?.PolicySha256,
            GatewayGeneration: applied?.GatewayGeneration,
            Providers: applied?.AllProviders() ?? [])
        {
            WorkloadContainerId = applied?.Containers?.WorkloadId,
            SupervisorContainerId = applied?.Containers?.SupervisorId,
            ContainerStatus = applied?.Containers?.Status,
            ContainersObservedAt = applied?.Containers?.ObservedAt,
            BackupLastSuccessAt = backup.LastSuccess,
            BackupState = backup.State,
        };
    }
}
