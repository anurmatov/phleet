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

public class TaskManagerMidTurnInjectionTests
{
    [Fact]
    public async Task StartTask_UserMessageDuringSession_InjectsIntoRunningTurn()
    {
        var executor = new ControllableExecutor { InjectionResult = MidTurnInjectionResult.Injected };
        var counter = new InjectionOutcomeCounter();
        var sink = Substitute.For<IMessageSink>();
        var manager = BuildManager(executor, sink, counter);

        var idle = WaitForIdle(manager, 123);
        manager.StartTask(123, "first", "first", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);

        manager.StartTask(123, "second", "second", isSessionTask: true);
        await executor.WaitForInjectionCountAsync(1);

        Assert.Contains("[NEW MESSAGE", executor.InjectedTasks.Single());
        Assert.Contains("second", executor.InjectedTasks.Single());
        Assert.DoesNotContain("priority", executor.InjectedTasks.Single(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, counter.GetCount("claude", InjectionOutcomeCounter.Injected));

        executor.ReleaseAllTurns();
        await idle;
    }

    [Fact]
    public async Task StartTask_MoreThanThreeMessagesInOneTurn_QueuesTheRemainder()
    {
        var executor = new ControllableExecutor { InjectionResult = MidTurnInjectionResult.Injected };
        var counter = new InjectionOutcomeCounter();
        var sink = Substitute.For<IMessageSink>();
        var manager = BuildManager(executor, sink, counter);

        var idle = WaitForIdle(manager, 123);
        manager.StartTask(123, "first", "first", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);

        for (var i = 1; i <= 5; i++)
            manager.StartTask(123, $"message {i}", $"message {i}", isSessionTask: true);

        await executor.WaitForInjectionCountAsync(3);
        await counter.WaitForCountAsync("claude", InjectionOutcomeCounter.DegradedToQueue, 2);
        executor.ReleaseAllTurns();
        await idle;

        Assert.Equal(3, executor.InjectedTasks.Count);
        Assert.Equal(3, executor.ExecutedTasks.Count);
        for (var i = 1; i <= 5; i++)
        {
            var message = $"message {i}";
            Assert.True(
                executor.ExecutedTasks.Contains(message) || executor.InjectedTasks.Any(t => t.Contains(message, StringComparison.Ordinal)),
                $"{message} was neither injected nor delivered from the fallback inbox");
        }
        Assert.Equal(3, counter.GetCount("claude", InjectionOutcomeCounter.Injected));
        Assert.Equal(2, counter.GetCount("claude", InjectionOutcomeCounter.DegradedToQueue));
    }

    [Fact]
    public async Task StartTask_InjectionRacingTurnCompletion_IsDeterministicInjectedOrQueued()
    {
        var executor = new ControllableExecutor
        {
            InjectionResult = MidTurnInjectionResult.Injected,
            HoldInjection = true,
        };
        var counter = new InjectionOutcomeCounter();
        var sink = Substitute.For<IMessageSink>();
        var manager = BuildManager(executor, sink, counter);

        var idle = WaitForIdle(manager, 123);
        manager.StartTask(123, "first", "first", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);

        manager.StartTask(123, "second", "second", isSessionTask: true);
        await executor.WaitForInjectionStartedAsync();
        executor.ReleaseAllTurns();
        await Task.Delay(50);

        Assert.True(manager.HasRunningTasks(123));

        executor.ReleaseInjection();
        await idle;

        Assert.Equal(1, counter.GetCount("claude", InjectionOutcomeCounter.Injected));
        Assert.Equal(0, counter.GetCount("claude", InjectionOutcomeCounter.DegradedToQueue));
        Assert.Single(executor.InjectedTasks);
        Assert.Equal(["first"], executor.ExecutedTasks);
    }

    [Theory]
    [InlineData(MidTurnInjectionStatus.Unsupported, InjectionOutcomeCounter.DegradedToQueue)]
    [InlineData(MidTurnInjectionStatus.NoActiveTurn, InjectionOutcomeCounter.DegradedToQueue)]
    [InlineData(MidTurnInjectionStatus.Failed, InjectionOutcomeCounter.FailedThenQueued)]
    public async Task StartTask_UnavailableInjection_QueuesForTurnEndDelivery(MidTurnInjectionStatus status, string expectedOutcome)
    {
        var result = status switch
        {
            MidTurnInjectionStatus.Unsupported => MidTurnInjectionResult.Unsupported,
            MidTurnInjectionStatus.NoActiveTurn => MidTurnInjectionResult.NoActiveTurn("turn ended"),
            MidTurnInjectionStatus.Failed => MidTurnInjectionResult.Failed("write failed"),
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
        };
        var executor = new ControllableExecutor { InjectionResult = result };
        var counter = new InjectionOutcomeCounter();
        var sink = Substitute.For<IMessageSink>();
        var manager = BuildManager(executor, sink, counter);

        var idle = WaitForIdle(manager, 123);
        manager.StartTask(123, "first", "first", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);

        manager.StartTask(123, "second", "second", isSessionTask: true);
        await counter.WaitForCountAsync("claude", expectedOutcome, 1);

        executor.ReleaseAllTurns();
        await idle;

        Assert.Empty(executor.InjectedTasks);
        Assert.Equal(["first", "second"], executor.ExecutedTasks);
        Assert.Equal(1, counter.GetCount("claude", expectedOutcome));
    }

    [Fact]
    public async Task StartTask_CheckInDuringRunningTurn_IsDeferredNotDropped()
    {
        var executor = new ControllableExecutor { InjectionResult = MidTurnInjectionResult.Injected };
        var counter = new InjectionOutcomeCounter();
        var sink = Substitute.For<IMessageSink>();
        var manager = BuildManager(executor, sink, counter);

        manager.StartTask(123, "first", "first", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);

        manager.StartTask(123, "check-in", "check-in", isSessionTask: true, source: TaskSource.CheckIn);
        executor.ReleaseAllTurns();
        await executor.WaitForExecuteCountAsync(2);
        await WaitUntilIdleAsync(manager, 123);

        Assert.Empty(executor.InjectedTasks);
        Assert.Equal(["first", "check-in"], executor.ExecutedTasks);
    }

    [Fact]
    public async Task HandleCancel_DrainsFallbackInboxToGlobalQueue()
    {
        var executor = new ControllableExecutor { InjectionResult = MidTurnInjectionResult.Unsupported };
        var counter = new InjectionOutcomeCounter();
        var sink = Substitute.For<IMessageSink>();
        var manager = BuildManager(executor, sink, counter);

        manager.StartTask(123, "first", "first", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);

        manager.StartTask(123, "second", "second", isSessionTask: true);
        await counter.WaitForCountAsync("claude", InjectionOutcomeCounter.DegradedToQueue, 1);

        await manager.HandleCancel(123, "all");
        await executor.WaitForExecuteCountAsync(2);
        executor.ReleaseAllTurns();
        await WaitUntilIdleAsync(manager, 123);

        Assert.Empty(executor.InjectedTasks);
        Assert.Equal(["first", "second"], executor.ExecutedTasks);
    }

    [Theory]
    [InlineData(TaskSource.Relay, true)]
    [InlineData(TaskSource.NewCommand, false)]
    public async Task StartTask_NonConversationalSourcesDuringSession_AreNotInjected(TaskSource source, bool isSessionTask)
    {
        var executor = new ControllableExecutor { InjectionResult = MidTurnInjectionResult.Injected };
        var counter = new InjectionOutcomeCounter();
        var sink = Substitute.For<IMessageSink>();
        var manager = BuildManager(executor, sink, counter);

        var idle = WaitForIdle(manager, 123);
        manager.StartTask(123, "first", "first", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);

        manager.StartTask(123, "second", "second", isSessionTask, source: source);
        await executor.WaitForExecuteCountAsync(2);
        executor.ReleaseAllTurns();
        await idle;

        Assert.Empty(executor.InjectedTasks);
        Assert.Equal(["first", "second"], executor.ExecutedTasks);
    }

    [Fact]
    public async Task StartTask_InjectedMessageAfterError_IsRedeliveredAsPossibleDuplicate()
    {
        var executor = new ControllableExecutor { InjectionResult = MidTurnInjectionResult.Injected };
        executor.ErrorResults.Enqueue(true);
        executor.ProcessExitResults.Enqueue(true);
        var counter = new InjectionOutcomeCounter();
        var sink = Substitute.For<IMessageSink>();
        var manager = BuildManager(executor, sink, counter);

        var idle = WaitForIdle(manager, 123);
        manager.StartTask(123, "first", "first", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);

        manager.StartTask(123, "second", "second", isSessionTask: true);
        await executor.WaitForInjectionCountAsync(1);

        executor.ReleaseAllTurns();
        await idle;

        Assert.Equal(["first", "second"], executor.ExecutedTasks);
        Assert.Equal(1, counter.GetCount("claude", InjectionOutcomeCounter.PossibleDuplicateAfterResume));
    }

    [Fact]
    public async Task StartTask_InjectedMessageAfterNonProcessError_IsNotRedelivered()
    {
        var executor = new ControllableExecutor { InjectionResult = MidTurnInjectionResult.Injected };
        executor.ErrorResults.Enqueue(true);
        var counter = new InjectionOutcomeCounter();
        var sink = Substitute.For<IMessageSink>();
        var manager = BuildManager(executor, sink, counter);

        var idle = WaitForIdle(manager, 123);
        manager.StartTask(123, "first", "first", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);

        manager.StartTask(123, "second", "second", isSessionTask: true);
        await executor.WaitForInjectionCountAsync(1);

        executor.ReleaseAllTurns();
        await idle;

        Assert.Equal(["first"], executor.ExecutedTasks);
        Assert.Equal(0, counter.GetCount("claude", InjectionOutcomeCounter.PossibleDuplicateAfterResume));
    }


    private static TaskManager BuildManager(IAgentExecutor executor, IMessageSink sink, InjectionOutcomeCounter counter)
    {
        var options = Options.Create(new AgentOptions
        {
            Name = "test",
            Role = "test",
            WorkDir = "/tmp",
            Provider = "claude",
            MaxConcurrentTasks = 5,
        });
        var manager = new TaskManager(options, executor, new SessionManager(), NullLogger<TaskManager>.Instance, counter);
        manager.Sink = sink;
        return manager;
    }

    private static async Task WaitForIdle(TaskManager manager, long chatId)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.OnStatusChanged += () =>
        {
            if (!manager.HasRunningTasks(chatId))
                tcs.TrySetResult();
        };
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task WaitUntilIdleAsync(TaskManager manager, long chatId)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (manager.HasRunningTasks(chatId))
            await Task.Delay(10, cts.Token);
    }

