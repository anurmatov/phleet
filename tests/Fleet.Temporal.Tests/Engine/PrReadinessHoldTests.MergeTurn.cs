using System.Text.Json;
using System.Text.Json.Nodes;
using Fleet.Temporal.Activities;
using Fleet.Temporal.Models;
using Fleet.Temporal.Tests.Activities;
using Temporalio.Api.Common.V1;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Converters;
using Temporalio.Exceptions;

namespace Fleet.Temporal.Tests.Engine;

/// <summary>
/// The shipped merge turn with the engine and the bridge that keep <c>noRedelivery</c>: what the
/// seed's <c>phase4_merge</c> step schedules, what a cancellation around it ends as, and what the
/// real <see cref="DelegateToAgentActivity"/> sends for it.
///
/// <para>
/// Two things here are not the shipped seed, and say so where they are used. A test that needs
/// the run to stand on the engine alone removes the repeat of the read after the merge turn, so
/// no sleep follows it. A test that needs to read <c>merge_status</c>, which no shipped step
/// renders, appends it to the instruction of the read that follows.
/// </para>
/// </summary>
public sealed partial class PrReadinessHoldTests
{
    // ── cancellation around the merge turn ───────────────────────────────────

    /// <summary>
    /// The run is cancelled while a read that precedes the merge turn is out. That read's
    /// <c>ignoreFailure</c> swallows the cancellation, and the read does not answer until the run
    /// has closed. The run ends <c>Canceled</c> and no merge command is scheduled.
    ///
    /// <para>
    /// On the shipped seed this does not depend on the engine's check at the merge turn: a read
    /// that was cancelled left no answer, no answer is held, and the hold's own timer ends a
    /// cancelled run. The turn is never reached. This pins the outcome of the whole.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("merge_preflight")]
    [InlineData("pin_after_approval")]
    public async Task CancelWhileAReadBeforeTheMergeTurnIsOut_Canceled_NoMergeCommand(string read)
    {
        var run = await CancelDuringAsync(read, new Script());

        Assert.Equal(WorkflowExecutionStatus.Canceled, run.Status);
        Assert.IsType<CanceledFailureException>(run.Failure!.InnerException);
        Assert.DoesNotContain(run.Calls, c => c.Step is "phase4_merge" or "phase4_merge_pinned" or "verify_merge_status" or "notify_merge_failed");
        // Nothing was scheduled after the read that was out when the cancel arrived.
        Assert.Equal(read, ScheduledDelegates(run.History)[^1]);
        Assert.DoesNotContain("phase4_merge", ScheduledDelegates(run.History));
        Assert.Empty(FiredSeconds(run.History));
        Assert.Empty(run.ChildStarts("UweDocMaintenanceWorkflow"));
    }

    /// <summary>
    /// The run is cancelled while the merge turn itself is out, and the turn does not answer until
    /// the run has closed. The run ends <c>Canceled</c>: no read of the merged state, no failure
    /// notice, no second command.
    /// </summary>
    [Fact]
    public async Task CancelWhileTheMergeTurnIsOut_Canceled_NoReadback_NoSecondCommand()
    {
        var run = await CancelDuringAsync("phase4_merge", new Script());

        Assert.Equal(WorkflowExecutionStatus.Canceled, run.Status);
        Assert.IsType<CanceledFailureException>(run.Failure!.InnerException);
        Assert.Single(run.Calls, c => c.Step == "phase4_merge");
        Assert.DoesNotContain(run.Calls, c => c.Step is "verify_merge_status" or "notify_merge_failed");
        Assert.Equal("phase4_merge", ScheduledDelegates(run.History)[^1]);
        Assert.Single(ScheduledDelegates(run.History), s => s == "phase4_merge");
        // The activity was told to stop, which is how the agent comes to be told.
        Assert.Contains(run.History.Events, e => e.EventType == EventType.ActivityTaskCancelRequested);
        Assert.Empty(FiredSeconds(run.History));
        Assert.Empty(run.ChildStarts("UweDocMaintenanceWorkflow"));
    }

