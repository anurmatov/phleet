using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Fleet.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RabbitMQ.Client;

namespace Fleet.Agent.Tests;

public sealed class ClientReplyOwnershipTests
{
    internal const string Stale = "SYNTHETIC_WORKFLOW_REPORT";
    internal const string Owned = "SYNTHETIC_HUMAN_ANSWER";

    public static IEnumerable<object?[]> RecoveryCases()
    {
        foreach (var source in new[] { TaskSource.Relay, TaskSource.Bridge })
        foreach (var origin in new[] { null, RecoveredSegment.User, RecoveredSegment.Notification, RecoveredSegment.Open, "mixed" })
        foreach (var final in new[] { false, true })
            yield return [source, origin, final];
    }

    [Theory]
    [MemberData(nameof(RecoveryCases))]
    public async Task QueuedClient_UnownedRecovery_IsSuppressedBeforeEveryOutput(
        TaskSource source, string? origin, bool final)
    {
        await using var h = new Harness();
        await h.StartWorkflowAsync(source);
        Assert.Equal(TaskDispatchOutcome.Queued, await h.SubmitAsync("s_host"));
        Assert.Empty(h.Executor.Injected);
        h.Executor.Release(0, Result(Stale));
        await h.Executor.WaitForTurnsAsync(2);
        // Exercise leading AND later recovery. A FinalResult must not enter the completion buffer.
        h.Executor.Release(1, Recovery(origin, final), new AgentProgress
        {
            EventType = "assistant", Summary = "SYNTHETIC_COMMENTARY", IsSignificant = true,
        }, Recovery(origin, final), Result(Owned));
        await h.WaitIdleAsync();
        await h.Pump.DrainOnceAsync(default);

        Assert.DoesNotContain(h.Adapter.Events, e => e.Kind == ConversationEventKind.TurnRecoveredAnswer);
        var terminal = Assert.Single(h.Adapter.Events, e => e.Kind == ConversationEventKind.TurnFinal);
        Assert.Equal(Owned, terminal.PayloadAs<TurnFinalPayload>()!.Text);
        Assert.Equal("s_host", terminal.Identity.SubmissionId);
        Assert.Equal(Owned, Assert.Single(h.Completions, c => c.Source == TaskSource.UserMessage).Text);
        Assert.DoesNotContain(h.Sink.ReceivedCalls(), c => c.GetArguments().Contains(OutboundOrigin.Human)
            && c.GetArguments().Any(a => a?.ToString()?.Contains(Stale) == true));
        Assert.Equal(2, h.Counter.GetCount("test", "unowned_client_recovery_suppressed"));
        await h.AssertWorkflowEnvelopeAsync();
    }

