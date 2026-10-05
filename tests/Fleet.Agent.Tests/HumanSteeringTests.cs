using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Fleet.Agent.Services.JournalFiles;
using Fleet.Conversations.Contracts;
using Fleet.Journal.Client;
using Fleet.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Tests;

/// <summary>
/// #406 Part B on the live dispatch path: a verified human's message steers the running workflow
/// turn with a text-only copy and still gets its own reply turn; the workflow's callback, terminal
/// event, binding and ledger do not move; steering-only answers are drained and discarded and
/// nothing else is. The workflow runs on chat key 900; humans write from 101 and 202.
/// </summary>
public sealed class HumanSteeringTests
{
    private const long WorkflowChat = 900, U1Chat = 101, U2Chat = 202, Group = -300;
    private const long U1 = 7101, U2 = 7202;
    private const string Dm = "[telegram_message_id: 11] human-only";

    public static TheoryData<string, MidTurnInjectionStatus> Providers => new()
    {
        { "claude", MidTurnInjectionStatus.Injected },
        { "codex", MidTurnInjectionStatus.NoActiveTurn },
        { "gemini", MidTurnInjectionStatus.Unsupported },
    };

    public static IEnumerable<object[]> ProviderWorkflows()
    {
        foreach (var provider in Providers)
        foreach (var source in new[] { TaskSource.Relay, TaskSource.Bridge })
            yield return [provider[0], provider[1], source];
    }

    public static IEnumerable<object[]> ProviderWorkflowLanes()
    {
        foreach (var row in ProviderWorkflows())
        foreach (var primary in new[] { true, false }) yield return [.. row, primary];
    }

    // --- S1 / S4: in-flight workflow + DM ---

    [Theory]
    [MemberData(nameof(ProviderWorkflows))]
    public async Task S1_InFlightWorkflow_VerifiedHumanDm_IsQueuedThenSteeredOnce(string provider, MidTurnInjectionStatus status, TaskSource source)
    {
        await using var h = new Harness(provider, status);
        await h.StartWorkflowAsync(source);
        var image = new MessageImage([1, 2, 3], "image/png");
        var document = new MessageDocument("synthetic-file", "application/pdf", 3, "synthetic.pdf");
        Assert.Equal(TaskDispatchOutcome.Queued, await h.DmAsync(U1Chat, U1, images: [image], documents: [document]));

        var notice = Assert.Single(h.Sink.Sent, s => s.ChatId == U1Chat);
        Assert.Equal(status == MidTurnInjectionStatus.Injected
            ? "message delivered to my current task, reply pending here."
            : "I'm busy right now — your message is queued (position 1). I'll get to it once my current task finishes.", notice.Text);

        var injection = Assert.Single(h.Executor.Injections);
        Assert.StartsWith(TaskManager.SteeringHeader, injection.Text);
        Assert.Contains(Dm, injection.Text);
        Assert.DoesNotContain("[NEW MESSAGE", injection.Text);
        Assert.Contains("(2 attachments will be available in the reply turn.)", injection.Text);
        Assert.Equal((0, 0), (injection.Images, injection.Documents));
        Assert.Equal(status == MidTurnInjectionStatus.Injected ? 1 : 0, h.Count(InjectionOutcomeCounter.SteeredNonHumanTurn));
        Assert.Equal(status == MidTurnInjectionStatus.Injected ? 0 : 1, h.Count(InjectionOutcomeCounter.SteerNotDelivered));
        Assert.Equal(0, h.Count(InjectionOutcomeCounter.Injected));
        Assert.Single(h.Manager.GetQueueSnapshot());
        await h.FinishAsync();
        Assert.Single(h.Sink.Sent, e => e.ChatId == U1Chat && e.Text == "Now processing your queued message...");
        Assert.Single(h.Sink.Replies, e => e.ChatId == U1Chat);
    }

    // --- S2 / S4: workflow callback integrity ---

    [Theory]
    [MemberData(nameof(ProviderWorkflows))]
    public async Task S2_SteeredWorkflowCallbackAndTerminalEqualTheControlRun(string provider, MidTurnInjectionStatus status, TaskSource source)
    {
        var control = await RunWorkflowAsync(provider, status, source, dm: false);
        var steered = await RunWorkflowAsync(provider, status, source, dm: true);
        Assert.StartsWith(TaskManager.SteeringHeader, Assert.Single(steered.Harness.Executor.Injections).Text);
        Assert.Equal(control.Completion, steered.Completion);
        AssertTerminalEqual(control.Terminal, steered.Terminal);

        var human = Assert.Single(steered.Harness.Completions, c => c.ChatId == U1Chat);
        Assert.Equal((TaskSource.UserMessage, (string?)null, (string?)null), (human.Source, human.CorrelationId, human.TaskId));
        var humanTerminal = Assert.Single(steered.Harness.Events.Terminals(U1Chat));
        Assert.Equal("submission-101", humanTerminal.Identity.SubmissionId);
        Assert.Contains("submission-101", ((TurnFinalPayload)humanTerminal.Payload!).MergedSubmissionIds);
        Assert.DoesNotContain("submission-101", ((TurnFinalPayload)steered.Terminal.Payload!).MergedSubmissionIds);
    }

    // --- S3 / S4: reply turn ---

    [Theory]
    [MemberData(nameof(ProviderWorkflowLanes))]
    public async Task S3_ReplyTurn_RunsInTheHumansChatWithThePrefixOnce_AndQueueStateIsUnchanged(
        string provider, MidTurnInjectionStatus status, TaskSource source, bool primary)
    {
        var priority = primary ? TaskPriority.PrimaryHuman : TaskPriority.Routine;
        await using var control = new Harness(provider, status);
        await control.StartWorkflowAsync(source);
        await control.Manager.StartTask(U2Chat, "routine-other", "other", true, userId: U2);
        await control.DmAsync(U1Chat, U1, priority: priority, steeringEligible: false);
        var expected = control.Manager.GetQueueSnapshot().Select(e => (e.ChatId, e.Priority, e.PartCount)).ToList();
        await control.FinishAsync();

        await using var h = new Harness(provider, status);
        await h.StartWorkflowAsync(source);
        await h.Manager.StartTask(U2Chat, "routine-other", "other", true, userId: U2);
        await h.DmAsync(U1Chat, U1, priority: priority);
        Assert.Equal(expected, h.Manager.GetQueueSnapshot().Select(e => (e.ChatId, e.Priority, e.PartCount)).ToList());

        h.Executor.Release();
        await h.Executor.WaitStarted(2);
        var injected = status == MidTurnInjectionStatus.Injected;
        var first = expected[0].ChatId;
        var reply = first == U1Chat ? h.Executor.Tasks[1] : (await NextAsync(h, 3))[2];
        Assert.Equal(injected ? $"{TaskManager.SteeredPartPrefix}\n\n{Dm}" : Dm, reply);
        Assert.Equal(injected ? 1 : 0, Occurrences(string.Join("\n", h.Executor.Tasks), TaskManager.SteeredPartPrefix));
        await h.FinishAsync();

        Assert.Contains(h.Sink.Sent, s => s.ChatId == U1Chat && s.Text.Contains("human-only"));
        Assert.DoesNotContain(h.Sink.Sent, s => s.ChatId == WorkflowChat && s.Text.Contains("human-only"));
        var workflow = Assert.Single(h.Completions, c => c.ChatId == WorkflowChat);
        Assert.DoesNotContain("human-only", workflow.Result);
    }