    /// <summary>
    /// The same cancel with the one thing behind the merge turn taken away. In the shipped seed an
    /// unanswered read of the merged state is followed by a 60-second sleep before the next read,
    /// and a sleep ends a cancelled run by itself. Here the read is not repeated (the seed is
    /// changed for this test: <c>merge_readback_loop</c> runs once), so after the turn come only
    /// steps that swallow a failure, then the outcome branch.
    ///
    /// <para>
    /// The run still ends <c>Canceled</c>, because the engine does not let the turn's
    /// <c>ignoreFailure</c> swallow a cancellation. If it did, the cancelled turn would read as
    /// "not merged" and the run would end <c>Failed</c> with <c>delivery_failed</c>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task CancelWhileTheMergeTurnIsOut_IsEndedByTheEngine_NotByASleepThatHappensToFollow()
    {
        var run = await CancelDuringAsync("phase4_merge", new Script(),
            mutateSeed: seed => Step(seed, "merge_readback_loop")["maxIterations"] = 1);

        Assert.Equal(WorkflowExecutionStatus.Canceled, run.Status);
        Assert.IsType<CanceledFailureException>(run.Failure!.InnerException);
        Assert.Null(run.FailureMessage);
        Assert.Single(ScheduledDelegates(run.History), s => s == "phase4_merge");
        Assert.Equal("phase4_merge", ScheduledDelegates(run.History)[^1]);
        Assert.DoesNotContain(run.Calls, c => c.Step is "verify_merge_status" or "notify_merge_failed");
    }

    // ── the activity input ───────────────────────────────────────────────────

    /// <summary>
    /// What the engine schedules for the shipped steps. The merge turn carries eight arguments:
    /// retries off, no continuation, an unset budget, no repo, and the flag. Every other
    /// delegation of the run, each read before and after the turn included, carries the five it
    /// always carried.
    /// </summary>
    [Fact]
    public async Task MergeTurn_IsTheOnlyDelegationScheduledWithTheNoRedeliveryFlag()
    {
        // One needs_human_review verdict first, so the run also probes and pauses.
        var script = new Script { Review = round => round == 1 ? "needs_human_review" : "approved" };
        var run = await RunAsync(script, async (handle, h) =>
        {
            await WaitPhaseAsync(handle, h, "human-review");
            await handle.SignalAsync("human-review", [Json(new { Decision = "approved" })]);
            await h.WaitForGateVisitAsync(handle, "merge-approval:1");
            await h.SignalHumanApprovalAsync(handle);
        });
        Assert.Equal(WorkflowExecutionStatus.Completed, run.Status);

        var inputs = DelegateHistoryScenario.DelegateInputs(run.History)
            .Select(p => (Step: Decode<string>(p.Payloads_[2]).Split('/')[^1], Payloads: p.Payloads_)).ToList();

        var merge = Assert.Single(inputs, i => i.Step == "phase4_merge").Payloads;
        Assert.Equal(8, merge.Count);
        Assert.Equal(Coordinator, Decode<string>(merge[0]));
        Assert.Contains($"--match-head-commit {script.Head}", Decode<string>(merge[1]));
        Assert.EndsWith("/phase4_merge", Decode<string>(merge[2]));
        Assert.False(Decode<bool>(merge[3]));           // retryOnIncomplete
        Assert.Equal(0, Decode<int>(merge[4]));         // maxIncompleteRetries
        Assert.Equal(0, Decode<int>(merge[5]));         // agentBudgetSeconds: not explicit
        Assert.Null(Decode<string?>(merge[6]));         // repo: none, the slot is there to reach the next
        Assert.True(Decode<bool>(merge[7]));            // noRedelivery

        var others = inputs.Where(i => i.Step != "phase4_merge").ToList();
        Assert.All(others, i => Assert.Contains(i.Payloads.Count, new[] { 5, 7 }));
        foreach (var step in new[] { "phase1_implement", "ci_probe", "phase2_5_unresolved_check", "pin", "pin_after_approval", "merge_preflight", "verify_merge_status" })
            Assert.Equal(5, Assert.Single(others, i => i.Step == step).Payloads.Count);
    }