    private sealed class ControllableExecutor : IAgentExecutor
    {
        private readonly TaskCompletionSource _releaseTurns = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _injectionStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseInjection = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<string> _executedTasks = new();
        private readonly ConcurrentQueue<string> _injectedTasks = new();

        public ConcurrentQueue<bool> ErrorResults { get; } = new();
        public ConcurrentQueue<bool> ProcessExitResults { get; } = new();
        public MidTurnInjectionResult InjectionResult { get; init; } = MidTurnInjectionResult.Injected;
        public bool HoldInjection { get; init; }
        public IReadOnlyList<string> ExecutedTasks => _executedTasks.ToList();
        public IReadOnlyList<string> InjectedTasks => _injectedTasks.ToList();
        public string? LastSessionId => "session";
        public DateTimeOffset LastActivity => DateTimeOffset.UtcNow;
        public bool IsProcessWarm => true;

        public async IAsyncEnumerable<AgentProgress> ExecuteAsync(
            string task,
            IReadOnlyList<MessageImage>? images = null,
            IReadOnlyList<MessageDocument>? documents = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            _executedTasks.Enqueue(task);
            await _releaseTurns.Task.WaitAsync(ct);
            var isError = ErrorResults.TryDequeue(out var error) && error;
            var isProcessExit = ProcessExitResults.TryDequeue(out var processExit) && processExit;
            yield return new AgentProgress
            {
                EventType = "result",
                Summary = isError ? "executor failed" : task,
                FinalResult = isError ? "executor failed" : task,
                IsErrorResult = isError,
                IsProcessExit = isProcessExit,
            };
        }