    // --- S5: another human's turn ---

    [Theory]
    [InlineData(TaskSource.UserMessage)]
    [InlineData(TaskSource.DebouncedGroupBatch)]
    [InlineData(TaskSource.NewCommand)]
    public async Task S5_AnotherHumansRunningTurn_IsNeverSteered(TaskSource running)
    {
        foreach (var (provider, status) in ProviderPairs())
        {
            await using var h = new Harness(provider, status);
            await h.Manager.StartTask(U2Chat, "other-human", "other", running != TaskSource.NewCommand, running, userId: U2);
            await h.Executor.WaitStarted(1);
            Assert.Equal(TaskDispatchOutcome.Queued, await h.DmAsync(U1Chat, U1));
            Assert.Empty(h.Executor.Injections);
            Assert.Equal(0, h.SteeringCounts());
            Assert.Equal(BusyAtOne, Assert.Single(h.Sink.NoticeAttempts).Text);
            h.Executor.Release();
            await h.Executor.WaitStarted(2);
            // 202's Inbox stayed empty: its turn completed on its own text, no continuation ran.
            Assert.Equal(["other-human", Dm], h.Executor.Tasks);
            Assert.Equal("other-human", Assert.Single(h.Completions).Result);
            await h.FinishAsync();
        }
    }

    // --- S6: second human on a steered turn ---

    [Fact]
    public async Task S6_FirstSteererOwnsTheTurn_OtherHumansAreQueuedOnly()
    {
        await using var h = new Harness("claude", MidTurnInjectionStatus.Injected);
        await h.StartWorkflowAsync(TaskSource.Relay);
        Assert.Equal(TaskDispatchOutcome.Queued, await h.DmAsync(U1Chat, U1));
        Assert.Equal(TaskDispatchOutcome.Queued, await h.DmAsync(U2Chat, U2, task: "[telegram_message_id: 12] second-human", messageId: 12));
        Assert.Single(h.Executor.Injections);
        Assert.Equal(1, h.Count(InjectionOutcomeCounter.SteerRefusedOtherHuman));
        Assert.Contains("(position 2)", Assert.Single(h.Sink.NoticeAttempts, e => e.ChatId == U2Chat).Text);
        Assert.Equal(TaskDispatchOutcome.Queued, await h.DmAsync(U1Chat, U1, task: "[telegram_message_id: 13] again", messageId: 13));
        Assert.Equal(2, h.Executor.Injections.Count);
        Assert.Equal(2, h.Count(InjectionOutcomeCounter.SteeredNonHumanTurn));
        await h.FinishAsync();
        // U2 still gets a reply turn of its own.
        Assert.Contains(h.Completions, c => c.ChatId == U2Chat && c.Result.Contains("second-human"));
    }

    [Fact]
    public async Task S6_GroupChat_ADifferentUserInTheSameChatIsRefused()
    {
        await using var h = new Harness("claude", MidTurnInjectionStatus.Injected);
        await h.StartWorkflowAsync(TaskSource.Relay);
        Assert.Equal(TaskDispatchOutcome.Queued, await h.DmAsync(Group, U1));
        Assert.Equal(TaskDispatchOutcome.Queued, await h.DmAsync(Group, U2, task: "[telegram_message_id: 12] same-group", messageId: 12));
        Assert.Single(h.Executor.Injections);
        Assert.Equal(1, h.Count(InjectionOutcomeCounter.SteerRefusedOtherHuman));
        await h.FinishAsync();
    }

    // --- S7: not eligible ---

    [Fact]
    public async Task S7_IneligibleSourcesAndCheckInTurns_AreNeverSteered()
    {
        await using var h = new Harness("claude", MidTurnInjectionStatus.Injected);
        await h.StartWorkflowAsync(TaskSource.Relay);
        await h.Manager.StartTask(U1Chat, "/new", "new", false, TaskSource.NewCommand, userId: U1, steeringEligible: true);
        await h.Manager.StartTask(Group, "batch", "batch", true, TaskSource.DebouncedGroupBatch, userId: U1, steeringEligible: true);
        await h.Manager.StartTask(U2Chat, "relay", "relay", true, TaskSource.Relay, relaySender: "agent2");
        await h.DmAsync(U1Chat, U1, steeringEligible: false); // reaction, or any caller without the router's flag
        await h.Manager.StartTask(U2Chat, "client", "client", true, userId: 0);
        Assert.Empty(h.Executor.Injections);
        Assert.Equal(0, h.SteeringCounts());
        await h.FinishAsync();

        await using var checkIn = new Harness("claude", MidTurnInjectionStatus.Injected);
        await checkIn.Manager.StartTask(WorkflowChat, "check-in", "check-in", true, TaskSource.CheckIn);
        await checkIn.Executor.WaitStarted(1);
        Assert.Equal(TaskDispatchOutcome.Queued, await checkIn.DmAsync(U1Chat, U1));
        Assert.Empty(checkIn.Executor.Injections);
        await checkIn.FinishAsync();
    }

    // --- S8: guards ---

    public static IEnumerable<object[]> Guards() =>
        new[] { "final_answer_gate", "no_active_turn", "unsupported", "failed", "exception", "closed", "cap", "queue_full", "steer_only_answer" }
            .Select(g => new object[] { g });

