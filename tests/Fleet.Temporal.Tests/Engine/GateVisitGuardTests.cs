using System.Collections.Concurrent;
using System.Text.Json;
using Fleet.Temporal.Engine;
using Fleet.Temporal.Models;
using Microsoft.Extensions.Logging;
using Temporalio.Activities;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.OperatorService.V1;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Converters;
using Temporalio.Testing;
using Temporalio.Worker;

namespace Fleet.Temporal.Tests.Engine;

/// <summary>
/// #436 §1 — gate visits (<c>visitVar</c>) and the delegated-approval consumption guard
/// (<c>delegatedGuard</c>) on <c>wait_for_signal</c>.
///
/// <para>
/// These drive the PRODUCTION <see cref="UniversalWorkflow"/> in a time-skipping
/// <see cref="WorkflowEnvironment"/> with stub activities, and assert on recorded history wherever
/// the claim is about commands: which <c>GateVisit</c> values were upserted, which timers started
/// and for how long, and when each reminder was scheduled. The guard definitions use the
/// templates the seed definitions use — <c>{{vars.&lt;visitVar&gt;}}</c> and
/// <c>{{vars.review_ref}}</c>, the latter set by a <c>set_variable</c> step.
/// </para>
///
/// <para>Generic placeholder names only.</para>
/// </summary>
public sealed class GateVisitGuardTests
{
    private const string Gate = "merge-approval";
    private const string ReviewRef = "0123456789abcdef0123456789abcdef01234567";
    private const string ValidEvidence = "https://example.com/review/valid";
    private const string StaleEvidence = "https://example.com/review/stale";
    private const string DecisionResult = "{{vars.decision.Decision | default: 'none'}}|{{vars.decision.Evidence | default: 'human'}}";

    // ── AC-E1: visit ids, the GateVisit attribute, and byte-identity without visitVar ──────────

    /// <summary>
    /// Re-entry mints a new visit: <c>wait:1</c>, then <c>wait:2</c>. Each parked visit upserts
    /// <c>GateVisit</c> BEFORE <c>Phase</c> and clears it to <c>""</c> when it ends — here once by
    /// a live signal and once by the auto-complete timeout.
    /// </summary>
    [Fact]
    public async Task VisitVar_MintsANewVisitOnEachEntry_AndUpsertsThenClearsGateVisit()
    {
        var definition = Definition(
            new LoopStep
            {
                MaxIterations = 2,
                Steps =
                [
                    new WaitForSignalStep
                    {
                        Name = "gate",
                        SignalName = "wait",
                        Phase = "waiting",
                        VisitVar = "gate_visit",
                        OutputVar = "w",
                        TimeoutMinutes = 60,
                        MaxReminders = 0,
                        AutoCompleteOnTimeout = true,
                    },
                    new SetVariableStep { Vars = new() { ["trail"] = "{{vars.trail}}{{vars.gate_visit}}={{vars.w.Decision}};" } },
                ],
            },
            Result("{{vars.trail}}"));

        var run = await RunAsync(definition, async (_, handle, _) =>
        {
            await WaitForHistoryAsync(handle, h => TimersStarted(h).Count >= 1);
            await handle.SignalAsync("wait", [Json("""{"Decision":"approved"}""")]);
        });

        Assert.Equal("wait:1=approved;wait:2=timeout;", run.Result);
        Assert.Equal(
            ["GateVisit=wait:1", "Phase=waiting", "GateVisit=", "GateVisit=wait:2", "Phase=waiting", "GateVisit="],
            Upserts(run.History, "GateVisit", "Phase"));
    }

    /// <summary>
    /// One counter for the whole run, advanced only by waits that set <c>visitVar</c>. A wait
    /// resolved from the buffer still mints its visit (the guard needs it) but never parks, so it
    /// upserts nothing. A wait without <c>visitVar</c> neither counts nor upserts.
    /// </summary>
    [Fact]
    public async Task TheVisitCounter_IsSharedAcrossVisitVarWaits_AndABufferedResumeUpsertsNothing()
    {
        var definition = Definition(
            new DelegateStep { Name = "tick", Target = "agent1", Instruction = "work" },
            Wait("alpha", visitVar: "v1"),
            Wait("beta"),
            Wait("gamma", visitVar: "v3"),
            Result("{{vars.v1}}|{{vars.v3}}|{{vars.alpha.Decision}}"));

        var run = await RunAsync(definition, async (_, handle, activities) =>
        {
            await activities.Started("tick").WaitAsync(TimeSpan.FromSeconds(20));
            await handle.SignalAsync("alpha", [Json("""{"Decision":"approved"}""")]);
            activities.Release("tick");
        }, held: ["tick"]);

        Assert.Equal("alpha:1|gamma:2|approved", run.Result);
        Assert.Equal(["GateVisit=gamma:2", "GateVisit="], Upserts(run.History, "GateVisit"));
    }

