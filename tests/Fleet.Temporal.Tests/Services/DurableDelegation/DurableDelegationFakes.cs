using Fleet.Temporal.Configuration;
using Fleet.Temporal.Models;
using Fleet.Temporal.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fleet.Temporal.Tests.Services.DurableDelegation;

/// <summary>A clock the test moves.</summary>
internal sealed class FakeClock : TimeProvider
{
    private long _ticks = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}

/// <summary>Stands in for Temporal: records every call and answers what the test scripted.</summary>
internal sealed class FakeGateway : IAsyncActivityGateway
{
    public List<string> Calls { get; } = [];
    public List<(string TaskId, AgentTaskResult Result)> Completed { get; } = [];
    public List<(string TaskId, Exception Failure)> Failed { get; } = [];
    public List<string> Cancelled { get; } = [];

    /// <summary>When each heartbeat was made, on <see cref="Clock"/>.</summary>
    public List<(string TaskId, DateTimeOffset At)> HeartbeatTimes { get; } = [];

    public TimeProvider Clock { get; set; } = TimeProvider.System;

    public ActivityCallOutcome Heartbeat { get; set; } = ActivityCallOutcome.Ok;
    public ActivityCallOutcome Complete { get; set; } = ActivityCallOutcome.Ok;
    public ActivityCallOutcome Fail { get; set; } = ActivityCallOutcome.Ok;
    public ActivityCallOutcome ReportCancellation { get; set; } = ActivityCallOutcome.Ok;

    /// <summary>Overrides <see cref="Heartbeat"/> for one task id.</summary>
    public Dictionary<string, ActivityCallOutcome> HeartbeatFor { get; } = new(StringComparer.Ordinal);

    public int Count(string call)
    {
        lock (Calls) return Calls.Count(c => c.StartsWith(call + ":", StringComparison.Ordinal));
    }

    public Task<ActivityCallOutcome> HeartbeatAsync(PendingDelegation delegation, CancellationToken ct)
    {
        lock (Calls)
        {
            Calls.Add($"heartbeat:{delegation.TaskId}");
            HeartbeatTimes.Add((delegation.TaskId, Clock.GetUtcNow()));
        }

        return Task.FromResult(HeartbeatFor.GetValueOrDefault(delegation.TaskId, Heartbeat));
    }

    public Task<ActivityCallOutcome> CompleteAsync(PendingDelegation delegation, AgentTaskResult result, CancellationToken ct)
    {
        lock (Calls)
        {
            Calls.Add($"complete:{delegation.TaskId}");
            if (Complete == ActivityCallOutcome.Ok) Completed.Add((delegation.TaskId, result));
        }

        return Task.FromResult(Complete);
    }

    public Task<ActivityCallOutcome> FailAsync(PendingDelegation delegation, Exception failure, CancellationToken ct)
    {
        lock (Calls)
        {
            Calls.Add($"fail:{delegation.TaskId}");
            if (Fail == ActivityCallOutcome.Ok) Failed.Add((delegation.TaskId, failure));
        }

        return Task.FromResult(Fail);
    }

    public Task<ActivityCallOutcome> ReportCancellationAsync(PendingDelegation delegation, CancellationToken ct)
    {
        lock (Calls)
        {
            Calls.Add($"report-cancellation:{delegation.TaskId}");
            if (ReportCancellation == ActivityCallOutcome.Ok) Cancelled.Add(delegation.TaskId);
        }

        return Task.FromResult(ReportCancellation);
    }
}

/// <summary>
/// Stands in for the broker and the orchestrator: records what would have been sent. A test can
/// make any send slow, or make it never return, through <see cref="BeforeSend"/>.
/// </summary>
internal sealed class FakeRelay : IDelegationRelay
{
    private readonly Lock _lock = new();
    private readonly List<(string Agent, string TaskId, string Instruction, string? Repo)> _published = [];
    private readonly List<(string Agent, string TaskId)> _cancels = [];
    private readonly List<(string TaskId, string Detail)> _timeoutReports = [];
    private readonly List<string> _started = [];
    private int _publishAttempts;
    private int _inProgress;
    private int _mostInProgress;

    public IReadOnlyList<(string Agent, string TaskId, string Instruction, string? Repo)> Published
    {
        get { lock (_lock) return [.. _published]; }
    }

    public IReadOnlyList<(string Agent, string TaskId)> Cancels
    {
        get { lock (_lock) return [.. _cancels]; }
    }

