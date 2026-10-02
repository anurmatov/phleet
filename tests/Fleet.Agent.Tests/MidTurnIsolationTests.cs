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

/// <summary>Runtime conversation and completion ownership, independently of queue priority.</summary>
public sealed partial class MidTurnIsolationTests
{
    public static TheoryData<string, MidTurnInjectionStatus> Providers => new()
    {
        { "claude", MidTurnInjectionStatus.Injected },
        { "codex", MidTurnInjectionStatus.NoActiveTurn },
        { "gemini", MidTurnInjectionStatus.Unsupported },
    };

    public static IEnumerable<object[]> NonHumanTurns()
    {
        foreach (var provider in Providers)
        foreach (var source in new[] { TaskSource.Relay, TaskSource.Bridge, TaskSource.CheckIn, TaskSource.NewCommand })
            yield return [provider[0], provider[1], source];
    }

    public static IEnumerable<object[]> PrimaryNonHumanTurns()
    {
        foreach (var row in NonHumanTurns())
        foreach (var primary in new[] { true, false }) yield return [.. row, primary];
    }

    public static IEnumerable<object[]> HumanTurns()
    {
        foreach (var provider in Providers)
        foreach (var source in new[] { TaskSource.UserMessage, TaskSource.DebouncedGroupBatch })
            yield return [provider[0], provider[1], source];
    }

