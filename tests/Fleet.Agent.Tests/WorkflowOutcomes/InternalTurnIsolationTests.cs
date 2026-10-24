using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Fleet.Agent.Tests.Harness;
using Fleet.Journal.Client;
using Fleet.Protocol;
using Fleet.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RabbitMQ.Client;

namespace Fleet.Agent.Tests.WorkflowOutcomes;

/// <summary>
/// #476 AC11 output suppression: an internal <see cref="TaskSource.WorkflowOutcome"/> turn yields
/// zero human, client, journal, relay and bridge publications on every exit path, while ordinary
/// DM, group, relay and bridge turns behave exactly as before. Asserted on the real TaskManager,
/// RelayCompletionPublisher (capturing broker channel), CompletionContextBuffer and GroupBehavior.
/// </summary>
public sealed class InternalTurnIsolationTests : IDisposable
{
    private const string Suite = "WorkflowOutcomeSuite";
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), $"wo-isolation-{Guid.NewGuid():N}");

    public InternalTurnIsolationTests() => Directory.CreateDirectory(_workDir);

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best effort */ }
    }

    private sealed class Rig
    {
        public required TaskManager Manager { get; init; }
        public required IMessageSink Sink { get; init; }
        public required RecordingEvents Events { get; init; }
        public required ConversationRegistry Registry { get; init; }
        public required ConcurrentQueue<string> Relayed { get; init; }
        public required GroupBehavior Behavior { get; init; }
        public required ScriptedExecutor Executor { get; init; }
        public List<WorkflowOutcomeTurnEnded> Ended { get; } = [];
        public int Completed;
        public int ToolUses;
    }

    private Rig Build(ScriptedExecutor executor, bool withRegistry = true)
    {
        var agentOpts = Options.Create(new AgentOptions
        {
            Name = "coordinator", Role = "test", WorkDir = _workDir, Provider = "claude", ShortName = "coord",
        });
        var telegramOpts = Options.Create(new TelegramOptions());
        var sink = Substitute.For<IMessageSink>();
        var events = new RecordingEvents();
        var registry = new ConversationRegistry();
        var manager = new TaskManager(agentOpts, executor, new SessionManager(), NullLogger<TaskManager>.Instance,
            events: events, sink: sink, registry: withRegistry ? registry : null);

        var relayed = new ConcurrentQueue<string>();
        var relay = CapturingRelay(agentOpts, relayed);
        _ = new RelayCompletionPublisher(manager, relay, NullLogger<RelayCompletionPublisher>.Instance);

        var allowlist = new AllowlistHolder(telegramOpts);
        var commands = new CommandDispatcher(manager, executor, agentOpts, NullLogger<CommandDispatcher>.Instance, sink: sink);
        var behavior = new GroupBehavior(agentOpts, telegramOpts, allowlist, executor, relay, manager, commands,
            new PromptAssembler(executor), NullLogger<GroupBehavior>.Instance, sink: sink);
        _ = new CompletionContextBuffer(manager, behavior, sink);

        var rig = new Rig
        {
            Manager = manager, Sink = sink, Events = events, Registry = registry, Relayed = relayed,
            Behavior = behavior, Executor = executor,
        };
        manager.OnTaskCompleted += (_, _, _, _, _, _, _, _) => Interlocked.Increment(ref rig.Completed);
        manager.OnToolUse += (_, _, _) => Interlocked.Increment(ref rig.ToolUses);
        manager.OnWorkflowOutcomeTurnEnded += e => { lock (rig.Ended) rig.Ended.Add(e); };
        return rig;
    }

    private static (long Key, ConversationRef Ref, Guid BatchId) Admit(Rig rig)
    {
        var batchId = Guid.NewGuid();
        var reference = WorkflowOutcomeCompletionHandler.ConversationFor(batchId, "coordinator", 1);
        return (rig.Registry.Resolve(reference), reference, batchId);
    }

    private static Task<TaskDispatchOutcome> StartInternal(Rig rig, long key, ConversationRef reference, Guid batchId) =>
        rig.Manager.StartTask(key, "review prompt", WorkflowOutcomeCompletionHandler.DisplayText, isSessionTask: false,
            source: TaskSource.WorkflowOutcome, relaySender: null, correlationId: null,
            taskId: WorkflowOutcomeTurnIdentity.TaskId(batchId), userId: 0,
            identity: WorkflowOutcomeCompletionHandler.IdentityFor(reference));

    private static async Task WaitAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("timed out waiting for " + what);
            await Task.Delay(10);
        }
    }

    private static void AssertSilent(Rig rig, long key)
    {
        Assert.Empty(rig.Sink.ReceivedCalls());
        Assert.Empty(rig.Events.Published);
        Assert.Equal(0, rig.Completed);
        Assert.Equal(0, rig.ToolUses);
        Assert.Empty(rig.Relayed);
        Assert.Empty(rig.Behavior.GetGroupBuffer(key).GetEntries());
    }

    private static AgentProgress Warning(string text) => new() { EventType = "warning", Summary = text, IsSignificant = true };
    private static AgentProgress Recovered(string text) => new() { EventType = "recovered_answer", Summary = text, IsSignificant = true };
    private static AgentProgress Error(string text) => new() { EventType = "error", Summary = text };

    public static TheoryData<string> ExitPaths => new()
    {
        "final-with-progress", "warning", "recovered-answer", "error-event", "error-result", "no-output", "idle", "crash", "cancel",
    };

    [Theory]
    [Trait(Suite, "coordinator")]
    [MemberData(nameof(ExitPaths))]
    public async Task InternalTurn_EveryExitPath_PublishesNothingAnywhereAndEndsOnce(string path)
    {
        var tools = Enumerable.Range(0, 6).Select(i => ScriptedExecutor.Tool("Bash", "{\"command\":\"ls\"}")).ToList();
        var executor = path switch
        {
            "final-with-progress" => new ScriptedExecutor([.. tools, ScriptedExecutor.Final("{\"internal\":true}")]),
            "warning" => new ScriptedExecutor([Warning("capability notice"), ScriptedExecutor.Final("x")]),
            "recovered-answer" => new ScriptedExecutor([Recovered("a stale answer"), ScriptedExecutor.Final("x")]),
            "error-event" => new ScriptedExecutor([Error("executor failed")]),
            "error-result" => new ScriptedExecutor([ScriptedExecutor.ErrorFinal("partial")]),
            "no-output" => new ScriptedExecutor(Array.Empty<AgentProgress>()),
            "idle" => new ScriptedExecutor([ScriptedExecutor.Final("IDLE")]),
            "crash" => new ScriptedExecutor(tools.Take(1).ToArray()) { ThrowAfterScript = new InvalidOperationException("boom") },
            "cancel" => new ScriptedExecutor(tools.Take(2).ToArray()) { BlockUntilCancelled = true },
            _ => throw new ArgumentOutOfRangeException(nameof(path)),
        };
        var rig = Build(executor);
        var (key, reference, batchId) = Admit(rig);

        Assert.Equal(TaskDispatchOutcome.Ran, await StartInternal(rig, key, reference, batchId));
        if (path == "cancel")
        {
            await executor.FirstTurnStarted;
            await rig.Manager.CancelAllAsync();
        }
        var taskId = WorkflowOutcomeTurnIdentity.TaskId(batchId);
        await WaitAsync(() => !rig.Manager.IsTaskIdActive(taskId), "the internal task to end");
        await Task.Delay(100); // a stray relay publication is fire-and-forget; give it time to land

        var ended = Assert.Single(rig.Ended);
        Assert.Equal(key, ended.ChatId);
        Assert.Equal(taskId, ended.TaskId);
        Assert.Equal(TaskSource.WorkflowOutcome, ended.Source);
        if (path == "final-with-progress")
        {
            Assert.Equal(CompletionKind.Completed, ended.Kind);
            Assert.Equal("{\"internal\":true}", ended.FinalText);
        }
        else
        {
            Assert.True(ended.Kind != CompletionKind.Completed || ended.FinalText is null or "x");
        }
        AssertSilent(rig, key);
    }

    [Fact]
    [Trait(Suite, "coordinator")]
    public async Task InternalTurn_FinalText_IsTheLastResultNotTheConcatenation()
    {
        var executor = new ScriptedExecutor([
            new AgentProgress { EventType = "result", Summary = "first", FinalResult = "first progress text" },
            ScriptedExecutor.Final("{\"final\":1}")]);
        var rig = Build(executor);
        var (key, reference, batchId) = Admit(rig);

        await StartInternal(rig, key, reference, batchId);
        await WaitAsync(() => rig.Ended.Count == 1, "the turn end");

        Assert.Equal("{\"final\":1}", rig.Ended[0].FinalText);
    }

    [Fact]
    [Trait(Suite, "coordinator")]
    public async Task InternalTurn_QueueFull_SendsNoNoticeAndIsNotStarted()
    {
        var executor = new ScriptedExecutor { BlockUntilCancelled = true };
        var rig = Build(executor);
        await rig.Manager.StartTask(1001, "busy", "busy", isSessionTask: false, source: TaskSource.Relay,
            relaySender: "peer", taskId: "wf/running");
        await executor.FirstTurnStarted;
        for (var i = 0; i < DispatchQueue.MaxQueueDepth; i++)
            await rig.Manager.StartTask(2000 + i, "queued", "queued", isSessionTask: false, source: TaskSource.Relay,
                relaySender: "peer", taskId: $"wf/{i}");
        rig.Sink.ClearReceivedCalls();

        var (key, reference, batchId) = Admit(rig);
        Assert.Equal(TaskDispatchOutcome.QueueFull, await StartInternal(rig, key, reference, batchId));

        Assert.DoesNotContain(rig.Sink.ReceivedCalls(), c => c.GetArguments().FirstOrDefault() is long chat && chat == key);
        Assert.Empty(rig.Sink.ReceivedCalls());
        Assert.False(rig.Manager.IsTaskIdActive(WorkflowOutcomeTurnIdentity.TaskId(batchId)));
        Assert.Empty(rig.Ended);
        Assert.DoesNotContain(rig.Events.Published, e => e.Key == key || e.Channel == ChannelIds.WorkflowOutcome);
        await rig.Manager.CancelAllAsync();
    }

    public static TheoryData<string> Forgeries => new()
    {
        "internal-on-telegram-key", "internal-on-client-key", "internal-wrong-task-id", "internal-wrong-identity",
        "internal-unregistered-reserved-key", "internal-without-registry", "human-into-internal-key", "relay-into-internal-key",
    };

    [Theory]
    [Trait(Suite, "coordinator")]
    [MemberData(nameof(Forgeries))]
    public async Task Admission_WrongSourceKeyOrRef_IsRejectedWithNoOutput(string forgery)
    {
        var executor = new ScriptedExecutor([ScriptedExecutor.Final("should never run")]);
        var rig = Build(executor, withRegistry: forgery != "internal-without-registry");
        var (key, reference, batchId) = Admit(rig);
        var identity = WorkflowOutcomeCompletionHandler.IdentityFor(reference);
        var taskId = WorkflowOutcomeTurnIdentity.TaskId(batchId);

        var outcome = forgery switch
        {
            "internal-on-telegram-key" => await rig.Manager.StartTask(4242, "x", "x", false, TaskSource.WorkflowOutcome,
                taskId: taskId, identity: identity),
            "internal-on-client-key" => await rig.Manager.StartTask(
                rig.Registry.Resolve(new ConversationRef("example-adapter", batchId.ToString("D"), reference.PrincipalId)),
                "x", "x", false, TaskSource.WorkflowOutcome, taskId: taskId, identity: identity),
            "internal-wrong-task-id" => await rig.Manager.StartTask(key, "x", "x", false, TaskSource.WorkflowOutcome,
                taskId: WorkflowOutcomeTurnIdentity.TaskId(Guid.NewGuid()), identity: identity),
            "internal-wrong-identity" => await rig.Manager.StartTask(key, "x", "x", false, TaskSource.WorkflowOutcome,
                taskId: taskId, identity: identity with { PrincipalId = "coordinator/2" }),
            "internal-unregistered-reserved-key" => await rig.Manager.StartTask(key + 1000, "x", "x", false,
                TaskSource.WorkflowOutcome, taskId: taskId, identity: identity),
            "internal-without-registry" => await StartInternal(rig, key, reference, batchId),
            "human-into-internal-key" => await rig.Manager.StartTask(key, "x", "x", true, TaskSource.UserMessage, userId: 4242),
            "relay-into-internal-key" => await rig.Manager.StartTask(key, "x", "x", false, TaskSource.Relay,
                relaySender: "peer", taskId: "wf/forged"),
            _ => throw new ArgumentOutOfRangeException(nameof(forgery)),
        };

        Assert.Equal(TaskDispatchOutcome.Dropped, outcome);
        await Task.Delay(100);
        Assert.Empty(executor.Tasks);
        Assert.Empty(rig.Ended);
        Assert.Empty(rig.Sink.ReceivedCalls());
        Assert.Empty(rig.Events.Published);
        Assert.Empty(rig.Relayed);
        Assert.False(rig.Manager.IsTaskIdActive(taskId));
    }

    // ── ordinary turns: unchanged ─────────────────────────────────────────────────────────

    public static TheoryData<string> OrdinaryTurns => new() { "dm", "group", "relay", "bridge" };

    [Theory]
    [Trait(Suite, "coordinator")]
    [MemberData(nameof(OrdinaryTurns))]
    public async Task OrdinaryTurn_WithTheOutcomeBoundaryInPlace_EmitsExactlyWhatItDidBefore(string kind)
    {
        async Task<(string Sink, string Events, string Relayed, int Completed)> Run(bool withRegistry)
        {
            var tools = Enumerable.Range(0, 6).Select(_ => ScriptedExecutor.Tool("Read", "{\"path\":\"a\"}")).ToArray();
            var rig = Build(new ScriptedExecutor([.. tools, ScriptedExecutor.Final("the answer")]), withRegistry);
            if (withRegistry) Admit(rig); // an internal conversation exists beside the ordinary one
            var done = new TaskCompletionSource();
            rig.Manager.OnTaskCompleted += (_, _, _, _, _, _, _, _) => done.TrySetResult();
            _ = kind switch
            {
                "dm" => await rig.Manager.StartTask(4242, "hi", "hi", true, TaskSource.UserMessage, userId: 4242),
                "group" => await rig.Manager.StartTask(-4242, "hi", "hi", true, TaskSource.UserMessage, userId: 4242),
                "relay" => await rig.Manager.StartTask(0, "do it", "do it", false, TaskSource.Relay, relaySender: "peer", taskId: "wf/1"),
                "bridge" => await rig.Manager.StartTask(0, "do it", "do it", false, TaskSource.Bridge, relaySender: "bridge",
                    correlationId: "corr-1", taskId: "wf/2"),
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
            await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitAsync(() => rig.Manager.IsIdle, "the turn to end");
            if (kind is "relay" or "bridge") await WaitAsync(() => !rig.Relayed.IsEmpty, "the relay publication");
            await Task.Delay(50);

            var sink = string.Join("\n", rig.Sink.ReceivedCalls()
                .Where(c => c.GetMethodInfo().Name != nameof(IMessageSink.SendTypingAsync))
                .Select(c => c.GetMethodInfo().Name + "(" + string.Join(",", c.GetArguments().Select(Describe)) + ")"));
            // Kinds and channels, not counts: typing progress is timer-driven.
            var events = string.Join(",", rig.Events.Published.Select(e => e.Kind + "@" + e.Channel).Distinct());
            return (sink, events, string.Join("\n", rig.Relayed), rig.Completed);
        }

        var before = await Run(withRegistry: false);
        var after = await Run(withRegistry: true);

        Assert.NotEmpty(before.Sink + before.Relayed);
        Assert.Equal(before.Sink, after.Sink);
        Assert.Equal(before.Events, after.Events);
        Assert.Equal(before.Relayed, after.Relayed);
        Assert.Equal(before.Completed, after.Completed);
    }

    private static string Describe(object? arg) => arg switch
    {
        null => "null",
        AgentReply r => $"reply[{r.Content}|{r.StatsSuffix}|{r.ToolBlockHtml}]",
        CancellationToken => "ct",
        _ => arg.ToString() ?? "",
    };

    [Fact]
    [Trait(Suite, "coordinator")]
    public async Task NextTurnAfterAnInternalTurn_DropsALeadingRecoveredAnswerInsteadOfSendingIt()
    {
        var executor = new ScriptedExecutor([ScriptedExecutor.Final("{\"internal\":true}")]);
        executor.Enqueue([Recovered("leftover internal review text"), ScriptedExecutor.Final("hello human")]);
        var rig = Build(executor);
        var (key, reference, batchId) = Admit(rig);
        await StartInternal(rig, key, reference, batchId);
        await WaitAsync(() => rig.Manager.IsIdle && rig.Ended.Count == 1, "the internal turn");

        var done = new TaskCompletionSource();
        rig.Manager.OnTaskCompleted += (_, _, _, _, _, _, _, _) => done.TrySetResult();
        await rig.Manager.StartTask(4242, "hi", "hi", true, TaskSource.UserMessage, userId: 4242);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var sent = string.Join("\n", rig.Sink.ReceivedCalls().SelectMany(c => c.GetArguments()).Select(Describe));
        Assert.Contains("hello human", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("leftover", sent, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(Suite, "coordinator")]
    public async Task NextTurnWithoutAnInternalTurn_StillDeliversItsRecoveredAnswer()
    {
        var executor = new ScriptedExecutor([Recovered("a late answer"), ScriptedExecutor.Final("hello human")]);
        var rig = Build(executor);
        var done = new TaskCompletionSource();
        rig.Manager.OnTaskCompleted += (_, _, _, _, _, _, _, _) => done.TrySetResult();

        await rig.Manager.StartTask(4242, "hi", "hi", true, TaskSource.UserMessage, userId: 4242);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var sent = string.Join("\n", rig.Sink.ReceivedCalls().SelectMany(c => c.GetArguments()).Select(Describe));
        Assert.Contains("a late answer", sent, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(Suite, "coordinator")]
    public async Task InternalTurn_IsNotAHumanTurnAndIsNeverSteered()
    {
        var executor = new ScriptedExecutor { BlockUntilCancelled = true, InjectionResult = MidTurnInjectionResult.Injected };
        var rig = Build(executor);
        var (key, reference, batchId) = Admit(rig);
        await StartInternal(rig, key, reference, batchId);
        await executor.FirstTurnStarted;

        Assert.False(rig.Manager.TryGetCurrentHumanTurn(key, out _));
        await rig.Manager.StartTask(4242, "a human message", "a human message", true, TaskSource.UserMessage,
            userId: 4242, steeringEligible: true);

        Assert.Empty(executor.Injected);
        await rig.Manager.CancelAllAsync();
    }

    // ── completion subscribers skip the internal source (defence in depth) ──────────────

    [Fact]
    [Trait(Suite, "coordinator")]
    public async Task RelayPublisherAndContextBuffer_SkipAnInternalCompletionEvenWithSenderFields()
    {
        var rig = Build(new ScriptedExecutor());
        var (key, _, batchId) = Admit(rig);

        rig.Manager.RaiseTaskCompletedForTest(key, "{\"internal\":true}", relaySender: "bridge",
            source: TaskSource.WorkflowOutcome, correlationId: "corr-x", taskId: WorkflowOutcomeTurnIdentity.TaskId(batchId));
        rig.Manager.RaiseTaskCompletedForTest(key, "{\"internal\":true}", relaySender: "peer",
            source: TaskSource.WorkflowOutcome, taskId: WorkflowOutcomeTurnIdentity.TaskId(batchId));
        await Task.Delay(150);

        Assert.Empty(rig.Relayed);
        Assert.Empty(rig.Behavior.GetGroupBuffer(key).GetEntries());

        // The same call with an ordinary source still publishes and buffers.
        rig.Manager.RaiseTaskCompletedForTest(0, "answer", relaySender: "peer", source: TaskSource.Relay, taskId: "wf/9");
        await WaitAsync(() => !rig.Relayed.IsEmpty, "the ordinary relay publication");
        Assert.NotEmpty(rig.Behavior.GetGroupBuffer(0).GetEntries());
    }

    [Fact]
    [Trait(Suite, "coordinator")]
    public void TurnBinding_ForAnInternalTurn_IsUnbound()
    {
        var journal = new JournalHttpClient(new HttpClient { BaseAddress = new Uri("http://comms.invalid") }, "ingest-placeholder");
        var bindings = new TurnBindingPublisher(journal, NullLogger<TurnBindingPublisher>.Instance);
        var key = new ConversationRegistry().Resolve(
            WorkflowOutcomeCompletionHandler.ConversationFor(Guid.NewGuid(), "coordinator", 1));
        bindings.ObserveChat(key, 42, "private");

        bindings.BeginTurn(key, TaskSource.WorkflowOutcome);

        Assert.Equal("unbound", bindings.Current.State);
    }

    // ── capturing relay, the same shape as RelayCompletionPublisherTests ─────────────────

    private static GroupRelayService CapturingRelay(IOptions<AgentOptions> agentOptions, ConcurrentQueue<string> relayed)
    {
        var relay = new GroupRelayService(agentOptions, Options.Create(new RabbitMqOptions { Host = "localhost" }),
            NullLogger<GroupRelayService>.Instance);
        var channel = Substitute.For<IChannel>();
        channel.BasicPublishAsync<BasicProperties>(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<BasicProperties>(),
                Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.CompletedTask)
            .AndDoes(call =>
            {
                var body = call.ArgAt<ReadOnlyMemory<byte>>(4);
                using var document = JsonDocument.Parse(Encoding.UTF8.GetString(body.Span));
                var payload = document.RootElement;
                // The message id and timestamp differ per run; everything else must match.
                relayed.Enqueue(call.ArgAt<string>(1) + " " + payload.GetProperty("Type").GetString() + " "
                    + payload.GetProperty("Text").GetString());
            });
        foreach (var (name, value) in new (string, object)[] { ("_publishChannel", channel), ("_initialized", true) })
            relay.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(relay, value);
        return relay;
    }
}
