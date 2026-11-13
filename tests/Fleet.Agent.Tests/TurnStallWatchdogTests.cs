using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Fleet.Agent;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Fleet.Protocol;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Tests;

/// <summary>
/// A turn has no deadline, only a limit on silence: if the executor yields nothing for
/// <c>Agent:TurnStallTimeoutMinutes</c>, the turn is stopped and reported as failed. The clock is
/// a manual one, so nothing here waits for real time to pass.
/// </summary>
public class TurnStallWatchdogTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private const int LimitMinutes = 30;

    [Fact]
    public async Task SilenceForTheWholeLimit_StopsTheTurnAndReportsItFailed()
    {
        var time = new ManualTimeProvider();
        var executor = new ScriptedExecutor();
        var sink = new RecordingSink();
        var events = new RecordingEvents();
        var manager = BuildManager(executor, sink, time, LimitMinutes, events);
        var completed = new TaskCompletionSource<(string Result, bool IsPartial, CompletionKind Kind)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        manager.OnTaskCompleted += (_, result, _, _, isPartial, _, _, kind) => completed.TrySetResult((result, isPartial, kind));

        Assert.Equal(TaskDispatchOutcome.Ran,
            await manager.StartTask(5, "PROMPT-NOT-FOR-USERS", "display", isSessionTask: true,
                source: TaskSource.Relay, relaySender: "peer", taskId: "wf/step"));
        await executor.WaitForTurnAsync(1);

        // One minute short of the limit: still running.
        time.Advance(TimeSpan.FromMinutes(LimitMinutes - 1));
        Assert.False(completed.Task.IsCompleted);
        Assert.True(manager.HasRunningTasks(5));

        time.Advance(TimeSpan.FromMinutes(1));
        var outcome = await completed.Task.WaitAsync(Wait);

        var expected = $"Task failed: no output from the model for {LimitMinutes} minutes, so the turn was stopped (stall watchdog).";
        Assert.Equal(expected, outcome.Result);
        Assert.True(outcome.IsPartial);
        Assert.Equal(CompletionKind.Failed, outcome.Kind);
        // The executor's read was cancelled, which is what makes each provider stop its process.
        Assert.True(await executor.FirstTurnCancelled.Task.WaitAsync(Wait));

        await WaitUntilAsync(() => !manager.HasRunningTasks(5));
        Assert.Contains(sink.Texts, t => t.Contains(expected));
        Assert.DoesNotContain(sink.Texts, t => t.Contains("Task cancelled."));
        Assert.Contains(ConversationEventKind.TurnError, events.Kinds);
        Assert.DoesNotContain(ConversationEventKind.TurnCanceled, events.Kinds);
        Assert.DoesNotContain(ConversationEventKind.TurnOutcomeUnknown, events.Kinds);

        // The slot is free again: the next turn runs.
        executor.Script.Writer.TryWrite(Result("after the stall"));
        Assert.Equal(TaskDispatchOutcome.Ran, await manager.StartTask(5, "next", "next", isSessionTask: true));
        await WaitUntilAsync(() => sink.Texts.Any(t => t.Contains("after the stall")));
    }

    [Fact]
    public async Task EveryEventRestartsTheLimit_SoALongTurnThatKeepsReportingIsNeverStopped()
    {
        var time = new ManualTimeProvider();
        var executor = new ScriptedExecutor();
        var sink = new RecordingSink();
        var manager = BuildManager(executor, sink, time, LimitMinutes);

        _ = manager.StartTask(5, "long", "long", isSessionTask: true);
        await executor.WaitForTurnAsync(1);

        // Six times the limit in total, never a full limit without an event.
        for (var i = 0; i < 8; i++)
        {
            time.Advance(TimeSpan.FromMinutes(LimitMinutes - 1));
            executor.Script.Writer.TryWrite(new AgentProgress { EventType = "assistant", Summary = "working" });
            // The executor is asked for the next event only after the turn loop has handled this one.
            await executor.WaitForYieldedAsync(i + 1);
        }

        Assert.True(manager.HasRunningTasks(5));
        executor.Script.Writer.TryWrite(Result("finished after a long run"));
        await WaitUntilAsync(() => !manager.HasRunningTasks(5));

        Assert.Contains(sink.Texts, t => t.Contains("finished after a long run"));
        Assert.DoesNotContain(sink.Texts, t => t.Contains("stall watchdog"));
    }

    [Fact]
    public async Task ZeroTurnsTheWatchdogOff()
    {
        var time = new ManualTimeProvider();
        var executor = new ScriptedExecutor();
        var sink = new RecordingSink();
        var manager = BuildManager(executor, sink, time, limitMinutes: 0);

        _ = manager.StartTask(5, "silent", "silent", isSessionTask: true);
        await executor.WaitForTurnAsync(1);

        time.Advance(TimeSpan.FromDays(30));
        Assert.Equal(0, time.TimerCount);
        Assert.True(manager.HasRunningTasks(5));

        executor.Script.Writer.TryWrite(Result("still here"));
        await WaitUntilAsync(() => !manager.HasRunningTasks(5));
        Assert.Contains(sink.Texts, t => t.Contains("still here"));
        Assert.DoesNotContain(sink.Texts, t => t.Contains("stall watchdog"));
    }

    [Fact]
    public async Task ACancelDuringTheLimit_IsStillReportedAsACancel()
    {
        var time = new ManualTimeProvider();
        var executor = new ScriptedExecutor();
        var sink = new RecordingSink();
        var manager = BuildManager(executor, sink, time, LimitMinutes);

        _ = manager.StartTask(5, "to cancel", "to cancel", isSessionTask: true);
        await executor.WaitForTurnAsync(1);
        time.Advance(TimeSpan.FromMinutes(LimitMinutes - 1));

        await manager.HandleCancel(5, "all");
        await WaitUntilAsync(() => !manager.HasRunningTasks(5));

        Assert.Contains(sink.Texts, t => t.Contains("Task cancelled."));
        Assert.DoesNotContain(sink.Texts, t => t.Contains("stall watchdog"));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(TurnStallTimeout.DefaultMinutes, true)]
    [InlineData(TurnStallTimeout.MaxMinutes, true)]
    [InlineData(-1, false)]
    [InlineData(TurnStallTimeout.MaxMinutes + 1, false)]
    public void TheLimitIsValidatedAtStartup(int minutes, bool valid)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agent:Name"] = "fleet-agent1",
            ["Agent:Role"] = "generic-role",
            ["Agent:WorkDir"] = "/workspace",
            ["Agent:Provider"] = "claude",
            ["Agent:Model"] = "sonnet",
            ["Agent:TurnStallTimeoutMinutes"] = minutes.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.Configure<AgentOptions>(configuration.GetSection(AgentOptions.Section));
        using var provider = services.BuildServiceProvider();

        if (valid)
        {
            AgentHostRegistration.ValidateStartupConfiguration(provider, _ => null, _ => false);
        }
        else
        {
            var fault = Assert.Throws<InvalidOperationException>(
                () => AgentHostRegistration.ValidateStartupConfiguration(provider, _ => null, _ => false));
            Assert.Contains("Agent:TurnStallTimeoutMinutes", fault.Message);
        }
    }

    [Fact]
    public void TheDefaultIsThreeHours()
    {
        var options = new AgentOptions { Name = "a", Role = "r", WorkDir = "/w" };
        Assert.Equal(180, options.TurnStallTimeoutMinutes);
    }

    private static AgentProgress Result(string text) =>
        new() { EventType = "result", Summary = text, FinalResult = text };

    private static TaskManager BuildManager(IAgentExecutor executor, IMessageSink sink, TimeProvider time,
        int limitMinutes, IConversationEventPublisher? events = null)
    {
        var options = Options.Create(new AgentOptions
        {
            Name = "test",
            Role = "test",
            WorkDir = "/tmp",
            Provider = "claude",
            ShowStats = false,
            TurnStallTimeoutMinutes = limitMinutes,
        });
        return new TaskManager(options, executor, new SessionManager(), NullLogger<TaskManager>.Instance,
            events: events, sink: sink, timeProvider: time);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(Wait);
        while (!condition())
            await Task.Delay(10, cts.Token);
    }

    /// <summary>A clock that only moves when told to, with the timers created on it.</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public int TimerCount { get { lock (_gate) return _timers.Count; } }

        public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            lock (_gate) _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            List<ManualTimer> due;
            lock (_gate)
            {
                _now += by;
                due = _timers.Where(t => t.Due is { } at && at <= _now).ToList();
                foreach (var timer in due) timer.Due = null;
            }
            foreach (var timer in due) timer.Fire();
        }

        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset? Due { get; set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._gate)
                    Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                return true;
            }

            public void Fire() => callback(state);

            public void Dispose() { lock (owner._gate) owner._timers.Remove(this); }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>Each turn yields what the test writes to <see cref="Script"/>, and nothing else.</summary>
    private sealed class ScriptedExecutor : IAgentExecutor
    {
        private int _turns;
        private int _yielded;

        public Channel<AgentProgress> Script { get; } = Channel.CreateUnbounded<AgentProgress>();
        public TaskCompletionSource<bool> FirstTurnCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? LastSessionId => "session";
        public DateTimeOffset LastActivity => DateTimeOffset.UtcNow;
        public bool IsProcessWarm => true;

        public async IAsyncEnumerable<AgentProgress> ExecuteAsync(
            string task,
            IReadOnlyList<MessageImage>? images = null,
            IReadOnlyList<MessageDocument>? documents = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var turn = Interlocked.Increment(ref _turns);
            while (true)
            {
                AgentProgress next;
                try
                {
                    next = await Script.Reader.ReadAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    if (turn == 1) FirstTurnCancelled.TrySetResult(true);
                    throw;
                }

                yield return next;
                Interlocked.Increment(ref _yielded);
                if (next.FinalResult is not null) yield break;
            }
        }

        public async Task WaitForTurnAsync(int expected)
        {
            using var cts = new CancellationTokenSource(Wait);
            while (Volatile.Read(ref _turns) < expected)
                await Task.Delay(10, cts.Token);
        }

        /// <summary>Returns once the consumer has come back for more after the given number of events.</summary>
        public async Task WaitForYieldedAsync(int expected)
        {
            using var cts = new CancellationTokenSource(Wait);
            while (Volatile.Read(ref _yielded) < expected)
                await Task.Delay(10, cts.Token);
        }

        public Task StopProcessAsync() => Task.CompletedTask;
        public Task<bool> TryStopProcessAsync() => Task.FromResult(false);
        public void RequestRestart() { }
        public IAsyncEnumerable<AgentProgress> SendCommandAsync(string command, CancellationToken ct = default) =>
            ExecuteAsync(command, ct: ct);
        public IReadOnlyCollection<BackgroundTaskInfo> GetActiveBackgroundTasks() => [];
        public Task<bool> CancelBackgroundTaskAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingSink : IMessageSink
    {
        private readonly ConcurrentQueue<string> _texts = new();
        public IReadOnlyCollection<string> Texts => _texts.ToArray();

        public Task SendTextAsync(long chatId, string text, CancellationToken ct = default)
        {
            _texts.Enqueue(text);
            return Task.CompletedTask;
        }

        public Task SendTypingAsync(long chatId, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendPhotoAsync(long chatId, string filePath, string? caption, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingEvents : IConversationEventPublisher
    {
        private readonly ConcurrentQueue<string> _kinds = new();
        public IReadOnlyCollection<string> Kinds => _kinds.ToArray();

        public void Publish(ConversationEvent evt) => _kinds.Enqueue(evt.Kind);

        public bool Publish<TPayload>(long chatId, string kind, ConversationIdentity identity, TPayload? payload)
            where TPayload : class
        {
            _kinds.Enqueue(kind);
            return true;
        }
    }
}