    [Theory]
    [MemberData(nameof(PrimaryNonHumanTurns))]
    public async Task SameChat_NonHumanOrNewTurn_QueuesWithoutChangingItsCompletion(
        string provider, MidTurnInjectionStatus status, TaskSource source, bool primary)
    {
        await using var executor = new ControlledExecutor(status);
        var sink = Substitute.For<IMessageSink>();
        var counter = new InjectionOutcomeCounter();
        var manager = Manager(provider, executor, sink, counter);
        var results = new ConcurrentQueue<(TaskSource Source, string Result, string? Correlation)>();
        manager.OnTaskCompleted += (_, result, _, completedSource, _, correlation, _, _) =>
            results.Enqueue((completedSource, result, correlation));
        // #406: a verified human steers a running Relay/Bridge turn (one text-only copy) and still
        // gets its own queued reply turn; CheckIn and /new turns are never steered.
        var workflow = source is TaskSource.Relay or TaskSource.Bridge;
        var human = workflow && status == MidTurnInjectionStatus.Injected ? $"{TaskManager.SteeredPartPrefix}\n\nhuman-only" : "human-only";
        try
        {
            Assert.Equal(TaskDispatchOutcome.Ran, await manager.StartTask(101, "workflow-only", "workflow", true,
                source: source, correlationId: "synthetic-correlation"));
            await executor.WaitStarted(1);
            Assert.Equal(TaskDispatchOutcome.Queued, await manager.StartTask(101, "human-only", "human", true, priority: primary ? TaskPriority.PrimaryHuman : TaskPriority.Routine,
                steeringEligible: workflow));
            Assert.Equal(workflow ? 1 : 0, executor.InjectionAttempts);
            Assert.Single(manager.GetQueueSnapshot());
            Assert.Equal(primary ? TaskPriority.PrimaryHuman : TaskPriority.Routine, manager.GetQueueSnapshot()[0].Priority);
            Assert.Equal(TaskSource.UserMessage, manager.GetQueueSnapshot()[0].Source);
            Assert.Empty(results);

            executor.Release();
            await executor.WaitStarted(2);
            var completion = Assert.Single(results);
            Assert.Equal(source, completion.Source);
            Assert.Equal("workflow-only", completion.Result);
            Assert.Equal("synthetic-correlation", completion.Correlation);
            Assert.Equal(human, executor.Tasks[1]);
            Assert.Equal(source is TaskSource.Relay or TaskSource.Bridge or TaskSource.CheckIn ? 1 : 0,
                counter.GetCount(provider, "not_injected_workflow_turn"));
            executor.Release();
            await Until(() => results.Count == 2 && !manager.HasRunningTasks(101));
            Assert.Equal((TaskSource.UserMessage, human, (string?)null), results.Last());
        }
        finally { await manager.CancelAllAsync(); }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CrossDm_QueuesWithoutWritingToOtherHumanTurn(string provider, MidTurnInjectionStatus status)
    {
        await using var executor = new ControlledExecutor(status);
        var manager = Manager(provider, executor);
        var completed = new ConcurrentQueue<(long Chat, string Text)>();
        manager.OnTaskCompleted += (chat, text, _, _, _, _, _, _) => completed.Enqueue((chat, text));
        try
        {
            await manager.StartTask(101, "other-person", "other", true);
            await executor.WaitStarted(1);
            Assert.Equal(TaskDispatchOutcome.Queued, await manager.StartTask(202, "new-person", "new", true, priority: TaskPriority.PrimaryHuman));
            Assert.Equal(0, executor.InjectionAttempts);
            Assert.Equal(202, Assert.Single(manager.GetQueueSnapshot()).ChatId);
            Assert.Equal(TaskPriority.PrimaryHuman, manager.GetQueueSnapshot()[0].Priority);
            executor.Release(); await executor.WaitStarted(2);
            Assert.Equal((101L, "other-person"), Assert.Single(completed));
            Assert.Equal("new-person", executor.Tasks[1]);
            executor.Release(); await Until(() => completed.Count == 2 && !manager.HasRunningTasks(202));
        }
        finally { await manager.CancelAllAsync(); }
    }

    [Theory]
    [MemberData(nameof(HumanTurns))]
    public async Task SameChat_HumanTurn_PreservesProviderInjectionOrInboxFallback(
        string provider, MidTurnInjectionStatus status, TaskSource runningSource)
    {
        await using var executor = new ControlledExecutor(status);
        var sink = Substitute.For<IMessageSink>();
        var manager = Manager(provider, executor, sink);
        try
        {
            await manager.StartTask(101, "human-turn", "first", true, runningSource);
            await executor.WaitStarted(1);
            Assert.Equal(status == MidTurnInjectionStatus.Injected ? TaskDispatchOutcome.Injected : TaskDispatchOutcome.Queued,
                await manager.StartTask(101, "[reply_to_message_id: 5] same-chat", "second", true, priority: TaskPriority.PrimaryHuman));
            Assert.Equal(1, executor.InjectionAttempts);
            Assert.Empty(manager.GetQueueSnapshot());
            Assert.Contains("[reply_to_message_id: 5]", executor.LastInjection);
            await sink.DidNotReceive().SendTextAsync(101, Arg.Is<string>(s => s.Contains("busy")));
            executor.Release();
            if (status != MidTurnInjectionStatus.Injected)
            {
                await executor.WaitStarted(2);
                Assert.Contains("[reply_to_message_id: 5]", executor.Tasks[1]);
                executor.Release();
            }
            await Until(() => !manager.HasRunningTasks(101));
        }
        finally { await manager.CancelAllAsync(); }
    }

    [Theory]
    [InlineData(TaskSource.Relay)]
    [InlineData(TaskSource.Bridge)]
    [InlineData(TaskSource.CheckIn)]
    [InlineData(TaskSource.NewCommand)]
    public async Task CheckIn_DuringNonHumanOrNewTurn_IsDropped(TaskSource source)
    {
        await using var executor = new ControlledExecutor(MidTurnInjectionStatus.Injected);
        var manager = Manager("claude", executor);
        try
        {
            await manager.StartTask(101, "running", "running", true, source);
            await executor.WaitStarted(1);
            Assert.Equal(TaskDispatchOutcome.Dropped, await manager.StartTask(101, "check-in", "check-in", true, TaskSource.CheckIn));
            Assert.Empty(manager.GetQueueSnapshot());
            Assert.Equal(0, executor.InjectionAttempts);
            executor.Release(); await Until(() => !manager.HasRunningTasks(101));
            Assert.Single(executor.Tasks);
        }
        finally { await manager.CancelAllAsync(); }
    }

    [Theory]
    [InlineData(TaskSource.Relay)]
    [InlineData(TaskSource.Bridge)]
    [InlineData(TaskSource.CheckIn)]
    [InlineData(TaskSource.NewCommand)]
    public async Task DirectFallback_NonHumanTurn_NeverAcceptsHumanInbox(TaskSource source)
    {
        await using var executor = new ControlledExecutor(MidTurnInjectionStatus.Unsupported);
        var manager = Manager("gemini", executor);
        using var cts = new CancellationTokenSource();
        var running = new RunningTask
        {
            Id = 1, Description = "workflow", StartedAt = DateTimeOffset.UtcNow,
            Cts = cts, IsSessionTask = true, Source = source,
        };
        var identity = new Fleet.Protocol.ConversationIdentity
        {
            PrincipalId = "synthetic", Role = Fleet.Protocol.PrincipalRole.Owner,
            ChannelId = ChannelIds.Telegram, ConversationId = "101",
            SubmissionId = "synthetic-submission", Attempt = 1,
        };
        var message = new MidTurnMessage("human-only", "human", true, TaskSource.UserMessage,
            null, null, null, null, null, 101, DateTimeOffset.UtcNow, identity);
        Assert.Equal(TaskDispatchOutcome.Queued,
            await manager.EnqueueForTurnEndAsync(101, running, message, notifyUser: false));
        Assert.False(running.Inbox.Reader.TryRead(out _));
        var queued = Assert.Single(manager.GetQueueSnapshot());
        Assert.Same(identity, queued.FirstPart.Identity);
        Assert.Equal(TaskSource.UserMessage, queued.Source);
        await manager.CancelAllAsync();
    }

    [Fact]
    public async Task Binding_HangingPut_DoesNotBlockDispatch_AndInjectionKeepsSequence()
    {
        using var handler = new HangingBindingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://journal.test") };
        using var publisher = new TurnBindingPublisher(new Fleet.Journal.Client.JournalHttpClient(http, "synthetic-token"),
            NullLogger<TurnBindingPublisher>.Instance);
        await publisher.StartAsync(default);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        publisher.ObserveChat(101, 7001, "private");
        await using var executor = new ControlledExecutor(MidTurnInjectionStatus.Injected);
        var manager = Manager("claude", executor, publisher: publisher);
        try
        {
            Assert.Equal(TaskDispatchOutcome.Ran, await manager.StartTask(101, "human-turn", "human", true));
            await executor.WaitStarted(1);
            Assert.False(handler.Completed);
            var bound = publisher.Current;
            Assert.Equal("bound", bound.State);
            Assert.Equal(TaskDispatchOutcome.Injected, await manager.StartTask(101, "reply", "reply", true));
            Assert.Equal(bound, publisher.Current);
            executor.Release();
            await Until(() => !manager.HasRunningTasks(101));
            Assert.Equal("unbound", publisher.Current.State);
        }
        finally
        {
            await manager.CancelAllAsync();
            await publisher.StopAsync(default);
        }
    }

    private sealed class HangingBindingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Completed;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { Completed = true; }
            return new(System.Net.HttpStatusCode.NoContent);
        }
    }

    internal static TaskManager Manager(string provider, ControlledExecutor executor, IMessageSink? sink = null,
        InjectionOutcomeCounter? counter = null, TurnBindingPublisher? publisher = null, TurnOriginLedger? ledger = null) => new(
        Options.Create(new AgentOptions { Name = "agent1", Role = "test", WorkDir = "/tmp", Provider = provider }),
        executor, new SessionManager(), NullLogger<TaskManager>.Instance, counter, sink: sink, turnBindings: publisher, ledger: ledger);

    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, deadline.Token);
    }

    internal sealed class ControlledExecutor(MidTurnInjectionStatus status, TurnOriginLedger? ledger = null) : IAgentExecutor
    {
        private readonly SemaphoreSlim _release = new(0);
        private readonly ConcurrentQueue<string> _tasks = new();
        public IReadOnlyList<string> Tasks => _tasks.ToArray();
        public int InjectionAttempts { get; private set; }
        public string LastInjection { get; private set; } = "";
        public string? LastSessionId => "synthetic-session";
        public DateTimeOffset LastActivity => DateTimeOffset.UtcNow;
        public bool IsProcessWarm => true;
        public async IAsyncEnumerable<AgentProgress> ExecuteAsync(string task,
            IReadOnlyList<MessageImage>? images = null, IReadOnlyList<MessageDocument>? documents = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            using var interval = ledger?.OpenTurn();
            _tasks.Enqueue(task);
            await _release.WaitAsync(ct);
            yield return new AgentProgress { EventType = "result", Summary = task, FinalResult = task };
        }
        public Task<MidTurnInjectionResult> TryInjectMessageAsync(string task,
            IReadOnlyList<MessageImage>? images = null, IReadOnlyList<MessageDocument>? documents = null,
            CancellationToken ct = default)
        {
            InjectionAttempts++; LastInjection = task;
            return Task.FromResult(status switch
            {
                MidTurnInjectionStatus.Injected => MidTurnInjectionResult.Injected,
                MidTurnInjectionStatus.NoActiveTurn => MidTurnInjectionResult.NoActiveTurn("synthetic -32600"),
                _ => MidTurnInjectionResult.Unsupported,
            });
        }
        public void Release() => _release.Release();
        public Task WaitStarted(int count) => Until(() => _tasks.Count >= count);
        public Task StopProcessAsync() => Task.CompletedTask;
        public Task<bool> TryStopProcessAsync() => Task.FromResult(false);
        public void RequestRestart() { }
        public IAsyncEnumerable<AgentProgress> SendCommandAsync(string command, CancellationToken ct = default) => ExecuteAsync(command, ct: ct);
        public IReadOnlyCollection<BackgroundTaskInfo> GetActiveBackgroundTasks() => [];
        public Task<bool> CancelBackgroundTaskAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
