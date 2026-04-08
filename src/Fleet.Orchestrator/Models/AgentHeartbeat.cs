namespace Fleet.Orchestrator.Models;

/// <summary>
/// Message published by fleet agents on the fleet.orchestrator topic exchange.
/// Routing key: "heartbeat" or "registration".
/// </summary>
public sealed record AgentHeartbeat(
    string AgentName,
    string Status,
    DateTimeOffset Timestamp,
    string? CurrentTask = null,
    string? CurrentTaskId = null,
    string? Version = null,
    string? Endpoint = null,
    string? Role = null,
    string? Model = null,
    string? ContainerName = null,
    string[]? Capabilities = null,
    int QueuedCount = 0,
    QueuedMessageInfo[]? QueuedMessages = null,
    BackgroundTaskSummary[]? BackgroundTasks = null);

/// <summary>Snapshot of a queued message waiting to be processed by an agent.</summary>
public sealed record QueuedMessageInfo(string Preview, string Source, DateTimeOffset QueuedAt);

/// <summary>Summary of an active background subagent task, included in heartbeat messages.</summary>
public sealed record BackgroundTaskSummary(
    string TaskId,
    string Description,
    string TaskType,
    int ElapsedSeconds,
    string? Summary);

/// <summary>
/// In-memory snapshot of a known agent's last heartbeat, with computed staleness state.
/// </summary>
public sealed class AgentState
{
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DeadThreshold  = TimeSpan.FromSeconds(90);

    public required string AgentName     { get; init; }
    public string  ReportedStatus        { get; set; } = "unknown";
    public string? CurrentTask           { get; set; }
    /// <summary>Bridge taskId (format: {workflowId}/{step}) when executing a Temporal-delegated task.</summary>
    public string? CurrentTaskId         { get; set; }
    public string? Version               { get; set; }
    public string? Endpoint              { get; set; }
    public string? Role                  { get; set; }
    public string? Model                 { get; set; }
    public string? ContainerName         { get; set; }
    public string[]? Capabilities        { get; set; }
    public DateTimeOffset LastSeen       { get; set; }
    public DateTimeOffset RegisteredAt   { get; set; }
    /// <summary>When the current task started (set on task transition).</summary>
    public DateTimeOffset? TaskStartedAt { get; set; }
    /// <summary>When the agent's container was started (from Docker API State.StartedAt).</summary>
    public DateTimeOffset? ContainerStartedAt { get; set; }
    /// <summary>Number of messages waiting in the agent's FIFO queue.</summary>
    public int QueuedCount               { get; set; }
    /// <summary>Preview of up to 5 queued messages (oldest first).</summary>
    public QueuedMessageInfo[]? QueuedMessages { get; set; }
    /// <summary>Active background subagent tasks spawned via the Agent tool with run_in_background=true.</summary>
    public BackgroundTaskSummary[]? BackgroundTasks { get; set; }

    /// <summary>True when this entry was synthesized from DB config and has never sent a heartbeat.</summary>
    public bool IsDbOnly { get; set; }

    /// <summary>Host port allocated to this agent for cancel-request proxying (127.0.0.1:{HostPort}:8080).</summary>
    public int? HostPort { get; set; }

    /// <summary>Effective status factoring in last-seen time for stale/dead detection.</summary>
    public string EffectiveStatus
    {
        get
        {
            if (IsDbOnly) return "not_provisioned";
            var age = DateTimeOffset.UtcNow - LastSeen;
            return age > DeadThreshold  ? "dead"
                 : age > StaleThreshold ? "stale"
                 : ReportedStatus;
        }
    }
}
