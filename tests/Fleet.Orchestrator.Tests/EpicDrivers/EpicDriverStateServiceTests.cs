using System.Text.Json;
using Fleet.Orchestrator.Configuration;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Models;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Tests.EpicGrants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fleet.Orchestrator.Tests.EpicDrivers;

internal sealed class DriverTemporal : IEpicGrantTemporal, IEpicDriverTemporal
{
    public bool IsConfigured { get; set; } = true;
    public List<WorkflowSummary> Runs { get; } = [];
    public Dictionary<string, EpicRunDescription> Descriptions { get; } = [];
    public Dictionary<string, EpicRunHistory> Histories { get; } = [];
    public Dictionary<string, EpicCloseResult> Results { get; } = [];
    public List<(string Id, string Run, string Signal, object Payload)> Signals { get; } = [];
    public int Lists { get; private set; }
    public int CloseReads { get; private set; }
    public string? FailHistory { get; set; }
    public string? VisibilityError { get; set; }
    public string SendResult { get; set; } = "sent";
    public Func<string, CancellationToken, Task>? BeforeHistory { get; set; }

    public void Add(string id, string type, string status = "Running", string? gate = null, string? parent = null, string? parentRun = null)
    {
        Runs.Add(new(id, id + "-run", type, "fleet", "fleet", status, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            IssueNumber: 42, Repo: "example/repo", Phase: gate is null ? "working" : "merge-approval"));
        Descriptions[id] = new(status, id + "-run", type, DateTimeOffset.UtcNow, parent, parentRun, gate, gate is null ? null : new string('a', 40), "pass", IssueNumber: 42, Phase: gate is null ? "working" : "merge-approval", Repo: "example/repo");
        Histories[id] = new("{\"WaiterWorkflowId\":\"driver\"}", type, 1);
    }
    public Task<EpicRunDescription?> DescribeAsync(string ns, string id, string? run, CancellationToken ct) =>
        Task.FromResult(Descriptions.GetValueOrDefault(id) is { } d && (run is null || d.RunId == run) ? d : null);
    public async Task<EpicRunHistory?> ReadRunAsync(string ns, string id, string run, CancellationToken ct)
    {
        if (BeforeHistory is not null) await BeforeHistory(id, ct);
        if (id == FailHistory) throw new InvalidOperationException("read failed");
        return Histories.GetValueOrDefault(id);
    }
    public Task<EpicCloseResult?> ReadCloseResultAsync(string ns, string id, string run, CancellationToken ct)
    { CloseReads++; return Task.FromResult(Results.GetValueOrDefault(id)); }
    public Task SignalAsync(string ns, string id, string run, string name, string payload, CancellationToken ct) => throw new InvalidOperationException("State must not signal");
    public Task<EpicVisibilityResult> ListAsync(string ns, string query, int? limit, CancellationToken ct)
    {
        Lists++;
        var rows = Runs.Where(r => query.Contains("UweCarteBlancheWorkflow") ? r.WorkflowType == "UweCarteBlancheWorkflow"
            : r.WorkflowType != "UweCarteBlancheWorkflow").ToArray();
        if (query.Contains("ExecutionStatus!=\"Running\"")) rows = rows.Where(r => r.Status != "Running").ToArray();
        else if (!query.Contains("CloseTime")) rows = rows.Where(r => r.Status == "Running").ToArray();
        return Task.FromResult(new EpicVisibilityResult(rows.Take(limit ?? int.MaxValue).ToArray(),
            VisibilityError is null && rows.Length <= (limit ?? int.MaxValue), VisibilityError ?? (rows.Length > limit ? "candidates_incomplete" : null)));
    }
    public Task<string> SignalWorkflowAsync(string ns, string id, string run, string name, object payload, CancellationToken ct)
    { Signals.Add((id, run, name, payload)); return Task.FromResult(SendResult); }
}

