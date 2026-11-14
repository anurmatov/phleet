using Fleet.Temporal.Models;
using Fleet.Temporal.Services;

namespace Fleet.Temporal.Tests.Services.DurableDelegation;

/// <summary>
/// What the relay lane must never let out, and never let out of order.
///
/// <para>
/// The first group is seen from the agent's side, through <see cref="TaskIdOnlyReceiver"/>: the
/// agent knows a piece of work by its task id and nothing else, and a later attempt of a step
/// reuses its predecessor's task id. A cancel meant for one execution therefore stops whatever is
/// running under that id when it arrives, and a cancel that has left the bridge cannot be called
/// back. The only protection the bridge can give is order: the successor's directive is not
/// published while such a cancel is owed, still out, or may still arrive.
/// </para>
///
/// <para>
/// These are regressions for a lane that queued sends behind four places, recycled a place whose
/// call was still hung, and published a successor's directive the moment it was recorded. They
/// use only what the service had then, so they can be run against it and seen to fail.
/// </para>
/// </summary>
public sealed class DurableDelegationRelayOrderTests
{
    private const string Agent = "reviewer-one";
    private const string TaskId = "wf-1/review";
    private static readonly TimeSpan Promptly = TimeSpan.FromSeconds(5);
    private static readonly AgentTaskResult Done = new("all done", "completed");

