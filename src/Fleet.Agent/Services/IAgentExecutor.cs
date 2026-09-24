using Fleet.Agent.Models;

namespace Fleet.Agent.Services;

/// <summary>
/// Abstraction over the LLM process that executes agent tasks.
/// Implementations manage process lifecycle, streaming I/O, and session state.
/// </summary>
public enum MidTurnInjectionStatus
{
    Injected,
    Unsupported,
    NoActiveTurn,
    Failed,
}

public sealed record MidTurnInjectionResult(MidTurnInjectionStatus Status, string? Error = null)
{
    public static MidTurnInjectionResult Injected { get; } = new(MidTurnInjectionStatus.Injected);
    public static MidTurnInjectionResult Unsupported { get; } = new(MidTurnInjectionStatus.Unsupported);
    public static MidTurnInjectionResult NoActiveTurn(string? error = null) => new(MidTurnInjectionStatus.NoActiveTurn, error);
    public static MidTurnInjectionResult Failed(string error) => new(MidTurnInjectionStatus.Failed, error);
}

public interface IAgentExecutor : IAsyncDisposable
{
    /// <summary>
    /// Send a task to the LLM process, streaming progress events. Emits exactly one
    /// <see cref="AgentProgress.PromptAcceptedEventType"/> event per call, once the provider has the
    /// prompt, and none when the call fails before that point.
    /// </summary>
    IAsyncEnumerable<AgentProgress> ExecuteAsync(
        string task,
        IReadOnlyList<MessageImage>? images = null,
        IReadOnlyList<MessageDocument>? documents = null,
        CancellationToken ct = default);

    /// <summary>
    /// Attempts to deliver additional user input into the currently active turn without
    /// waiting for that turn to finish. Providers without a live injection primitive
    /// return Unsupported so TaskManager can queue the message for the next turn.
    /// </summary>
    Task<MidTurnInjectionResult> TryInjectMessageAsync(
        string task,
        IReadOnlyList<MessageImage>? images = null,
        IReadOnlyList<MessageDocument>? documents = null,
        CancellationToken ct = default) =>
        Task.FromResult(MidTurnInjectionResult.Unsupported);

    /// <summary>Stop the running process gracefully.</summary>
    Task StopProcessAsync();

    /// <summary>Try to stop the process; returns false if it wasn't running.</summary>
    Task<bool> TryStopProcessAsync();

    /// <summary>Request a process restart on the next execution.</summary>
    void RequestRestart();

    /// <summary>
    /// Send a raw command (e.g. /compact, /status) directly to the executor's stdin.
    /// Returns the result text, or null if the process isn't running.
    /// </summary>
    IAsyncEnumerable<AgentProgress> SendCommandAsync(string command, CancellationToken ct = default);

    /// <summary>
    /// True when the process is alive and has context in memory,
    /// so callers can skip re-sending redundant context.
    /// </summary>
    bool IsProcessWarm { get; }

    /// <summary>Session/resume token from the last execution.</summary>
    string? LastSessionId { get; }

    /// <summary>
    /// Incremented every time the provider compacts the live conversation (#347). The project
    /// context ledger resets when this moves, because an attachment that was summarised away is
    /// no longer in the model's context. Providers without compaction keep it at zero.
    /// </summary>
    /// <remarks>
    /// Default-implemented so executors that predate the ledger (test doubles included) compile
    /// unchanged and report "never compacted".
    /// </remarks>
    int CompactionEpoch => 0;

    /// <summary>When the last task was sent or received.</summary>
    DateTimeOffset LastActivity { get; }

    /// <summary>
    /// Snapshot of currently active background subagent tasks.
    /// Populated from task_started/task_progress/task_notification NDJSON events.
    /// </summary>
    IReadOnlyCollection<BackgroundTaskInfo> GetActiveBackgroundTasks();

    /// <summary>
    /// Request cancellation of a specific background subagent task by ID.
    /// Sends a TaskStop command to the Claude process via stdin.
    /// Returns false if the task ID is not found in the active set.
    /// </summary>
    Task<bool> CancelBackgroundTaskAsync(string taskId, CancellationToken ct = default);
}
