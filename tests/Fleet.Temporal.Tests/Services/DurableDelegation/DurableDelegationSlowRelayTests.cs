using Fleet.Temporal.Models;
using Fleet.Temporal.Services;

namespace Fleet.Temporal.Tests.Services.DurableDelegation;

/// <summary>
/// A heartbeat has a deadline and a relay send does not, so the pass that heartbeats must never
/// wait for the relay: not for a cancel, a timeout notice, a re-send or a continuation, and not
/// for its own record's any more than for another's.
///
/// <para>
/// These are regressions for the pass that made those sends inline. Ten cancels of ten seconds
/// each put the eleventh delegation's heartbeat a hundred seconds out, past the ninety-second
/// heartbeat timeout of a consensus review, and Temporal failed an activity whose agent was
/// working. They use only what the service had then (plus hooks on the fake relay), so they can be
/// run against it and seen to fail.
/// </para>
/// </summary>
public sealed class DurableDelegationSlowRelayTests
{
    private static readonly TimeSpan Promptly = TimeSpan.FromSeconds(5);
    private static readonly AgentTaskResult Done = new("all done", "completed");

    /// <summary>Shortens how long a send keeps its place, where the service has such a setting.</summary>
    private static void SetRelaySendTimeout(DurableDelegationService service, TimeSpan value) =>
        typeof(DurableDelegationService)
            .GetProperty("RelaySendTimeout", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.SetValue(service, value);

    /// <summary>
    /// The reviewer's probe. Ten delegations have been cancelled by their workflows and each
    /// cancel takes the relay ten seconds; an eleventh is alive. Its heartbeat is made in the same
    /// pass at the time the pass began, not a hundred seconds later, and it is still pending
    /// afterwards. The ten cancels are all sent, off the pass.
    /// </summary>
    [Fact]
    public async Task TenSlowCancels_DoNotPostponeAnotherActivitysHeartbeat()
    {
        using var h = new DurableHarness();
        for (var i = 0; i < 10; i++)
        {
            var taskId = $"wf-1/cancelled-{i:00}";
            await h.StartAsync(DurableHarness.Start(taskId));
            h.Gateway.HeartbeatFor[taskId] = ActivityCallOutcome.CancelRequested;
        }

        // Sorts after the others, so it is the last one a pass reaches.
        await h.StartAsync(DurableHarness.Start("wf-1/z-live"));
        await h.SettleAsync();
        h.Relay.BeforeSend = async (kind, _, _) =>
        {
            if (kind != "cancel") return;
            h.Clock.Advance(TimeSpan.FromSeconds(10));
            await Task.Yield();
        };
        var passBegan = h.Clock.GetUtcNow();

        await h.Service.TickAsync(default);

        var live = Assert.Single(h.Gateway.HeartbeatTimes, t => t.TaskId == "wf-1/z-live");
        Assert.Equal(passBegan, live.At);
        Assert.Contains(await h.PendingAsync(), p => p.TaskId == "wf-1/z-live" && p.State == "waiting");
        // Temporal was told about every cancellation in that same pass, without waiting either.
        Assert.Equal(10, h.Gateway.Cancelled.Count);

        await h.SettleAsync();
        await DurableHarness.WaitForAsync(() => h.Relay.Cancels.Count == 10, "the ten cancels were sent");
        Assert.Equal(
            Enumerable.Range(0, 10).Select(i => ("reviewer-one", $"wf-1/cancelled-{i:00}")).ToHashSet(),
            h.Relay.Cancels.ToHashSet());
        Assert.Equal("wf-1/z-live", Assert.Single(await h.PendingAsync()).TaskId);
    }

    /// <summary>
    /// A relay that never returns and cannot be told to stop, with every kind of send owed: a
    /// cancel, a timeout notice, a five-minute re-send for each delegation, and a continuation.
    /// No pass and no reply waits for any of it, the waiting delegations are heartbeated on every
    /// pass, and Temporal is told about the cancel and the timeout in the first pass. The four
    /// calls that were made stay four however long they hang, and what could not be sent stays
    /// owed. When the relay comes back, a record's cancel goes out after that record's own
    /// directive has ended, each cancel and notice is sent once, and nothing is dropped or
    /// completed twice.
    /// </summary>
    [Fact]
    public async Task AHungRelay_NeverHoldsUpAPassOrAReply_AndDelaysOnlyRelayEffects()
    {
        using var h = new DurableHarness();
        SetRelaySendTimeout(h.Service, TimeSpan.FromMilliseconds(50));
        await h.StartAsync(DurableHarness.Start("wf-1/cancelled", budgetSeconds: 3600));
        await h.StartAsync(DurableHarness.Start("wf-1/continued", budgetSeconds: 3600));
        await h.StartAsync(DurableHarness.Start("wf-1/live", budgetSeconds: 3600));
        await h.StartAsync(DurableHarness.Start("wf-1/timed-out", budgetSeconds: 60));
        h.Gateway.HeartbeatFor["wf-1/cancelled"] = ActivityCallOutcome.CancelRequested;
        Assert.Equal(4, h.Relay.Published.Count);

        // From here on nothing the relay is asked to send returns, and every re-send is due.
        var relayIsBack = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Relay.BeforeSend = (_, _, _) => relayIsBack.Task;
        h.Clock.Advance(TimeSpan.FromMinutes(6));

        // A reply that needs a continuation is settled without waiting for the continuation.
        Assert.Equal(
            DurableReplyOutcome.Continued,
            await h.Service.TryHandleReplyAsync("wf-1/continued", new AgentTaskResult("part one", "incomplete"), default,
                    await h.ExecutionIdAsync("wf-1/continued"))
                .WaitAsync(Promptly));
        // The continuation and the three re-sends that were due are made, and hang.
        await DurableHarness.WaitForAsync(() => h.Relay.Started.Count == 8, "the continuation and the re-sends were started");

        // Pass after pass with the relay silent, well past the time every call was given.
        for (var pass = 1; pass <= 5; pass++)
        {
            await h.Service.TickAsync(default).WaitAsync(Promptly);

            Assert.Equal(pass, h.Gateway.HeartbeatTimes.Count(t => t.TaskId == "wf-1/live"));
            Assert.Equal(pass, h.Gateway.HeartbeatTimes.Count(t => t.TaskId == "wf-1/continued"));
            // Told once, in the first pass.
            Assert.Equal("wf-1/cancelled", Assert.Single(h.Gateway.Cancelled));
            Assert.Equal("wf-1/timed-out", Assert.Single(h.Gateway.Failed).TaskId);
            await Task.Delay(60);
        }

        // Nothing more was started behind the four that hang, nothing has been sent, and nothing
        // was dropped for want of it.
        Assert.Equal(8, h.Relay.Started.Count);
        Assert.Equal(4, h.Relay.MostInProgress);
        Assert.Empty(h.Relay.Cancels);
        Assert.Empty(h.Relay.TimeoutReports);
        Assert.Equal(4, h.Relay.Published.Count);
        Assert.Equal(
            [("wf-1/cancelled", "cancelling"), ("wf-1/continued", "waiting"), ("wf-1/live", "waiting"), ("wf-1/timed-out", "timed-out")],
            (await h.PendingAsync()).Select(p => (p.TaskId, p.State)).ToArray());

        relayIsBack.SetResult();
        await DurableHarness.WaitForAsync(
            () => h.Relay.Cancels.Count == 1 && h.Relay.TimeoutReports.Count == 1 && h.Relay.Published.Count == 8,
            "the held-up sends went out");
        await h.SettleAsync();
        await h.TickAsync();

        // The cancel was not made until that record's own directive, which was already out, had ended.
        var events = h.Relay.Events.ToList();
        Assert.True(events.LastIndexOf("end:publish:wf-1/cancelled") < events.IndexOf("start:cancel:wf-1/cancelled"));
        Assert.Equal(("reviewer-one", "wf-1/cancelled"), Assert.Single(h.Relay.Cancels));
        Assert.Equal("wf-1/timed-out", Assert.Single(h.Relay.TimeoutReports).TaskId);
        Assert.Equal(8, h.Relay.Published.Count);
        // The two that only owed the relay something are gone, once; the waiting two remain.
        Assert.Equal(["wf-1/continued", "wf-1/live"], (await h.PendingAsync()).Select(p => p.TaskId).ToArray());
        Assert.Single(h.Gateway.Cancelled);
        Assert.Single(h.Gateway.Failed);
        h.Restart();
        Assert.Equal(["wf-1/continued", "wf-1/live"], (await h.PendingAsync()).Select(p => p.TaskId).ToArray());

        // And the delegation whose continuation was held up completes normally.
        Assert.Equal(
            DurableReplyOutcome.Completed,
            await h.ReplyAsync("wf-1/continued/incomplete-retry-1", new AgentTaskResult("part two", "completed")));
        Assert.Equal(new AgentTaskResult("part one\npart two", "completed"), Assert.Single(h.Gateway.Completed).Result);
    }

    /// <summary>
    /// Sends that are owed all at once, as after a restart, go out a few at a time: more than one,
    /// so one slow send does not hold the rest, and never more than the lane allows.
    /// </summary>
    [Fact]
    public async Task ManyOwedSends_GoOutAFewAtATime()
    {
        using var h = new DurableHarness();
        for (var i = 0; i < 12; i++)
            await h.StartAsync(DurableHarness.Start($"wf-1/task-{i:00}", budgetSeconds: 3600));
        h.Relay.BeforeSend = (_, _, _) => Task.Delay(100);
        h.Clock.Advance(TimeSpan.FromMinutes(6));

        await h.Service.TickAsync(default).WaitAsync(Promptly);
        await h.SettleAsync();
        await DurableHarness.WaitForAsync(() => h.Relay.Published.Count == 24, "the twelve re-sends went out");

        Assert.InRange(h.Relay.MostInProgress, 2, 4);
        Assert.Equal(12, h.Gateway.Count("heartbeat"));
    }

    /// <summary>
    /// A send can finish long after it started. By then a later attempt may own the task id: its
    /// record is not the one the cancel was for, so the finished cancel must not mark it cancelled
    /// or remove it.
    /// </summary>
    [Fact]
    public async Task ACancelThatFinishesLate_DoesNotTouchTheAttemptThatReplacedItsRecord()
    {
        using var h = new DurableHarness();
        await h.StartAsync(DurableHarness.Start(instruction: "first attempt"));
        h.Gateway.Heartbeat = ActivityCallOutcome.CancelRequested;
        var cancelMayFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Relay.BeforeSend = (kind, _, _) => kind == "cancel" ? cancelMayFinish.Task : Task.CompletedTask;

        await h.Service.TickAsync(default).WaitAsync(Promptly);
        await DurableHarness.WaitForAsync(() => h.Relay.Started.Contains("cancel:wf-1/review"), "the cancel was started");

        // The workflow's next attempt of the same step, under the same task id. Started on the
        // service itself: the first attempt's cancel is still out, so the lane is not idle.
        h.Gateway.Heartbeat = ActivityCallOutcome.Ok;
        await h.Service.StartDelegationAsync(DurableHarness.Start(instruction: "second attempt") with { Attempt = 2 }, default)
            .WaitAsync(Promptly);

        cancelMayFinish.SetResult();
        await h.SettleAsync();
        await DurableHarness.WaitForAsync(() => h.Relay.Cancels.Count == 1, "the first attempt's cancel was sent");
        await h.TickAsync();

        var successor = Assert.Single(await h.PendingAsync());
        Assert.Equal("waiting", successor.State);
        // Still heartbeated: once when the first attempt was found cancelled, once now.
        Assert.Equal(2, h.Gateway.Count("heartbeat"));
        Assert.Equal(DurableReplyOutcome.Completed, await h.ReplyAsync("wf-1/review", Done));
        Assert.Single(h.Gateway.Completed);
        Assert.Single(h.Relay.Cancels);
    }

    /// <summary>
    /// The other late finish: the agent's answer completed the delegation while a re-send was
    /// still on its way. The record is gone; the finished send must not bring it back or complete
    /// anything a second time.
    /// </summary>
    [Fact]
    public async Task AReSendThatFinishesAfterTheAnswer_ChangesNothing()
    {
        using var h = new DurableHarness();
        await h.StartAsync(DurableHarness.Start(budgetSeconds: 3600));
        var reSendMayFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Relay.BeforeSend = (_, _, _) => reSendMayFinish.Task;
        h.Clock.Advance(TimeSpan.FromMinutes(6));

        await h.Service.TickAsync(default).WaitAsync(Promptly);
        await DurableHarness.WaitForAsync(() => h.Relay.Started.Count == 2, "the re-send was started");

        Assert.Equal(
            DurableReplyOutcome.Completed,
            await h.Service.TryHandleReplyAsync("wf-1/review", Done, default, await h.ExecutionIdAsync("wf-1/review"))
                .WaitAsync(Promptly));
        Assert.Empty(await h.PendingAsync());

        reSendMayFinish.SetResult();
        await h.SettleAsync();
        await DurableHarness.WaitForAsync(() => h.Relay.Published.Count == 2, "the re-send finished");

        Assert.Empty(await h.PendingAsync());
        Assert.Single(h.Gateway.Completed);
        h.Restart();
        Assert.Empty(await h.PendingAsync());
    }

    /// <summary>
    /// A cancel is one best-effort attempt, as it was inside the activity. One that hangs is told
    /// to stop when its time is up; a relay that honours that ends the call, the attempt has been
    /// made, and the record does not wait any longer.
    /// </summary>
    [Fact]
    public async Task ACancelThatHangs_IsToldToStopWhenItsTimeIsUp_AndThatWasTheOneAttempt()
    {
        using var h = new DurableHarness();
        SetRelaySendTimeout(h.Service, TimeSpan.FromMilliseconds(50));
        await h.StartAsync(DurableHarness.Start());
        h.Gateway.Heartbeat = ActivityCallOutcome.CancelRequested;
        h.Relay.BeforeSend = (kind, _, ct) => kind == "cancel" ? Task.Delay(Timeout.Infinite, ct) : Task.CompletedTask;

        await h.Service.TickAsync(default).WaitAsync(Promptly);
        await h.SettleAsync();

        Assert.Empty(await h.PendingAsync());
        Assert.Empty(h.Relay.Cancels);
        Assert.Single(h.Gateway.Cancelled);
        h.Restart();
        Assert.Empty(await h.PendingAsync());
    }

    /// <summary>
    /// A cancel whose call never ends, whatever it is told, is not written off: it may still
    /// arrive. Its record stays, the cancel stays owed in the file, and a new process sends it.
    /// Meanwhile it holds one place and nothing else: other delegations are heartbeated and their
    /// sends go out.
    /// </summary>
    [Fact]
    public async Task ACancelThatNeverEnds_StaysOwed_AndHoldsOnlyItsOwnPlace()
    {
        using var h = new DurableHarness();
        SetRelaySendTimeout(h.Service, TimeSpan.FromMilliseconds(50));
        await h.StartAsync(DurableHarness.Start("wf-1/cancelled", budgetSeconds: 3600));
        await h.StartAsync(DurableHarness.Start("wf-1/other", budgetSeconds: 3600));
        h.Gateway.HeartbeatFor["wf-1/cancelled"] = ActivityCallOutcome.CancelRequested;
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Relay.BeforeSend = (kind, _, _) => kind == "cancel" ? never.Task : Task.CompletedTask;

        await h.Service.TickAsync(default).WaitAsync(Promptly);
        await DurableHarness.WaitForAsync(() => h.Relay.Started.Contains("cancel:wf-1/cancelled"), "the cancel was started");
        h.Clock.Advance(TimeSpan.FromMinutes(6));
        for (var pass = 0; pass < 4; pass++)
        {
            await Task.Delay(60);
            await h.Service.TickAsync(default).WaitAsync(Promptly);
        }

        await DurableHarness.WaitForAsync(() => h.Relay.Published.Count == 3, "the other delegation's re-send went out");
        Assert.Equal(5, h.Gateway.HeartbeatTimes.Count(t => t.TaskId == "wf-1/other"));
        Assert.Equal(
            [("wf-1/cancelled", "cancelling"), ("wf-1/other", "waiting")],
            (await h.PendingAsync()).Select(p => (p.TaskId, p.State)).ToArray());
        Assert.Equal(1, h.Relay.Started.Count(s => s == "cancel:wf-1/cancelled"));

        // A new process has only the file, and the relay answers it.
        h.Relay.BeforeSend = null;
        h.Restart();
        await h.TickAsync();

        Assert.Equal(("reviewer-one", "wf-1/cancelled"), Assert.Single(h.Relay.Cancels));
        Assert.Equal("wf-1/other", Assert.Single(await h.PendingAsync()).TaskId);
    }
}
