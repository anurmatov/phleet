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

/// <remarks>
/// <para>
/// <b>Turn-origin ledger contract (#394).</b> An executor given a <see cref="TurnOriginLedger"/>
/// records every period in which its provider may be acting, so a Telegram MCP tool send can be
/// journaled only when it provably answered a human:
/// </para>
/// <list type="bullet">
/// <item>Every entry point that runs the provider (<see cref="ExecuteAsync"/>,
/// <see cref="ReadInjectedTurnAnswersAsync"/>, <see cref="SendCommandAsync"/>) opens an interval
/// <b>immediately after</b> it acquires its turn lock and closes it in the <c>finally</c> that
/// releases the lock — never while waiting, never after release. It takes the origin the caller
/// set with <see cref="TurnOriginLedger.Pending"/>, or unknown when there is none (warmup, the CLI);
/// <see cref="SendCommandAsync"/> is always unknown. An executor without a lock (Gemini) spans each
/// provider process from start to exit instead.</item>
/// <item>A persistent process's stdout reader reports every turn-content event and every terminal
/// event to its <see cref="TurnOriginLedger.ProviderActivity"/>, which opens an unknown interval for
/// a turn the provider started on its own. Only that turn's terminal event, stdout EOF or a kill
/// that has completed (after <c>WaitForExitAsync</c>) closes it — never <see cref="RequestRestart"/>,
/// a cancelled reader, a <see cref="TryStopProcessAsync"/> that returned false, silence or a
/// timeout.</item>
/// </list>
/// </remarks>
public interface IAgentExecutor : IAsyncDisposable
{
    /// <summary>Send a task to the LLM process, streaming progress events.</summary>
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

    /// <summary>
    /// After a turn that had messages injected into it, reads the answers of any further turns
    /// the provider started on its own for those messages (#369), one result per such turn.
    /// Called only when an injection happened; the wait for a turn to start is bounded, so an
    /// absorbed injection costs a short pause and nothing else. Providers that always fold an
    /// injection into the running turn, or cannot inject, yield nothing.
    /// </summary>
    IAsyncEnumerable<AgentProgress> ReadInjectedTurnAnswersAsync(int injectedMessages, CancellationToken ct = default) =>
        NoInjectedTurnAnswersAsync();

    private static async IAsyncEnumerable<AgentProgress> NoInjectedTurnAnswersAsync()
    {
        await Task.CompletedTask;
        yield break;
    }

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