internal sealed class DriverFixture : IDisposable
{
    public DriverTemporal Temporal { get; } = new();
    public ManualTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);
    public ServiceProvider Services { get; }
    public EpicRunLinkage Linkage { get; }
    public EpicDriverStateService State { get; }
    public DriverFixture()
    {
        var database = Guid.NewGuid().ToString();
        Services = new ServiceCollection().AddDbContext<OrchestratorDbContext>(o => o.UseInMemoryDatabase(database)).BuildServiceProvider();
        Temporal.Add("driver", "UweCarteBlancheWorkflow");
        Linkage = new(Temporal, Temporal);
        State = new(Services.GetRequiredService<IServiceScopeFactory>(), Temporal, Linkage, Clock,
            new ConfigurationBuilder().Build(), NullLogger<EpicDriverStateService>.Instance);
    }
    public EpicDriverWatcherService Watcher(bool enabled = true, int interval = 300) => new(
        Services.GetRequiredService<IServiceScopeFactory>(), Temporal, Temporal, Linkage,
        Options.Create(new EpicDriverWatcherOptions { Enabled = enabled, IntervalSeconds = interval }),
        Options.Create(new TemporalOptions { Address = "example:7233", Namespaces = ["fleet"] }), Clock,
        NullLogger<EpicDriverWatcherService>.Instance);
    public async Task SeedDefinitionAsync(bool hasWake = true)
    {
        using var scope = Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        db.WorkflowDefinitions.Add(new() { Name = "UweCarteBlancheWorkflow", Namespace = "fleet", TaskQueue = "fleet", Version = 1,
            Definition = hasWake ? "{\"type\":\"wait_for_signal\",\"name\":\"epic_wake_wait\"}" : "{\"type\":\"noop\"}" });
        await db.SaveChangesAsync();
    }
    public void Dispose() => Services.Dispose();
}

public class EpicDriverStateServiceTests
{
    [Fact]
    public async Task Read_ParkedChildAndApprovedOutcome_AreReadOnlyAndRunPinned()
    {
        using var f = new DriverFixture();
        f.Temporal.Add("pr", "UwePrImplementationWorkflow", gate: "merge-approval:2");
        f.Temporal.Add("design", "UweDesignWorkflow", "Completed");
        f.Temporal.Results["design"] = new("json/plain", "\"{\\\"IssueNumber\\\":42}\"");
        var first = await f.State.ReadAsync("fleet", "driver", default);
        Assert.Empty(first.Errors);
        Assert.Equal(64, first.Fingerprint.Length);
        Assert.Equal("human_decide", first.Children.Single(c => c.WorkflowId == "pr").NextAction);
        Assert.Equal("approved", first.Children.Single(c => c.WorkflowId == "design").Outcome);
        Assert.Empty(f.Temporal.Signals);
        var reads = f.Temporal.Lists;
        f.Clock.Advance(TimeSpan.FromSeconds(20));
        var cached = await f.State.ReadAsync("fleet", "driver", default);
        Assert.True(cached.Cached); Assert.Equal(first.ComputedAt, cached.ComputedAt);
        Assert.Equal(first.Fingerprint, cached.Fingerprint); Assert.Equal(reads, f.Temporal.Lists);
        f.Clock.Advance(TimeSpan.FromSeconds(11));
        Assert.False((await f.State.ReadAsync("fleet", "driver", default)).Cached);
        Assert.Equal(1, f.Temporal.CloseReads);
    }

    [Fact]
    public async Task Read_MissingExactRunAttribute_CannotApproveUsingStaleVisibility()
    {
        using var f = new DriverFixture(); f.Temporal.Add("design", "UweDesignWorkflow", "Completed");
        f.Temporal.Descriptions["design"] = f.Temporal.Descriptions["design"] with { IssueNumber = null };
        f.Temporal.Results["design"] = new("json/plain", "{\"IssueNumber\":42}");
        var state = await f.State.ReadAsync("fleet", "driver", default);
        Assert.Equal("invalid_result", Assert.Single(state.Children).Outcome);
        Assert.Null(Assert.Single(state.Children).OutcomeIssue);
    }