        public async Task<MidTurnInjectionResult> TryInjectMessageAsync(
            string task,
            IReadOnlyList<MessageImage>? images = null,
            IReadOnlyList<MessageDocument>? documents = null,
            CancellationToken ct = default)
        {
            _injectionStarted.TrySetResult();
            if (HoldInjection)
                await _releaseInjection.Task.WaitAsync(ct);
            if (InjectionResult.Status == MidTurnInjectionStatus.Injected)
                _injectedTasks.Enqueue(task);
            return InjectionResult;
        }

        public void ReleaseAllTurns() => _releaseTurns.TrySetResult();
        public void ReleaseInjection() => _releaseInjection.TrySetResult();
        public Task StopProcessAsync() => Task.CompletedTask;
        public Task<bool> TryStopProcessAsync() => Task.FromResult(false);
        public void RequestRestart() { }
        public IAsyncEnumerable<AgentProgress> SendCommandAsync(string command, CancellationToken ct = default) => ExecuteAsync(command, ct: ct);
        public IReadOnlyCollection<BackgroundTaskInfo> GetActiveBackgroundTasks() => [];
        public Task<bool> CancelBackgroundTaskAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async Task WaitForExecuteCountAsync(int expected)
        {
            await WaitUntilAsync(() => _executedTasks.Count >= expected);
        }

        public async Task WaitForInjectionCountAsync(int expected)
        {
            await WaitUntilAsync(() => _injectedTasks.Count >= expected);
        }

        public Task WaitForInjectionStartedAsync() =>
            _injectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!condition())
                await Task.Delay(10, cts.Token);
        }
    }
}

file static class InjectionOutcomeCounterTestExtensions
{
    public static async Task WaitForCountAsync(this InjectionOutcomeCounter counter, string provider, string outcome, long expected)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (counter.GetCount(provider, outcome) < expected)
            await Task.Delay(10, cts.Token);
    }
}