    [Theory]
    [MemberData(nameof(Guards))]
    public async Task S8_GuardsRefuseOrSkipTheWrite_TheMessageStaysQueued_AndTheCallbackIsUnchanged(string guard)
    {
        var control = await RunWorkflowAsync("claude", MidTurnInjectionStatus.Injected, TaskSource.Relay, dm: false);
        var status = guard switch
        {
            "final_answer_gate" or "no_active_turn" => MidTurnInjectionStatus.NoActiveTurn,
            "unsupported" => MidTurnInjectionStatus.Unsupported,
            "failed" => MidTurnInjectionStatus.Failed,
            _ => MidTurnInjectionStatus.Injected,
        };
        await using var h = new Harness("claude", status) { ThrowOnInject = guard == "exception" };
        await h.StartWorkflowAsync(TaskSource.Relay);
        switch (guard)
        {
            case "cap":
                for (var i = 0; i < 4; i++)
                    Assert.Equal(TaskDispatchOutcome.Queued, await h.DmAsync(U1Chat, U1, task: $"[telegram_message_id: {20 + i}] cap-{i}", messageId: 20 + i));
                Assert.Equal(3, h.Executor.Injections.Count);
                Assert.Equal(1, h.Count(InjectionOutcomeCounter.SteerNotDelivered));
                break;
            case "queue_full":
                for (var i = 0; i < DispatchQueue.MaxQueueDepth; i++)
                    await h.Manager.StartTask(1000 + i, $"filler-{i}", "filler", true, TaskSource.Relay, relaySender: "agent2");
                Assert.Equal(TaskDispatchOutcome.QueueFull, await h.DmAsync(U1Chat, U1));
                Assert.Empty(h.Executor.Injections);
                Assert.Equal(0, h.SteeringCounts());
                break;
            case "closed":
                await h.DmAsync(U1Chat, U1);
                h.Executor.HoldDrain();
                h.Executor.Release();
                await h.Executor.WaitDrainStarted();
                Assert.Equal(TaskDispatchOutcome.Queued, await h.DmAsync(U1Chat, U1, task: "[telegram_message_id: 30] during-drain", messageId: 30));
                Assert.Single(h.Executor.Injections);
                Assert.Contains(h.Manager.GetQueueSnapshot(), e => e.ChatId == U1Chat);
                h.Executor.ReleaseDrain();
                break;
            case "steer_only_answer":
                h.Executor.OwnTurnAnswers.Enqueue("STEER-ONLY");
                Assert.Equal(TaskDispatchOutcome.Queued, await h.DmAsync(U1Chat, U1));
                break;
            default:
                Assert.Equal(TaskDispatchOutcome.Queued, await h.DmAsync(U1Chat, U1));
                Assert.Single(h.Executor.Injections);
                Assert.Equal(1, h.Count(InjectionOutcomeCounter.SteerNotDelivered));
                break;
        }
        if (guard is "final_answer_gate" or "no_active_turn" or "unsupported" or "failed" or "exception")
            Assert.Equal(BusyAtOne, Assert.Single(h.Sink.NoticeAttempts, e => e.ChatId == U1Chat).Text);
        if (guard == "queue_full")
        {
            Assert.DoesNotContain(h.Sink.NoticeAttempts, e => e.ChatId == U1Chat);
            Assert.Single(h.Sink.Sent, e => e.ChatId == U1Chat && e.Text.StartsWith("Queue is full"));
        }
        if (guard is not ("queue_full" or "closed"))
            Assert.Contains(h.Manager.GetQueueSnapshot(), e => e.ChatId == U1Chat);
        await h.FinishAsync();

        Assert.Equal(control.Completion, Assert.Single(h.Completions, c => c.ChatId == WorkflowChat));
        AssertTerminalEqual(control.Terminal, Assert.Single(h.Events.Terminals(WorkflowChat)));
        // No Inbox write: the workflow turn never ran a continuation with human text.
        Assert.Equal("workflow-only", h.Executor.Tasks[0]);
        Assert.DoesNotContain(h.Executor.Tasks.Skip(1), t => t == "workflow-only");
        Assert.DoesNotContain(h.Sink.Sent, s => s.Text.Contains("STEER-ONLY"));
        Assert.DoesNotContain(h.Events.RecoveredTexts(), t => t.Contains("STEER-ONLY"));
    }

    // --- S9: resume and cancel ---

    [Fact]
    public async Task S9_ProcessExitOfASteeredTurn_RedeliversNothingFromTheHuman()
    {
        await using var h = new Harness("claude", MidTurnInjectionStatus.Injected);
        h.Executor.ProcessExitTurns.Add(0);
        await h.StartWorkflowAsync(TaskSource.Relay);
        await h.DmAsync(U1Chat, U1);
        await h.FinishAsync();
        Assert.Equal(["workflow-only", $"{TaskManager.SteeredPartPrefix}\n\n{Dm}"], h.Executor.Tasks);
        Assert.Equal(0, h.Count(InjectionOutcomeCounter.PossibleDuplicateAfterResume));
        Assert.Empty(h.Executor.ReadCalls);
        Assert.False(h.Manager.SteeringResidueForTest);
    }

    [Fact]
    public async Task S9_CancelAfterAnInjectedCopy_NoDrainNoFlag_AndALegitimateRecoveredAnswerIsDelivered()
    {
        await using var h = new Harness("claude", MidTurnInjectionStatus.Injected);
        h.Executor.Leading[1] = Recovered((RecoveredSegment.User, "LEGIT-RECOVERED"));
        await h.StartWorkflowAsync(TaskSource.Relay);
        await h.DmAsync(U1Chat, U1);
        Assert.True(await h.Manager.CancelByBridgeTaskIdAsync("synthetic-wf/step"));
        await h.Executor.WaitStarted(2);
        Assert.False(h.Manager.SteeringResidueForTest);
        Assert.Empty(h.Executor.ReadCalls);
        var workflow = Assert.Single(h.Completions, c => c.ChatId == WorkflowChat);
        Assert.Equal(("Task cancelled.", CompletionKind.Failed, "synthetic-correlation"), (workflow.Result, workflow.Kind, workflow.CorrelationId));
        Assert.Contains(TaskManager.SteeredPartPrefix, h.Executor.Tasks[1]);
        await h.FinishAsync();

        Assert.Contains(h.Sink.Sent, s => s.ChatId == U1Chat && s.Text == "LEGIT-RECOVERED");
        Assert.Contains("LEGIT-RECOVERED", h.Events.RecoveredTexts(U1Chat));
        Assert.Equal(0, h.Count(InjectionOutcomeCounter.SteerAnswerDiscarded));
    }

    // --- S10: binding and receipts ---

    [Fact]
    public async Task S10_SteeredWorkflowTurnStaysUnbound_HasNoHumanInterval_AndTheReplyTurnBinds()
    {
        await using var h = new Harness("claude", MidTurnInjectionStatus.Injected);
        await h.StartWorkflowAsync(TaskSource.Relay);
        var before = h.Binding.Current;
        Assert.Equal("unbound", before.State);
        await h.DmAsync(U1Chat, U1);
        Assert.Single(h.Executor.Injections);
        Assert.Equal(before, h.Binding.Current);

        var handler = new JournalFilesTestDoubles.Handler();
        using var http = new HttpClient(handler) { BaseAddress = new("http://journal.test") };
        var tools = new JournalFilesTools(new JournalHttpClient(http, "ingest", readToken: "read"), h.Binding,
            new JournalFileStore(Path.Combine(Path.GetTempPath(), "steer-" + Guid.NewGuid().ToString("N"))), new JournalFilesCounter(),
            NullLogger<JournalFilesTools>.Instance);
        Assert.Equal(JournalAttachmentRequest.Unavailable("no_bound_conversation"), await tools.FetchAsync(new(TelegramMessageId: 11), default));
        Assert.Equal(0, handler.Calls);
        await Task.Delay(100);
        Assert.Equal(before, h.Binding.Current);
        Assert.DoesNotContain(h.Ledger.SnapshotForTests(), i => i.Origin == TurnOrigin.Human);

        h.Executor.Release();
        await h.Executor.WaitStarted(2);
        Assert.Equal(("bound", (long?)U1Chat), (h.Binding.Current.State, h.Binding.Current.ChatId));
        Assert.Contains(h.Ledger.SnapshotForTests(), i => i.Origin == TurnOrigin.Human && i.End is null);
        await h.FinishAsync();
    }

    // --- S11: own-turn answer drained ---