    [Fact]
    public async Task Read_ActualDeadline_ReturnsTheThreeAlreadyLinkedChildren()
    {
        using var f = new DriverFixture(); var clock = new DeadlineClock(f.Clock.Now);
        for (var i = 0; i < 5; i++) f.Temporal.Add("child-" + i, "UwePrImplementationWorkflow");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var count = 0;
        f.Temporal.BeforeHistory = async (id, ct) =>
        {
            if (id is "child-0" or "child-1" or "child-2")
            { if (Interlocked.Increment(ref count) == 3) ready.TrySetResult(); return; }
            await ready.Task.WaitAsync(ct);
            // Linked collection records the completed reads before cancellation is released.
            await Task.Yield();
            clock.Advance(TimeSpan.FromSeconds(45));
            await Task.Delay(Timeout.Infinite, ct);
        };
        var state = new EpicDriverStateService(f.Services.GetRequiredService<IServiceScopeFactory>(), f.Temporal,
            f.Linkage, clock, new ConfigurationBuilder().Build(), NullLogger<EpicDriverStateService>.Instance);
        var reply = await state.ReadAsync("fleet", "driver", default);
        Assert.Equal(3, reply.Children.Count); Assert.Contains("deadline", reply.Errors); Assert.Equal("none", reply.Fingerprint);
        clock.Advance(TimeSpan.FromSeconds(20)); var cached = await state.ReadAsync("fleet", "driver", default);
        Assert.True(cached.Cached); Assert.Equal(reply.ComputedAt, cached.ComputedAt); Assert.Equal(reply.Children, cached.Children);
    }

    [Theory]
    [InlineData("deadline")]
    [InlineData("candidates_page_failed")]
    public async Task Read_PartialReplyCache_NeverClearsOrRenewsErrors(string error)
    {
        using var f = new DriverFixture(); f.Temporal.VisibilityError = error;
        f.Temporal.Add("pr", "UwePrImplementationWorkflow");
        var first = await f.State.ReadAsync("fleet", "driver", default);
        Assert.Contains(error, first.Errors); Assert.Equal("none", first.Fingerprint); Assert.Single(first.Children);
        f.Clock.Advance(TimeSpan.FromSeconds(20));
        var second = await f.State.ReadAsync("fleet", "driver", default);
        Assert.True(second.Cached); Assert.Equal(first.ComputedAt, second.ComputedAt); Assert.Equal(first.Errors, second.Errors);
        f.Clock.Advance(TimeSpan.FromSeconds(9)); Assert.True((await f.State.ReadAsync("fleet", "driver", default)).Cached);
        f.Clock.Advance(TimeSpan.FromSeconds(2)); Assert.False((await f.State.ReadAsync("fleet", "driver", default)).Cached);
    }

    [Theory]
    [InlineData(500, false)]
    [InlineData(501, true)]
    public async Task Read_CandidateBound_FailsClosedRatherThanSilentlyOmitting(int count, bool incomplete)
    {
        using var f = new DriverFixture();
        for (var i = 0; i < count; i++)
        {
            var id = "child-" + i; f.Temporal.Add(id, "UwePrImplementationWorkflow");
            f.Temporal.Histories[id] = new("{}", "UwePrImplementationWorkflow", 1);
        }
        var reply = await f.State.ReadAsync("fleet", "driver", default);
        Assert.Equal(incomplete, reply.Errors.Contains("candidates_incomplete"));
        Assert.Equal(incomplete, reply.Fingerprint == "none");
        Assert.Empty(reply.Children);
    }

    [Fact]
    public async Task Read_TooManyLinkedChildrenAndHistoryFailure_ReturnErrors()
    {
        using var f = new DriverFixture();
        for (var i = 0; i < 52; i++) f.Temporal.Add("child-" + i, "UwePrImplementationWorkflow");
        f.Temporal.FailHistory = "child-0";
        var reply = await f.State.ReadAsync("fleet", "driver", default);
        Assert.Equal(50, reply.Children.Count); Assert.True(reply.Truncated); Assert.Equal("none", reply.Fingerprint);
        Assert.Contains("children_truncated", reply.Errors); Assert.Contains("linkage_unknown:child-0", reply.Errors);
    }
}


internal sealed class DeadlineClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;
    private readonly List<DeadlineTimer> _timers = [];
    public override DateTimeOffset GetUtcNow() => _now;
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new DeadlineTimer(this, callback, state, _now + dueTime);
        _timers.Add(timer); return timer;
    }
    public void Advance(TimeSpan by)
    {
        _now += by;
        foreach (var timer in _timers.ToArray()) timer.Fire(_now);
    }
    private sealed class DeadlineTimer(DeadlineClock clock, TimerCallback callback, object? state, DateTimeOffset due) : ITimer
    {
        private bool _disposed;
        private DateTimeOffset _due = due;
        public void Fire(DateTimeOffset now)
        { if (!_disposed && now >= _due) { _disposed = true; callback(state); } }
        public bool Change(TimeSpan dueTime, TimeSpan period)
        { if (_disposed) return false; _due = clock._now + dueTime; return true; }
        public void Dispose() => _disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