    /// <summary>The third way a visit ends — the timeout that throws — clears GateVisit too.</summary>
    [Fact]
    public async Task AThrownTimeout_StillClearsGateVisit()
    {
        var definition = Definition(
            new WaitForSignalStep
            {
                Name = "gate",
                SignalName = "wait",
                VisitVar = "gate_visit",
                TimeoutMinutes = 30,
                MaxReminders = 0,
                IgnoreFailure = true,
            },
            Result("{{vars.gate_visit}}"));

        var run = await RunAsync(definition, (_, _, _) => Task.CompletedTask);

        Assert.Equal("wait:1", run.Result);
        Assert.Equal(["GateVisit=wait:1", "GateVisit="], Upserts(run.History, "GateVisit"));
    }

    /// <summary>The fixture really is a pre-change history: no new fields recorded, no GateVisit.</summary>
    [Fact]
    public void TheGateWaitFixture_IsAPreChangeHistory()
    {
        var history = GateWaitFixture();

        var definitionJson = history.Events
            .Where(e => e.ActivityTaskCompletedEventAttributes is not null)
            .Select(e => e.ActivityTaskCompletedEventAttributes.Result.Payloads_[0].Data.ToStringUtf8())
            .First(json => json.Contains("\"wait_for_signal\"", StringComparison.Ordinal));
        Assert.DoesNotContain("VisitVar", definitionJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DelegatedGuard", definitionJson, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Upserts(history, "GateVisit"));
        Assert.Contains(history.Events, e => e.EventType == EventType.WorkflowExecutionSignaled);
        Assert.Contains(history.Events, e => e.EventType == EventType.TimerStarted);
    }

    /// <summary>
    /// Replay safety. A wait recorded before #436 — buffered, live, indefinite, auto-complete
    /// timeout after a reminder, thrown timeout — replays against the current engine with no
    /// non-determinism error.
    /// </summary>
    [Fact]
    public async Task APreChangeWaitHistory_ReplaysCleanlyAgainstTheCurrentEngine()
    {
        var replayer = new WorkflowReplayer(new WorkflowReplayerOptions().AddWorkflow<UniversalWorkflow>());
        await replayer.ReplayWorkflowAsync(GateWaitFixture());
    }

    /// <summary>
    /// Replay does not compare command ATTRIBUTES (timer lengths, activity inputs, upsert values),
    /// so the same scenario runs live on the current engine and its command sequence is compared
    /// with the recorded one, entry by entry.
    /// </summary>
    [Fact]
    public async Task AWaitWithoutVisitVar_EmitsTheSameCommandSequenceAsBefore()
    {
        var history = await GateWaitHistoryScenario.RunAsync(GateWaitHistoryScenario.Definition());

        Assert.Equal(
            GateWaitHistoryScenario.Commands(GateWaitFixture()),
            GateWaitHistoryScenario.Commands(history));
    }

    // ── AC-E2: no marker → today's behaviour ────────────────────────────────────────────────

    /// <summary>
    /// A payload without <c>GrantId</c> — a human approval, the configured CTO's
    /// <c>changes_requested</c> feedback, a relayed plain string — is accepted by a guarded wait
    /// exactly as an unguarded wait accepts it, live or from the buffer, and nothing is discarded.
    /// </summary>
    [Theory]
    [InlineData("""{"Decision":"approved"}""", false)]
    [InlineData("""{"Decision":"approved"}""", true)]
    [InlineData("""{"Decision":"changes_requested","Comment":"please fix the edge case"}""", false)]
    [InlineData("""{"Decision":"changes_requested","Comment":"please fix the edge case"}""", true)]
    [InlineData("""{"Decision":"rejected","Comment":"not needed"}""", false)]
    [InlineData("\"approved\"", false)]
    public async Task APayloadWithoutTheMarker_IsAcceptedExactlyAsToday(string payloadJson, bool buffered)
    {
        async Task<Run> RunOnce(bool guarded)
        {
            var gate = GuardedGate(timeoutMinutes: 60);
            if (!guarded) gate = gate with { VisitVar = null, DelegatedGuard = null };

            var definition = Definition(
                new DelegateStep { Name = "tick", Target = "agent1", Instruction = "work" },
                PublishReviewRef(ReviewRef),
                gate,
                Result("{{vars.decision}}"));

            return await RunAsync(definition, async (_, handle, activities) =>
            {
                await activities.Started("tick").WaitAsync(TimeSpan.FromSeconds(20));
                if (buffered)
                {
                    await handle.SignalAsync(Gate, [Json(payloadJson)]);
                    activities.Release("tick");
                }
                else
                {
                    activities.Release("tick");
                    await WaitForHistoryAsync(handle, h => TimersStarted(h).Count >= 1);
                    await handle.SignalAsync(Gate, [Json(payloadJson)]);
                }
            }, held: ["tick"]);
        }

        var unguarded = await RunOnce(guarded: false);
        var guardedRun = await RunOnce(guarded: true);

        Assert.Equal(Json(payloadJson).ToString(), guardedRun.Result);
        Assert.Equal(unguarded.Result, guardedRun.Result);
        Assert.Empty(Discards(guardedRun.Logs));
        Assert.Equal(TimersStarted(unguarded.History).Count, TimersStarted(guardedRun.History).Count);
    }

    // ── AC-E3: a marked payload must match the current visit and artifact ───────────────────

    /// <summary>
    /// Each mismatch is discarded with one warning. The wait keeps its ORIGINAL deadline — the
    /// re-wait timer is the remainder, never a fresh 60 minutes — and the valid delegated approval
    /// that follows is accepted.
    /// </summary>
    [Theory]
    [InlineData("""{"Decision":"approved","GrantId":"grant-1","VisitId":"merge-approval:2","ArtifactRef":"0123456789abcdef0123456789abcdef01234567","Evidence":"https://example.com/review/stale"}""")]
    [InlineData("""{"Decision":"approved","GrantId":"grant-1","VisitId":"merge-approval:1","ArtifactRef":"ffffffffffffffffffffffffffffffffffffffff","Evidence":"https://example.com/review/stale"}""")]
    [InlineData("""{"Decision":"approved","GrantId":"grant-1","VisitId":"merge-approval:1","ArtifactRef":"0123456789ABCDEF0123456789ABCDEF01234567","Evidence":"https://example.com/review/stale"}""")]
    [InlineData("""{"Decision":"approved","GrantId":"grant-1","ArtifactRef":"0123456789abcdef0123456789abcdef01234567","Evidence":"https://example.com/review/stale"}""")]
    [InlineData("""{"Decision":"approved","GrantId":"grant-1","VisitId":"merge-approval:1","Evidence":"https://example.com/review/stale"}""")]
    [InlineData("""{"Decision":"approved","GrantId":"grant-1","VisitId":1,"ArtifactRef":"0123456789abcdef0123456789abcdef01234567","Evidence":"https://example.com/review/stale"}""")]
    [InlineData("""{"Decision":"approved","grantid":null,"VisitId":"merge-approval:0","ArtifactRef":"0123456789abcdef0123456789abcdef01234567","Evidence":"https://example.com/review/stale"}""")]
    [InlineData("""{"Decision":"approved","grantId":"grant-1","visitId":"merge-approval:3","artifactRef":"0123456789abcdef0123456789abcdef01234567","evidence":"https://example.com/review/stale"}""")]
    public async Task AMismatchedDelegatedPayload_IsDiscarded_AndAFollowingValidOneIsAccepted(string stale)
    {
        var definition = Definition(PublishReviewRef(ReviewRef), GuardedGate(timeoutMinutes: 60), Result(DecisionResult));

        var run = await RunAsync(definition, async (_, handle, _) =>
        {
            await WaitForHistoryAsync(handle, h => TimersStarted(h).Count >= 1);
            await handle.SignalAsync(Gate, [Json(stale)]);
            await WaitForHistoryAsync(handle, h => TimersStarted(h).Count >= 2);
            await handle.SignalAsync(Gate, [Delegated("merge-approval:1", ReviewRef, ValidEvidence)]);
        });

        Assert.Equal($"approved|{ValidEvidence}", run.Result);
        AssertDiscards(run, "merge-approval:1", count: 1);

        // The re-wait runs to the original deadline: strictly less than the full 60 minutes.
        var timers = TimersStarted(run.History);
        Assert.Equal(TimeSpan.FromMinutes(60), timers[0].Duration);
        Assert.InRange(timers[1].Duration, TimeSpan.FromMinutes(59), TimeSpan.FromMinutes(60) - TimeSpan.FromMilliseconds(1));
        Assert.Equal(["GateVisit=merge-approval:1", "GateVisit="], Upserts(run.History, "GateVisit"));
    }

    /// <summary>
    /// The positive control: the exact delegated approval is accepted, and the marker and the
    /// required fields are found whatever their case — the values still compare ordinally.
    /// </summary>
    [Theory]
    [InlineData("""{"Decision":"approved","GrantId":"grant-1","VisitId":"merge-approval:1","ArtifactRef":"0123456789abcdef0123456789abcdef01234567","Evidence":"https://example.com/review/valid"}""")]
    [InlineData("""{"decision":"approved","grantId":"grant-1","visitId":"merge-approval:1","artifactRef":"0123456789abcdef0123456789abcdef01234567","evidence":"https://example.com/review/valid"}""")]
    public async Task AMatchingDelegatedPayload_IsAccepted(string payload)
    {
        var definition = Definition(PublishReviewRef(ReviewRef), GuardedGate(timeoutMinutes: 60), Result(DecisionResult));

        var run = await RunAsync(definition, async (_, handle, _) =>
        {
            await WaitForHistoryAsync(handle, h => TimersStarted(h).Count >= 1);
            await handle.SignalAsync(Gate, [Json(payload)]);
        });

        Assert.Equal($"approved|{ValidEvidence}", run.Result);
        Assert.Empty(Discards(run.Logs));
    }

    /// <summary>
    /// An empty rendered <c>review_ref</c> — no review published, or the publish step set it to
    /// <c>""</c> — can never be matched, not even by a payload whose <c>ArtifactRef</c> is also
    /// empty. Delegation is impossible; the human path is untouched.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnEmptyRenderedReviewRef_DiscardsEveryDelegatedPayload_ButNotAHumanOne(bool publishEmpty)
    {
        StepDefinition[] steps = publishEmpty
            ? [PublishReviewRef(""), GuardedGate(timeoutMinutes: 60), Result(DecisionResult)]
            : [GuardedGate(timeoutMinutes: 60), Result(DecisionResult)];

        var run = await RunAsync(Definition(steps), async (_, handle, _) =>
        {
            await WaitForHistoryAsync(handle, h => TimersStarted(h).Count >= 1);
            await handle.SignalAsync(Gate, [Delegated("merge-approval:1", "", StaleEvidence)]);
            await WaitForHistoryAsync(handle, h => TimersStarted(h).Count >= 2);
            await handle.SignalAsync(Gate, [Delegated("merge-approval:1", null, StaleEvidence)]);
            await WaitForHistoryAsync(handle, h => TimersStarted(h).Count >= 3);
            await handle.SignalAsync(Gate, [Json("""{"Decision":"approved"}""")]);
        });

        Assert.Equal("approved|human", run.Result);
        AssertDiscards(run, "merge-approval:1", count: 2);
    }

    // ── AC-E4: a buffered stale approval is judged by the visit that consumes it ────────────

    /// <summary>
    /// Visit 1 ends on human feedback. A delegated approval naming <c>merge-approval:1</c> then
    /// arrives while the run is revising — no waiter, so it is buffered. Visit 2 takes it at entry,
    /// the guard sees the wrong visit and discards it, and the wait PARKS: it upserts
    /// <c>merge-approval:2</c> and runs to its own timeout. The approval is never applied.
    /// </summary>
    [Fact]
    public async Task ABufferedApprovalForAnEndedVisit_IsDiscardedAtTheNextVisit()
    {
        var definition = Definition(
            PublishReviewRef(ReviewRef),
            GuardedGate(name: "visit_1", timeoutMinutes: 60),
            new SetVariableStep { Vars = new() { ["first"] = "{{vars.decision.Decision}}" } },
            new DelegateStep { Name = "revise", Target = "agent1", Instruction = "revise" },
            GuardedGate(name: "visit_2", timeoutMinutes: 60),
            Result("{{vars.first}}|{{vars.decision.Decision}}|{{vars.merge_visit}}"));

        var run = await RunAsync(definition, async (_, handle, activities) =>
        {
            await WaitForHistoryAsync(handle, h => TimersStarted(h).Count >= 1);
            await handle.SignalAsync(Gate, [Json("""{"Decision":"changes_requested","Comment":"fix it"}""")]);

            await activities.Started("revise").WaitAsync(TimeSpan.FromSeconds(20));
            await handle.SignalAsync(Gate, [Delegated("merge-approval:1", ReviewRef, StaleEvidence)]);
            activities.Release("revise");
        }, held: ["revise"]);

        Assert.Equal("changes_requested|timeout|merge-approval:2", run.Result);
        AssertDiscards(run, "merge-approval:2", count: 1);
        Assert.DoesNotContain(run.Logs, l => l.Message.Contains("already buffered", StringComparison.Ordinal));
        Assert.Equal(
            ["GateVisit=merge-approval:1", "GateVisit=", "GateVisit=merge-approval:2", "GateVisit="],
            Upserts(run.History, "GateVisit"));
    }

    // ── AC-E6: discard mechanics ────────────────────────────────────────────────────────────

    /// <summary>
    /// A stale approval delivered live at minute 10 of a 30-minute slice. The re-wait is the
    /// remaining 20 minutes, so the one reminder (<c>maxReminders: 1</c>) still fires at minute 30,
    /// not 40, and the 60-minute total ends at minute 60, not 70. Had the discard counted as a
    /// reminder or reset the slice, the reminder would be missing or late.
    ///
    /// The recorded history then replays: the guarded path is deterministic.
    /// </summary>
    [Fact]
    public async Task ALiveStaleApproval_KeepsTheSliceEnd_TheReminderSchedule_AndTheDeadline()
    {
        var definition = Definition(
            PublishReviewRef(ReviewRef),
            GuardedGate(timeoutMinutes: 60, reminderMinutes: 30, maxReminders: 1, notify: Notify()),
            Result(DecisionResult));

        var run = await RunAsync(definition, async (env, handle, _) =>
        {
            await WaitForHistoryAsync(handle, h => TimersStarted(h).Count >= 1);
            await env.DelayAsync(TimeSpan.FromMinutes(10));
            await handle.SignalAsync(Gate, [Delegated("merge-approval:7", ReviewRef, StaleEvidence)]);
            await WaitForHistoryAsync(handle, h => TimersStarted(h).Count >= 2);
        });

        Assert.Equal("timeout|human", run.Result);
        AssertDiscards(run, "merge-approval:1", count: 1);

        var timers = TimersStarted(run.History);
        Assert.Equal(3, timers.Count);
        Assert.Equal(TimeSpan.FromMinutes(30), timers[0].Duration);
        Assert.InRange(timers[1].Duration, TimeSpan.FromMinutes(19), TimeSpan.FromMinutes(20));
        Assert.Equal(TimeSpan.FromMinutes(30), timers[2].Duration);

        var parkedAt = timers[0].At;
        var notifies = NotifySchedules(run.History);
        Assert.Equal(2, notifies.Count); // initial + exactly one reminder
        AssertNear(TimeSpan.FromMinutes(30), notifies[1] - parkedAt);

        var completedAt = run.History.Events
            .Single(e => e.EventType == EventType.WorkflowExecutionCompleted).EventTime.ToDateTime();
        AssertNear(TimeSpan.FromMinutes(60), completedAt - parkedAt);

        var replayer = new WorkflowReplayer(new WorkflowReplayerOptions().AddWorkflow<UniversalWorkflow>());
        await replayer.ReplayWorkflowAsync(run.History);
    }

    /// <summary>
    /// A stale approval completes the waiter; a valid one right behind it finds the waiter already
    /// completed and goes to the buffer. The drain after the discard takes it and accepts it — no
    /// re-wait timer is ever started, so it cannot have arrived any other way.
    /// </summary>
    [Fact]
    public async Task AValidApprovalBufferedBehindAStaleOne_IsAcceptedByTheDrain()
    {
        var definition = Definition(
            PublishReviewRef(ReviewRef),
            GuardedGate(timeoutMinutes: 60, notify: Notify()),
            Result(DecisionResult));

        var run = await RunAsync(definition, async (_, handle, activities) =>
        {
            await activities.Started("notify").WaitAsync(TimeSpan.FromSeconds(20));
            await handle.SignalAsync(Gate, [Delegated("merge-approval:9", ReviewRef, StaleEvidence)]);
            await handle.SignalAsync(Gate, [Delegated("merge-approval:1", ReviewRef, ValidEvidence)]);
            activities.Release("notify");
        }, held: ["notify"]);

        Assert.Equal($"approved|{ValidEvidence}", run.Result);
        AssertDiscards(run, "merge-approval:1", count: 1);
        Assert.Empty(TimersStarted(run.History));
    }

    /// <summary>
    /// Two stale approvals: the first completes the waiter, the second is buffered. Both are
    /// discarded — the second by the drain, which removes it — and a valid live approval then
    /// resolves visit 1. The second stale entry names <c>merge-approval:2</c>, so if it had been
    /// left in the buffer, visit 2 would have taken it and been APPROVED. Instead visit 2 parks and
    /// times out, and exactly two discards are logged.
    /// </summary>
    [Fact]
    public async Task AStaleBufferedEntry_IsRemovedByTheDrain_AndNeverSeenAgain()
    {
        var definition = Definition(
            PublishReviewRef(ReviewRef),
            GuardedGate(name: "visit_1", timeoutMinutes: 60, notify: Notify()),
            new SetVariableStep { Vars = new() { ["first"] = DecisionResult } },
            GuardedGate(name: "visit_2", timeoutMinutes: 60),
            Result("{{vars.first}}#{{vars.decision.Decision}}"));

        var run = await RunAsync(definition, async (_, handle, activities) =>
        {
            await activities.Started("notify").WaitAsync(TimeSpan.FromSeconds(20));
            await handle.SignalAsync(Gate, [Delegated("merge-approval:0", ReviewRef, StaleEvidence)]);
            await handle.SignalAsync(Gate, [Delegated("merge-approval:2", ReviewRef, StaleEvidence)]);
            activities.Release("notify");

            await WaitForHistoryAsync(handle, h => TimersStarted(h).Count >= 1);
            await handle.SignalAsync(Gate, [Delegated("merge-approval:1", ReviewRef, ValidEvidence)]);
        }, held: ["notify"]);

        Assert.Equal($"approved|{ValidEvidence}#timeout", run.Result);
        AssertDiscards(run, "merge-approval:1", count: 2);
        Assert.DoesNotContain(run.Logs, l => l.Message.Contains("already buffered", StringComparison.Ordinal));
        Assert.Equal(
            ["GateVisit=merge-approval:1", "GateVisit=", "GateVisit=merge-approval:2", "GateVisit="],
            Upserts(run.History, "GateVisit"));
    }

    /// <summary>
    /// An indefinite guarded wait: discard, re-register, drain, wait again — with no timer at all.
    /// </summary>
    [Fact]
    public async Task AnIndefiniteGuardedWait_DiscardsAndKeepsWaiting_WithoutTimers()
    {
        var definition = Definition(
            PublishReviewRef(ReviewRef),
            GuardedGate(timeoutMinutes: null, notify: Notify()),
            Result(DecisionResult));

        var logs = new GateVisitGuardLogs();
        var run = await RunAsync(definition, async (_, handle, activities) =>
        {
            await activities.Started("notify").WaitAsync(TimeSpan.FromSeconds(20));
            await handle.SignalAsync(Gate, [Delegated("merge-approval:4", ReviewRef, StaleEvidence)]);
            activities.Release("notify");

            await WaitForAsync(() => Discards(logs.Entries).Count >= 1);
            await handle.SignalAsync(Gate, [Delegated("merge-approval:1", ReviewRef, ValidEvidence)]);
        }, held: ["notify"], logs: logs);

        Assert.Equal($"approved|{ValidEvidence}", run.Result);
        AssertDiscards(run, "merge-approval:1", count: 1);
        Assert.Empty(TimersStarted(run.History));
        Assert.Equal(["GateVisit=merge-approval:1", "GateVisit="], Upserts(run.History, "GateVisit"));
    }

    // ── AC-E5: validation ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("""{"type":"wait_for_signal","name":"named_gate","signalName":"merge-approval","visitVar":"{{vars.x}}"}""", "visitVar")]
    [InlineData("""{"type":"wait_for_signal","name":"named_gate","signalName":"merge-approval","visitVar":""}""", "visitVar")]
    [InlineData("""{"type":"wait_for_signal","name":"named_gate","signalName":"merge-approval","visitVar":"  "}""", "visitVar")]
    [InlineData("""{"type":"wait_for_signal","name":"named_gate","signalName":"merge-approval","delegatedGuard":{"marker":"GrantId","require":{"VisitId":"{{vars.v}}"}}}""", "visitVar")]
    [InlineData("""{"type":"wait_for_signal","name":"named_gate","signalName":"merge-approval","visitVar":"v","delegatedGuard":{"marker":"GrantId","require":{"ArtifactRef":"{{vars.review_ref}}"}}}""", "VisitId")]
    [InlineData("""{"type":"wait_for_signal","name":"named_gate","signalName":"merge-approval","visitVar":"v","delegatedGuard":{"marker":"GrantId"}}""", "VisitId")]
    [InlineData("""{"type":"wait_for_signal","name":"named_gate","signalName":"merge-approval","visitVar":"v","delegatedGuard":{"marker":" ","require":{"VisitId":"{{vars.v}}"}}}""", "marker")]
    [InlineData("""{"type":"wait_for_signal","name":"named_gate","signalName":"merge-approval","visitVar":"v","delegatedGuard":{"require":{"VisitId":"{{vars.v}}"}}}""", "marker")]
    public void AnInvalidGateVisitOrGuard_IsRejected_NamingTheStep(string step, string mentions)
    {
        var root = Parse($$"""{"type":"sequence","steps":[{{step}}]}""");

        var ex = Assert.Throws<InvalidOperationException>(() => WorkflowDefinitionValidator.Validate(root, "ExampleWorkflow"));

        Assert.Contains("named_gate", ex.Message);
        Assert.Contains("ExampleWorkflow", ex.Message);
        Assert.Contains(mentions, ex.Message);
    }

    /// <summary>The seed shape validates and deserializes from the camelCase JSON field names.</summary>
    [Fact]
    public void TheSeedGuardShape_IsValid_AndDeserializesFromCamelCase()
    {
        var root = Parse("""
            {"type":"sequence","steps":[
              {"type":"wait_for_signal","name":"merge_gate","signalName":"merge-approval","visitVar":"merge_visit",
               "delegatedGuard":{"marker":"GrantId","require":{"VisitId":"{{vars.merge_visit}}","ArtifactRef":"{{vars.review_ref}}"}}},
              {"type":"wait_for_signal","name":"plain_gate","signalName":"doc-review","visitVar":"doc_visit"}
            ]}
            """);

        WorkflowDefinitionValidator.Validate(root, "ExampleWorkflow");

        var gate = Assert.IsType<WaitForSignalStep>(((SequenceStep)root).Steps[0]);
        Assert.Equal("merge_visit", gate.VisitVar);
        Assert.Equal("GrantId", gate.DelegatedGuard!.Marker);
        Assert.Equal("{{vars.merge_visit}}", gate.DelegatedGuard.Require!["VisitId"]);
        Assert.Equal("{{vars.review_ref}}", gate.DelegatedGuard.Require["ArtifactRef"]);
        Assert.Null(Assert.IsType<WaitForSignalStep>(((SequenceStep)root).Steps[1]).DelegatedGuard);
    }

    // ── harness ─────────────────────────────────────────────────────────────────────────────

    private sealed record Run(string? Result, WorkflowHistory History, IReadOnlyList<GateVisitGuardLogs.Entry> Logs);

    private sealed record TimerStart(TimeSpan Duration, DateTime At);

    private static WaitForSignalStep GuardedGate(
        string name = "merge_gate",
        int? timeoutMinutes = 60,
        int? reminderMinutes = null,
        int maxReminders = 0,
        DelegateStep? notify = null) => new()
    {
        Name = name,
        SignalName = Gate,
        Phase = Gate,
        OutputVar = "decision",
        VisitVar = "merge_visit",
        DelegatedGuard = new DelegatedGuard
        {
            Marker = "GrantId",
            Require = new()
            {
                ["VisitId"] = "{{vars.merge_visit}}",
                ["ArtifactRef"] = "{{vars.review_ref}}",
            },
        },
        TimeoutMinutes = timeoutMinutes,
        ReminderIntervalMinutes = reminderMinutes,
        MaxReminders = maxReminders,
        NotifyStep = notify,
        AutoCompleteOnTimeout = true,
    };

    private static WaitForSignalStep Wait(string signal, string? visitVar = null) => new()
    {
        Name = signal,
        SignalName = signal,
        VisitVar = visitVar,
        OutputVar = signal,
        TimeoutMinutes = 60,
        MaxReminders = 0,
        AutoCompleteOnTimeout = true,
    };

    private static DelegateStep Notify() => new() { Name = "notify", Target = "agent1", Instruction = "please decide" };

    private static SetVariableStep PublishReviewRef(string value) => new() { Vars = new() { ["review_ref"] = value } };

    private static SetVariableStep Result(string template) => new() { Vars = new() { ["_result"] = template } };

    private static WorkflowDefinitionModel Definition(params StepDefinition[] steps) => new()
    {
        Name = "example-workflow",
        Namespace = "default",
        TaskQueue = "test",
        Root = new SequenceStep { Steps = steps },
    };

    private static StepDefinition Parse(string json) =>
        JsonSerializer.Deserialize<StepDefinition>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    /// <summary>The five-field payload the orchestrator sends; a null field is omitted.</summary>
    private static JsonElement Delegated(string? visitId, string? artifactRef, string evidence)
    {
        var payload = new Dictionary<string, object?> { ["Decision"] = "approved", ["GrantId"] = "grant-1" };
        if (visitId is not null) payload["VisitId"] = visitId;
        if (artifactRef is not null) payload["ArtifactRef"] = artifactRef;
        payload["Evidence"] = evidence;
        return JsonSerializer.SerializeToElement(payload);
    }

    private static WorkflowHistory GateWaitFixture() =>
        WorkflowHistory.FromJson(
            GateWaitHistoryScenario.WorkflowId,
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, GateWaitHistoryScenario.FixturePath)));

    /// <summary>
    /// Starts the definition, lets <paramref name="drive"/> signal it, then awaits the result —
    /// which is what lets the time-skipping server run the remaining timers out.
    /// </summary>
    private static async Task<Run> RunAsync(
        WorkflowDefinitionModel definition,
        Func<WorkflowEnvironment, WorkflowHandle, GateVisitGuardActivities, Task> drive,
        string[]? held = null,
        GateVisitGuardLogs? logs = null)
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        await env.Client.Connection.OperatorService.AddSearchAttributesAsync(
            new AddSearchAttributesRequest
            {
                Namespace = env.Client.Options.Namespace,
                SearchAttributes =
                {
                    ["Phase"] = IndexedValueType.Keyword,
                    [UniversalWorkflow.GateVisitAttribute] = IndexedValueType.Keyword,
                },
            });

        logs ??= new GateVisitGuardLogs();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var activities = new GateVisitGuardActivities(definition, held ?? []);
        var taskQueue = $"gate-visit-{Guid.NewGuid():N}";
        var workflowId = $"gate-visit-{Guid.NewGuid():N}";

        string? result = null;
        using var worker = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions(taskQueue) { LoggerFactory = loggerFactory }
                .AddWorkflow<UniversalWorkflow>()
                .AddAllActivities(activities.GetType(), activities));
        await worker.ExecuteAsync(async () =>
        {
            var handle = await env.Client.StartWorkflowAsync(
                "example-workflow",
                Array.Empty<object?>(),
                new WorkflowOptions(id: workflowId, taskQueue: taskQueue));
            await drive(env, handle, activities);
            result = await handle.GetResultAsync<string>().WaitAsync(TimeSpan.FromSeconds(60));
        });

        var history = await env.Client.GetWorkflowHandle(workflowId).FetchHistoryAsync();
        return new Run(result, history, logs.Entries);
    }

    private static async Task WaitForHistoryAsync(WorkflowHandle handle, Func<WorkflowHistory, bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!condition(await handle.FetchHistoryAsync()))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("history condition not reached");
            await Task.Delay(50);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not reached");
            await Task.Delay(50);
        }
    }

    private static List<TimerStart> TimersStarted(WorkflowHistory history) =>
        history.Events
            .Where(e => e.EventType == EventType.TimerStarted)
            .Select(e => new TimerStart(e.TimerStartedEventAttributes.StartToFireTimeout.ToTimeSpan(), e.EventTime.ToDateTime()))
            .ToList();

    /// <summary>When each <c>notify</c> delegation (initial notification and reminders) was scheduled.</summary>
    private static List<DateTime> NotifySchedules(WorkflowHistory history) =>
        history.Events
            .Where(e => e.ActivityTaskScheduledEventAttributes?.ActivityType?.Name == "DelegateToAgent"
                && DataConverter.Default.PayloadConverter
                    .ToValue<string>(e.ActivityTaskScheduledEventAttributes.Input.Payloads_[2])
                    .EndsWith("/notify", StringComparison.Ordinal))
            .Select(e => e.EventTime.ToDateTime())
            .ToList();

    /// <summary>Every upsert of the named attributes, in history order, as <c>Name=value</c>.</summary>
    private static List<string> Upserts(WorkflowHistory history, params string[] names) =>
        history.Events
            .Where(e => e.EventType == EventType.UpsertWorkflowSearchAttributes)
            .SelectMany(e => e.UpsertWorkflowSearchAttributesEventAttributes.SearchAttributes.IndexedFields
                .Where(kv => names.Contains(kv.Key))
                .OrderBy(kv => Array.IndexOf(names, kv.Key))
                .Select(kv => $"{kv.Key}={DataConverter.Default.PayloadConverter.ToValue<string>(kv.Value)}"))
            .ToList();

    private static List<GateVisitGuardLogs.Entry> Discards(IEnumerable<GateVisitGuardLogs.Entry> logs) =>
        logs.Where(l => l.Message.StartsWith("delegated signal discarded: stale visit or artifact", StringComparison.Ordinal))
            .ToList();

    /// <summary>
    /// Exactly <paramref name="count"/> discard warnings, each carrying the signal and the wait's
    /// own visit as structured fields — and no log line anywhere carrying a payload's evidence.
    /// </summary>
    private static void AssertDiscards(Run run, string visit, int count)
    {
        var discards = Discards(run.Logs);
        Assert.Equal(count, discards.Count);
        Assert.All(discards, d =>
        {
            Assert.Equal(LogLevel.Warning, d.Level);
            Assert.Equal(Gate, d.Fields["Signal"]);
            Assert.Equal(visit, d.Fields["Visit"]);
        });
        Assert.DoesNotContain(run.Logs, l => l.Message.Contains("https://example.com", StringComparison.Ordinal));
    }

    private static void AssertNear(TimeSpan expected, TimeSpan actual) =>
        Assert.InRange(actual, expected - TimeSpan.FromSeconds(10), expected + TimeSpan.FromSeconds(10));
}