    public IReadOnlyList<(string TaskId, string Detail)> TimeoutReports
    {
        get { lock (_lock) return [.. _timeoutReports]; }
    }

    /// <summary>Every send that was started, finished or not: <c>publish:id</c>, <c>cancel:id</c>, <c>timeout:id</c>.</summary>
    public IReadOnlyList<string> Started
    {
        get { lock (_lock) return [.. _started]; }
    }

    /// <summary>The most sends that were in progress at one moment.</summary>
    public int MostInProgress => Volatile.Read(ref _mostInProgress);

    /// <summary>When set, every publish throws it.</summary>
    public Exception? PublishFailure { get; set; }

    /// <summary>Whether the bridge has its configuration yet; a relay that is not ready is asked for nothing.</summary>
    public bool Ready { get; set; } = true;

    public int PublishAttempts => Volatile.Read(ref _publishAttempts);

    /// <summary>
    /// Awaited at the start of every send with its kind (<c>publish</c>, <c>cancel</c>,
    /// <c>timeout</c>) and the record, before anything is recorded as sent.
    /// </summary>
    public Func<string, PendingDelegation, CancellationToken, Task>? BeforeSend { get; set; }

    /// <summary>What <see cref="SendCancelAsync"/> answers when the cancel went out.</summary>
    public CancelDelivery CancelOutcome { get; set; } = CancelDelivery.Settled;

    /// <summary>Where what is sent arrives, when a test wants to see it from the agent's side.</summary>
    public TaskIdOnlyReceiver? Receiver { get; set; }

    /// <summary>Every call's start and end, in order: <c>start:cancel:id</c>, <c>end:cancel:id</c>.</summary>
    public IReadOnlyList<string> Events
    {
        get { lock (_lock) return [.. _events]; }
    }

    private readonly List<string> _events = [];
    private readonly List<(string Agent, string TaskId, string? ExecutionId)> _cancelRequests = [];

    /// <summary>Every cancel that went out, with the execution id it named (null if none).</summary>
    public IReadOnlyList<(string Agent, string TaskId, string? ExecutionId)> CancelRequests
    {
        get { lock (_lock) return [.. _cancelRequests]; }
    }

    /// <summary>
    /// The execution id of a record, null when it has none. Read by name so that these tests also
    /// build against the bridge as it was before records had one; there it is always null, which
    /// is what that bridge put on the wire.
    /// </summary>
    internal static string? ExecutionIdOf(PendingDelegation delegation) =>
        typeof(PendingDelegation).GetProperty("ExecutionId")?.GetValue(delegation) is string { Length: > 0 } id ? id : null;

    private async Task EnterAsync(string kind, PendingDelegation delegation, CancellationToken ct)
    {
        lock (_lock)
        {
            _started.Add($"{kind}:{delegation.CurrentTaskId}");
            _events.Add($"start:{kind}:{delegation.CurrentTaskId}");
        }

        var now = Interlocked.Increment(ref _inProgress);
        int seen;
        while (now > (seen = Volatile.Read(ref _mostInProgress)))
            Interlocked.CompareExchange(ref _mostInProgress, now, seen);

        try
        {
            if (BeforeSend is { } before) await before(kind, delegation, ct);
        }
        finally
        {
            Interlocked.Decrement(ref _inProgress);
        }
    }

    private void Ended(string kind, PendingDelegation delegation)
    {
        lock (_lock) _events.Add($"end:{kind}:{delegation.CurrentTaskId}");
    }

    public async Task PublishDirectiveAsync(PendingDelegation delegation, CancellationToken ct)
    {
        Interlocked.Increment(ref _publishAttempts);
        try
        {
            await EnterAsync("publish", delegation, ct);
            if (PublishFailure is not null) throw PublishFailure;
            lock (_lock)
            {
                _published.Add((delegation.Agent, delegation.CurrentTaskId, delegation.CurrentInstruction, delegation.Repo));
                Receiver?.Directive(delegation.Agent, delegation.CurrentTaskId, delegation.CurrentInstruction, ExecutionIdOf(delegation));
            }
        }
        finally
        {
            Ended("publish", delegation);
        }
    }

