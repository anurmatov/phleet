using System.Text;
using Fleet.Temporal.Activities;
using Fleet.Temporal.Configuration;
using Fleet.Temporal.Models;
using Fleet.Temporal.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RabbitMQ.Client;
using Temporalio.Testing;

namespace Fleet.Temporal.Tests.Activities;

/// <summary>
/// <c>noRedelivery</c> on the in-process wait (durable delegation off), through the real
/// <see cref="DelegateToAgentActivity"/>: the directive published before the wait begins is the
/// only one. The broker is a recording substitute and the clock is the test's, so "five minutes"
/// and "the whole budget" are the production values, not scaled-down stand-ins.
///
/// <para>
/// Each claim has a control beside it that runs the same wait without the flag and counts the
/// second directive. That is what shows the harness can see a re-send or a continuation at all,
/// and it is the behaviour the opted-in tests would show if the flag did nothing.
/// </para>
/// </summary>
public sealed class DelegateNoRedeliveryActivityTests
{
    private const string Agent = "agent-a";
    private const string TaskId = "wf-1/merge";

    /// <summary>Eleven minutes: past the two re-sends a wait without the flag makes (at 5 and at 10).</summary>
    private const int BudgetSeconds = 660;

    /// <summary>Also built by the seed-level test that runs a shipped step through this activity.</summary>
    internal sealed class Harness
    {
        public required DelegateToAgentActivity Activity { get; init; }
        public required TaskCompletionRegistry Registry { get; init; }
        public required ManualTime Time { get; init; }

        /// <summary>The task id of every message that reached the broker, in order.</summary>
        public required List<string> PublishedTaskIds { get; init; }

        /// <summary>Publishes of the delegation's own directive: the task id, or a continuation of it.</summary>
        public List<string> Directives
        {
            get
            {
                lock (PublishedTaskIds)
                    return [.. PublishedTaskIds.Where(id => id == TaskId || id.StartsWith(TaskId + "/incomplete-retry-", StringComparison.Ordinal))];
            }
        }
    }