    [Fact]
    public async Task S11_SteeringOwnTurnAnswer_IsDrainedUnderTheWorkflowOrigin_AndDiscarded()
    {
        var control = await RunWorkflowAsync("claude", MidTurnInjectionStatus.Injected, TaskSource.Relay, dm: false);
        await using var h = new Harness("claude", MidTurnInjectionStatus.Injected);
        h.Executor.OwnTurnAnswers.Enqueue("STEER-ONLY");
        await h.StartWorkflowAsync(TaskSource.Relay);
        await h.DmAsync(U1Chat, U1);
        h.Executor.HoldDrain();
        h.Executor.Release();
        await h.Executor.WaitDrainStarted();
        await Task.Delay(200);
        Assert.Single(h.Executor.Tasks); // the reply turn waits for the drain
        Assert.Empty(h.Completions);
        h.Executor.ReleaseDrain();
        await h.FinishAsync();

        Assert.Equal([1], h.Executor.ReadCalls);
        Assert.Equal([TurnOrigin.Relay], h.Executor.ReadOrigins);
        Assert.Equal(control.Completion, Assert.Single(h.Completions, c => c.ChatId == WorkflowChat));
        Assert.DoesNotContain(h.Sink.Sent, s => s.Text.Contains("STEER-ONLY"));
        Assert.DoesNotContain(h.Events.RecoveredTexts(), t => t.Contains("STEER-ONLY"));
        Assert.Equal(1, h.Count(InjectionOutcomeCounter.SteerAnswerDiscarded));
        Assert.Equal(0, h.Count(InjectionOutcomeCounter.AnsweredAsSeparateTurn));
        Assert.False(h.Manager.SteeringResidueForTest);
    }

    // --- S12: late result ---

    [Theory]
    [InlineData("a", false)]
    [InlineData("a", true)]
    [InlineData("b", false)]
    [InlineData("b", true)]
    public async Task S12_AfterAShortDrain_OnlyUserSegmentsOfTheNextLeadingRecoveredAnswerAreDropped(string row, bool nextIsWorkflow)
    {
        await using var h = new Harness("claude", MidTurnInjectionStatus.Injected);
        h.Executor.Leading[1] = row == "a"
            ? Recovered((RecoveredSegment.User, "STEER-ONLY"))
            : Recovered((RecoveredSegment.Notification, "BG-NOTE"), (RecoveredSegment.User, "STEER-ONLY"), (RecoveredSegment.Open, "TAIL"));
        await h.StartWorkflowAsync(TaskSource.Relay);
        if (nextIsWorkflow)
            await h.Manager.StartTask(WorkflowChat, "workflow-2", "workflow-2", true, TaskSource.Relay, relaySender: "synthetic-sender",
                correlationId: "correlation-2");
        await h.DmAsync(U1Chat, U1);
        await h.FinishAsync();

        var next = nextIsWorkflow ? WorkflowChat : U1Chat;
        Assert.Equal([1], h.Executor.ReadCalls);
        Assert.DoesNotContain(h.Sink.Sent, s => s.Text.Contains("STEER-ONLY"));
        Assert.DoesNotContain(h.Events.RecoveredTexts(), t => t.Contains("STEER-ONLY"));
        Assert.Equal(1, h.Count(InjectionOutcomeCounter.SteerAnswerDiscarded));
        if (row == "a")
            Assert.Empty(h.Events.RecoveredTexts());
        else
        {
            Assert.Equal(["BG-NOTE\nTAIL"], h.Events.RecoveredTexts(next));
            Assert.Contains(h.Sink.Sent, s => s.ChatId == next && s.Text == "BG-NOTE\nTAIL");
        }
        // The next task's own answer and callback are unchanged.
        var own = nextIsWorkflow ? "workflow-2" : $"{TaskManager.SteeredPartPrefix}\n\n{Dm}";
        Assert.Contains(h.Completions, c => c.ChatId == next && c.Result == own);
        Assert.False(h.Manager.SteeringResidueForTest);
    }

    [Fact]
    public async Task S12c_SteeredTurnEndingInProcessExit_DoesNotArmTheGuard()
    {
        await using var h = new Harness("claude", MidTurnInjectionStatus.Injected);
        h.Executor.ProcessExitTurns.Add(0);
        h.Executor.Leading[1] = Recovered((RecoveredSegment.User, "LEGIT-RECOVERED"));
        await h.StartWorkflowAsync(TaskSource.Relay);
        await h.DmAsync(U1Chat, U1);
        await h.FinishAsync();
        Assert.Empty(h.Executor.ReadCalls);
        Assert.Equal(["LEGIT-RECOVERED"], h.Events.RecoveredTexts(U1Chat));
        Assert.Contains(h.Sink.Sent, s => s.ChatId == U1Chat && s.Text == "LEGIT-RECOVERED");
        Assert.Equal(0, h.Count(InjectionOutcomeCounter.SteerAnswerDiscarded));
    }

    [Fact]
    public async Task S12d_ATaskWithNoSteeredTurnBeforeIt_DeliversItsLeadingRecoveredAnswerFromSummary()
    {
        await using var h = new Harness("claude", MidTurnInjectionStatus.Injected);
        h.Executor.Leading[2] = Recovered((RecoveredSegment.User, "THIRD-RECOVERED"));
        await h.StartWorkflowAsync(TaskSource.Relay);
        await h.DmAsync(U1Chat, U1);
        await h.Manager.StartTask(U2Chat, "third", "third", true, userId: U2);
        await h.FinishAsync();
        Assert.Equal(3, h.Executor.Tasks.Count);
        Assert.Equal(["THIRD-RECOVERED"], h.Events.RecoveredTexts(U2Chat));
        Assert.Contains(h.Sink.Sent, s => s.ChatId == U2Chat && s.Text == "THIRD-RECOVERED");
    }

    // --- S13: normal delivery preserved ---

