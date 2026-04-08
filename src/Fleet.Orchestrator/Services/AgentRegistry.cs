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
    private readonly ConcurrentDictionary<string, (string Status, string? Task, int QueuedCount, int BgTaskCount)> _lastBroadcast = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentBag<WebSocket> _sockets = [];
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
                    QueuedMessages   = heartbeat.QueuedMessages,
                    BackgroundTasks  = heartbeat.BackgroundTasks,
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
                existing.QueuedMessages = heartbeat.QueuedMessages;
                existing.BackgroundTasks = heartbeat.BackgroundTasks;
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
        var prev = _lastBroadcast.GetOrAdd(state.AgentName, _ => ("", null, 0, 0));

        // Dedup is by count only, not content — summary/elapsed changes within a stable-count
        // window will not trigger a push and will instead wait for the next heartbeat that
        // changes the count (or status/task).
        if (!force && prev.Status == effective && prev.Task == task && prev.QueuedCount == queued && prev.BgTaskCount == bgCount)
            return;

        _lastBroadcast[state.AgentName] = (effective, task, queued, bgCount);
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
}
