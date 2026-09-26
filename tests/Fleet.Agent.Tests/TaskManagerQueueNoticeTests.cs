using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Tests;

/// <summary>
/// #369: the busy notice and "Now processing" are sent only when the agent is busy on work that did
/// not come from this chat — another chat's turn, or a workflow directive. A message that waits behind
/// its own chat's turn, including the injected-answer read window, is queued silently and answered.
/// </summary>
public class TaskManagerQueueNoticeTests
{
    private const long Chat = 123;
    private const long OtherChat = 456;
    private const string BusyNotice = "I'm busy right now — your message is queued (position 1). I'll get to it once my current task finishes.";
    private const string TurnBusyNotice = "I'm busy right now — your message is queued. I'll get to it once my current turn finishes.";
    private const string NowProcessing = "Now processing your queued message...";

    [Fact]
    public async Task SameChat_MessageDuringTheInjectedAnswerReadWindow_IsQueuedSilently_AndAnswered()
    {
        var (manager, executor, sink) = Build();
        executor.HoldTurn("first");
        executor.HoldReadWindow();

        _ = manager.StartTask(Chat, "first", "first", isSessionTask: true);
        await executor.WaitUntilAsync(() => executor.Executed.Contains("first"));
        _ = manager.StartTask(Chat, "second", "second", isSessionTask: true);
        await executor.WaitUntilAsync(() => executor.InjectedCount == 1);
        executor.ReleaseTurn("first");
        await executor.ReadWindowEntered;

        var outcome = await manager.StartTask(Chat, "third", "third", isSessionTask: true);
        Assert.Equal(TaskDispatchOutcome.Queued, outcome);
        executor.ReleaseReadWindow();
        await sink.WaitForAsync(Chat, "answer to third");

        Assert.Equal(["answer to first", "answer to second", "answer to third"], sink.TextsFor(Chat));
        Assert.Equal(["first", "third"], executor.Executed);
    }

    [Fact]
    public async Task SameChat_MessageBehindTheChatsOwnTask_IsQueuedSilently_AndAnswered()
    {
        var (manager, executor, sink) = Build();
        executor.HoldTurn("own");

        _ = manager.StartTask(Chat, "own", "own", isSessionTask: false, source: TaskSource.NewCommand);
        await executor.WaitUntilAsync(() => executor.Executed.Contains("own"));
        var outcome = await manager.StartTask(Chat, "next", "next", isSessionTask: true);
        Assert.Equal(TaskDispatchOutcome.Queued, outcome);
        executor.ReleaseTurn("own");
        await sink.WaitForAsync(Chat, "answer to next");

        Assert.Equal(["answer to own", "answer to next"], sink.TextsFor(Chat));
    }

    [Fact]
    public async Task AnotherChatBusy_TheMessageGetsBothNotices_AsToday()
    {
        var (manager, executor, sink) = Build();
        executor.HoldTurn("other");

        _ = manager.StartTask(OtherChat, "other", "other", isSessionTask: true);
        await executor.WaitUntilAsync(() => executor.Executed.Contains("other"));
        var outcome = await manager.StartTask(Chat, "mine", "mine", isSessionTask: true);
        Assert.Equal(TaskDispatchOutcome.Queued, outcome);
        executor.ReleaseTurn("other");
        await sink.WaitForAsync(Chat, "answer to mine");

        Assert.Equal([BusyNotice, NowProcessing, "answer to mine"], sink.TextsFor(Chat));
    }

    [Fact]
    public async Task WorkflowDirectiveBusyInTheSameChat_TheFailedInjectionGetsTheNotice_AsToday()
    {
        var (manager, executor, sink) = Build();
        executor.HoldTurn("directive");
        executor.FailInjections = true;

        _ = manager.StartTask(Chat, "directive", "directive", isSessionTask: true,
            source: TaskSource.Relay, relaySender: "workflow", taskId: "wf/1");
        await executor.WaitUntilAsync(() => executor.Executed.Contains("directive"));
        var outcome = await manager.StartTask(Chat, "mine", "mine", isSessionTask: true);

        Assert.Equal(TaskDispatchOutcome.Queued, outcome);
        Assert.Contains(TurnBusyNotice, sink.TextsFor(Chat));
        executor.ReleaseTurn("directive");
    }