    /// <summary>
    /// Like the real relay it never throws. A call stopped through <paramref name="ct"/> before it
    /// went out delivers nothing and cannot say what happened.
    /// </summary>
    public async Task<CancelDelivery> SendCancelAsync(PendingDelegation delegation, CancellationToken ct = default)
    {
        try
        {
            await EnterAsync("cancel", delegation, ct);
            lock (_lock)
            {
                _cancels.Add((delegation.Agent, delegation.CurrentTaskId));
                _cancelRequests.Add((delegation.Agent, delegation.CurrentTaskId, ExecutionIdOf(delegation)));
                Receiver?.Cancel(delegation.Agent, delegation.CurrentTaskId, ExecutionIdOf(delegation));
            }

            return CancelOutcome;
        }
        catch (OperationCanceledException)
        {
            return CancelDelivery.Unknown;
        }
        finally
        {
            Ended("cancel", delegation);
        }
    }

    public async Task ReportTimeoutAsync(PendingDelegation delegation, string detail, CancellationToken ct = default)
    {
        try
        {
            await EnterAsync("timeout", delegation, ct);
            lock (_lock) _timeoutReports.Add((delegation.TaskId, detail));
        }
        catch (OperationCanceledException)
        {
            // Stopped before it went out.
        }
        finally
        {
            Ended("timeout", delegation);
        }
    }
}

/// <summary>
/// The agent, as far as a delegation can tell. A directive whose task id it is already working on
/// is dropped as a repeat, so the work in progress is what answers it.
///
/// <para>
/// By default it is the agent from before execution ids: it knows a piece of work by its task id
/// and nothing else, and a cancel stops whatever is running under the task id, whoever the cancel
/// was meant for. With <see cref="ComparesExecutionIds"/> it follows the rule the agent has now
/// (<c>TaskManager.CancelBridgeExecutionAsync</c>, which has its own tests against the real code):
/// a cancel that names an execution stops only the task whose directive named the same one, and a
/// cancel that names none is matched by task id as before.
/// </para>
/// </summary>
internal sealed class TaskIdOnlyReceiver
{
    public sealed class Execution
    {
        public required int Number { get; init; }
        public required string Agent { get; init; }
        public required string TaskId { get; init; }
        public required string Instruction { get; init; }
        public string? ExecutionId { get; init; }
        public bool Cancelled { get; set; }
        public bool Running => !Cancelled;
    }

    private readonly Lock _lock = new();
    private readonly List<Execution> _executions = [];
    private readonly List<string> _log = [];

    /// <summary>The agent with the execution-id check; false is the agent from before it.</summary>
    public bool ComparesExecutionIds { get; init; }

    public IReadOnlyList<Execution> Executions
    {
        get { lock (_lock) return [.. _executions]; }
    }

    /// <summary>What arrived, in order: <c>directive:id→#n</c>, <c>cancel:id→#n</c>, <c>cancel:id→nothing</c>.</summary>
    public IReadOnlyList<string> Log
    {
        get { lock (_lock) return [.. _log]; }
    }

    /// <summary>The execution that a directive with this instruction is being answered by.</summary>
    public Execution? Serving(string instruction)
    {
        lock (_lock) return _serving.GetValueOrDefault(instruction);
    }

    private readonly Dictionary<string, Execution> _serving = new(StringComparer.Ordinal);

    public void Directive(string agent, string taskId, string instruction, string? executionId = null)
    {
        lock (_lock)
        {
            var running = _executions.LastOrDefault(e => e.Running && e.Agent == agent && e.TaskId == taskId);
            if (running is null)
            {
                running = new Execution
                {
                    Number = _executions.Count + 1, Agent = agent, TaskId = taskId, Instruction = instruction,
                    ExecutionId = ComparesExecutionIds ? executionId : null,
                };
                _executions.Add(running);
            }

            _serving[instruction] = running;
            _log.Add($"directive:{taskId}→#{running.Number}");
        }
    }

    public void Cancel(string agent, string taskId, string? executionId = null)
    {
        lock (_lock)
        {
            var named = ComparesExecutionIds && !string.IsNullOrEmpty(executionId);
            var hit = _executions
                .Where(e => e.Running && e.Agent == agent && e.TaskId == taskId
                            && (!named || string.Equals(e.ExecutionId, executionId, StringComparison.Ordinal)))
                .ToList();
            foreach (var execution in hit) execution.Cancelled = true;
            _log.Add(hit.Count == 0 ? $"cancel:{taskId}→nothing" : $"cancel:{taskId}→#{string.Join(",#", hit.Select(e => e.Number))}");
        }
    }
}

/// <summary>Keeps every formatted log line, so a test can assert what never reaches the log.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<string> Lines { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Lines) Lines.Add($"{logLevel}: {formatter(state, exception)} {exception}");
    }
}

