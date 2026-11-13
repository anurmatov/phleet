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

    private async Task EnterAsync(string kind, PendingDelegation delegation, CancellationToken ct)
    {
        lock (_lock) _started.Add($"{kind}:{delegation.CurrentTaskId}");
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

    public async Task PublishDirectiveAsync(PendingDelegation delegation, CancellationToken ct)
    {
        Interlocked.Increment(ref _publishAttempts);
        await EnterAsync("publish", delegation, ct);
        if (PublishFailure is not null) throw PublishFailure;
        lock (_lock)
            _published.Add((delegation.Agent, delegation.CurrentTaskId, delegation.CurrentInstruction, delegation.Repo));
    }

    public async Task SendCancelAsync(PendingDelegation delegation)
    {
        await EnterAsync("cancel", delegation, CancellationToken.None);
        lock (_lock) _cancels.Add((delegation.Agent, delegation.CurrentTaskId));
    }

    public async Task ReportTimeoutAsync(PendingDelegation delegation, string detail)
    {
        await EnterAsync("timeout", delegation, CancellationToken.None);
        lock (_lock) _timeoutReports.Add((delegation.TaskId, detail));
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

    public DurableHarness(bool enabled = true)
    {
        Directory = Path.Combine(Path.GetTempPath(), "durable-delegation-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        Gateway.Clock = Clock;
        Service = Build(enabled);
    }

    /// <summary>A new service instance reading the same file: what a bridge restart is.</summary>
    public DurableDelegationService Restart(bool enabled = true) => Service = Build(enabled);

    private DurableDelegationService Build(bool enabled) => new(
        Options.Create(new DurableDelegationOptions { Enabled = enabled, StateFile = StateFile }),
        Gateway, Relay, Log, Clock);

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