    [Fact]
    public async Task S13_SameChatInjectedOwnTurnAnswer_IsStillDelivered()
    {
        await using var h = new Harness("claude", MidTurnInjectionStatus.Injected);
        h.Executor.OwnTurnAnswers.Enqueue("SEPARATE");
        await h.Manager.StartTask(U1Chat, "first", "first", true, userId: U1, identity: Identity(U1Chat, "submission-first"));
        await h.Executor.WaitStarted(1);
        Assert.Equal(TaskDispatchOutcome.Injected, await h.DmAsync(U1Chat, U1));
        await h.FinishAsync();
        Assert.Contains(h.Sink.Sent, s => s.ChatId == U1Chat && s.Text == "SEPARATE");
        Assert.Equal(1, h.Count(InjectionOutcomeCounter.AnsweredAsSeparateTurn));
        Assert.Equal(0, h.Count(InjectionOutcomeCounter.SteerAnswerDiscarded));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task S13_WithNoSteeringOrAFullDrain_ALeadingRecoveredAnswerIsStillDelivered(bool steered)
    {
        await using var h = new Harness("claude", MidTurnInjectionStatus.Injected);
        if (steered) h.Executor.OwnTurnAnswers.Enqueue("STEER-ONLY");
        h.Executor.Leading[1] = Recovered((RecoveredSegment.User, "LEGIT-RECOVERED"));
        await h.StartWorkflowAsync(TaskSource.Relay);
        await h.DmAsync(U1Chat, U1, steeringEligible: steered);
        await h.FinishAsync();
        Assert.Equal(["LEGIT-RECOVERED"], h.Events.RecoveredTexts(U1Chat));
        Assert.Contains(h.Sink.Sent, s => s.ChatId == U1Chat && s.Text == "LEGIT-RECOVERED");
        Assert.Equal(steered ? 1 : 0, h.Count(InjectionOutcomeCounter.SteerAnswerDiscarded));
    }

    private const string BusyAtOne = "I'm busy right now — your message is queued (position 1). I'll get to it once my current task finishes.";
    private const string BusyAtTwo = "I'm busy right now — your message is queued (position 2). I'll get to it once my current task finishes.";
    private const string NoticeFailure = "Failed to send steering queue notice for chat {ChatId}";
    private const string SteerFailure = "Human steering attempt for chat {ChatId} escaped its handler";
    private const string NoticeSkipped = "Steering notice skipped: queue entry already dispatched";

    public static IEnumerable<object[]> NoticeFailures()
    {
        foreach (var status in new[] { MidTurnInjectionStatus.Injected, MidTurnInjectionStatus.NoActiveTurn })
        foreach (var failure in new[] { "sync", "fault", "steer", "lock" })
        foreach (var throwingLogger in new[] { false, true })
        foreach (var primary in new[] { false, true })
            yield return [status, failure, throwingLogger, primary];
    }

    [Theory]
    [MemberData(nameof(NoticeFailures))]
    public async Task SteeringNotice_Failure_PreservesQueueReservationsLocksAndExactlyOnceReply(
        MidTurnInjectionStatus status, string failure, bool throwingLogger, bool primary)
    {
        var priority = primary ? TaskPriority.PrimaryHuman : TaskPriority.Routine;
        await using var control = new Harness("claude", status);
        await control.StartWorkflowAsync(TaskSource.Relay);
        await control.DmAsync(U1Chat, U1, priority: priority, steeringEligible: false, taskId: "human-entry");
        var expected = control.Manager.GetQueueSnapshot().Select(e => (e.ChatId, e.Priority, e.PartCount)).ToArray();
        var reservations = (control.Manager.ActiveTaskIdCountForTest, control.Manager.PrimaryDedupCountForTest);
        await control.FinishAsync();

        var logger = new CapturingLogger();
        if (throwingLogger) logger.ThrowOnTemplates.UnionWith([NoticeFailure, SteerFailure, NoticeSkipped]);
        await using var h = new Harness("claude", status, logger);
        await h.StartWorkflowAsync(TaskSource.Relay);
        h.Sink.ThrowOnNotice = failure == "sync";
        h.Sink.FaultOnNotice = failure == "fault";
        if (failure == "steer") h.Manager.SteerAttemptStartingForTest = () => throw new InvalidOperationException("synthetic");
        if (failure == "lock") h.Manager.NoticeLockWaitThrowsForTest = () => throw new InvalidOperationException("synthetic");
        Assert.Equal(TaskDispatchOutcome.Queued,
            await h.DmAsync(U1Chat, U1, priority: priority, taskId: "human-entry"));
        var entry = Assert.Single(h.Manager.GetQueueSnapshot());
        Assert.Equal(expected, h.Manager.GetQueueSnapshot().Select(e => (e.ChatId, e.Priority, e.PartCount)).ToArray());
        Assert.Equal(reservations, (h.Manager.ActiveTaskIdCountForTest, h.Manager.PrimaryDedupCountForTest));
        Assert.Equal(1, entry.QueueDispatchLock.CurrentCount);
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Equal(failure == "steer" ? SteerFailure : NoticeFailure, warning.Template);
        Assert.Equal(throwingLogger ? 1 : 0, logger.Throws);
        var attempts = h.Sink.NoticeAttempts.Where(e => e.ChatId == U1Chat).ToArray();
        if (failure == "lock") Assert.Empty(attempts);
        else
            Assert.Equal(status == MidTurnInjectionStatus.Injected && failure != "steer"
                ? TaskManager.SteeringDeliveredNotice : BusyAtOne, Assert.Single(attempts).Text);

        // Subsequent work still reaches both dispatch locks. A delivered copy from 101 makes it the
        // turn's owner, so 202 is refused; otherwise 202 makes its own injection attempt.
        h.Manager.SteerAttemptStartingForTest = null;
        h.Manager.NoticeLockWaitThrowsForTest = null;
        h.Sink.ThrowOnNotice = h.Sink.FaultOnNotice = false;
        var u1Owns = status == MidTurnInjectionStatus.Injected && failure != "steer";
        var u2Injected = status == MidTurnInjectionStatus.Injected && failure == "steer";
        var before = (Injections: h.Executor.Injections.Count, Steered: h.Count(InjectionOutcomeCounter.SteeredNonHumanTurn),
            NotDelivered: h.Count(InjectionOutcomeCounter.SteerNotDelivered), Refused: h.Count(InjectionOutcomeCounter.SteerRefusedOtherHuman));
        Assert.Equal(TaskDispatchOutcome.Queued, await h.DmAsync(U2Chat, U2, task: "second-human", messageId: 12));
        Assert.Equal((u1Owns ? 0 : 1, u2Injected ? 1L : 0, status == MidTurnInjectionStatus.NoActiveTurn ? 1L : 0, u1Owns ? 1L : 0),
            (h.Executor.Injections.Count - before.Injections, h.Count(InjectionOutcomeCounter.SteeredNonHumanTurn) - before.Steered,
                h.Count(InjectionOutcomeCounter.SteerNotDelivered) - before.NotDelivered,
                h.Count(InjectionOutcomeCounter.SteerRefusedOtherHuman) - before.Refused));
        Assert.Equal(u2Injected ? TaskManager.SteeringDeliveredNotice : BusyAtTwo,
            Assert.Single(h.Sink.NoticeAttempts, e => e.ChatId == U2Chat).Text);
        await h.FinishAsync();
        Assert.Single(h.Completions, e => e.ChatId == U1Chat);
        Assert.Single(h.Sink.Replies, e => e.ChatId == U1Chat);
        Assert.Equal(1, h.Executor.Tasks.Count(t => t.Contains(Dm)));
        Assert.Equal(failure == "lock" ? 0 : 1,
            h.Sink.Sent.Count(e => e.ChatId == U1Chat && e.Text == "Now processing your queued message..."));
        Assert.Equal(attempts, h.Sink.NoticeAttempts.Where(e => e.ChatId == U1Chat).ToArray());
        Assert.Equal(1, entry.QueueDispatchLock.CurrentCount);
        Assert.Equal(0, h.Manager.ActiveTaskIdCountForTest);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SteeringNotice_ClaimedBeforeNotice_SkipsNoticeAndReleasesLock(bool throwingLogger)
    {
        await using var control = new Harness("claude", MidTurnInjectionStatus.Injected);
        await control.StartWorkflowAsync(TaskSource.Relay);
        await control.DmAsync(U1Chat, U1, steeringEligible: false, taskId: "human-entry");
        var expected = control.Manager.GetQueueSnapshot().Select(e => (e.ChatId, e.Priority, e.PartCount)).ToArray();
        var reservations = (control.Manager.ActiveTaskIdCountForTest, control.Manager.PrimaryDedupCountForTest);
        await control.FinishAsync();
        var logger = new CapturingLogger();
        if (throwingLogger) logger.ThrowOnTemplates.Add(NoticeSkipped);
        await using var h = new Harness("claude", MidTurnInjectionStatus.Injected, logger);
        await h.StartWorkflowAsync(TaskSource.Relay);
        QueuedMessage? entry = null;
        h.Manager.SteeringNoticePendingForTest = () =>
        {
            entry = Assert.Single(h.Manager.GetQueueSnapshot());
            Assert.Equal(expected, h.Manager.GetQueueSnapshot().Select(e => (e.ChatId, e.Priority, e.PartCount)).ToArray());
            Assert.Equal(reservations, (h.Manager.ActiveTaskIdCountForTest, h.Manager.PrimaryDedupCountForTest));
            Assert.False(entry.BusyNoticeSent);
            h.Executor.Release();
            h.Executor.WaitStarted(2).GetAwaiter().GetResult();
        };
        Assert.Equal(TaskDispatchOutcome.Queued, await h.DmAsync(U1Chat, U1, taskId: "human-entry"));
        Assert.True(entry!.Claimed);
        Assert.False(entry.BusyNoticeSent);
        Assert.Equal(1, entry.QueueDispatchLock.CurrentCount);
        Assert.Empty(h.Sink.NoticeAttempts);
        Assert.Single(logger.Entries, e => e.Template == NoticeSkipped);
        Assert.Equal(throwingLogger ? 1 : 0, logger.Throws);
        h.Manager.SteeringNoticePendingForTest = null;
        await h.FinishAsync();
        Assert.Single(h.Completions, e => e.ChatId == U1Chat);
        Assert.Single(h.Sink.Replies, e => e.ChatId == U1Chat);
        Assert.DoesNotContain(h.Sink.Sent, e => e.Text == "Now processing your queued message...");
    }

    [Theory]
    [InlineData(MidTurnInjectionStatus.Injected)]
    [InlineData(MidTurnInjectionStatus.NoActiveTurn)]
    public async Task SteeringNotice_MergedPartsAndFreshOverflow_NoticeOnlyForFreshEntries(MidTurnInjectionStatus status)
    {
        await using var h = new Harness("claude", status);
        await h.StartWorkflowAsync(TaskSource.Bridge);
        for (var i = 0; i < QueuedMessage.MaxParts; i++)
            await h.DmAsync(U1Chat, U1, task: $"part-{i}", messageId: 100 + i);
        Assert.Single(h.Sink.NoticeAttempts);
        Assert.Equal(QueuedMessage.MaxParts, Assert.Single(h.Manager.GetQueueSnapshot()).PartCount);
        await h.DmAsync(U1Chat, U1, task: "fresh-overflow", messageId: 200);
        Assert.Equal(2, h.Manager.GetQueueSnapshot().Count);
        Assert.Equal(2, h.Sink.NoticeAttempts.Count);
        // The first entry hit the steering cap: the fresh overflow remains queued and gets busy text.
        Assert.Contains("(position 2)", h.Sink.NoticeAttempts.Last().Text);
        await h.FinishAsync();
    }

    [Fact]
    public async Task SteeringNotice_SuppressedGroup_NoNoticeEvenWhenInjected()
    {
        await using var h = new Harness("claude", MidTurnInjectionStatus.Injected, suppressToolMessages: true);
        await h.StartWorkflowAsync(TaskSource.Relay);
        Assert.Equal(TaskDispatchOutcome.Queued, await h.DmAsync(Group, U1));
        Assert.Single(h.Executor.Injections);
        Assert.Empty(h.Sink.NoticeAttempts);
        await h.FinishAsync();
        Assert.DoesNotContain(h.Sink.Sent, e => e.ChatId == Group && e.Text == "Now processing your queued message...");
    }

    [Fact]
    public async Task SteeringNotice_AwaitedSend_ReleasesEntryLockBeforeWaiting()
    {
        await using var h = new Harness("claude", MidTurnInjectionStatus.Injected);
        await h.StartWorkflowAsync(TaskSource.Relay);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Sink.NoticeGate = gate.Task;
        var dispatch = h.DmAsync(U1Chat, U1);
        var entry = Assert.Single(h.Manager.GetQueueSnapshot());
        Assert.Single(h.Sink.NoticeAttempts);
        Assert.False(dispatch.IsCompleted);
        Assert.Equal(1, entry.QueueDispatchLock.CurrentCount);
        h.Executor.Release();
        await h.Executor.WaitStarted(2);
        Assert.True(entry.Claimed);
        Assert.Single(h.Sink.Sent, e => e.ChatId == U1Chat && e.Text == "Now processing your queued message...");
        gate.SetResult();
        Assert.Equal(TaskDispatchOutcome.Queued, await dispatch);
        await h.FinishAsync();
        Assert.Single(h.Sink.Replies, e => e.ChatId == U1Chat);
    }

    private sealed class CapturingLogger : ILogger<TaskManager>
    {
        public HashSet<string> ThrowOnTemplates { get; } = [];
        public ConcurrentQueue<(LogLevel Level, string Template)> Entries { get; } = new();
        public int Throws;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var template = (state as IEnumerable<KeyValuePair<string, object?>>)?
                .FirstOrDefault(e => e.Key == "{OriginalFormat}").Value?.ToString() ?? "";
            Entries.Enqueue((level, template));
            if (ThrowOnTemplates.Contains(template))
            {
                Interlocked.Increment(ref Throws);
                throw new InvalidOperationException("synthetic logger failure");
            }
        }
    }

    // --- #429: executor outcomes at the steering harness ---

    /// <summary>
    /// #429 AC7f (S18): final text parsed during the injection's flush still reports Injected, and
    /// the CLI may answer the steer in an extra turn. The human still gets exactly one answer, from
    /// the queued reply turn, and the extra-turn answer is discarded.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Issue429_InjectedSteerAnsweredInAnExtraTurn_HumanGetsExactlyOneAnswer(bool asRecoveredSegment)
    {
        await using var h = new Harness("claude", MidTurnInjectionStatus.Injected);
        if (asRecoveredSegment)
            h.Executor.Leading[1] = Recovered((RecoveredSegment.User, "STEER-EXTRA-TURN"));
        else
            h.Executor.OwnTurnAnswers.Enqueue("STEER-EXTRA-TURN");
        await h.StartWorkflowAsync(TaskSource.Relay);

        Assert.Equal(TaskDispatchOutcome.Queued, await h.DmAsync(U1Chat, U1));
        await h.FinishAsync();

        Assert.Single(h.Executor.Injections);
        Assert.Equal(1, h.Count(InjectionOutcomeCounter.SteeredNonHumanTurn));
        Assert.Equal(["workflow-only", $"{TaskManager.SteeredPartPrefix}\n\n{Dm}"], h.Executor.Tasks);
        Assert.Equal(1, h.Count(InjectionOutcomeCounter.SteerAnswerDiscarded));
        Assert.Equal(0, h.Count(InjectionOutcomeCounter.AnsweredAsSeparateTurn));
        Assert.DoesNotContain(h.Sink.Sent, s => s.Text.Contains("STEER-EXTRA-TURN"));
        Assert.DoesNotContain(h.Events.RecoveredTexts(), t => t.Contains("STEER-EXTRA-TURN"));
        Assert.Single(h.Completions, c => c.ChatId == U1Chat);
        Assert.Single(h.Sink.Sent, s => s.ChatId == U1Chat && s.Text.Contains("human-only"));
    }

    /// <summary>
    /// #429 AC7g (S19): a kill overtook the injection's write, so the executor reports Failed. The
    /// message is not recorded as steered, is queued once, and is answered once by its own turn.
    /// </summary>
    [Fact]
    public async Task Issue429_SteerOvertakenByTeardown_QueuedOnceAndAnsweredOnceWithoutThePrefix()
    {
        await using var h = new Harness("claude", MidTurnInjectionStatus.Failed);
        await h.StartWorkflowAsync(TaskSource.Relay);

        Assert.Equal(TaskDispatchOutcome.Queued, await h.DmAsync(U1Chat, U1));
        await h.FinishAsync();

        Assert.Single(h.Executor.Injections);
        Assert.Equal(0, h.Count(InjectionOutcomeCounter.SteeredNonHumanTurn));
        Assert.Equal(1, h.Count(InjectionOutcomeCounter.SteerNotDelivered));
        Assert.Equal(["workflow-only", Dm], h.Executor.Tasks);
        Assert.Empty(h.Executor.ReadCalls);
        Assert.Empty(h.Events.RecoveredTexts());
        Assert.Single(h.Completions, c => c.ChatId == U1Chat && c.Result == Dm);
        Assert.Single(h.Sink.Sent, s => s.ChatId == U1Chat && s.Text.Contains("human-only"));
    }

    // --- helpers ---

    private static IEnumerable<(string, MidTurnInjectionStatus)> ProviderPairs() =>
        Providers.Select(p => ((string)p[0], (MidTurnInjectionStatus)p[1]));

    private static async Task<(Completion Completion, Published Terminal, Harness Harness)> RunWorkflowAsync(
        string provider, MidTurnInjectionStatus status, TaskSource source, bool dm)
    {
        var h = new Harness(provider, status);
        await h.StartWorkflowAsync(source);
        if (dm) Assert.Equal(TaskDispatchOutcome.Queued, await h.DmAsync(U1Chat, U1));
        await h.FinishAsync();
        await h.DisposeAsync();
        return (Assert.Single(h.Completions, c => c.ChatId == WorkflowChat), Assert.Single(h.Events.Terminals(WorkflowChat)), h);
    }

    private static void AssertTerminalEqual(Published expected, Published actual)
    {
        Assert.Equal(expected.Kind, actual.Kind);
        var (e, a) = ((TurnFinalPayload)expected.Payload!, (TurnFinalPayload)actual.Payload!);
        Assert.Equal((e.Text, e.Completion, e.IsPartial, e.Truncated), (a.Text, a.Completion, a.IsPartial, a.Truncated));
        Assert.Equal(e.MergedSubmissionIds, a.MergedSubmissionIds);
    }

    private static AgentProgress Recovered(params (string Origin, string Text)[] segments) => new()
    {
        IsSignificant = true,
        EventType = "recovered_answer",
        Summary = string.Join("\n", segments.Select(s => s.Text)),
        RecoveredSegments = [.. segments.Select(s => new RecoveredSegment(s.Text, s.Origin))],
    };

    private static ConversationIdentity Identity(long chat, string submission) => new()
    {
        PrincipalId = "synthetic", Role = PrincipalRole.Owner, ChannelId = ChannelIds.Telegram,
        ConversationId = chat.ToString(System.Globalization.CultureInfo.InvariantCulture), SubmissionId = submission, Attempt = 1,
    };

    private static int Occurrences(string text, string value) => (text.Length - text.Replace(value, "").Length) / value.Length;

    private static async Task<IReadOnlyList<string>> NextAsync(Harness h, int count)
    {
        h.Executor.Release();
        await h.Executor.WaitStarted(count);
        return h.Executor.Tasks;
    }

    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, deadline.Token);
    }

    internal sealed record Completion(long ChatId, string Result, string? RelaySender, TaskSource Source, bool IsPartial,
        string? CorrelationId, string? TaskId, CompletionKind Kind);

    internal sealed record Published(long ChatId, string Kind, ConversationIdentity Identity, object? Payload);

    private sealed class Harness : IAsyncDisposable
    {
        private readonly HttpClient _http = new() { BaseAddress = new("http://journal.test") };
        public SteeringExecutor Executor { get; }
        public TaskManager Manager { get; }
        public RecordingSink Sink { get; } = new();
        public RecordingEvents Events { get; } = new();
        public InjectionOutcomeCounter Counter { get; } = new();
        public TurnOriginLedger Ledger { get; } = new();
        public TurnBindingPublisher Binding { get; }
        public ConcurrentQueue<Completion> Completions { get; } = new();
        public bool ThrowOnInject { set => Executor.ThrowOnInject = value; }
        private readonly string _provider;

        public Harness(string provider, MidTurnInjectionStatus status, ILogger<TaskManager>? logger = null, bool suppressToolMessages = false)
        {
            _provider = provider;
            Executor = new SteeringExecutor(status, Ledger);
            Binding = new TurnBindingPublisher(new JournalHttpClient(_http, "ingest", readToken: "read"), NullLogger<TurnBindingPublisher>.Instance);
            foreach (var chat in new[] { U1Chat, U2Chat }) Binding.ObserveChat(chat, 1, "private");
            Manager = new TaskManager(
                Options.Create(new AgentOptions { Name = "agent1", Role = "test", WorkDir = "/tmp", Provider = provider, ShowStats = false, SuppressToolMessages = suppressToolMessages }),
                Executor, new SessionManager(), logger ?? NullLogger<TaskManager>.Instance, Counter, Events, sink: Sink, ledger: Ledger, turnBindings: Binding);
            Manager.OnTaskCompleted += (chat, result, sender, source, partial, correlation, taskId, kind) =>
                Completions.Enqueue(new(chat, result, sender, source, partial, correlation, taskId, kind));
        }

        public long Count(string outcome) => Counter.GetCount(_provider, outcome);
        public long SteeringCounts() => Count(InjectionOutcomeCounter.SteeredNonHumanTurn) + Count(InjectionOutcomeCounter.SteerNotDelivered)
            + Count(InjectionOutcomeCounter.SteerRefusedOtherHuman);

        public async Task StartWorkflowAsync(TaskSource source)
        {
            Assert.Equal(TaskDispatchOutcome.Ran, await Manager.StartTask(WorkflowChat, "workflow-only", "workflow", true, source,
                relaySender: source == TaskSource.Bridge ? "bridge" : "synthetic-sender", correlationId: "synthetic-correlation",
                taskId: "synthetic-wf/step", identity: Identity(WorkflowChat, "submission-wf")));
            await Executor.WaitStarted(1);
        }

        public Task<TaskDispatchOutcome> DmAsync(long chat, long user, string task = Dm, long messageId = 11,
            TaskPriority priority = TaskPriority.Routine, bool steeringEligible = true,
            IReadOnlyList<MessageImage>? images = null, IReadOnlyList<MessageDocument>? documents = null, string? taskId = null) =>
            Manager.StartTask(chat, task, "human", true, images: images, documents: documents, userId: user,
                taskId: taskId, identity: Identity(chat, $"submission-{chat}"), priority: priority, telegramMessageId: messageId,
                steeringEligible: steeringEligible);

        /// <summary>Releases every turn until the runtime is idle and the queue empty.</summary>
        public async Task FinishAsync()
        {
            Executor.ReleaseDrain();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            // Idle and empty on three consecutive polls: a drain dequeues before it registers.
            for (var quiet = 0; quiet < 3; )
            {
                Executor.Release();
                await Task.Delay(20, deadline.Token);
                quiet = Manager.GetOrchestratorStatus().Status == "idle" && Manager.GetQueueSnapshot().Count == 0 ? quiet + 1 : 0;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Manager.CancelAllAsync();
            Binding.Dispose();
            _http.Dispose();
        }
    }

    internal sealed class RecordingSink : IMessageSink
    {
        public ConcurrentQueue<(long ChatId, string Text)> Sent { get; } = new();
        public ConcurrentQueue<(long ChatId, string Text)> NoticeAttempts { get; } = new();
        public ConcurrentQueue<(long ChatId, string Text)> Replies { get; } = new();
        public bool ThrowOnNotice { get; set; }
        public bool FaultOnNotice { get; set; }
        public Task? NoticeGate { get; set; }
        public bool RendersReplies => true;
        public Task SendReplyAsync(long chatId, AgentReply reply, OutboundOrigin origin, CancellationToken ct = default)
        {
            Replies.Enqueue((chatId, reply.ComposeText()));
            return SendTextAsync(chatId, reply.ComposeText(), ct);
        }
        public Task SendTextAsync(long chatId, string text, CancellationToken ct = default)
        {
            if (text == TaskManager.SteeringDeliveredNotice || text.StartsWith("I'm busy right now — your message is queued (position"))
            {
                NoticeAttempts.Enqueue((chatId, text));
                if (ThrowOnNotice) throw new InvalidOperationException("synthetic send failure");
                if (FaultOnNotice) return Task.FromException(new InvalidOperationException("synthetic send failure"));
                if (NoticeGate is not null) { Sent.Enqueue((chatId, text)); return NoticeGate; }
            }
            Sent.Enqueue((chatId, text));
            return Task.CompletedTask;
        }
        public Task SendTypingAsync(long chatId, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendPhotoAsync(long chatId, string filePath, string? caption, CancellationToken ct = default) => Task.CompletedTask;
    }

    internal sealed class RecordingEvents : IConversationEventPublisher
    {
        private readonly ConcurrentQueue<Published> _events = new();
        public void Publish(ConversationEvent evt) { }
        public bool Publish<TPayload>(long runtimeConversationKey, string kind, ConversationIdentity identity, TPayload? payload)
            where TPayload : class
        {
            _events.Enqueue(new(runtimeConversationKey, kind, identity, payload));
            return true;
        }
        public IReadOnlyList<Published> Terminals(long chat) =>
            [.. _events.Where(e => e.ChatId == chat && e.Kind is ConversationEventKind.TurnFinal or ConversationEventKind.TurnCanceled
                or ConversationEventKind.TurnError)];
        public IReadOnlyList<string> RecoveredTexts(long? chat = null) =>
            [.. _events.Where(e => e.Kind == ConversationEventKind.TurnRecoveredAnswer && (chat is null || e.ChatId == chat))
                .Select(e => ((TurnRecoveredAnswerPayload)e.Payload!).Text)];
    }

    /// <summary>
    /// The ControlledExecutor blocking shape, plus scripted own-turn answers (as InjectedTurnExecutor),
    /// a scripted leading recovered_answer per turn index, and scripted process exits.
    /// </summary>
    internal sealed class SteeringExecutor(MidTurnInjectionStatus status, TurnOriginLedger ledger) : IAgentExecutor
    {
        private readonly SemaphoreSlim _release = new(0);
        private readonly ConcurrentQueue<string> _tasks = new();
        private TaskCompletionSource _drainGate = Open();
        private readonly TaskCompletionSource _drainStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<string> Tasks => [.. _tasks];
        public ConcurrentQueue<(string Text, int Images, int Documents)> Injections { get; } = new();
        public ConcurrentQueue<string> OwnTurnAnswers { get; } = new();
        public ConcurrentQueue<int> ReadCallsQueue { get; } = new();
        public IReadOnlyList<int> ReadCalls => [.. ReadCallsQueue];
        public ConcurrentQueue<TurnOrigin> ReadOriginsQueue { get; } = new();
        public IReadOnlyList<TurnOrigin> ReadOrigins => [.. ReadOriginsQueue];
        public ConcurrentDictionary<int, AgentProgress> Leading { get; } = new();
        public HashSet<int> ProcessExitTurns { get; } = [];
        public bool ThrowOnInject { get; set; }
        public string? LastSessionId => "synthetic-session";
        public DateTimeOffset LastActivity => DateTimeOffset.UtcNow;
        public bool IsProcessWarm => true;

        private static TaskCompletionSource Open()
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gate.TrySetResult();
            return gate;
        }

        public async IAsyncEnumerable<AgentProgress> ExecuteAsync(string task,
            IReadOnlyList<MessageImage>? images = null, IReadOnlyList<MessageDocument>? documents = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            using var interval = ledger.OpenTurn();
            var index = _tasks.Count;
            _tasks.Enqueue(task);
            if (Leading.TryGetValue(index, out var leading)) yield return leading;
            await _release.WaitAsync(ct);
            if (ProcessExitTurns.Contains(index))
            {
                yield return new AgentProgress { EventType = "error", Summary = "synthetic exit", IsProcessExit = true };
                yield break;
            }
            yield return new AgentProgress { EventType = "result", Summary = task, FinalResult = task };
        }

        public Task<MidTurnInjectionResult> TryInjectMessageAsync(string task,
            IReadOnlyList<MessageImage>? images = null, IReadOnlyList<MessageDocument>? documents = null,
            CancellationToken ct = default)
        {
            Injections.Enqueue((task, images?.Count ?? 0, documents?.Count ?? 0));
            if (ThrowOnInject) throw new InvalidOperationException("synthetic");
            return Task.FromResult(status switch
            {
                MidTurnInjectionStatus.Injected => MidTurnInjectionResult.Injected,
                MidTurnInjectionStatus.NoActiveTurn => MidTurnInjectionResult.NoActiveTurn("synthetic -32600"),
                MidTurnInjectionStatus.Failed => MidTurnInjectionResult.Failed("synthetic"),
                _ => MidTurnInjectionResult.Unsupported,
            });
        }

        public async IAsyncEnumerable<AgentProgress> ReadInjectedTurnAnswersAsync(int injectedMessages,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            using var interval = ledger.OpenTurn();
            ReadCallsQueue.Enqueue(injectedMessages);
            ReadOriginsQueue.Enqueue(ledger.SnapshotForTests()[^1].Origin);
            _drainStarted.TrySetResult();
            await _drainGate.Task.WaitAsync(ct);
            for (var i = 0; i < injectedMessages && OwnTurnAnswers.TryDequeue(out var answer); i++)
                yield return new AgentProgress { IsSignificant = true, EventType = "result", Summary = answer, FinalResult = answer };
        }

        public void HoldDrain() => _drainGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ReleaseDrain() => _drainGate.TrySetResult();
        public Task WaitDrainStarted() => _drainStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
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