    /// <summary>The delegated path's merge step schedules what it scheduled before the field: five arguments, retries on.</summary>
    [Fact]
    public async Task DelegatedMerge_SchedulesFiveArguments_AndNothingInTheRunCarriesTheFlag()
    {
        var script = new Script();
        var run = await RunAsync(script, async (handle, h) =>
        {
            await h.WaitForGateVisitAsync(handle, "merge-approval:1");
            await handle.SignalAsync("merge-approval", [Delegated("merge-approval:1", script.Head)]);
        });
        Assert.Equal(WorkflowExecutionStatus.Completed, run.Status);

        var inputs = DelegateHistoryScenario.DelegateInputs(run.History)
            .Select(p => (Step: Decode<string>(p.Payloads_[2]).Split('/')[^1], Payloads: p.Payloads_)).ToList();

        var pinned = Assert.Single(inputs, i => i.Step == "phase4_merge_pinned").Payloads;
        Assert.Equal(5, pinned.Count);
        Assert.True(Decode<bool>(pinned[3]));           // retryOnIncomplete: the default
        Assert.Equal(3, Decode<int>(pinned[4]));        // maxIncompleteRetries: the default
        Assert.All(inputs, i => Assert.Contains(i.Payloads.Count, new[] { 5, 7 }));
        Assert.DoesNotContain(inputs, i => i.Step is "phase4_merge" or "merge_preflight");
        Assert.Equal(5, Assert.Single(inputs, i => i.Step == "pin_after_approval").Payloads.Count);
        Assert.Equal(5, Assert.Single(inputs, i => i.Step == "verify_merge_status").Payloads.Count);
    }

    // ── the merge turn through the real activity ─────────────────────────────

    /// <summary>
    /// The merge turn answers <c>incomplete</c>. The turn runs through the real activity with the
    /// arguments the engine scheduled, and the answer arrives the way an agent's does. The
    /// directive went to the broker once and no continuation followed it; the step's status
    /// variable reads <c>incomplete</c>; and what the run does next is decided by the read of the
    /// merged state, not by the turn.
    ///
    /// <para>
    /// <c>merge_status</c> is rendered by no shipped step. To read it, the seed is changed for
    /// this test in one place: the instruction of the read that follows the turn ends with it.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("merged")]
    [InlineData("open")]
    public async Task MergeTurnAnswersIncomplete_OneDirective_NoContinuation_ReadbackDecides(string readback)
    {
        var turn = new RealMergeTurn();
        var script = new Script { Verify = _ => readback };
        var run = await RunAsync(script, async (handle, h) =>
        {
            await h.WaitForGateVisitAsync(handle, "merge-approval:1");
            await h.SignalHumanApprovalAsync(handle);
            await WaitForCallAsync(h, "phase4_merge");
            await turn.AnswerAsync(new AgentTaskResult("The command started and my context ran out.", "incomplete"));
        }, allowFailure: true, delegateOverride: turn.Take, mutateSeed: ShowMergeStatusInTheReadback);

        Assert.Equal([turn.TaskId!], turn.Directives);
        Assert.DoesNotContain(turn.Published, id => id.Contains("/incomplete-retry-", StringComparison.Ordinal));
        Assert.Single(ScheduledDelegates(run.History), s => s == "phase4_merge");
        Assert.DoesNotContain(DelegateHistoryScenario.DelegateInputs(run.History),
            p => Decode<string>(p.Payloads_[2]).Contains("/incomplete-retry-", StringComparison.Ordinal));

        var read = Assert.Single(run.Calls, c => c.Step == "verify_merge_status");
        Assert.EndsWith("\nmerge_status=incomplete", read.Instruction);

        if (readback == "merged")
        {
            Assert.Equal(WorkflowExecutionStatus.Completed, run.Status);
            Assert.Single(run.ChildStarts("UweDocMaintenanceWorkflow"));
            Assert.DoesNotContain(run.Calls, c => c.Step == "notify_merge_failed");
        }
        else
        {
            Assert.Equal(WorkflowExecutionStatus.Failed, run.Status);
            Assert.Equal(SeedReviewLimitTests.DeliveryFailed, run.FailureMessage);
            Assert.Single(run.Calls, c => c.Step == "notify_merge_failed");
            Assert.Empty(run.ChildStarts("UweDocMaintenanceWorkflow"));
        }
    }

