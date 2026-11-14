using Fleet.Temporal.Models;
using Fleet.Temporal.Services;

namespace Fleet.Temporal.Tests.Services.DurableDelegation;

/// <summary>
/// A bridge that has just started heartbeats its pending activities before it has fetched its
/// configuration from the orchestrator: that fetch retries for up to five minutes and a heartbeat
/// timeout is ninety seconds. Until the fetch has succeeded the bridge does not know who the
/// escalation target is or which chat its directives belong to, so nothing is SENT before then.
///
/// <para>
/// These are regressions for the service that sent as soon as it started: a record that had run
/// out of budget while the bridge was down had its one timeout notice published to an empty
/// escalation target, and was then removed. They use only what the service had then.
/// </para>
/// </summary>
public sealed class DurableDelegationStartUpTests
{
    /// <summary>
    /// An expired record is found at start-up. Temporal is told at once. The notice is not sent
    /// while the configuration is missing, it is not lost to a second restart, and when the
    /// configuration arrives it is sent once and the record goes.
    /// </summary>
    [Fact]
    public async Task AnExpiredRecordFoundAtStartUp_KeepsItsNoticeUntilTheEscalationTargetIsKnown()
    {
        using var h = new DurableHarness();
        await h.StartAsync(DurableHarness.Start(budgetSeconds: 60));
        h.Clock.Advance(TimeSpan.FromMinutes(2));

        // The bridge comes back up; the orchestrator has not been reached yet.
        h.Relay.Ready = false;
        h.Restart();
        await h.TickAsync();

        var failed = Assert.Single(h.Gateway.Failed);
        Assert.IsType<TimeoutException>(failed.Failure);
        Assert.Empty(h.Relay.TimeoutReports);
        Assert.Equal("timed-out", Assert.Single(await h.PendingAsync()).State);

        // Still owed after another restart, and after passes that have nothing left to tell Temporal.
        h.Restart();
        await h.TickAsync();
        await h.TickAsync();
        Assert.Empty(h.Relay.TimeoutReports);
        Assert.Single(h.Gateway.Failed);
        Assert.Equal("timed-out", Assert.Single(await h.PendingAsync()).State);

        h.Relay.Ready = true;
        await h.TickAsync();

        var report = Assert.Single(h.Relay.TimeoutReports);
        Assert.Equal("wf-1/review", report.TaskId);
        Assert.Equal(failed.Failure.Message, report.Detail);
        Assert.Empty(await h.PendingAsync());

        await h.TickAsync();
        Assert.Single(h.Relay.TimeoutReports);
    }

    /// <summary>
    /// The same holds for everything else the relay sends. Heartbeats are not held: the pass goes
    /// on, and what it finds owed is sent when the configuration is there.
    /// </summary>
    [Fact]
    public async Task NothingIsSentBeforeTheConfigurationIsLoaded_AndHeartbeatsAreNotHeld()
    {
        using var h = new DurableHarness();
        await h.StartAsync(DurableHarness.Start("wf-1/cancelled", budgetSeconds: 3600));
        await h.StartAsync(DurableHarness.Start("wf-1/continued", budgetSeconds: 3600));
        await h.StartAsync(DurableHarness.Start("wf-1/waiting", budgetSeconds: 3600));
        await h.ReplyAsync("wf-1/continued", new AgentTaskResult("part one", "incomplete"));
        Assert.Equal(4, h.Relay.Started.Count);
        h.Clock.Advance(TimeSpan.FromMinutes(6));

        h.Relay.Ready = false;
        h.Restart();
        h.Gateway.HeartbeatFor["wf-1/cancelled"] = ActivityCallOutcome.CancelRequested;
        await h.TickAsync();
        await h.TickAsync();

        Assert.Equal(2, h.Gateway.HeartbeatTimes.Count(t => t.TaskId == "wf-1/waiting"));
        Assert.Equal(2, h.Gateway.HeartbeatTimes.Count(t => t.TaskId == "wf-1/continued"));
        Assert.Equal("wf-1/cancelled", Assert.Single(h.Gateway.Cancelled));
        Assert.Equal(4, h.Relay.Started.Count);
        Assert.Equal(3, (await h.PendingAsync()).Count);

        h.Relay.Ready = true;
        await h.TickAsync();

        Assert.Equal(
            ["cancel:wf-1/cancelled", "publish:wf-1/continued/incomplete-retry-1", "publish:wf-1/waiting"],
            h.Relay.Started.Skip(4).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(["wf-1/continued", "wf-1/waiting"], (await h.PendingAsync()).Select(p => p.TaskId).ToArray());
    }
}
