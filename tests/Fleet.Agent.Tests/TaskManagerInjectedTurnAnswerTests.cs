using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Tests;

/// <summary>
/// #369 at the chat: when Claude CLI runs an injected message as its own turn, its answer reaches
/// the chat as its own reply, right after the turn's answer and as soon as the executor yields it,
/// instead of waiting for the user's next message. An absorbed injection, and a turn with no
/// injection, deliver exactly what they did before.
/// </summary>
public class TaskManagerInjectedTurnAnswerTests
{
    private const long Chat = 123;

    [Fact]
    public async Task InjectionAbsorbed_OneCombinedAnswer_OneDelivery()
    {
        var (manager, executor, sink, counter) = Build(extraAnswers: []);
        await RunFirstTurnWithInjectionAsync(manager, executor);

        Assert.Equal(["answer to first"], sink.Texts);
        Assert.Equal([1], executor.ReadInjectedCalls);
        Assert.Equal(0, counter.GetCount("claude", InjectionOutcomeCounter.AnsweredAsSeparateTurn));
    }

    [Fact]
    public async Task InjectionRunAsItsOwnTurn_TwoDeliveries_TheSecondAsSoonAsItsResult()
    {
        var (manager, executor, sink, counter) = Build(extraAnswers: ["answer to second"]);
        await RunFirstTurnWithInjectionAsync(manager, executor);

        Assert.Equal(["answer to first", "answer to second"], sink.Texts);
        Assert.Equal([1], executor.ReadInjectedCalls);
        var yielded = executor.ExtraYieldedAt.Single();
        var delivered = sink.SentAt[1];
        Assert.True(Stopwatch.GetElapsedTime(yielded, delivered) < TimeSpan.FromSeconds(1),
            "the injected message's answer must be delivered as soon as the executor yields it");
        Assert.Equal(1, counter.GetCount("claude", InjectionOutcomeCounter.AnsweredAsSeparateTurn));
        // Only one turn was sent: the second answer came from the injection, not from a new send.
        Assert.Equal(["first"], executor.ExecutedTasks);
    }

    [Fact]
    public async Task NoInjection_NothingIsAwaited_AndDeliveryIsUnchanged()
    {
        var (manager, executor, sink, _) = Build(extraAnswers: ["must not appear"]);
        var idle = WaitForIdle(manager);
        _ = manager.StartTask(Chat, "first", "first", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);
        executor.ReleaseTurns();
        await idle;

        Assert.Equal(["answer to first"], sink.Texts);
        Assert.Empty(executor.ReadInjectedCalls);
    }

    private static async Task RunFirstTurnWithInjectionAsync(TaskManager manager, InjectedTurnExecutor executor)
    {
        var idle = WaitForIdle(manager);
        _ = manager.StartTask(Chat, "first", "first", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);
        _ = manager.StartTask(Chat, "second", "second", isSessionTask: true);
        await executor.WaitForInjectionCountAsync(1);
        executor.ReleaseTurns();
        await idle;
    }

    private static (TaskManager, InjectedTurnExecutor, RecordingSink, InjectionOutcomeCounter) Build(IReadOnlyList<string> extraAnswers)
    {
        var executor = new InjectedTurnExecutor(extraAnswers);
        var sink = new RecordingSink();
        var counter = new InjectionOutcomeCounter();
        var options = Options.Create(new AgentOptions
        {
            Name = "test",
            Role = "test",
            WorkDir = "/tmp",
            Provider = "claude",
            ShowStats = false,
        });
        var manager = new TaskManager(options, executor, new SessionManager(), NullLogger<TaskManager>.Instance, counter, sink: sink);
        return (manager, executor, sink, counter);
    }

    private static async Task WaitForIdle(TaskManager manager)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.OnStatusChanged += () =>
        {
            if (!manager.HasRunningTasks(Chat))
                tcs.TrySetResult();
        };
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class RecordingSink : IMessageSink
    {
        private readonly ConcurrentQueue<(string Text, long At)> _sent = new();

        public List<string> Texts => _sent.Select(s => s.Text).ToList();
        public List<long> SentAt => _sent.Select(s => s.At).ToList();

        public Task SendTextAsync(long chatId, string text, CancellationToken ct = default)
        {
            _sent.Enqueue((text, Stopwatch.GetTimestamp()));
            return Task.CompletedTask;
        }

        public Task SendTypingAsync(long chatId, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendPhotoAsync(long chatId, string filePath, string? caption, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>
    /// Holds the first turn open until released so a second message is injected into it, then
    /// answers the turn, and replays <c>extraAnswers</c> as separately run injected turns.
    /// </summary>
    private sealed class InjectedTurnExecutor(IReadOnlyList<string> extraAnswers) : IAgentExecutor
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<string> _executed = new();
        private readonly ConcurrentQueue<string> _injected = new();

        public List<string> ExecutedTasks => _executed.ToList();
        public ConcurrentQueue<int> ReadInjectedCallsQueue { get; } = new();
        public List<int> ReadInjectedCalls => ReadInjectedCallsQueue.ToList();
        public List<long> ExtraYieldedAt { get; } = [];
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
            yield return new AgentProgress
            {
                EventType = "result",
                Summary = $"answer to {task}",
                FinalResult = $"answer to {task}",
            };
        }

        public Task<MidTurnInjectionResult> TryInjectMessageAsync(
            string task,
            IReadOnlyList<MessageImage>? images = null,
            IReadOnlyList<MessageDocument>? documents = null,
            CancellationToken ct = default)
        {
            _injected.Enqueue(task);
            return Task.FromResult(MidTurnInjectionResult.Injected);
        }

        public async IAsyncEnumerable<AgentProgress> ReadInjectedTurnAnswersAsync(
            int injectedMessages,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            ReadInjectedCallsQueue.Enqueue(injectedMessages);
            foreach (var answer in extraAnswers.Take(injectedMessages))
            {
                await Task.Delay(50, ct);
                ExtraYieldedAt.Add(Stopwatch.GetTimestamp());
                yield return new AgentProgress
                {
                    IsSignificant = true,
                    EventType = "result",
                    Summary = answer,
                    FinalResult = answer,
                };
            }
        }

        public void ReleaseTurns() => _release.TrySetResult();
        public Task StopProcessAsync() => Task.CompletedTask;
        public Task<bool> TryStopProcessAsync() => Task.FromResult(false);
        public void RequestRestart() { }
        public IAsyncEnumerable<AgentProgress> SendCommandAsync(string command, CancellationToken ct = default) => ExecuteAsync(command, ct: ct);
        public IReadOnlyCollection<BackgroundTaskInfo> GetActiveBackgroundTasks() => [];
        public Task<bool> CancelBackgroundTaskAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task WaitForExecuteCountAsync(int expected) => WaitUntilAsync(() => _executed.Count >= expected);
        public Task WaitForInjectionCountAsync(int expected) => WaitUntilAsync(() => _injected.Count >= expected);

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!condition())
                await Task.Delay(10, cts.Token);
        }
    }
}