    private static (TaskManager, GatedExecutor, ChatRecordingSink) Build()
    {
        var executor = new GatedExecutor();
        var sink = new ChatRecordingSink();
        var options = Options.Create(new AgentOptions
        {
            Name = "test",
            Role = "test",
            WorkDir = "/tmp",
            Provider = "claude",
            ShowStats = false,
        });
        var manager = new TaskManager(options, executor, new SessionManager(), NullLogger<TaskManager>.Instance,
            new InjectionOutcomeCounter(), sink: sink);
        return (manager, executor, sink);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(10, cts.Token);
    }

    private sealed class ChatRecordingSink : IMessageSink
    {
        private readonly ConcurrentQueue<(long ChatId, string Text)> _sent = new();

        public List<string> TextsFor(long chatId) => _sent.Where(s => s.ChatId == chatId).Select(s => s.Text).ToList();

        public Task WaitForAsync(long chatId, string text) => WaitUntilAsync(() => TextsFor(chatId).Contains(text));

        public Task SendTextAsync(long chatId, string text, CancellationToken ct = default)
        {
            _sent.Enqueue((chatId, text));
            return Task.CompletedTask;
        }

        public Task SendTypingAsync(long chatId, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendPhotoAsync(long chatId, string filePath, string? caption, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>
    /// Answers each turn with "answer to {task}". A held turn waits until released; a held read
    /// window keeps the injected-answer read open until released, then yields "answer to second".
    /// </summary>
    private sealed class GatedExecutor : IAgentExecutor
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _turnGates = new();
        private readonly ConcurrentQueue<string> _executed = new();
        private readonly TaskCompletionSource _readWindowEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource? _readGate;
        private int _injected;

        public bool FailInjections { get; set; }
        public List<string> Executed => [.. _executed];
        public int InjectedCount => Volatile.Read(ref _injected);
        public Task ReadWindowEntered => _readWindowEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        public string? LastSessionId => "session";
        public DateTimeOffset LastActivity => DateTimeOffset.UtcNow;
        public bool IsProcessWarm => true;

        public void HoldTurn(string task) => _turnGates[task] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ReleaseTurn(string task) => _turnGates[task].TrySetResult();
        public void HoldReadWindow() => _readGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ReleaseReadWindow() => _readGate?.TrySetResult();
        public Task WaitUntilAsync(Func<bool> condition) => TaskManagerQueueNoticeTests.WaitUntilAsync(condition);

        public async IAsyncEnumerable<AgentProgress> ExecuteAsync(
            string task,
            IReadOnlyList<MessageImage>? images = null,
            IReadOnlyList<MessageDocument>? documents = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            _executed.Enqueue(task);
            if (_turnGates.TryGetValue(task, out var gate))
                await gate.Task.WaitAsync(ct);
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
            if (FailInjections)
                return Task.FromResult(MidTurnInjectionResult.Failed("stdin closed"));
            Interlocked.Increment(ref _injected);
            return Task.FromResult(MidTurnInjectionResult.Injected);
        }

        public async IAsyncEnumerable<AgentProgress> ReadInjectedTurnAnswersAsync(
            int injectedMessages,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            _readWindowEntered.TrySetResult();
            if (_readGate is { } gate)
                await gate.Task.WaitAsync(ct);
            for (var i = 0; i < injectedMessages; i++)
            {
                yield return new AgentProgress
                {
                    IsSignificant = true,
                    EventType = "result",
                    Summary = "answer to second",
                    FinalResult = "answer to second",
                };
            }
        }

        public Task StopProcessAsync() => Task.CompletedTask;
        public Task<bool> TryStopProcessAsync() => Task.FromResult(false);
        public void RequestRestart() { }
        public IAsyncEnumerable<AgentProgress> SendCommandAsync(string command, CancellationToken ct = default) => ExecuteAsync(command, ct: ct);
        public IReadOnlyCollection<BackgroundTaskInfo> GetActiveBackgroundTasks() => [];
        public Task<bool> CancelBackgroundTaskAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