/// <summary>A service over fakes, and a way to build a second one over the same state file.</summary>
internal sealed class DurableHarness : IDisposable
{
    public string Directory { get; }
    public string StateFile => Path.Combine(Directory, "pending-delegations.json");
    public FakeClock Clock { get; } = new();
    public FakeGateway Gateway { get; } = new();
    public FakeRelay Relay { get; } = new();
    public CapturingLogger<DurableDelegationService> Log { get; } = new();
    public DurableDelegationService Service { get; private set; }

    private readonly IDelegationRelay? _relayInstead;

    /// <param name="relay">Used in place of <see cref="Relay"/>: the real relay over a recording wire.</param>
    public DurableHarness(bool enabled = true, IDelegationRelay? relay = null)
    {
        Directory = Path.Combine(Path.GetTempPath(), "durable-delegation-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        Gateway.Clock = Clock;
        _relayInstead = relay;
        Service = Build(enabled);
    }

    /// <summary>A new service instance reading the same file: what a bridge restart is.</summary>
    public DurableDelegationService Restart(bool enabled = true) => Service = Build(enabled);

    private DurableDelegationService Build(bool enabled) => new(
        Options.Create(new DurableDelegationOptions { Enabled = enabled, StateFile = StateFile }),
        Gateway, _relayInstead ?? Relay, Log, Clock);

    /// <summary>A delegation started the way the activity starts one.</summary>
    public async Task StartAsync(DelegationStart start)
    {
        await Service.StartDelegationAsync(start, default);
        await SettleAsync();
    }

    /// <summary>One pass, and then everything the relay lane had to send because of it.</summary>
    public async Task TickAsync()
    {
        await Service.TickAsync(default);
        await SettleAsync();
    }

    /// <summary>An agent's reply, and then everything the relay lane had to send because of it.</summary>
    public async Task<DurableReplyOutcome> ReplyAsync(string taskId, AgentTaskResult result)
    {
        var outcome = await Service.TryHandleReplyAsync(taskId, result, default);
        await SettleAsync();
        return outcome;
    }

    /// <summary>
    /// Waits until the relay lane has nothing in progress and nothing asked for. Looked up by
    /// name so that these tests also build against the service as it was when relay sends ran
    /// inside the pass (it has no such method and needs no waiting): that is how a regression
    /// here is shown to fail on the code it is a regression for.
    /// </summary>
    public async Task SettleAsync()
    {
        var idle = typeof(DurableDelegationService).GetMethod(
            "RelayIdleAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        // Bounded: a test that leaves a send hanging and then waits for the lane to be idle is a
        // mistake in the test, and it should say so and not hang the run.
        if (idle is not null) await ((Task)idle.Invoke(Service, null)!).WaitAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>Waits, in real time, for something a relay send or a pass will make true.</summary>
    public static async Task WaitForAsync(Func<bool> condition, string what, int seconds = 10)
    {
        var deadline = Environment.TickCount64 + seconds * 1000;
        while (!condition())
        {
            Assert.True(Environment.TickCount64 < deadline, $"timed out waiting until {what}");
            await Task.Delay(10);
        }
    }

    public static DelegationStart Start(
        string taskId = "wf-1/review",
        string agent = "reviewer-one",
        string instruction = "review the change",
        int budgetSeconds = 600,
        bool retryOnIncomplete = true,
        int maxIncompleteRetries = 3,
        string? repo = null,
        string @namespace = "fleet") => new(
        TaskId: taskId,
        Agent: agent,
        Instruction: instruction,
        Repo: repo,
        Budget: TimeSpan.FromSeconds(budgetSeconds),
        RetryOnIncomplete: retryOnIncomplete,
        MaxIncompleteRetries: maxIncompleteRetries,
        Namespace: @namespace,
        WorkflowId: "wf-1",
        RunId: "run-1",
        WorkflowType: "example-workflow",
        ActivityId: "7",
        Attempt: 1,
        TaskToken: [1, 2, 3, 4]);

    public async Task<IReadOnlyList<PendingDelegationView>> PendingAsync() => (await Service.SnapshotAsync()).Pending;

    public void Dispose()
    {
        try { System.IO.Directory.Delete(Directory, recursive: true); }
        catch (IOException) { /* a temp directory; the OS clears it */ }
    }
}