// ---------------------------------------------------------------------------
// Test-only stubs
// ---------------------------------------------------------------------------

/// <summary>
/// Stubs the engine's load activities and the agent delegation. Delegations whose step name is
/// listed as held block until <see cref="Release"/>, which gives a test a window in which the
/// workflow is provably busy inside that step.
/// </summary>
internal sealed class GateVisitGuardActivities(WorkflowDefinitionModel definition, string[] held)
{
    private readonly Dictionary<string, (TaskCompletionSource Started, TaskCompletionSource Release)> _holds =
        held.ToDictionary(
            name => name,
            _ => (new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                  new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)));

    public Task Started(string step) => _holds[step].Started.Task;

    public void Release(string step) => _holds[step].Release.TrySetResult();

    [Activity("LoadWorkflowDefinition")]
    public WorkflowDefinitionModel LoadDefinition(string _) => definition;

    [Activity("LoadWorkflowConfig")]
    public JsonElement LoadConfig() => JsonSerializer.SerializeToElement(new { });

    [Activity("DelegateToAgent")]
    public async Task<AgentTaskResult> DelegateAsync(
        string target,
        string instruction,
        string taskId,
        bool retryOnIncomplete,
        int maxIncompleteRetries,
        int agentBudgetSeconds = 0,
        string? repo = null)
    {
        var step = taskId.Split('/').LastOrDefault() ?? taskId;
        if (_holds.TryGetValue(step, out var hold))
        {
            hold.Started.TrySetResult();
            await hold.Release.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        return new AgentTaskResult($"{step} done", "completed");
    }
}

/// <summary>Captures the worker's log lines, including the workflow logger's structured fields.</summary>
internal sealed class GateVisitGuardLogs : ILoggerProvider
{
    public sealed record Entry(LogLevel Level, string Message, IReadOnlyDictionary<string, object?> Fields);

    private readonly ConcurrentQueue<Entry> _entries = new();

    public IReadOnlyList<Entry> Entries => _entries.ToList();

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void Dispose() { }

    private sealed class Logger(GateVisitGuardLogs owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var fields = state is IEnumerable<KeyValuePair<string, object?>> kvs
                ? kvs.GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.Last().Value)
                : new Dictionary<string, object?>();
            owner._entries.Enqueue(new Entry(logLevel, formatter(state, exception), fields));
        }
    }
}
