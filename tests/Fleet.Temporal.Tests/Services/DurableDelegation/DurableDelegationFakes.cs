using Fleet.Temporal.Configuration;
using Fleet.Temporal.Models;
using Fleet.Temporal.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fleet.Temporal.Tests.Services.DurableDelegation;

/// <summary>A clock the test moves.</summary>
internal sealed class FakeClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Stands in for Temporal: records every call and answers what the test scripted.</summary>
internal sealed class FakeGateway : IAsyncActivityGateway
{
    public List<string> Calls { get; } = [];
    public List<(string TaskId, AgentTaskResult Result)> Completed { get; } = [];
    public List<(string TaskId, Exception Failure)> Failed { get; } = [];
    public List<string> Cancelled { get; } = [];

    public ActivityCallOutcome Heartbeat { get; set; } = ActivityCallOutcome.Ok;
    public ActivityCallOutcome Complete { get; set; } = ActivityCallOutcome.Ok;
    public ActivityCallOutcome Fail { get; set; } = ActivityCallOutcome.Ok;
    public ActivityCallOutcome ReportCancellation { get; set; } = ActivityCallOutcome.Ok;

    /// <summary>Overrides <see cref="Heartbeat"/> for one task id.</summary>
    public Dictionary<string, ActivityCallOutcome> HeartbeatFor { get; } = new(StringComparer.Ordinal);

    public int Count(string call) => Calls.Count(c => c.StartsWith(call + ":", StringComparison.Ordinal));

    public Task<ActivityCallOutcome> HeartbeatAsync(PendingDelegation delegation, CancellationToken ct)
    {
        Calls.Add($"heartbeat:{delegation.TaskId}");
        return Task.FromResult(HeartbeatFor.GetValueOrDefault(delegation.TaskId, Heartbeat));
    }

    public Task<ActivityCallOutcome> CompleteAsync(PendingDelegation delegation, AgentTaskResult result, CancellationToken ct)
    {
        Calls.Add($"complete:{delegation.TaskId}");
        if (Complete == ActivityCallOutcome.Ok) Completed.Add((delegation.TaskId, result));
        return Task.FromResult(Complete);
    }

    public Task<ActivityCallOutcome> FailAsync(PendingDelegation delegation, Exception failure, CancellationToken ct)
    {
        Calls.Add($"fail:{delegation.TaskId}");
        if (Fail == ActivityCallOutcome.Ok) Failed.Add((delegation.TaskId, failure));
        return Task.FromResult(Fail);
    }

    public Task<ActivityCallOutcome> ReportCancellationAsync(PendingDelegation delegation, CancellationToken ct)
    {
        Calls.Add($"report-cancellation:{delegation.TaskId}");
        if (ReportCancellation == ActivityCallOutcome.Ok) Cancelled.Add(delegation.TaskId);
        return Task.FromResult(ReportCancellation);
    }
}

/// <summary>Stands in for the broker and the orchestrator: records what would have been sent.</summary>
internal sealed class FakeRelay : IDelegationRelay
{
    public List<(string Agent, string TaskId, string Instruction, string? Repo)> Published { get; } = [];
    public List<(string Agent, string TaskId)> Cancels { get; } = [];
    public List<(string TaskId, string Detail)> TimeoutReports { get; } = [];

    /// <summary>When set, every publish throws it.</summary>
    public Exception? PublishFailure { get; set; }

    public int PublishAttempts { get; private set; }

    public Task PublishDirectiveAsync(PendingDelegation delegation, CancellationToken ct)
    {
        PublishAttempts++;
        if (PublishFailure is not null) return Task.FromException(PublishFailure);
        lock (Published)
            Published.Add((delegation.Agent, delegation.CurrentTaskId, delegation.CurrentInstruction, delegation.Repo));
        return Task.CompletedTask;
    }

    public Task SendCancelAsync(PendingDelegation delegation)
    {
        Cancels.Add((delegation.Agent, delegation.CurrentTaskId));
        return Task.CompletedTask;
    }

    public Task ReportTimeoutAsync(PendingDelegation delegation, string detail)
    {
        TimeoutReports.Add((delegation.TaskId, detail));
        return Task.CompletedTask;
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
        Service = Build(enabled);
    }

    /// <summary>A new service instance reading the same file: what a bridge restart is.</summary>
    public DurableDelegationService Restart(bool enabled = true) => Service = Build(enabled);

    private DurableDelegationService Build(bool enabled) => new(
        Options.Create(new DurableDelegationOptions { Enabled = enabled, StateFile = StateFile }),
        Gateway, Relay, Log, Clock);

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