    private static void SetRelaySendTimeout(DurableDelegationService service, TimeSpan value) =>
        typeof(DurableDelegationService)
            .GetProperty("RelaySendTimeout", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.SetValue(service, value);

    private static DelegationStart Attempt(int attempt, string instruction) =>
        DurableHarness.Start(instruction: instruction, budgetSeconds: 3600) with { Attempt = attempt };

    // ── a cancel reaches only the execution it was issued for ────────────────

    /// <summary>
    /// The workflow cancels a delegation and runs the step again under the same task id while the
    /// first attempt's cancel is still on its way. The cancel takes longer than it is given, and
    /// cannot be stopped. For all that time the successor is recorded and heartbeated, and its
    /// directive is not published. When the cancel arrives it finds the execution it was meant
    /// for; only then does the successor's directive go out, and what it starts is never
    /// cancelled.
    /// </summary>
    [Fact]
    public async Task ADelayedCancel_NeverReachesTheSameIdSuccessor_AtAReceiverThatMatchesByTaskIdOnly()
    {
        using var h = new DurableHarness();
        var agent = new TaskIdOnlyReceiver();
        h.Relay.Receiver = agent;
        SetRelaySendTimeout(h.Service, TimeSpan.FromMilliseconds(50));
        await h.StartAsync(Attempt(1, "first attempt"));

        h.Gateway.Heartbeat = ActivityCallOutcome.CancelRequested;
        var cancelArrives = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Relay.BeforeSend = (kind, _, _) => kind == "cancel" ? cancelArrives.Task : Task.CompletedTask;
        await h.Service.TickAsync(default).WaitAsync(Promptly);
        await DurableHarness.WaitForAsync(() => h.Relay.Started.Contains($"cancel:{TaskId}"), "the cancel was started");

        // The next attempt of the same step. The activity is not kept waiting.
        h.Gateway.Heartbeat = ActivityCallOutcome.Ok;
        await h.Service.StartDelegationAsync(Attempt(2, "second attempt"), default).WaitAsync(Promptly);

        for (var pass = 1; pass <= 4; pass++)
        {
            await Task.Delay(60);
            await h.Service.TickAsync(default).WaitAsync(Promptly);
        }

        // Four passes, well past the cancel's time: the successor is alive in Temporal and has
        // not been announced to the agent.
        Assert.Equal(5, h.Gateway.Count("heartbeat"));
        Assert.Equal("waiting", Assert.Single(await h.PendingAsync()).State);
        Assert.Equal([$"directive:{TaskId}→#1"], agent.Log);

        cancelArrives.SetResult();
        await DurableHarness.WaitForAsync(() => agent.Log.Count == 3, "the cancel arrived and the successor's directive followed");
        await h.SettleAsync();

        Assert.Equal([$"directive:{TaskId}→#1", $"cancel:{TaskId}→#1", $"directive:{TaskId}→#2"], agent.Log);
        var successor = agent.Serving("second attempt");
        Assert.NotNull(successor);
        Assert.Equal(2, successor.Number);
        Assert.True(successor.Running);
        Assert.Single(h.Relay.Cancels);

        Assert.Equal(DurableReplyOutcome.Completed, await h.ReplyAsync(TaskId, Done));
        Assert.True(agent.Serving("second attempt")!.Running);
    }

    /// <summary>
    /// The bridge stopped waiting for a cancel without learning what became of it: the request
    /// had left, and the orchestrator may still pass it on. A successor recorded after that is
    /// held for as long as the cancel can still arrive, across a restart too. Here it does arrive,
    /// ten seconds late, and finds only the execution it was meant for.
    /// </summary>
    [Fact]
    public async Task ACancelWhoseOutcomeIsUnknown_HoldsTheSuccessorUntilItCanNoLongerArrive()
    {
        using var h = new DurableHarness();
        var agent = new TaskIdOnlyReceiver();
        h.Relay.Receiver = agent;
        await h.StartAsync(Attempt(1, "first attempt"));

        // The call ends at the bridge with nothing delivered and nothing known.
        h.Gateway.Heartbeat = ActivityCallOutcome.CancelRequested;
        h.Relay.BeforeSend = (kind, _, _) =>
            kind == "cancel" ? Task.FromCanceled(new CancellationToken(canceled: true)) : Task.CompletedTask;
        await h.TickAsync();
        Assert.Empty(await h.PendingAsync());
        Assert.Empty(h.Relay.Cancels);

        h.Gateway.Heartbeat = ActivityCallOutcome.Ok;
        h.Relay.BeforeSend = null;
        await h.StartAsync(Attempt(2, "second attempt"));
        Assert.Equal([$"directive:{TaskId}→#1"], agent.Log);

        // A new process, ten seconds on: still held, from the file alone.
        h.Clock.Advance(TimeSpan.FromSeconds(10));
        h.Restart();
        await h.TickAsync();
        Assert.Equal([$"directive:{TaskId}→#1"], agent.Log);
        Assert.Equal("waiting", Assert.Single(await h.PendingAsync()).State);

        // The cancel the bridge gave up on is applied by the agent now.
        agent.Cancel(Agent, TaskId);

        h.Clock.Advance(TimeSpan.FromSeconds(25));
        await h.TickAsync();

        Assert.Equal([$"directive:{TaskId}→#1", $"cancel:{TaskId}→#1", $"directive:{TaskId}→#2"], agent.Log);
        Assert.Equal(2, agent.Serving("second attempt")!.Number);
        Assert.True(agent.Serving("second attempt")!.Running);
    }

    /// <summary>
    /// A cancel that was owed but had not been sent when its record was replaced is not dropped
    /// with the record and is not left to race the successor: the successor carries it, in the
    /// file, and it is sent and has ended before the successor's directive is published.
    /// </summary>
    [Fact]
    public async Task ACancelStillOwedWhenItsRecordIsReplaced_IsSentBeforeTheSuccessorsDirective_EvenAfterARestart()
    {
        using var h = new DurableHarness();
        var agent = new TaskIdOnlyReceiver();
        h.Relay.Receiver = agent;
        await h.StartAsync(Attempt(1, "first attempt"));

        // Temporal is told at once; the cancel cannot go out yet.
        h.Gateway.Heartbeat = ActivityCallOutcome.CancelRequested;
        h.Relay.Ready = false;
        await h.TickAsync();
        Assert.Single(h.Gateway.Cancelled);
        Assert.Empty(h.Relay.Cancels);

        h.Gateway.Heartbeat = ActivityCallOutcome.Ok;
        await h.StartAsync(Attempt(2, "second attempt"));
        Assert.Equal([$"directive:{TaskId}→#1"], agent.Log);

        h.Restart();
        h.Relay.Ready = true;
        await h.TickAsync();

        Assert.Equal([$"directive:{TaskId}→#1", $"cancel:{TaskId}→#1", $"directive:{TaskId}→#2"], agent.Log);
        Assert.Equal((Agent, TaskId), Assert.Single(h.Relay.Cancels));
        var successor = Assert.Single(await h.PendingAsync());
        Assert.Equal("waiting", successor.State);
        Assert.True(agent.Serving("second attempt")!.Running);

        // Sent once: later passes owe nothing.
        await h.TickAsync();
        Assert.Single(h.Relay.Cancels);
    }

    /// <summary>
    /// The same order holds when the predecessor's first directive is itself still on its way:
    /// the cancel carried for it does not overtake it (it would find nothing to stop, and the
    /// directive would then start work nobody is waiting for), and the successor's directive
    /// comes last.
    /// </summary>
    [Fact]
    public async Task ACancelCarriedForAPredecessor_WaitsForThePredecessorsOwnDirective()
    {
        using var h = new DurableHarness();
        var agent = new TaskIdOnlyReceiver();
        h.Relay.Receiver = agent;
        var firstDirectiveLands = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Relay.BeforeSend = (kind, record, _) =>
            kind == "publish" && record.CurrentInstruction == "first attempt" ? firstDirectiveLands.Task : Task.CompletedTask;

        var first = h.Service.StartDelegationAsync(Attempt(1, "first attempt"), default);
        await DurableHarness.WaitForAsync(() => h.Relay.Started.Contains($"publish:{TaskId}"), "the first directive was started");

        h.Gateway.Heartbeat = ActivityCallOutcome.CancelRequested;
        await h.Service.TickAsync(default).WaitAsync(Promptly);
        h.Gateway.Heartbeat = ActivityCallOutcome.Ok;
        await h.Service.StartDelegationAsync(Attempt(2, "second attempt"), default).WaitAsync(Promptly);
        await h.Service.TickAsync(default).WaitAsync(Promptly);
        await h.Service.TickAsync(default).WaitAsync(Promptly);

        Assert.Empty(agent.Log);
        Assert.Equal([$"publish:{TaskId}"], h.Relay.Started);

        firstDirectiveLands.SetResult();
        await first.WaitAsync(Promptly);
        await DurableHarness.WaitForAsync(() => agent.Log.Count == 3, "the cancel and the successor's directive followed");
        await h.SettleAsync();

        Assert.Equal([$"directive:{TaskId}→#1", $"cancel:{TaskId}→#1", $"directive:{TaskId}→#2"], agent.Log);
        Assert.True(agent.Serving("second attempt")!.Running);
    }

    // ── nothing stale is sent ─────────────────────────────────────────────────

    /// <summary>
    /// Five re-sends are due and four calls is all the lane makes. The fifth delegation is
    /// cancelled while it waits for room. When room opens its directive is not sent: it would
    /// tell the agent to start work that has just been cancelled. Its cancel is.
    /// </summary>
    [Fact]
    public async Task ADirectiveThatWasWaitingForRoom_IsNotSentOnceItsRecordIsCancelled()
    {
        using var h = new DurableHarness();
        foreach (var name in new[] { "a", "b", "c", "d", "z-waiting" })
            await h.StartAsync(DurableHarness.Start($"wf-1/{name}", budgetSeconds: 3600));

        var roomOpens = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Relay.BeforeSend = (kind, _, _) => kind == "publish" ? roomOpens.Task : Task.CompletedTask;
        h.Clock.Advance(TimeSpan.FromMinutes(6));
        await h.Service.TickAsync(default).WaitAsync(Promptly);
        await DurableHarness.WaitForAsync(() => h.Relay.Started.Count == 9, "four of the five re-sends were started");
        Assert.Equal(1, h.Relay.Started.Count(s => s == "publish:wf-1/z-waiting"));

        h.Gateway.HeartbeatFor["wf-1/z-waiting"] = ActivityCallOutcome.CancelRequested;
        await h.Service.TickAsync(default).WaitAsync(Promptly);

        roomOpens.SetResult();
        await DurableHarness.WaitForAsync(
            () => h.Relay.Cancels.Contains((Agent, "wf-1/z-waiting")), "the cancelled delegation's cancel went out");
        await h.SettleAsync();

        Assert.Equal(1, h.Relay.Started.Count(s => s == "publish:wf-1/z-waiting"));
        Assert.Equal(1, h.Relay.Published.Count(p => p.TaskId == "wf-1/z-waiting"));
        Assert.DoesNotContain(await h.PendingAsync(), p => p.TaskId == "wf-1/z-waiting");
        Assert.Equal(9, h.Relay.Published.Count);
    }

    // ── the bound is on calls, not on places ──────────────────────────────────

    /// <summary>
    /// Twelve re-sends are due and the relay returns from none of them, whatever it is told. Four
    /// calls are made and no more, however long they hang and however many passes go by; every
    /// delegation is heartbeated on every pass; and the eight that could not be sent are still
    /// owed, in the file, where a new process finds them.
    /// </summary>
    [Fact]
    public async Task HungCalls_NeverExceedTheCap_AndWhatCouldNotBeSentStaysOwed()
    {
        using var h = new DurableHarness();
        SetRelaySendTimeout(h.Service, TimeSpan.FromMilliseconds(40));
        for (var i = 0; i < 12; i++)
            await h.StartAsync(DurableHarness.Start($"wf-1/task-{i:00}", budgetSeconds: 3600));

        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Relay.BeforeSend = (_, _, _) => never.Task;
        h.Clock.Advance(TimeSpan.FromMinutes(6));

        for (var pass = 1; pass <= 8; pass++)
        {
            await h.Service.TickAsync(default).WaitAsync(Promptly);
            Assert.Equal(pass * 12, h.Gateway.Count("heartbeat"));
            await Task.Delay(60);
        }

        // Eight passes over half a second: every call is many times past its time.
        Assert.Equal(4, h.Relay.MostInProgress);
        Assert.Equal(12 + 4, h.Relay.Started.Count);
        Assert.Equal(12, (await h.PendingAsync()).Count);

        // A new process, and a relay that answers: all twelve go out.
        h.Relay.BeforeSend = null;
        h.Restart();
        await h.TickAsync();
        await DurableHarness.WaitForAsync(() => h.Relay.Published.Count == 24, "the twelve re-sends went out");
        Assert.Equal(12, (await h.PendingAsync()).Count);
    }
}
