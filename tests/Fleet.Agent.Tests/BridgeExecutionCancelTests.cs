using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Fleet.Agent.Tests;

/// <summary>
/// A workflow reuses a task id when it runs a step again, and the agent used to know a delegated
/// task by its task id alone. A cancel issued for one attempt that arrived after the next attempt
/// had started therefore stopped the wrong work. A durable delegation's directive now carries an
/// execution id, the agent keeps it with the task while the task is queued or running, and a
/// cancel that names an execution is applied only to the task that carries the same one.
///
/// <para>
/// These run against the real <see cref="TaskManager"/>, with the two things that reach it from
/// outside written out as they arrive: the directive's JSON and the cancel route's answer. A
/// cancel with no execution id, and a directive with none, behave exactly as they did; the
/// compatibility cases at the end pin that.
/// </para>
/// </summary>
public sealed class BridgeExecutionCancelTests
{
    private const string TaskId = "wf-1/review";
    private const long Chat = 2;

    /// <summary>One entry per turn the agent ran: what it was asked, and whether it was stopped.</summary>
    private sealed class Turns : IAgentExecutor
    {
        public sealed class Turn(string task)
        {
            public string Task { get; } = task;
            public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool Stopped;
            public bool Ended;
        }

        private readonly List<Turn> _turns = [];

        public IReadOnlyList<Turn> All
        {
            get { lock (_turns) return [.. _turns]; }
        }

        public async Task<Turn> WaitForAsync(int number)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (All.Count < number) await Task.Delay(10, cts.Token);
            return All[number - 1];
        }

