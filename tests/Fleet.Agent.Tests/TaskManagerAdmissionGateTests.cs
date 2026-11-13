using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Fleet.Agent.Tests;

/// <summary>
/// The agent runs one turn at a time. These tests drive the three places where that used to depend
/// on timing: two admissions deciding at once, an arrival between a turn leaving and the queue
/// being drained, and a message that reaches the queue after the drain already ran.
/// </summary>
public class TaskManagerAdmissionGateTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task TwoAdmissionsDecidingAtOnce_RegisterOneTurnAndQueueTheOther()
    {
        var executor = new HeldExecutor();
        var manager = BuildManager(executor);
        using var firstInside = new ManualResetEventSlim();
        using var secondInside = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var decisions = 0;
        manager.DispatchCheckpointForTest = checkpoint =>
        {
            if (checkpoint != "admission_idle") return;
            if (Interlocked.Increment(ref decisions) == 1)
            {
                firstInside.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }
            else
            {
                secondInside.Set();
            }
        };

        var first = Task.Run(() => manager.StartTask(1, "first", "first", isSessionTask: true));
        Assert.True(firstInside.Wait(Wait));
        var second = Task.Run(() => manager.StartTask(2, "second", "second", isSessionTask: true));

        // The first admission has found the agent idle and has not registered its turn yet. A
        // second admission must not reach the same conclusion while it is there.
        Assert.False(secondInside.Wait(TimeSpan.FromMilliseconds(300)));

        release.Set();
        Assert.Equal(TaskDispatchOutcome.Ran, await first.WaitAsync(Wait));
        Assert.Equal(TaskDispatchOutcome.Queued, await second.WaitAsync(Wait));
        await executor.WaitForExecuteCountAsync(1);

        Assert.Equal(["first"], executor.ExecutedTasks);
        Assert.True(manager.HasRunningTasks(1));
        Assert.False(manager.HasRunningTasks(2));
        Assert.Single(manager.GetQueueSnapshot());

        executor.ReleaseAllTurns();
        await executor.WaitForExecuteCountAsync(2);
        Assert.Equal(["first", "second"], executor.ExecutedTasks);
        await WaitUntilAsync(() => !manager.HasRunningTasks(1) && !manager.HasRunningTasks(2));
    }

    [Fact]
    public async Task ArrivalBetweenTurnEndAndQueueDrain_WaitsBehindQueuedWork()
    {
        var executor = new HeldExecutor();
        var manager = BuildManager(executor);

        _ = manager.StartTask(1, "running", "running", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);
        Assert.Equal(TaskDispatchOutcome.Queued,
            await manager.StartTask(2, "queued", "queued", isSessionTask: true));

        // The first status change after the finishing turn has left the running set. An arrival
        // there used to find nothing running and start at once, ahead of the queued entry; now the
        // queued entry's turn is registered in the same step, so the arrival waits behind it.
        Task<TaskDispatchOutcome>? late = null;
        var fired = 0;
        manager.OnStatusChanged += () =>
        {
            if (manager.HasRunningTasks(1)) return;
            if (Interlocked.Exchange(ref fired, 1) == 1) return;
            late = manager.StartTask(3, "late", "late", isSessionTask: true);
        };

        executor.ReleaseAllTurns();
        await executor.WaitForExecuteCountAsync(3);

        Assert.NotNull(late);
        Assert.Equal(TaskDispatchOutcome.Queued, await late!.WaitAsync(Wait));
        Assert.Equal(["running", "queued", "late"], executor.ExecutedTasks);
    }

    [Theory]
    [InlineData(TaskSource.UserMessage)]
    [InlineData(TaskSource.Relay)]
    [InlineData(TaskSource.CheckIn)]
    public async Task ArrivalWhenTheLastTurnHasStoppedCountingAsRunning_RunsAtOnce(TaskSource source)
    {
        // What an observer sees and what admission decides must be the same thing. This holds the
        // finishing turn at the first moment HasRunningTasks reports it gone, with nothing queued,
        // and sends a new message from there. Nothing is running, so it runs: not queued behind a
        // slot the finishing turn still holds, not told the agent is busy, and, for a check-in,
        // not dropped.
        var executor = new HeldExecutor();
        var sink = new TextSink();
        var manager = BuildManager(executor, sink: sink);

        _ = manager.StartTask(1, "running", "running", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);

        var late = new TaskCompletionSource<TaskDispatchOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? statusSeen = null;
        var fired = 0;
        manager.OnStatusChanged += () =>
        {
            if (manager.HasRunningTasks(1)) return;
            if (Interlocked.Exchange(ref fired, 1) == 1) return;
            statusSeen = manager.GetOrchestratorStatus().Status;
            var outcome = manager.StartTask(2, "late", "late", isSessionTask: source != TaskSource.CheckIn,
                source: source, relaySender: source == TaskSource.Relay ? "peer" : null);
            outcome.ContinueWith(t => late.TrySetResult(t.Result), TaskScheduler.Default);
        };

        executor.ReleaseAllTurns();

        Assert.Equal(TaskDispatchOutcome.Ran, await late.Task.WaitAsync(Wait));
        Assert.Equal("idle", statusSeen);
        await executor.WaitForExecuteCountAsync(2);
        await WaitUntilAsync(() => !manager.HasRunningTasks(2));
        Assert.DoesNotContain(sink.Texts, t => t.Contains("I'm busy") || t.Contains("Now processing your queued message"));
    }

    [Fact]
    public async Task StatusDoesNotSayIdleBetweenAFinishingTurnAndTheQueuedOneItHandsOverTo()
    {
        // The other half of the same rule: while queued work is being started the agent is busy,
        // and it must say so. The slot goes from the finishing turn to the next one in one step.
        var executor = new HeldExecutor();
        var manager = BuildManager(executor);

        _ = manager.StartTask(1, "running", "running", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);
        Assert.Equal(TaskDispatchOutcome.Queued,
            await manager.StartTask(2, "queued", "queued", isSessionTask: true));

        var statuses = new ConcurrentQueue<(string Status, bool Chat1, bool Chat2)>();
        manager.OnStatusChanged += () =>
            statuses.Enqueue((manager.GetOrchestratorStatus().Status, manager.HasRunningTasks(1), manager.HasRunningTasks(2)));

        executor.ReleaseAllTurns();
        await executor.WaitForExecuteCountAsync(2);
        await WaitUntilAsync(() => !manager.HasRunningTasks(1) && !manager.HasRunningTasks(2));
        await WaitUntilAsync(() => statuses.Any(s => s.Status == "idle"));

        var seen = statuses.ToList();
        var firstIdle = seen.FindIndex(s => s.Status == "idle");
        // Idle is reported once, at the end, after the queued turn too has finished.
        Assert.DoesNotContain(seen.Skip(firstIdle), s => s.Status != "idle");
        Assert.Contains(seen.Take(firstIdle), s => s is { Status: "busy", Chat1: false, Chat2: true });
        Assert.Equal(["running", "queued"], executor.ExecutedTasks);
    }

    [Fact]
    public async Task MessageQueuedAfterTheDrainAlreadyRan_StillStartsItsTurn()
    {
        var executor = new HeldExecutor();
        var manager = BuildManager(executor);
        using var parked = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        manager.DispatchCheckpointForTest = checkpoint =>
        {
            if (checkpoint != "mid_turn_delivery") return;
            parked.Set();
            resume.Wait(TimeSpan.FromSeconds(10));
        };

        _ = manager.StartTask(1, "running", "running", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);

        // The second message has seen the running turn and is about to be delivered into it.
        var late = Task.Run(() => manager.StartTask(1, "late", "late", isSessionTask: true));
        Assert.True(parked.Wait(Wait));

        // The turn ends, finds an empty queue and goes idle while that message is still in hand.
        executor.ReleaseAllTurns();
        await WaitUntilAsync(() => !manager.HasRunningTasks(1));
        // Lets the finishing turn get past its (empty) drain. It only makes a regression easier to
        // see: with the fix the outcome is the same whichever side runs first.
        await Task.Delay(100);

        resume.Set();
        Assert.Equal(TaskDispatchOutcome.Queued, await late.WaitAsync(Wait));

        // Nothing is running, so nothing would ever drain the queue: the enqueue must start it.
        await executor.WaitForExecuteCountAsync(2);
        Assert.Equal(["running", "late"], executor.ExecutedTasks);
        await WaitUntilAsync(() => manager.GetQueueSnapshot().Count == 0);
    }

    [Fact]
    public async Task MessageDuringAnExecutorTurnTheQueueDidNotStart_IsNotInjectedIntoIt()
    {
        // The warmup ping and /run go straight to the executor. While one runs, the executor's
        // live turn is not the registered one, so an injection would land in the wrong turn.
        var executor = new HeldExecutor { InjectionResult = MidTurnInjectionResult.Injected };
        var manager = BuildManager(executor);

        _ = manager.StartTask(1, "running", "running", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);

        using (manager.BeginExecutorSideUse())
        {
            Assert.Equal(TaskDispatchOutcome.Queued,
                await manager.StartTask(1, "during the ping", "during the ping", isSessionTask: true));
            Assert.Empty(executor.InjectedTasks);
        }

        // Once it is over, a message joins the running turn again.
        Assert.Equal(TaskDispatchOutcome.Injected,
            await manager.StartTask(1, "after the ping", "after the ping", isSessionTask: true));
        Assert.Contains("after the ping", Assert.Single(executor.InjectedTasks));

        // And the one that waited gets its own turn when the running one ends.
        executor.ReleaseAllTurns();
        await executor.WaitForExecuteCountAsync(2);
        Assert.Contains("during the ping", executor.ExecutedTasks[1]);
        await WaitUntilAsync(() => !manager.HasRunningTasks(1));
    }

    [Fact]
    public async Task CancelWhileAnInjectionIsRecorded_PublishesTheCancelWithoutFaulting()
    {
        // The cancel path reads the turn's merged submission ids while a mid-turn delivery may be
        // appending to them. This is a smoke test of that overlap, not a reproduction: it passed
        // before the ids were guarded too, because the overlap cannot be forced from outside.
        for (var round = 0; round < 20; round++)
        {
            var executor = new HeldExecutor { InjectionResult = MidTurnInjectionResult.Injected };
            var events = new RecordingEvents();
            var manager = BuildManager(executor, events);

            _ = manager.StartTask(1, "running", "running", isSessionTask: true, identity: Identity("s0"));
            await executor.WaitForExecuteCountAsync(1);

            var injections = Enumerable.Range(1, 3).Select(i => Task.Run(() =>
                manager.StartTask(1, $"m{i}", $"m{i}", isSessionTask: true, identity: Identity($"s{i}")))).ToArray();
            var cancel = Task.Run(() => manager.HandleCancel(1, "all"));

            await Task.WhenAll(injections).WaitAsync(Wait);
            await cancel.WaitAsync(Wait);
            // A message that lost the race with the cancel runs as a turn of its own.
            executor.ReleaseAllTurns();
            await WaitUntilAsync(() => manager.GetQueueSnapshot().Count == 0 && !manager.HasRunningTasks(1));

            Assert.DoesNotContain(Fleet.Protocol.ConversationEventKind.TurnOutcomeUnknown, events.Kinds);
        }
    }

    [Fact]
    public async Task MergedSubmissionIds_ReadWhileAnotherThreadAdds_NeverFaults()
    {
        // The interleaving the turn above cannot force: one thread snapshots for a terminal event
        // while another records injected submissions. Every snapshot must be a consistent copy.
        var running = new RunningTask
        {
            Id = 1,
            Description = "test",
            StartedAt = DateTimeOffset.UtcNow,
            Cts = new CancellationTokenSource(),
            IsSessionTask = true,
        };
        const int Rounds = 20_000;
        using var start = new ManualResetEventSlim();

        var writer = Task.Run(() =>
        {
            start.Wait();
            for (var i = 0; i < Rounds; i++)
            {
                running.AddMergedSubmissionId($"s{i}");
                if (i % 64 == 0) running.ReplaceMergedSubmissionIds([$"r{i}"]);
            }
        });
        var reader = Task.Run(() =>
        {
            start.Wait();
            for (var i = 0; i < Rounds; i++)
            {
                var snapshot = running.SnapshotMergedSubmissionIds();
                Assert.DoesNotContain(null, snapshot);
                Assert.Equal(snapshot.Count, snapshot.Distinct().Count());
            }
        });

        start.Set();
        await writer.WaitAsync(TimeSpan.FromSeconds(30));
        await reader.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(running.AddMergedSubmissionId($"s{Rounds - 1}"));
    }

    [Fact]
    public void MergedSubmissionIds_KeepOrderDropDuplicatesAndHandOutCopies()
    {
        var running = new RunningTask
        {
            Id = 1,
            Description = "test",
            StartedAt = DateTimeOffset.UtcNow,
            Cts = new CancellationTokenSource(),
            IsSessionTask = true,
        };

        Assert.True(running.AddMergedSubmissionId("a"));
        Assert.True(running.AddMergedSubmissionId("b"));
        Assert.False(running.AddMergedSubmissionId("a"));
        var before = running.SnapshotMergedSubmissionIds();

        running.ReplaceMergedSubmissionIds(["c", "c", "d"]);

        Assert.Equal(["a", "b"], before);
        Assert.Equal(["c", "d"], running.SnapshotMergedSubmissionIds());
    }

    private static Fleet.Protocol.ConversationIdentity Identity(string submission) => new()
    {
        PrincipalId = "p",
        Role = Fleet.Protocol.PrincipalRole.Owner,
        ChannelId = ChannelIds.Telegram,
        ConversationId = "1",
        SubmissionId = submission,
        Attempt = 1,
    };

    private sealed class TextSink : IMessageSink
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

    private static TaskManager BuildManager(IAgentExecutor executor, IConversationEventPublisher? events = null,
        IMessageSink? sink = null)
    {
        var options = Options.Create(new AgentOptions
        {
            Name = "test",
            Role = "test",
            WorkDir = "/tmp",
            Provider = "claude",
        });
        return new TaskManager(options, executor, new SessionManager(), NullLogger<TaskManager>.Instance,
            events: events, sink: sink ?? Substitute.For<IMessageSink>());
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(Wait);
        while (!condition())
            await Task.Delay(10, cts.Token);
    }

    private sealed class RecordingEvents : IConversationEventPublisher
    {
        private readonly ConcurrentQueue<string> _kinds = new();
        public IReadOnlyCollection<string> Kinds => _kinds.ToArray();

        public void Publish(Fleet.Protocol.ConversationEvent evt) => _kinds.Enqueue(evt.Kind);

        public bool Publish<TPayload>(long chatId, string kind, Fleet.Protocol.ConversationIdentity identity, TPayload? payload)
            where TPayload : class
        {
            _kinds.Enqueue(kind);
            return true;
        }
    }

    /// <summary>Every turn waits for <see cref="ReleaseAllTurns"/>, then answers with its own task text.</summary>
    private sealed class HeldExecutor : IAgentExecutor
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<string> _executed = new();
        private readonly ConcurrentQueue<string> _injected = new();

        public MidTurnInjectionResult InjectionResult { get; init; } = MidTurnInjectionResult.Unsupported;
        public IReadOnlyList<string> ExecutedTasks => _executed.ToList();
        public IReadOnlyList<string> InjectedTasks => _injected.ToList();
        public string? LastSessionId => "session";
        public DateTimeOffset LastActivity => DateTimeOffset.UtcNow;
        public bool IsProcessWarm => true;

        public async IAsyncEnumerable<AgentProgress> ExecuteAsync(
            string task,
            IReadOnlyList<MessageImage>? images = null,
            IReadOnlyList<MessageDocument>? documents = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            _executed.Enqueue(task);
            await _release.Task.WaitAsync(ct);
            yield return new AgentProgress { EventType = "result", Summary = task, FinalResult = task };
        }

        public Task<MidTurnInjectionResult> TryInjectMessageAsync(
            string task,
            IReadOnlyList<MessageImage>? images = null,
            IReadOnlyList<MessageDocument>? documents = null,
            CancellationToken ct = default)
        {
            if (InjectionResult.Status == MidTurnInjectionStatus.Injected) _injected.Enqueue(task);
            return Task.FromResult(InjectionResult);
        }

        public void ReleaseAllTurns() => _release.TrySetResult();
        public Task StopProcessAsync() => Task.CompletedTask;
        public Task<bool> TryStopProcessAsync() => Task.FromResult(false);
        public void RequestRestart() { }
        public IAsyncEnumerable<AgentProgress> SendCommandAsync(string command, CancellationToken ct = default) =>
            ExecuteAsync(command, ct: ct);
        public IReadOnlyCollection<BackgroundTaskInfo> GetActiveBackgroundTasks() => [];
        public Task<bool> CancelBackgroundTaskAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async Task WaitForExecuteCountAsync(int expected)
        {
            using var cts = new CancellationTokenSource(Wait);
            while (_executed.Count < expected)
                await Task.Delay(10, cts.Token);
        }
    }
}