    [Fact]
    public async Task RecoveredFinalResult_WithoutCurrentAnswer_DoesNotBecomeFinalOrCompletion()
    {
        await using var h = new Harness();
        await h.SubmitAsync("s_host");
        await h.Executor.WaitForTurnsAsync(1);
        h.Executor.Release(0, Recovery(null, true));
        await h.WaitIdleAsync();
        await h.Pump.DrainOnceAsync(default);
        Assert.Equal("", Assert.Single(h.Adapter.Events, e => e.Kind == ConversationEventKind.TurnFinal)
            .PayloadAs<TurnFinalPayload>()!.Text);
        Assert.DoesNotContain(Stale, Assert.Single(h.Completions).Text);
        Assert.Equal(1, h.Counter.GetCount("test", "unowned_client_recovery_suppressed"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OrdinaryInjection_WithFullQueue_PreservesMergedAndScopedAnswers(bool separate, bool full)
    {
        await using var h = new Harness();
        await h.SubmitAsync("s_host");
        await h.Executor.WaitForTurnsAsync(1);
        if (full)
            for (var i = 0; i < 20; i++)
                Assert.Equal(TaskDispatchOutcome.Queued,
                    await h.Manager.StartTask(1000 + i, "queued work", "work", true));
        var image = new MessageImage([1, 2, 3], "image/png");
        Assert.Equal(TaskDispatchOutcome.Injected,
            await h.Intake.SubmitAsync(h.Key, "SYNTHETIC_CORRECTION", submissionId: "s_child", images: [image]));
        Assert.Contains("SYNTHETIC_CORRECTION", Assert.Single(h.Executor.Injected));
        Assert.Same(image, Assert.Single(h.Executor.InjectedImages!));
        if (separate) h.Executor.ExtraAnswers = [Result("SYNTHETIC_SCOPED_ANSWER")];
        h.Executor.Release(0, Recovery("notification", true), Result(Owned));
        await Harness.WaitAsync(() => h.Completions.Any(c => c.Source == TaskSource.UserMessage));
        if (full) await h.Manager.CancelAllAsync();
        await h.WaitIdleAsync();
        await h.Pump.DrainOnceAsync(default);
        var terminal = Assert.Single(h.Adapter.Events, e => e.Kind == ConversationEventKind.TurnFinal);
        Assert.Equal(new[] { "s_host", "s_child" }, terminal.PayloadAs<TurnFinalPayload>()!.MergedSubmissionIds);
        Assert.Equal(Owned, terminal.PayloadAs<TurnFinalPayload>()!.Text);
        var recovered = h.Adapter.Events.Where(e => e.Kind == ConversationEventKind.TurnRecoveredAnswer).ToArray();
        if (separate)
            Assert.Equal("SYNTHETIC_SCOPED_ANSWER", Assert.Single(recovered).PayloadAs<TurnRecoveredAnswerPayload>()!.Text);
        else Assert.Empty(recovered);
        Assert.Single(h.Executor.Injected);
        Assert.DoesNotContain(Stale, h.Completions.First(c => c.Source == TaskSource.UserMessage).Text);
    }

    [Fact]
    public async Task RefusedInjection_InboxContinuation_PreservesCoalescedIdsAndOrder()
    {
        await using var h = new Harness();
        h.Executor.InjectionResult = MidTurnInjectionResult.NoActiveTurn();
        await h.SubmitAsync("s_host");
        await h.Executor.WaitForTurnsAsync(1);
        Assert.Equal(TaskDispatchOutcome.Queued, await h.SubmitAsync("s_child1"));
        Assert.Equal(TaskDispatchOutcome.Queued, await h.SubmitAsync("s_child2"));
        h.Executor.Release(0, Result(Owned));
        await h.Executor.WaitForTurnsAsync(2);
        Assert.True(h.Executor.Executed[1].IndexOf("s_child1", StringComparison.Ordinal)
            < h.Executor.Executed[1].IndexOf("s_child2", StringComparison.Ordinal));
        h.Executor.Release(1, Recovery(null, true), Result("SYNTHETIC_CONTINUATION"));
        await h.WaitIdleAsync();
        await h.Pump.DrainOnceAsync(default);
        var finals = h.Adapter.Events.Where(e => e.Kind == ConversationEventKind.TurnFinal).ToArray();
        Assert.Equal(2, finals.Length);
        Assert.Equal(new[] { "s_host" }, finals[0].PayloadAs<TurnFinalPayload>()!.MergedSubmissionIds);
        Assert.Equal(new[] { "s_child1", "s_child2" }, finals[1].PayloadAs<TurnFinalPayload>()!.MergedSubmissionIds);
        Assert.NotEqual(finals[0].Identity.TurnId, finals[1].Identity.TurnId);
        Assert.Equal(2, h.Executor.Executed.Count);
        Assert.DoesNotContain(Stale, Assert.Single(h.Completions).Text);
    }

    [Fact]
    public async Task ConfiguredPrimary_QueuesThenSteers_WithoutMovingReplyOrCallbackOwnership()
    {
        await using var h = new Harness(primary: true);
        await h.StartWorkflowAsync();
        Assert.Equal(TaskDispatchOutcome.Queued, await h.SubmitAsync("s_host"));
        Assert.Contains(TaskManager.SteeringHeader, Assert.Single(h.Executor.Injected));
        h.Executor.ExtraAnswers = [Result("SYNTHETIC_STEERING_ANSWER")];
        h.Executor.Release(0, Result(Stale));
        await h.Executor.WaitForTurnsAsync(2);
        Assert.Contains(TaskManager.SteeredPartPrefix, h.Executor.Executed[1]);
        h.Executor.Release(1, Recovery("user", true), Result(Owned));
        await h.WaitIdleAsync();
        await h.Pump.DrainOnceAsync(default);
        Assert.Equal(1, h.Counter.GetCount("test", InjectionOutcomeCounter.SteeredNonHumanTurn));
        Assert.Equal(1, h.Counter.GetCount("test", InjectionOutcomeCounter.SteerAnswerDiscarded));
        var terminal = Assert.Single(h.Adapter.Events, e => e.Kind == ConversationEventKind.TurnFinal);
        Assert.Equal(Owned, terminal.PayloadAs<TurnFinalPayload>()!.Text);
        Assert.Equal(new[] { "s_host" }, terminal.PayloadAs<TurnFinalPayload>()!.MergedSubmissionIds);
        Assert.DoesNotContain(h.Adapter.Events, e => e.Kind == ConversationEventKind.TurnRecoveredAnswer);
        Assert.Equal(2, h.Executor.Executed.Count);
        await h.AssertWorkflowEnvelopeAsync();
    }

    [Theory]
    [InlineData("telegram")]
    [InlineData("example-adapter")]
    public async Task NonClientRecovery_PreservesExistingDelivery(string channel)
    {
        await using var h = new Harness(channel: channel);
        await h.SubmitAsync("s_host");
        await h.Executor.WaitForTurnsAsync(1);
        h.Executor.Release(0, Recovery(null, false), Result(Owned));
        await h.WaitIdleAsync();
        if (channel != "telegram")
        {
            await h.Pump.DrainOnceAsync(default);
            Assert.Equal(Stale, Assert.Single(h.Adapter.Events, e => e.Kind == ConversationEventKind.TurnRecoveredAnswer)
                .PayloadAs<TurnRecoveredAnswerPayload>()!.Text);
        }
        await h.Sink.Received().SendReplyAsync(h.Key,
            Arg.Is<AgentReply>(r => r.Content == Stale), OutboundOrigin.Human, Arg.Any<CancellationToken>());
        Assert.Equal(0, h.Counter.GetCount("test", "unowned_client_recovery_suppressed"));
    }

    internal static AgentProgress Result(string text) => new() { EventType = "result", Summary = text, FinalResult = text };
    internal static AgentProgress Recovery(string? origin, bool final) => new()
    {
        EventType = "recovered_answer", Summary = Stale, FinalResult = final ? Stale : null,
        RecoveredSegments = origin switch
        {
            null => null,
            "mixed" => [new(Stale, RecoveredSegment.User), new(Stale, RecoveredSegment.Notification), new(Stale, RecoveredSegment.Open)],
            _ => [new(Stale, origin)],
        },
    };

    // Shared by recording-HTTP tests so both boundaries use the same real intake/runtime/pump.
    internal sealed class Harness : IAsyncDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), $"reply-ownership-{Guid.NewGuid():N}");
        private readonly RelayCompletionPublisher _publisher;
        private TaskSource _workflowSource;
        public ControlledExecutor Executor { get; } = new();
        public InjectionOutcomeCounter Counter { get; } = new();
        public IMessageSink Sink { get; } = Substitute.For<IMessageSink>();
        public TaskManager Manager { get; }
        public ConversationIntake Intake { get; }
        public ConversationEventPump Pump { get; }
        public RecordingAdapter Adapter { get; }
        public long Key { get; }
        public ConcurrentQueue<(TaskSource Source, string Text)> Completions { get; } = new();
        public ConcurrentQueue<JsonElement> Envelopes { get; } = new();

        public Harness(IChannelAdapter? south = null, string channel = "client", bool primary = false)
        {
            Directory.CreateDirectory(_dir);
            Sink.RendersReplies.Returns(true);
            var options = Options.Create(new AgentOptions { Name = "agent1", Role = "example", WorkDir = _dir, Provider = "test" });
            var telegram = Options.Create(new TelegramOptions());
            var relay = new GroupRelayService(options, Options.Create(new RabbitMqOptions { Host = "localhost" }), NullLogger<GroupRelayService>.Instance);
            var broker = Substitute.For<IChannel>();
            broker.BasicPublishAsync<BasicProperties>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(),
                Arg.Any<BasicProperties>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
                .Returns(ValueTask.CompletedTask).AndDoes(call =>
                {
                    using var doc = JsonDocument.Parse(call.ArgAt<ReadOnlyMemory<byte>>(4));
                    Envelopes.Enqueue(doc.RootElement.Clone());
                });
            typeof(GroupRelayService).GetField("_publishChannel", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(relay, broker);
            typeof(GroupRelayService).GetField("_initialized", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(relay, true);
            var registry = new ConversationRegistry();
            var counters = new ConversationEventCounters();
            var bus = new ConversationEventBus(registry, counters, NullLogger<ConversationEventBus>.Instance);
            Manager = new TaskManager(options, Executor, new SessionManager(), NullLogger<TaskManager>.Instance,
                injectionCounter: Counter, events: bus, sink: Sink);
            Manager.OnTaskCompleted += (_, text, _, source, _, _, _, _) => Completions.Enqueue((source, text));
            _publisher = new RelayCompletionPublisher(Manager, relay, NullLogger<RelayCompletionPublisher>.Instance);
            var allowlist = new AllowlistHolder(telegram);
            var commands = new CommandDispatcher(Manager, Executor, options, NullLogger<CommandDispatcher>.Instance, sink: Sink);
            var groups = new GroupBehavior(options, telegram, allowlist, Executor, relay, Manager, commands,
                new PromptAssembler(Executor), NullLogger<GroupBehavior>.Instance, sink: Sink);
            Intake = new ConversationIntake(registry, new PrincipalBinder(Options.Create(new ClientChannelOptions()), options, allowlist),
                Manager, groups, bus, NullLogger<ConversationIntake>.Instance,
                Options.Create(new ConversationsOptions { PrimaryHumanPrincipalId = primary ? "p_owner" : "" }));
            Key = channel == "telegram" ? 42 : Intake.OpenAuthorized(channel, "c_01HZZZ", "p_owner").RuntimeKey;
            Adapter = new RecordingAdapter(south, channel);
            Pump = new ConversationEventPump(bus, registry, counters, channel == "telegram" ? [] : [Adapter], NullLogger<ConversationEventPump>.Instance);
        }

        public Task<TaskDispatchOutcome?> SubmitAsync(string id) => Key == 42
            ? SubmitTelegramAsync(id) : Intake.SubmitAsync(Key, id, submissionId: id);
        private async Task<TaskDispatchOutcome?> SubmitTelegramAsync(string id) =>
            await Manager.StartTask(Key, id, id, true);
        public async Task StartWorkflowAsync(TaskSource source = TaskSource.Bridge)
        {
            Assert.Equal(TaskDispatchOutcome.Ran, await Manager.StartTask(7, "SYNTHETIC_WORKFLOW", "work", true,
                source: source, relaySender: source == TaskSource.Bridge ? "bridge" : "agent2",
                correlationId: "synthetic-correlation", taskId: "synthetic-task"));
            _workflowSource = source;
            await Executor.WaitForTurnsAsync(1);
        }
        public Task WaitIdleAsync() => WaitAsync(() => Manager.GetOrchestratorStatus().Status == "idle" && Manager.GetQueueSnapshot().Count == 0);
        public async Task AssertWorkflowEnvelopeAsync()
        {
            await WaitAsync(() => Envelopes.Count > 0);
            var envelope = Assert.Single(Envelopes);
            Assert.Single(Completions, c => c.Source == _workflowSource);
            Assert.Equal(_workflowSource == TaskSource.Bridge ? RelayMessageType.BridgeResponse : RelayMessageType.Response,
                envelope.GetProperty("Type").GetString());
            if (_workflowSource == TaskSource.Bridge)
                Assert.Equal("synthetic-correlation", envelope.GetProperty("CorrelationId").GetString());
            Assert.Equal("synthetic-task", envelope.GetProperty("TaskId").GetString());
            Assert.Equal(_workflowSource == TaskSource.Bridge ? Stale : "[status: completed]\n" + Stale,
                envelope.GetProperty("Text").GetString());
        }
        public static async Task WaitAsync(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!condition()) await Task.Delay(5, timeout.Token);
        }
        public async ValueTask DisposeAsync()
        {
            await Manager.CancelAllAsync();
            await WaitIdleAsync();
            _publisher.Dispose();
            Pump.Dispose();
            Directory.Delete(_dir, true);
        }
    }

    internal sealed class RecordingAdapter(IChannelAdapter? inner, string channel) : IChannelAdapter
    {
        public string ChannelId => channel;
        public List<ConversationEvent> Events { get; } = [];
        public async Task DeliverAsync(ConversationEvent evt, CancellationToken ct)
        {
            Events.Add(evt);
            if (inner is not null) await inner.DeliverAsync(evt, ct);
        }
    }

    internal sealed class ControlledExecutor : IAgentExecutor
    {
        private readonly ConcurrentDictionary<int, TaskCompletionSource<AgentProgress[]>> _turns = new();
        private readonly ConcurrentQueue<string> _executed = new();
        public IReadOnlyList<string> Executed => _executed.ToArray();
        private readonly ConcurrentQueue<string> _injected = new();
        public IReadOnlyList<string> Injected => _injected.ToArray();
        public IReadOnlyList<MessageImage>? InjectedImages { get; private set; }
        public MidTurnInjectionResult InjectionResult { get; set; } = MidTurnInjectionResult.Injected;
        public AgentProgress[] ExtraAnswers { get; set; } = [];
        public string? LastSessionId => "synthetic-session";
        public DateTimeOffset LastActivity => DateTimeOffset.UtcNow;
        public bool IsProcessWarm => true;
        public async IAsyncEnumerable<AgentProgress> ExecuteAsync(string task, IReadOnlyList<MessageImage>? images = null,
            IReadOnlyList<MessageDocument>? documents = null, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var index = Executed.Count;
            var completion = _turns.GetOrAdd(index, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
            _executed.Enqueue(task);
            foreach (var progress in await completion.Task.WaitAsync(ct)) yield return progress;
        }
        public void Release(int index, params AgentProgress[] progress) =>
            _turns.GetOrAdd(index, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).SetResult(progress);
        public Task WaitForTurnsAsync(int count) => Harness.WaitAsync(() => Executed.Count >= count);
        public Task<MidTurnInjectionResult> TryInjectMessageAsync(string task, IReadOnlyList<MessageImage>? images = null,
            IReadOnlyList<MessageDocument>? documents = null, CancellationToken ct = default)
        {
            _injected.Enqueue(task);
            InjectedImages = images;
            return Task.FromResult(InjectionResult);
        }
        public async IAsyncEnumerable<AgentProgress> ReadInjectedTurnAnswersAsync(int expectedAnswers,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            foreach (var answer in ExtraAnswers) yield return answer;
            ExtraAnswers = [];
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