        public async Task WaitUntilEndedAsync(Turn turn)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!Volatile.Read(ref turn.Ended)) await Task.Delay(10, cts.Token);
        }

        public string? LastSessionId => null;
        public DateTimeOffset LastActivity => DateTimeOffset.UtcNow;
        public bool IsProcessWarm => true;

        public async IAsyncEnumerable<AgentProgress> ExecuteAsync(
            string task,
            IReadOnlyList<MessageImage>? images = null,
            IReadOnlyList<MessageDocument>? documents = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var turn = new Turn(task);
            lock (_turns) _turns.Add(turn);
            try { await turn.Finish.Task.WaitAsync(ct); }
            catch (OperationCanceledException) { Volatile.Write(ref turn.Stopped, true); }
            Volatile.Write(ref turn.Ended, true);
            yield return new AgentProgress { EventType = "result", Summary = task, FinalResult = task, IsErrorResult = false };
        }

        public Task<MidTurnInjectionResult> TryInjectMessageAsync(string task, IReadOnlyList<MessageImage>? images = null, IReadOnlyList<MessageDocument>? documents = null, CancellationToken ct = default)
            => Task.FromResult(MidTurnInjectionResult.NoActiveTurn());
        public Task StopProcessAsync() => Task.CompletedTask;
        public Task<bool> TryStopProcessAsync() => Task.FromResult(false);
        public void RequestRestart() { }
        public IAsyncEnumerable<AgentProgress> SendCommandAsync(string command, CancellationToken ct = default) => ExecuteAsync(command, ct: ct);
        public IReadOnlyCollection<BackgroundTaskInfo> GetActiveBackgroundTasks() => [];
        public Task<bool> CancelBackgroundTaskAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static readonly IOptions<AgentOptions> AgentOpts = Options.Create(new AgentOptions
    {
        Name = "test", Role = "test", Provider = "claude", ShortName = "test",
        // Not the shared temp directory: the group buffer is persisted under WorkDir.
        WorkDir = Path.Combine(Path.GetTempPath(), "bridge-execution-" + Guid.NewGuid().ToString("N")),
        GroupDebounceSeconds = 30,
    });

    private static TaskManager Manager(IAgentExecutor executor) =>
        new(AgentOpts, executor, new SessionManager(), NullLogger<TaskManager>.Instance, sink: Substitute.For<IMessageSink>());

    /// <summary>A delegated directive as the relay hands it to the task manager.</summary>
    private static Task<TaskDispatchOutcome> DirectiveAsync(TaskManager manager, string instruction, string? executionId, string taskId = TaskId) =>
        manager.StartTask(Chat, instruction, instruction, isSessionTask: true,
            source: TaskSource.Relay, relaySender: "temporal-bridge", taskId: taskId, executionId: executionId);

    private static async Task UntilIdleAsync(TaskManager manager)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (manager.HasRunningTasks(Chat) || manager.ActiveTaskIdCountForTest > 0) await Task.Delay(10, cts.Token);
    }

    // ── the named regression ──────────────────────────────────────────────────

    /// <summary>
    /// Execution 1 of a task id finishes. The workflow runs the step again and execution 2 starts
    /// under the same task id. The cancel that was issued for execution 1 arrives only now. It
    /// names execution 1, so execution 2 goes on working; a cancel that names execution 2 stops it.
    /// </summary>
    [Fact]
    public async Task ADelayedCancelForExecutionOne_DoesNotStopExecutionTwoOfTheSameTaskId()
    {
        var turns = new Turns();
        var manager = Manager(turns);

        Assert.Equal(TaskDispatchOutcome.Ran, await DirectiveAsync(manager, "first attempt", "execution-1"));
        var first = await turns.WaitForAsync(1);
        first.Finish.SetResult();
        await UntilIdleAsync(manager);

        Assert.Equal(TaskDispatchOutcome.Ran, await DirectiveAsync(manager, "second attempt", "execution-2"));
        var second = await turns.WaitForAsync(2);

        Assert.Equal(BridgeCancelOutcome.OtherExecution, await manager.CancelBridgeExecutionAsync(TaskId, "execution-1"));

        await Task.Delay(100);
        Assert.False(second.Stopped);
        Assert.False(second.Ended);
        Assert.True(manager.HasRunningTasks(Chat));

        Assert.Equal(BridgeCancelOutcome.Cancelled, await manager.CancelBridgeExecutionAsync(TaskId, "execution-2"));
        await turns.WaitUntilEndedAsync(second);
        Assert.True(second.Stopped);
        Assert.False(first.Stopped);
    }

    // ── queued, running, finished ─────────────────────────────────────────────

    [Fact]
    public async Task ATaskThatIsStillQueued_IsRemovedOnlyByACancelForItsOwnExecution()
    {
        var turns = new Turns();
        var manager = Manager(turns);
        _ = manager.StartTask(1, "something else", "something else", isSessionTask: false);
        var blocker = await turns.WaitForAsync(1);

        Assert.Equal(TaskDispatchOutcome.Queued, await DirectiveAsync(manager, "queued work", "execution-1"));

        Assert.Equal(BridgeCancelOutcome.OtherExecution, await manager.CancelBridgeExecutionAsync(TaskId, "execution-0"));
        // Still queued, still holding its task id: a repeat of its directive is dropped.
        Assert.Equal(TaskDispatchOutcome.Dropped, await DirectiveAsync(manager, "queued work", "execution-1"));

        Assert.Equal(BridgeCancelOutcome.Cancelled, await manager.CancelBridgeExecutionAsync(TaskId, "execution-1"));
        // Gone from the queue, and the task id is free again.
        Assert.Equal(BridgeCancelOutcome.NotFound, await manager.CancelBridgeExecutionAsync(TaskId, "execution-1"));

        blocker.Finish.SetResult();
        await UntilIdleAsync(manager);
        Assert.Equal(["something else"], turns.All.Select(t => t.Task).ToArray());
    }

    [Fact]
    public async Task ARunningTask_IsStoppedByACancelForItsOwnExecution_AndNoOther()
    {
        var turns = new Turns();
        var manager = Manager(turns);
        await DirectiveAsync(manager, "work", "execution-1");
        var turn = await turns.WaitForAsync(1);

        Assert.Equal(BridgeCancelOutcome.OtherExecution, await manager.CancelBridgeExecutionAsync(TaskId, "execution-2"));
        Assert.Equal(BridgeCancelOutcome.NotFound, await manager.CancelBridgeExecutionAsync("wf-1/another-step", "execution-1"));
        await Task.Delay(50);
        Assert.False(turn.Stopped);

        Assert.Equal(BridgeCancelOutcome.Cancelled, await manager.CancelBridgeExecutionAsync(TaskId, "execution-1"));
        await turns.WaitUntilEndedAsync(turn);
        Assert.True(turn.Stopped);
    }

    /// <summary>A cancel that arrives after the work is done changes nothing and says so.</summary>
    [Fact]
    public async Task ATaskThatHasFinished_IsNotFound()
    {
        var turns = new Turns();
        var manager = Manager(turns);
        await DirectiveAsync(manager, "work", "execution-1");
        (await turns.WaitForAsync(1)).Finish.SetResult();
        await UntilIdleAsync(manager);

        Assert.Equal(BridgeCancelOutcome.NotFound, await manager.CancelBridgeExecutionAsync(TaskId, "execution-1"));
    }

    /// <summary>
    /// A repeat of a directive is dropped while its task id is held, as it always was, and the
    /// task keeps the execution id of the directive that started it.
    /// </summary>
    [Fact]
    public async Task ADirectiveDroppedAsARepeat_DoesNotChangeWhoseTaskItIs()
    {
        var turns = new Turns();
        var manager = Manager(turns);
        await DirectiveAsync(manager, "first attempt", "execution-1");
        var turn = await turns.WaitForAsync(1);

        Assert.Equal(TaskDispatchOutcome.Dropped, await DirectiveAsync(manager, "second attempt", "execution-2"));

        Assert.Equal(BridgeCancelOutcome.OtherExecution, await manager.CancelBridgeExecutionAsync(TaskId, "execution-2"));
        Assert.False(turn.Stopped);
        Assert.Equal(BridgeCancelOutcome.Cancelled, await manager.CancelBridgeExecutionAsync(TaskId, "execution-1"));
    }

    // ── between holding the task id and being admitted ───────────────────────

    /// <summary>
    /// The admission gate, taken by the test. While it is held no directive can be queued or
    /// registered; one that has arrived holds its task id and waits. The gate is re-entrant for
    /// the thread that holds it, and a cancel decides under it before its first await, so a
    /// cancel called from that thread runs in exactly that moment. Everything between
    /// <c>Enter</c> and <c>Exit</c> therefore stays on one thread: no await.
    /// </summary>
    private static Lock GateOf(TaskManager manager) =>
        (Lock)typeof(TaskManager).GetField("_admissionGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager)!;

    private static void UntilReserved(TaskManager manager, int count) =>
        Assert.True(
            SpinWait.SpinUntil(() => manager.ActiveTaskIdCountForTest == count, TimeSpan.FromSeconds(10)),
            $"expected {count} task id(s) to be held");

    private static readonly TimeSpan Promptly = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A directive has arrived and holds its task id, and has not been admitted yet. The cancel
    /// for its execution arrives in that moment. It used to find the task id held and nothing
    /// under it, answer <c>not_found</c>, and the directive was then admitted and ran: a cancel
    /// that had been answered as final had stopped nothing.
    ///
    /// <para>
    /// The cancel wins now: its answer is <c>cancelled</c>, the directive is neither queued nor
    /// run, and nothing is published for it. The task id is free at once, so the next attempt's
    /// directive, arriving while the cancelled one has still not got past the gate, is admitted
    /// and runs normally.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ACancelBetweenReservationAndAdmission_Wins_AndTheExecutionNeverStarts()
    {
        var turns = new Turns();
        var manager = Manager(turns);
        var completions = 0;
        manager.OnTaskCompleted += (_, _, _, _, _, _, _, _) => Interlocked.Increment(ref completions);
        var gate = GateOf(manager);
        Task<TaskDispatchOutcome> first, second;
        Task<BridgeCancelOutcome> cancel;

        gate.Enter();
        try
        {
            first = Task.Run(() => DirectiveAsync(manager, "first attempt", "execution-1"));
            UntilReserved(manager, 1);

            cancel = manager.CancelBridgeExecutionAsync(TaskId, "execution-1");
            Assert.True(cancel.IsCompleted, "the cancel was decided under the gate, with nothing to wait for");

            // The next attempt, while the first is still waiting at the gate.
            second = Task.Run(() => DirectiveAsync(manager, "second attempt", "execution-2"));
            Assert.True(
                SpinWait.SpinUntil(() => manager.ExecutionIdOf(TaskId) == "execution-2" || second.IsCompleted, Promptly),
                "the second attempt neither took the task id nor was turned away");
            Assert.False(first.IsCompleted);
        }
        finally
        {
            gate.Exit();
        }

        Assert.Equal(BridgeCancelOutcome.Cancelled, await cancel);
        Assert.Equal(TaskDispatchOutcome.Dropped, await first.WaitAsync(Promptly));
        Assert.Equal(TaskDispatchOutcome.Ran, await second.WaitAsync(Promptly));

        var turn = await turns.WaitForAsync(1);
        Assert.Equal("second attempt", turn.Task);
        Assert.Equal("execution-2", manager.ExecutionIdOf(TaskId));

        // The first attempt's cancel, delivered once more, is another execution's business now.
        Assert.Equal(BridgeCancelOutcome.OtherExecution, await manager.CancelBridgeExecutionAsync(TaskId, "execution-1"));
        Assert.False(turn.Stopped);
        Assert.Equal(BridgeCancelOutcome.Cancelled, await manager.CancelBridgeExecutionAsync(TaskId, "execution-2"));
        await turns.WaitUntilEndedAsync(turn);
        await UntilIdleAsync(manager);

        // One turn ran, and it was never the first attempt's. The only completion is the second's.
        Assert.Equal(["second attempt"], turns.All.Select(t => t.Task).ToArray());
        Assert.Equal(1, Volatile.Read(ref completions));
    }

    /// <summary>
    /// The same moment with the agent busy, where the directive would have been queued. It is not
    /// queued: when the running turn ends, nothing follows it.
    /// </summary>
    [Fact]
    public async Task ACancelBetweenReservationAndAdmission_Wins_WhenTheDirectiveWouldHaveBeenQueued()
    {
        var turns = new Turns();
        var manager = Manager(turns);
        _ = manager.StartTask(1, "something else", "something else", isSessionTask: false);
        var blocker = await turns.WaitForAsync(1);
        var gate = GateOf(manager);
        Task<TaskDispatchOutcome> directive;
        Task<BridgeCancelOutcome> cancel;

        gate.Enter();
        try
        {
            directive = Task.Run(() => DirectiveAsync(manager, "would be queued", "execution-1"));
            UntilReserved(manager, 1);
            cancel = manager.CancelBridgeExecutionAsync(TaskId, "execution-1");
            Assert.True(cancel.IsCompleted);
        }
        finally
        {
            gate.Exit();
        }

        Assert.Equal(BridgeCancelOutcome.Cancelled, await cancel);
        Assert.Equal(TaskDispatchOutcome.Dropped, await directive.WaitAsync(Promptly));
        Assert.Equal(0, manager.ActiveTaskIdCountForTest);

        blocker.Finish.SetResult();
        await turns.WaitUntilEndedAsync(blocker);
        using (var cts = new CancellationTokenSource(Promptly))
            while (manager.HasRunningTasks(1)) await Task.Delay(10, cts.Token);
        Assert.Equal(["something else"], turns.All.Select(t => t.Task).ToArray());
        Assert.False(manager.HasRunningTasks(Chat));
    }

    /// <summary>
    /// The ordering on the other side: the admission has the gate first. The cancel waits for it,
    /// then finds the task registered and stops it there. Whichever of the two gets the gate, the
    /// execution does not survive a cancel that was answered <c>cancelled</c>.
    /// </summary>
    [Fact]
    public async Task ACancelThatArrivesWhileTheDirectiveIsBeingAdmitted_FindsItAdmitted_AndStopsIt()
    {
        var turns = new Turns();
        var manager = Manager(turns);
        Task<BridgeCancelOutcome>? cancel = null;
        var cancelIsWaiting = new ManualResetEventSlim();
        manager.DispatchCheckpointForTest = checkpoint =>
        {
            // Inside the gate, the turn not yet registered. A cancel from another thread cannot
            // get in until this returns.
            if (checkpoint != "admission_idle" || cancel is not null) return;
            cancel = Task.Run(() =>
            {
                cancelIsWaiting.Set();
                return manager.CancelBridgeExecutionAsync(TaskId, "execution-1");
            });
            Assert.True(cancelIsWaiting.Wait(Promptly));
            Assert.False(cancel.IsCompleted);
        };

        Assert.Equal(TaskDispatchOutcome.Ran, await DirectiveAsync(manager, "work", "execution-1"));

        Assert.NotNull(cancel);
        Assert.Equal(BridgeCancelOutcome.Cancelled, await cancel.WaitAsync(Promptly));
        await UntilIdleAsync(manager);
        Assert.All(turns.All, turn => Assert.True(turn.Stopped));
    }

    /// <summary>
    /// And before the directive has arrived at all there is nothing to cancel, and the answer
    /// says so. It leaves nothing behind: the directive that arrives afterwards is admitted, and
    /// a cancel that follows it stops it.
    /// </summary>
    [Fact]
    public async Task ACancelBeforeTheDirectiveHasArrived_FindsNothing_AndLeavesNothingBehind()
    {
        var turns = new Turns();
        var manager = Manager(turns);

        Assert.Equal(BridgeCancelOutcome.NotFound, await manager.CancelBridgeExecutionAsync(TaskId, "execution-1"));

        Assert.Equal(TaskDispatchOutcome.Ran, await DirectiveAsync(manager, "work", "execution-1"));
        var turn = await turns.WaitForAsync(1);
        Assert.False(turn.Stopped);
        Assert.Equal(BridgeCancelOutcome.Cancelled, await manager.CancelBridgeExecutionAsync(TaskId, "execution-1"));
        await turns.WaitUntilEndedAsync(turn);
        Assert.True(turn.Stopped);
    }

    /// <summary>
    /// A cancel without an execution id is not changed in that moment either. It still finds
    /// nothing, and the directive is then admitted and runs, as on every agent before this.
    /// </summary>
    [Fact]
    public async Task ACancelWithoutAnExecutionId_BetweenReservationAndAdmission_BehavesAsBefore()
    {
        var turns = new Turns();
        var manager = Manager(turns);
        var gate = GateOf(manager);
        Task<TaskDispatchOutcome> directive;
        Task<bool> cancel;

        gate.Enter();
        try
        {
            directive = Task.Run(() => DirectiveAsync(manager, "work", "execution-1"));
            UntilReserved(manager, 1);
            cancel = manager.CancelByBridgeTaskIdAsync(TaskId);
            Assert.True(cancel.IsCompleted);
        }
        finally
        {
            gate.Exit();
        }

        Assert.False(await cancel);
        Assert.Equal(TaskDispatchOutcome.Ran, await directive.WaitAsync(Promptly));
        var turn = await turns.WaitForAsync(1);
        Assert.False(turn.Stopped);
        Assert.Equal(BridgeCancelOutcome.Cancelled, await manager.CancelBridgeExecutionAsync(TaskId, "execution-1"));
    }

    // ── compatibility ─────────────────────────────────────────────────────────

    /// <summary>
    /// A bridge from before the id, or the in-process delegation path, sends a cancel with no
    /// execution id. It is matched by task id, exactly as it was: the same late cancel as in the
    /// first test, without the id, stops execution 2. That is the behaviour the id exists to
    /// prevent, and it is unchanged for a caller that does not send one.
    /// </summary>
    [Fact]
    public async Task ACancelWithoutAnExecutionId_IsMatchedByTaskIdAlone_AsBefore()
    {
        var turns = new Turns();
        var manager = Manager(turns);
        await DirectiveAsync(manager, "first attempt", "execution-1");
        (await turns.WaitForAsync(1)).Finish.SetResult();
        await UntilIdleAsync(manager);
        await DirectiveAsync(manager, "second attempt", "execution-2");
        var second = await turns.WaitForAsync(2);

        Assert.True(await manager.CancelByBridgeTaskIdAsync(TaskId));

        await turns.WaitUntilEndedAsync(second);
        Assert.True(second.Stopped);
    }

    /// <summary>
    /// A cancel WITH an execution id that finds a task whose directive carried none leaves it
    /// alone. Every directive of a delegation that has an id carries it, and the agent takes the
    /// id from the directive that started the task, so a task without one was started by
    /// something else: the in-process path, or a bridge from before the id. The cancel was not
    /// issued for it. A cancel without an id still stops it.
    /// </summary>
    [Fact]
    public async Task ACancelWithAnExecutionId_LeavesATaskThatHasNone()
    {
        var turns = new Turns();
        var manager = Manager(turns);
        await DirectiveAsync(manager, "started without an id", executionId: null);
        var turn = await turns.WaitForAsync(1);

        Assert.Equal(BridgeCancelOutcome.NoExecutionId, await manager.CancelBridgeExecutionAsync(TaskId, "execution-1"));
        await Task.Delay(50);
        Assert.False(turn.Stopped);

        Assert.True(await manager.CancelByBridgeTaskIdAsync(TaskId));
        await turns.WaitUntilEndedAsync(turn);
        Assert.True(turn.Stopped);
    }

    // ── what arrives, and what is answered ────────────────────────────────────

    /// <summary>
    /// The directive as the temporal bridge writes it for a durable delegation, read the way the
    /// relay reads it, and handed on the way the relay hands it on: the execution id reaches the
    /// task. A directive without the field is what it always was.
    /// </summary>
    [Fact]
    public async Task TheExecutionIdOnADirective_ReachesTheTask()
    {
        const string withId = """
            {"ChatId":7,"Sender":"temporal-bridge","Text":"do the task","Timestamp":"2026-11-14T00:00:00+00:00",
             "Type":"directive","CorrelationId":null,"TaskId":"wf-1/review","WorkflowId":null,"SignalName":null,"Repo":null,
             "ExecutionId":"execution-1"}
            """;
        const string without = """
            {"ChatId":7,"Sender":"temporal-bridge","Text":"do the task","Timestamp":"2026-11-14T00:00:00+00:00",
             "Type":"directive","CorrelationId":null,"TaskId":"wf-1/review","WorkflowId":null,"SignalName":null,"Repo":null}
            """;
        var type = typeof(GroupRelayService).GetNestedType("RelayMessage", BindingFlags.NonPublic)!;
        string? ExecutionIdOf(string json) =>
            (string?)type.GetProperty("ExecutionId")!.GetValue(JsonSerializer.Deserialize(json, type));

        Assert.Equal("execution-1", ExecutionIdOf(withId));
        Assert.Null(ExecutionIdOf(without));

        var turns = new Turns();
        var manager = Manager(turns);
        var behavior = Behavior(manager, turns);
        behavior.OnRelayMessage(7, "temporal-bridge", "do the task", RelayMessageType.Directive,
            taskId: TaskId, executionId: ExecutionIdOf(withId));
        var turn = await turns.WaitForAsync(1);

        Assert.Equal(BridgeCancelOutcome.OtherExecution, await manager.CancelBridgeExecutionAsync(TaskId, "execution-2"));
        Assert.Equal(BridgeCancelOutcome.Cancelled, await manager.CancelBridgeExecutionAsync(TaskId, "execution-1"));
        await turns.WaitUntilEndedAsync(turn);
    }

    /// <summary>
    /// The field is written only when there is an id to write (on the answer to a task that has
    /// one, see <c>BridgeExecutionAnswerTests</c>): a message without one is the bytes it always was.
    /// </summary>
    [Fact]
    public void AMessageWithoutAnExecutionId_IsTheBytesItAlwaysWas()
    {
        var type = typeof(GroupRelayService).GetNestedType("RelayMessage", BindingFlags.NonPublic)!;
        var message = JsonSerializer.Deserialize(
            """{"ChatId":7,"Sender":"test","Text":"an answer","Timestamp":"2026-11-14T00:00:00+00:00","Type":"response","TaskId":"wf-1/review"}""",
            type)!;

        var json = JsonSerializer.Serialize(message, type);

        Assert.DoesNotContain("ExecutionId", json);
        Assert.Equal(
            """{"ChatId":7,"Sender":"test","Text":"an answer","Timestamp":"2026-11-14T00:00:00+00:00","Type":"response","CorrelationId":null,"TaskId":"wf-1/review","WorkflowId":null,"SignalName":null}""",
            json);
    }

    /// <summary>
    /// The answer to a cancel that named an execution. The id is echoed, which is how the caller
    /// knows it was compared, and the outcome is one of four fixed words.
    /// </summary>
    [Theory]
    [InlineData(BridgeCancelOutcome.Cancelled, true, "cancelled")]
    [InlineData(BridgeCancelOutcome.NotFound, false, "not_found")]
    [InlineData(BridgeCancelOutcome.OtherExecution, false, "other_execution")]
    [InlineData(BridgeCancelOutcome.NoExecutionId, false, "no_execution_id")]
    public void TheAnswerToACancelThatNamedAnExecution(BridgeCancelOutcome outcome, bool cancelled, string word)
    {
        var json = JsonSerializer.Serialize(
            BridgeCancelAnswer.From(TaskId, "execution-1", outcome), new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(
            $$"""{"cancelled":{{(cancelled ? "true" : "false")}},"taskId":"wf-1/review","executionId":"execution-1","outcome":"{{word}}"}""",
            json);
    }

    /// <summary>
    /// The route itself is a few lines in Program.cs that cannot be hosted from here, so its shape
    /// is read from the source: the execution id is an optional query parameter, a request with
    /// one is answered by the comparing method, and a request without one takes the old line.
    /// </summary>
    [Fact]
    public void TheCancelRoute_ComparesAnExecutionIdOnlyWhenOneIsGiven()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Fleet.sln"))) root = root.Parent;
        var program = File.ReadAllText(Path.Combine(root!.FullName, "src/Fleet.Agent/Program.cs"));
        var from = program.IndexOf("app.MapPost(\"/cancel/{**taskId}\"", StringComparison.Ordinal);
        var route = program[from..program.IndexOf("app.MapPost(\"/cancel_bg", from, StringComparison.Ordinal)];

        Assert.Contains("(string taskId, string? executionId, IServiceProvider sp)", route);
        Assert.Contains("if (!string.IsNullOrEmpty(executionId))", route);
        Assert.Contains("await taskManager.CancelBridgeExecutionAsync(decoded, executionId)", route);
        Assert.Contains("var cancelled = await taskManager.CancelByBridgeTaskIdAsync(decoded);", route);
        Assert.Contains("return Results.Ok(new { cancelled, taskId = decoded });", route);
    }

    private static GroupBehavior Behavior(TaskManager manager, IAgentExecutor executor)
    {
        var telegram = Options.Create(new TelegramOptions());
        var relay = new GroupRelayService(AgentOpts, Options.Create(new RabbitMqOptions()), NullLogger<GroupRelayService>.Instance);
        var commands = (CommandDispatcher)RuntimeHelpers.GetUninitializedObject(typeof(CommandDispatcher));
        return new GroupBehavior(AgentOpts, telegram, new AllowlistHolder(telegram), executor, relay,
            manager, commands, new PromptAssembler(executor), NullLogger<GroupBehavior>.Instance);
    }
}
