using System.Runtime.CompilerServices;
using Fleet.Agent.Models;
using Fleet.Agent.Services;

namespace Fleet.Agent.Tests.Harness;

/// <summary>
/// An <see cref="IAgentExecutor"/> that replays a recorded <see cref="AgentProgress"/> sequence
/// (D2, L2).
///
/// <para>Two very different uses, and the matrix keeps them apart:</para>
/// <list type="bullet">
/// <item>Fed a sequence captured by <see cref="ProviderFrameReplay"/> at L1, it carries real
/// frame-derived progress into the runtime, so the resulting row records a frame → progress →
/// client-event chain end to end.</item>
/// <item>Fed a sequence the harness authored, it exercises runtime behaviour no provider frame was
/// involved in. Every such row is <c>inferred</c>, reason "scripted progress, no frame replay" —
/// calling it <c>supported</c> would assert a frame the test never saw.</item>
/// </list>
/// </summary>
internal sealed class ScriptedExecutor : IAgentExecutor
{
    private readonly Queue<IReadOnlyList<AgentProgress>> _turns = new();
    private readonly TaskCompletionSource _firstTurnStarted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _turnCount;

    public ScriptedExecutor(params IReadOnlyList<AgentProgress>[] turns)
    {
        foreach (var turn in turns)
            _turns.Enqueue(turn);
    }

    /// <summary>Append another turn's script after construction.</summary>
    public ScriptedExecutor Enqueue(IReadOnlyList<AgentProgress> turn)
    {
        lock (_turns) _turns.Enqueue(turn);
        return this;
    }

    private readonly TaskCompletionSource _gate =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Block the turn until the cancellation token trips, yielding nothing (S7).</summary>
    public bool BlockUntilCancelled { get; init; }

    /// <summary>
    /// Hold the FIRST turn open until <see cref="Release"/> is called or the token trips, so a test
    /// can submit further messages while a turn is genuinely running (S5, S6, S10).
    /// </summary>
    public bool BlockFirstTurnUntilReleased { get; init; }

    /// <summary>Let a turn held by <see cref="BlockFirstTurnUntilReleased"/> finish its script.</summary>
    public void Release() => _gate.TrySetResult();

    /// <summary>Throw out of the enumerator after yielding the scripted prefix (S9 at L2).</summary>
    public Exception? ThrowAfterScript { get; init; }

    /// <summary>What <see cref="TryInjectMessageAsync"/> reports. Defaults to the interface default.</summary>
    public MidTurnInjectionResult InjectionResult { get; set; } = MidTurnInjectionResult.Unsupported;

    /// <summary>Completes when the first turn's enumerator has begun. Lets a test steer a live turn.</summary>
    public Task FirstTurnStarted => _firstTurnStarted.Task;

    /// <summary>Every task string handed to <see cref="ExecuteAsync"/>, in order.</summary>
    public List<string> Tasks { get; } = [];

    /// <summary>Every task string handed to <see cref="TryInjectMessageAsync"/>, in order.</summary>
    public List<string> Injected { get; } = [];

    /// <summary>How many times <see cref="RequestRestart"/> was called (S16 at L2).</summary>
    public int RestartRequests { get; private set; }

    public async IAsyncEnumerable<AgentProgress> ExecuteAsync(
        string task,
        IReadOnlyList<MessageImage>? images = null,
        IReadOnlyList<MessageDocument>? documents = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        Tasks.Add(task);
        var turnOrdinal = Interlocked.Increment(ref _turnCount);
        if (turnOrdinal == 1)
            _firstTurnStarted.TrySetResult();

        if (BlockFirstTurnUntilReleased && turnOrdinal == 1)
        {
            // A pure wait on an explicit release, never a timed one.
            await using var gateRegistration = ct.Register(() => _gate.TrySetCanceled(ct));
            await _gate.Task;
        }

        IReadOnlyList<AgentProgress> script;
        lock (_turns) script = _turns.Count > 0 ? _turns.Dequeue() : [];

        foreach (var progress in script)
        {
            ct.ThrowIfCancellationRequested();
            yield return progress;
        }

        if (ThrowAfterScript is not null)
            throw ThrowAfterScript;

        if (BlockUntilCancelled)
        {
            // A pure cancellation wait, never a timed one. There is deliberately no Task.Delay
            // anywhere in this harness: the turn ends when the token trips and at no other time
            // (AC15).
            var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var registration = ct.Register(() => blocked.TrySetCanceled(ct));
            await blocked.Task;
        }

        await Task.CompletedTask;
    }

    public Task<MidTurnInjectionResult> TryInjectMessageAsync(
        string task,
        IReadOnlyList<MessageImage>? images = null,
        IReadOnlyList<MessageDocument>? documents = null,
        CancellationToken ct = default)
    {
        Injected.Add(task);
        return Task.FromResult(InjectionResult);
    }

    public Task StopProcessAsync() => Task.CompletedTask;

    public Task<bool> TryStopProcessAsync() => Task.FromResult(false);

    public void RequestRestart() => RestartRequests++;

    public async IAsyncEnumerable<AgentProgress> SendCommandAsync(
        string command, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public bool IsProcessWarm => false;

    public string? LastSessionId => null;

    public DateTimeOffset LastActivity => DateTimeOffset.UnixEpoch;

    public IReadOnlyCollection<BackgroundTaskInfo> GetActiveBackgroundTasks() => [];

    public Task<bool> CancelBackgroundTaskAsync(string taskId, CancellationToken ct = default) =>
        Task.FromResult(false);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ── Progress builders shared by the L2-only scenarios ─────────────────────

    /// <summary>A terminal <c>result</c> carrying assistant text.</summary>
    public static AgentProgress Final(string text) => new()
    {
        EventType = "result",
        Summary = text,
        FinalResult = text,
        IsSignificant = true,
    };

    /// <summary>A terminal <c>result</c> flagged as an executor error.</summary>
    public static AgentProgress ErrorFinal(string text) => new()
    {
        EventType = "result",
        Summary = text,
        FinalResult = text,
        IsErrorResult = true,
        IsSignificant = true,
    };

    /// <summary>
    /// A terminal <c>result</c> reporting that the provider process exited mid-turn, which is the
    /// signal <c>TaskManager</c> uses to redeliver pending injections (S10).
    /// </summary>
    public static AgentProgress ProcessExit(string text) => new()
    {
        EventType = "result",
        Summary = text,
        FinalResult = text,
        IsErrorResult = true,
        IsProcessExit = true,
        IsSignificant = true,
    };

    /// <summary>A significant tool-use progress event.</summary>
    public static AgentProgress Tool(string toolName, string toolArgs = "{}") => new()
    {
        EventType = "tool_use",
        Summary = $"Using {toolName}",
        ToolName = toolName,
        ToolArgs = toolArgs,
        IsSignificant = true,
    };
}