    internal static Harness Build()
    {
        var publishedTaskIds = new List<string>();
        var channel = Substitute.For<IChannel>();
        channel.BasicPublishAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(),
                Arg.Any<BasicProperties>(), Arg.Any<ReadOnlyMemory<byte>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var json = Encoding.UTF8.GetString(call.ArgAt<ReadOnlyMemory<byte>>(4).Span);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                lock (publishedTaskIds) publishedTaskIds.Add(doc.RootElement.GetProperty("TaskId").GetString() ?? "");
                return ValueTask.CompletedTask;
            });

        var connection = Substitute.For<IConnection>();
        connection.CreateChannelAsync(Arg.Any<CreateChannelOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(channel));
        var connectionFactory = Substitute.For<IConnectionFactory>();
        connectionFactory.CreateConnectionAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(connection));

        var registry = new TaskCompletionRegistry(NullLogger<TaskCompletionRegistry>.Instance);
        var time = new ManualTime();
        var activity = new DelegateToAgentActivity(
            Options.Create(new RabbitMqOptions()),
            Options.Create(new TemporalBridgeOptions { AgentTimeoutSeconds = BudgetSeconds }),
            registry,
            connectionFactory,
            Substitute.For<IHttpClientFactory>(),
            NullLogger<DelegateToAgentActivity>.Instance,
            durable: null,
            time: time);

        return new Harness { Activity = activity, Registry = registry, Time = time, PublishedTaskIds = publishedTaskIds };
    }

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        var deadline = Environment.TickCount64 + 10_000;
        while (!condition())
        {
            Assert.True(Environment.TickCount64 < deadline, $"timed out waiting until {what}");
            await Task.Delay(5);
        }
    }

    /// <summary>
    /// Runs the wait with no answer for the whole budget, thirty seconds of the activity's clock at
    /// a time, and returns the timeout it ends in. Between steps it waits until the loop has
    /// heartbeated and is parked on its next delay, so every re-send that was going to happen in
    /// a step has happened before the next one starts.
    /// </summary>
    private static async Task<TimeoutException> RunOutTheBudgetAsync(Harness h, bool noRedelivery)
    {
        var heartbeats = 0;
        var env = new ActivityEnvironment { Heartbeater = _ => Interlocked.Increment(ref heartbeats) };

        var run = env.RunAsync(() => h.Activity.DelegateToAgentAsync(
            Agent, "merge the change", TaskId,
            retryOnIncomplete: false, maxIncompleteRetries: 0, noRedelivery: noRedelivery));

        // Parked: the budget timer and the first heartbeat delay.
        await WaitForAsync(() => h.Directives.Count == 1 && h.Time.ActiveTimers == 2, "the directive was published and the wait began");

        var steps = BudgetSeconds / 30;
        for (var step = 1; step < steps; step++)
        {
            h.Time.Advance(TimeSpan.FromSeconds(30));
            var expected = step;
            await WaitForAsync(
                () => Volatile.Read(ref heartbeats) == expected && h.Time.ActiveTimers == 2,
                $"heartbeat {expected} was made and the wait went on");
        }

        // The last thirty seconds: the budget is spent.
        h.Time.Advance(TimeSpan.FromSeconds(30));
        return await Assert.ThrowsAsync<TimeoutException>(() => run.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task NoRedelivery_LostAnswer_OnePublishThroughTheWholeBudget()
    {
        var h = Build();

        var timeout = await RunOutTheBudgetAsync(h, noRedelivery: true);

        // Eleven minutes with no answer, and the directive went to the broker once.
        Assert.Equal([TaskId], h.Directives);
        // The failure is the ordinary timeout, measured on the same clock, so the step's
        // ignoreFailure sees what it always saw.
        Assert.Contains("elapsed 11.0m (budget 11.0m", timeout.Message);
        Assert.Contains(TaskId, timeout.Message);
        Assert.Equal(0, h.Registry.PendingCount);
    }

    /// <summary>
    /// The control: the same eleven minutes without the flag re-send the directive at five and at
    /// ten minutes, as the wait always did.
    /// </summary>
    [Fact]
    public async Task WithoutNoRedelivery_TheSameLostAnswer_IsReSentEveryFiveMinutes()
    {
        var h = Build();

        await RunOutTheBudgetAsync(h, noRedelivery: false);

        Assert.Equal([TaskId, TaskId, TaskId], h.Directives);
    }

    [Fact]
    public async Task NoRedelivery_IncompleteAnswer_ZeroContinuationDirectives()
    {
        var h = Build();
        var env = new ActivityEnvironment();

        // The retry arguments are left at their defaults (on, three retries) on purpose: the
        // flag alone has to be enough.
        var run = env.RunAsync(() => h.Activity.DelegateToAgentAsync(Agent, "merge the change", TaskId, noRedelivery: true));

        await WaitForAsync(() => h.Registry.TryComplete(TaskId, new AgentTaskResult("half of it", "incomplete")), "the activity registered its wait");
        var result = await run.WaitAsync(TimeSpan.FromSeconds(30));

        // Returned as it came, and nothing went out after the first directive.
        Assert.Equal(new AgentTaskResult("half of it", "incomplete"), result);
        Assert.Equal([TaskId], h.Directives);
        Assert.Equal(0, h.Registry.PendingCount);
    }

    /// <summary>The control: without the flag the same answer is continued with a second directive.</summary>
    [Fact]
    public async Task WithoutNoRedelivery_TheSameIncompleteAnswer_IsContinued()
    {
        var h = Build();
        var env = new ActivityEnvironment();

        var run = env.RunAsync(() => h.Activity.DelegateToAgentAsync(Agent, "merge the change", TaskId));

        await WaitForAsync(() => h.Registry.TryComplete(TaskId, new AgentTaskResult("half of it", "incomplete")), "the activity registered its wait");
        await WaitForAsync(
            () => h.Registry.TryComplete(TaskId + "/incomplete-retry-1", new AgentTaskResult("the rest", "completed")),
            "the continuation registered its wait");
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal([TaskId, TaskId + "/incomplete-retry-1"], h.Directives);
    }

    /// <summary>
    /// The flag is the eighth positional argument, optional, and false when absent: a caller that
    /// sends five, six or seven payloads binds as it did.
    /// </summary>
    [Fact]
    public void TheFlag_IsTheLastOptionalParameter_AndDefaultsToFalse()
    {
        var method = typeof(DelegateToAgentActivity).GetMethod(nameof(DelegateToAgentActivity.DelegateToAgentAsync))!;
        var last = method.GetParameters()[^1];

        Assert.Equal("noRedelivery", last.Name);
        Assert.Equal(7, last.Position);
        Assert.Equal(typeof(bool), last.ParameterType);
        Assert.True(last.HasDefaultValue);
        Assert.Equal(false, last.DefaultValue);

        var definition = Temporalio.Activities.ActivityDefinition.Create(method, _ => null);
        Assert.Equal(3, definition.RequiredParameterCount);
    }
}

/// <summary>
/// A clock with timers that only the test moves: <see cref="Advance"/> sets the time forward and
/// runs every timer that has come due. What the activity's wait is built on (its heartbeat delay
/// and its budget) is then driven in whole steps, with nothing depending on how fast the machine
/// is.
/// </summary>
internal sealed class ManualTime : TimeProvider
{
    private readonly Lock _lock = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock) return _now;
    }

    // Elapsed time is read from the same clock, in ticks.
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_lock) return _now.UtcTicks;
    }

    /// <summary>Timers that are set and have not fired or been disposed.</summary>
    public int ActiveTimers
    {
        get { lock (_lock) return _timers.Count(t => t.DueAt is not null); }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_lock) _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_lock)
        {
            _now += by;
            due = [.. _timers.Where(t => t.DueAt <= _now).OrderBy(t => t.DueAt)];
            // One shot: nothing the wait uses is periodic.
            foreach (var timer in due) timer.DueAt = null;
        }

        foreach (var timer in due) timer.Fire();
    }

    private sealed class ManualTimer(ManualTime owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? DueAt { get; set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._lock)
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
            return true;
        }

        public void Dispose()
        {
            lock (owner._lock)
            {
                DueAt = null;
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