    /// <summary>
    /// The merge turn's answer is lost. The turn runs through the real activity on a clock the
    /// test moves: eleven minutes pass, past the points (five and ten minutes) at which a wait
    /// re-sends its directive, and the directive went to the broker once. The activity then ends
    /// in its ordinary timeout, the step's <c>ignoreFailure</c> sends the run to the read of the
    /// merged state, and that read decides. No second merge directive, no second merge activity.
    /// </summary>
    [Theory]
    [InlineData("merged")]
    [InlineData("open")]
    public async Task MergeTurnAnswerLost_OneDirectiveAcrossTheWholeBudget_ThenReadbackOnly(string readback)
    {
        var turn = new RealMergeTurn();
        var script = new Script { Verify = _ => readback };
        var run = await RunAsync(script, async (handle, h) =>
        {
            await h.WaitForGateVisitAsync(handle, "merge-approval:1");
            await h.SignalHumanApprovalAsync(handle);
            await WaitForCallAsync(h, "phase4_merge");
            await turn.RunOutTheBudgetAsync();
        }, allowFailure: true, delegateOverride: turn.Take);

        Assert.Equal([turn.TaskId!], turn.Directives);
        Assert.Equal(TimeSpan.FromMinutes(11), turn.Elapsed);
        Assert.Single(ScheduledDelegates(run.History), s => s == "phase4_merge");
        Assert.Contains(run.History.Events, e => e.EventType == EventType.ActivityTaskFailed);
        // After the turn: the read, and nothing that commands.
        Assert.Equal(readback == "merged" ? ["verify_merge_status"] : ["verify_merge_status", "notify_merge_failed"],
            run.Calls.SkipWhile(c => c.Step != "phase4_merge").Skip(1).Select(c => c.Step).TakeWhile(s => s != "prepare"));

        if (readback == "merged")
        {
            Assert.Equal(WorkflowExecutionStatus.Completed, run.Status);
            Assert.Single(run.ChildStarts("UweDocMaintenanceWorkflow"));
        }
        else
        {
            Assert.Equal(WorkflowExecutionStatus.Failed, run.Status);
            Assert.Equal(SeedReviewLimitTests.DeliveryFailed, run.FailureMessage);
        }
    }

    /// <summary>
    /// The control for the test above: the same lost answer with the flag taken off the step (the
    /// seed is changed for this test: <c>noRedelivery</c> is false on <c>phase4_merge</c>). The
    /// engine then schedules five arguments and the real activity re-sends the directive at five
    /// and at ten minutes. This is what the test above would count if the flag did nothing, and it
    /// shows that a re-send is visible here.
    /// </summary>
    [Fact]
    public async Task WithoutTheFlag_TheSameLostAnswer_IsReSentTwice()
    {
        var turn = new RealMergeTurn();
        var script = new Script { Verify = _ => "open" };
        var run = await RunAsync(script, async (handle, h) =>
        {
            await h.WaitForGateVisitAsync(handle, "merge-approval:1");
            await h.SignalHumanApprovalAsync(handle);
            await WaitForCallAsync(h, "phase4_merge");
            await turn.RunOutTheBudgetAsync();
        }, allowFailure: true, delegateOverride: turn.Take,
            mutateSeed: seed => Step(seed, "phase4_merge")["noRedelivery"] = false);

        Assert.Equal(5, Assert.Single(DelegateHistoryScenario.DelegateInputs(run.History),
            p => Decode<string>(p.Payloads_[2]).EndsWith("/phase4_merge", StringComparison.Ordinal)).Payloads_.Count);
        Assert.Equal([turn.TaskId!, turn.TaskId!, turn.TaskId!], turn.Directives);
    }

    // ── a merge that stays held ──────────────────────────────────────────────

