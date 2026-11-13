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
    // Each connected dashboard socket with the gate its sends go through: a WebSocket allows one
    // send at a time, and heartbeats, the poller and the state monitor all broadcast.
    private readonly ConcurrentDictionary<WebSocket, SemaphoreSlim> _sockets = new();

    /// <summary>
    /// How long one send may take before the client is treated as gone. Without it a client that
    /// stopped reading would hold its gate, and every later broadcast would wait behind it.
    /// </summary>
    internal TimeSpan SendTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Sockets currently registered. For tests.</summary>
    internal int SocketCount => _sockets.Count;
    private readonly TaskHistoryStore _taskHistory;
    private readonly ILogger<AgentRegistry> _logger;

    public AgentRegistry(TaskHistoryStore taskHistory, ILogger<AgentRegistry> logger)
    {
        _taskHistory = taskHistory;
        _logger = logger;
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
        };
        // Only add if not already present (heartbeat may have arrived first)
        if (_agents.TryAdd(dbAgent.Name, state))
            BroadcastIfChanged(state, force: true);
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

    public IReadOnlyCollection<AgentState> GetAll() =>
        _agents.Values.OrderBy(a => a.AgentName).ToList();

    public AgentState? Get(string agentName) =>
        _agents.TryGetValue(agentName, out var state) ? state : null;

    /// <summary>Removes an agent from the in-memory registry. Called after DB deletion to prevent ghost entries.</summary>
    public void Remove(string agentName) =>
        _agents.TryRemove(agentName, out _);

    public void AddSocket(WebSocket socket) => _sockets.TryAdd(socket, new SemaphoreSlim(1, 1));

    /// <summary>
    /// Forgets a socket. Its <c>/ws</c> handler calls this when the connection ends; a broadcast
    /// that finds a socket closed, or cannot send to it, does the same.
    /// </summary>
    public void RemoveSocket(WebSocket socket) => _sockets.TryRemove(socket, out _);

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
                // Through the socket's gate when it is registered: a broadcast may be on its way
                // to this socket at the same moment.
                if (!await SendAsync(socket, new ArraySegment<byte>(bytes)))
                    return;
            }
            catch
            {
                // Best-effort — ignore send errors for individual agent states
            }
        }
    }

    /// <summary>
    /// One send to one socket, after any send already in progress on it. False when the socket is
    /// not open or the send failed or timed out; the socket is then forgotten.
    /// </summary>
    private async Task<bool> SendAsync(WebSocket socket, ArraySegment<byte> message)
    {
        _sockets.TryGetValue(socket, out var gate);
        if (gate is not null) await gate.WaitAsync();
        try
        {
            if (socket.State != WebSocketState.Open)
            {
                RemoveSocket(socket);
                return false;
            }

            using var timeout = new CancellationTokenSource(SendTimeout);
            await socket.SendAsync(message, WebSocketMessageType.Text, endOfMessage: true, timeout.Token);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to send to WebSocket client ({Error}), removing it", ex.GetType().Name);
            RemoveSocket(socket);
            // A cancelled send leaves the socket aborted; make that explicit for any other failure.
            try { socket.Abort(); } catch { /* already gone */ }
            return false;
        }
        finally
        {
            gate?.Release();
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

    internal async Task BroadcastRawAsync(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var segment = new ArraySegment<byte>(bytes);
        var removed = 0;

        foreach (var socket in _sockets.Keys)
        {
            if (!await SendAsync(socket, segment))
                removed++;
        }

        if (removed > 0)
            _logger.LogDebug("Removed {Count} closed WebSocket connection(s)", removed);
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
}
