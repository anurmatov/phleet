using System.Text.Json.Serialization;

namespace Fleet.Contracts;

/// <summary>
/// What an agent publishes on <see cref="OrchestratorBroker.Exchange"/> once at start-up
/// (<see cref="OrchestratorBroker.Registration"/>) and every 30 seconds and on each state change
/// after that (<see cref="OrchestratorBroker.Heartbeat"/>).
/// </summary>
/// <remarks>
/// <para>
/// PascalCase, with nulls written, except where a member says otherwise. The orchestrator reads it
/// case-insensitively, because <see cref="JournalHeartbeat"/> is camelCase inside it.
/// </para>
/// <para>
/// The members an agent leaves out are the ones an older image never sent: <see cref="Journal"/>
/// when the journal is off (#377), and <see cref="PlanSha256"/> for a provider with no harness plan
/// (#460). <see cref="Endpoint"/> and <see cref="Capabilities"/> are read by the orchestrator and
/// sent by no agent in this repository.
/// </para>
/// </remarks>
public sealed record AgentHeartbeat(
    string AgentName,
    string Status,
    DateTimeOffset Timestamp,
    string? CurrentTask = null,
    string? CurrentTaskId = null,
    string? Version = null,
    string? ContainerName = null,
    string? Role = null,
    string? Model = null,
    List<string>? Projects = null,
    long UptimeSeconds = 0,
    int QueuedCount = 0,
    QueuedMessageInfo[]? QueuedMessages = null,
    BackgroundTaskSummary[]? BackgroundTasks = null,
    // null = an agent that predates the field; false = headless; true = connected.
    bool? TelegramConnected = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    JournalHeartbeat? Journal = null,
    int PriorityQueuedCount = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? PlanSha256 = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Endpoint = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string[]? Capabilities = null);

/// <summary>
/// The conversation journal on an agent (#377): counts and ages only. Null on a heartbeat means the
/// journal is off or the agent predates it.
/// </summary>
/// <param name="AuthFailed">1 while the listener refuses the token, 0 otherwise.</param>
public sealed record JournalHeartbeat(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("spoolDepth")] int SpoolDepth,
    [property: JsonPropertyName("oldestAgeSeconds")] long? OldestAgeSeconds,
    [property: JsonPropertyName("dropped")] long Dropped,
    [property: JsonPropertyName("dead")] int Dead,
    [property: JsonPropertyName("authFailed")] int AuthFailed,
    [property: JsonPropertyName("bindingFailed")] int BindingFailed = 0);

/// <summary>A message waiting in an agent's queue. No chat or message identifier.</summary>
public sealed record QueuedMessageInfo(string Preview, string Source, DateTimeOffset QueuedAt, bool Priority = false);

/// <summary>An active background subagent task on an agent.</summary>
public sealed record BackgroundTaskSummary(
    string TaskId,
    string Description,
    string TaskType,
    int ElapsedSeconds,
    string? Summary);