    /// <summary>
    /// The numbers the runbook states for a merge that stays held while CI stays green, with the
    /// approval given in the first delivery pass: one hour of hold, then thirty stalls of three
    /// hours, each with one notice and each followed by one check, until the sixty delivery passes
    /// are spent. No merge command at any point.
    /// </summary>
    [Fact]
    public async Task MergeHeldUnderGreenCi_ThirtyStalls_ThenTheDeliveryPassesRunOut()
    {
        var script = new Script { Preflight = _ => "held" };
        var run = await RunAsync(script, ApproveFirstVisit, captureWaiter: true, allowFailure: true);

        Assert.Equal(Enumerable.Repeat(300L, 12).Concat(Enumerable.Repeat(10800L, 30)), FiredSeconds(run.History));
        Assert.Equal(30, run.Calls.Count(c => c.Step == "notify_stalled"));
        Assert.Equal(30, run.Calls.Count(c => c.Step == "ci_probe"));
        // Thirteen checks in the hold, then one after each stall but the last.
        Assert.Equal(13 + 29, run.Calls.Count(c => c.Step == "merge_preflight"));
        Assert.Equal(13 + 29, run.Calls.Count(c => c.Step == "pin_after_approval"));
        Assert.DoesNotContain(run.Calls, c => c.Step is "phase4_merge" or "phase4_merge_pinned" or "verify_merge_status");
        Assert.Equal(["merge-approval:1"], run.Upserts("GateVisit").Where(v => v.Length > 0));
        Assert.Single(run.ConsensusInputs);

        Assert.Single(run.Calls, c => c.Step == "notify_outer_loop_exhausted");
        Assert.Equal(WorkflowExecutionStatus.Failed, run.Status);
        Assert.Equal(SeedReviewLimitTests.DeliveryPassesExhausted, run.FailureMessage);
        Assert.Equal("cancelled", run.WaiterDecision);
        ValidWrites(run);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Approves at the first visit, waits until <paramref name="step"/> is out, and cancels the
    /// run. The step does not answer until the run has closed, so what ends the run is what the
    /// workflow does with the cancellation, not anything the step returns.
    /// </summary>
    private static async Task<SeedRun> CancelDuringAsync(string step, Script script, Action<JsonNode>? mutateSeed = null)
    {
        using var release = new ManualResetEventSlim(false);
        script.Hold[step] = release;
        try
        {
            return await RunAsync(script, async (handle, h) =>
            {
                await h.WaitForGateVisitAsync(handle, "merge-approval:1");
                await h.SignalHumanApprovalAsync(handle);
                await WaitForCallAsync(h, step);
                await handle.CancelAsync();
                try
                {
                    await Assert.ThrowsAsync<WorkflowFailedException>(
                        () => handle.GetResultAsync<JsonElement?>().WaitAsync(TimeSpan.FromSeconds(60)));
                }
                finally
                {
                    release.Set();
                }
            }, allowFailure: true, mutateSeed: mutateSeed);
        }
        finally
        {
            release.Set();
        }
    }

    /// <summary>The step name of every <c>DelegateToAgent</c> the run scheduled, in order.</summary>
    private static List<string> ScheduledDelegates(WorkflowHistory history) =>
        [.. DelegateHistoryScenario.DelegateInputs(history).Select(p => Decode<string>(p.Payloads_[2]).Split('/')[^1])];

    private static T Decode<T>(Payload payload) => DataConverter.Default.PayloadConverter.ToValue<T>(payload);

    /// <summary>The one step of that name in the PR definition of a seed about to be served.</summary>
    private static JsonObject Step(JsonNode seed, string name)
    {
        var definition = seed["workflowDefinitions"]!.AsArray().Single(d => d!["name"]!.GetValue<string>() == Workflow)!["definition"]!;
        return Assert.Single(Nodes(definition), n => n["type"] is not null && n["name"]?.GetValue<string>() == name);

        static IEnumerable<JsonObject> Nodes(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    yield return obj;
                    foreach (var (_, value) in obj)
                        foreach (var child in Nodes(value)) yield return child;
                    break;
                case JsonArray array:
                    foreach (var item in array)
                        foreach (var child in Nodes(item)) yield return child;
                    break;
            }
        }
    }

    /// <summary>
    /// Makes <c>merge_status</c> visible: the read after a human-approved merge turn ends its
    /// instruction with the variable. Nothing else in the definition is touched.
    /// </summary>
    private static void ShowMergeStatusInTheReadback(JsonNode seed)
    {
        var read = Assert.Single(Step(seed, "merge_readback_loop")["steps"]!.AsArray(), s => s!["name"]?.GetValue<string>() == "verify_merge_status")!;
        read["instruction"] = read["instruction"]!.GetValue<string>() + "\nmerge_status={{vars.merge_status}}";
    }

    /// <summary>
    /// Sends the seed's <c>phase4_merge</c> delegation, and only that one, through the real
    /// <see cref="DelegateToAgentActivity"/> with exactly the arguments the engine scheduled. The
    /// broker is the recording substitute and the clock the manual one of
    /// <see cref="DelegateNoRedeliveryActivityTests"/>; the activity runs inside the worker, so its
    /// heartbeats, its cancellation and its failure are the real ones.
    /// </summary>
    private sealed class RealMergeTurn
    {
        private readonly DelegateNoRedeliveryActivityTests.Harness _bridge = DelegateNoRedeliveryActivityTests.Build();
        private readonly DateTimeOffset _started;

        public RealMergeTurn() => _started = _bridge.Time.GetUtcNow();

        /// <summary>The task id the engine gave the turn; null until the turn has been scheduled.</summary>
        public string? TaskId { get; private set; }

        /// <summary>The task id of every message that reached the broker, in order.</summary>
        public List<string> Published
        {
            get { lock (_bridge.PublishedTaskIds) return [.. _bridge.PublishedTaskIds]; }
        }

        /// <summary>Publishes of the turn's own directive: its task id, or a continuation of it.</summary>
        public List<string> Directives =>
            [.. Published.Where(id => id == TaskId || id.StartsWith(TaskId + "/incomplete-retry-", StringComparison.Ordinal))];

        /// <summary>How far the activity's clock has been moved.</summary>
        public TimeSpan Elapsed => _bridge.Time.GetUtcNow() - _started;

        public Task<AgentTaskResult>? Take(Call call, object?[] args)
        {
            if (call.Step != "phase4_merge") return null;

            TaskId = (string)args[2]!;
            return _bridge.Activity.DelegateToAgentAsync(
                (string)args[0]!,
                (string)args[1]!,
                (string)args[2]!,
                (bool)args[3]!,
                (int)args[4]!,
                args.Length > 5 ? (int)args[5]! : 0,
                args.Length > 6 ? (string?)args[6] : null,
                args.Length > 7 && (bool)args[7]!);
        }

        /// <summary>Completes the turn's wait the way the relay listener does when the agent's answer arrives.</summary>
        public Task AnswerAsync(AgentTaskResult answer) =>
            WaitForAsync(() => TaskId is { } id && _bridge.Registry.TryComplete(id, answer), "the activity registered its wait");

        /// <summary>
        /// Nobody answers. Moves the activity's clock through its whole budget (eleven minutes)
        /// thirty seconds at a time, waiting after each step until the wait is parked on its next
        /// heartbeat delay, so a re-send that was due in a step has happened before the next one.
        /// </summary>
        public async Task RunOutTheBudgetAsync()
        {
            // Parked: the budget timer and the first heartbeat delay.
            await WaitForAsync(() => Directives.Count == 1 && _bridge.Time.ActiveTimers == 2, "the directive was published and the wait began");

            for (var step = 1; step < 22; step++)
            {
                _bridge.Time.Advance(TimeSpan.FromSeconds(30));
                await WaitForAsync(() => _bridge.Time.ActiveTimers == 2, $"the wait went on after heartbeat {step}");
            }

            // The last thirty seconds: the budget is spent and the activity ends in its timeout.
            _bridge.Time.Advance(TimeSpan.FromSeconds(30));
            await WaitForAsync(() => _bridge.Registry.PendingCount == 0, "the wait ended");
        }

        private static async Task WaitForAsync(Func<bool> condition, string what)
        {
            var deadline = Environment.TickCount64 + 30_000;
            while (!condition())
            {
                Assert.True(Environment.TickCount64 < deadline, $"timed out waiting until {what}");
                await Task.Delay(5);
            }
        }
    }
}
