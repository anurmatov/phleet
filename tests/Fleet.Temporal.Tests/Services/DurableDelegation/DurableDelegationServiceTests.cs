using Fleet.Temporal.Models;
using Fleet.Temporal.Services;

namespace Fleet.Temporal.Tests.Services.DurableDelegation;

/// <summary>
/// What <see cref="DurableDelegationService"/> does with a pending delegation: everything the
/// activity's wait loop did (heartbeat, re-send, budget, continuation of an incomplete answer,
/// cancel), decided from the record alone and therefore the same after a restart. No Temporal
/// server and no broker: both are fakes, and the clock is the test's.
/// </summary>
public sealed class DurableDelegationServiceTests
{
    private static readonly AgentTaskResult Done = new("all done", "completed");

    // ── start ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AStartedDelegation_IsRecordedAndPublished()
    {
        using var h = new DurableHarness();

        await h.Service.StartDelegationAsync(DurableHarness.Start(repo: "owner/name"), default);

        var published = Assert.Single(h.Relay.Published);
        Assert.Equal(("reviewer-one", "wf-1/review", "review the change", "owner/name"), published);
        var pending = Assert.Single(await h.PendingAsync());
        Assert.Equal("wf-1/review", pending.TaskId);
        Assert.Equal("waiting", pending.State);
        Assert.Equal(h.Clock.GetUtcNow().AddSeconds(600), pending.Deadline);
        Assert.True(File.Exists(h.StateFile));
    }

    /// <summary>The activity failed when it could not publish; so does this, leaving nothing behind.</summary>
    [Fact]
    public async Task ADirectiveThatCannotBePublished_FailsTheStart_AndLeavesNoRecord()
    {
        using var h = new DurableHarness();
        h.Relay.PublishFailure = new InvalidOperationException("broker away");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Service.StartDelegationAsync(DurableHarness.Start(), default));

        Assert.Equal("broker away", thrown.Message);
        Assert.Empty(await h.PendingAsync());
        Assert.Empty(h.Restart().SnapshotAsync().Result.Pending);
    }

    // ── reply ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ACompletedReply_CompletesTheActivityVerbatim_AndRemovesTheRecord()
    {
        using var h = new DurableHarness();
        await h.Service.StartDelegationAsync(DurableHarness.Start(), default);

        var outcome = await h.Service.TryHandleReplyAsync("wf-1/review", Done, default);

        Assert.Equal(DurableReplyOutcome.Completed, outcome);
        Assert.Equal(("wf-1/review", Done), Assert.Single(h.Gateway.Completed));
        Assert.Empty(await h.PendingAsync());
    }

    [Fact]
    public async Task AReplyForAnUnknownTask_IsNotPending_SoTheCallerKeepsItsOldHandling()
    {
        using var h = new DurableHarness();
        await h.Service.StartDelegationAsync(DurableHarness.Start(), default);

        Assert.Equal(DurableReplyOutcome.NotPending, await h.Service.TryHandleReplyAsync("wf-2/other", Done, default));
        Assert.Equal(DurableReplyOutcome.NotPending, await h.Service.TryHandleReplyAsync("wf-1/review/timeout-report", Done, default));
        Assert.Empty(h.Gateway.Calls);
        Assert.Single(await h.PendingAsync());
    }

    /// <summary>Idempotent: the second copy of a reply finds no record and changes nothing.</summary>
    [Fact]
    public async Task ADuplicateReply_AfterCompletion_IsNotPending()
    {
        using var h = new DurableHarness();
        await h.Service.StartDelegationAsync(DurableHarness.Start(), default);
        await h.Service.TryHandleReplyAsync("wf-1/review", Done, default);

        Assert.Equal(DurableReplyOutcome.NotPending, await h.Service.TryHandleReplyAsync("wf-1/review", Done, default));
        Assert.Single(h.Gateway.Completed);
    }

    [Fact]
    public async Task ASwitchedOffService_HandlesNoReply_AndRecordsNothing()
    {
        using var h = new DurableHarness(enabled: false);

        Assert.False(h.Service.Enabled);
        Assert.Equal(DurableReplyOutcome.NotPending, await h.Service.TryHandleReplyAsync("wf-1/review", Done, default));
        await h.Service.TickAsync(default);
        Assert.Empty(h.Gateway.Calls);
        Assert.False(File.Exists(h.StateFile));
        var snapshot = await h.Service.SnapshotAsync();
        Assert.False(snapshot.Enabled);
        Assert.False(snapshot.Durable);
    }

    // ── incomplete answers ────────────────────────────────────────────────────

    [Fact]
    public async Task AnIncompleteReply_IsContinued_AndTheTextIsAccumulatedAsTheActivityDid()
    {
        using var h = new DurableHarness();
        await h.Service.StartDelegationAsync(DurableHarness.Start(repo: "owner/name"), default);

        Assert.Equal(
            DurableReplyOutcome.Continued,
            await h.Service.TryHandleReplyAsync("wf-1/review", new AgentTaskResult("part one", "incomplete"), default));

        var continuation = h.Relay.Published[1];
        Assert.Equal("wf-1/review/incomplete-retry-1", continuation.TaskId);
        Assert.Equal("owner/name", continuation.Repo);
        Assert.Equal(
            "continue where you left off — your previous response was truncated due to context limits.\n\n" +
            "Previous incomplete response:\npart one\n\n" +
            "Resume and complete the task.",
            continuation.Instruction);
        Assert.Empty(h.Gateway.Calls);

        Assert.Equal(
            DurableReplyOutcome.Completed,
            await h.Service.TryHandleReplyAsync("wf-1/review/incomplete-retry-1", new AgentTaskResult("part two", "completed"), default));

        var completed = Assert.Single(h.Gateway.Completed);
        Assert.Equal(new AgentTaskResult("part one\npart two", "completed"), completed.Result);
    }

    [Fact]
    public async Task AnAnswerStillIncompleteAfterEveryRetry_ReturnsWhatItHas()
    {
        using var h = new DurableHarness();
        await h.Service.StartDelegationAsync(DurableHarness.Start(maxIncompleteRetries: 2), default);

        await h.Service.TryHandleReplyAsync("wf-1/review", new AgentTaskResult("a", "incomplete"), default);
        await h.Service.TryHandleReplyAsync("wf-1/review/incomplete-retry-1", new AgentTaskResult("b", "incomplete"), default);
        var last = await h.Service.TryHandleReplyAsync("wf-1/review/incomplete-retry-2", new AgentTaskResult("c", "incomplete"), default);

        Assert.Equal(DurableReplyOutcome.Completed, last);
        Assert.Equal(3, h.Relay.Published.Count);
        Assert.Equal(new AgentTaskResult("a\nb\nc", "incomplete"), Assert.Single(h.Gateway.Completed).Result);
    }

    [Fact]
    public async Task RetryOnIncompleteOff_ReturnsTheIncompleteAnswerUnchanged()
    {
        using var h = new DurableHarness();
        await h.Service.StartDelegationAsync(DurableHarness.Start(retryOnIncomplete: false), default);

        await h.Service.TryHandleReplyAsync("wf-1/review", new AgentTaskResult("half", "incomplete"), default);

        Assert.Single(h.Relay.Published);
        Assert.Equal(new AgentTaskResult("half", "incomplete"), Assert.Single(h.Gateway.Completed).Result);
    }

    /// <summary>Each continuation gets the whole budget again, as each wait in the activity did.</summary>
    [Fact]
    public async Task AContinuation_StartsANewBudget()
    {
        using var h = new DurableHarness();
        await h.Service.StartDelegationAsync(DurableHarness.Start(budgetSeconds: 600), default);
        h.Clock.Advance(TimeSpan.FromSeconds(500));

        await h.Service.TryHandleReplyAsync("wf-1/review", new AgentTaskResult("part", "incomplete"), default);
        h.Clock.Advance(TimeSpan.FromSeconds(500));
        await h.Service.TickAsync(default);

        Assert.Empty(h.Gateway.Failed);
        Assert.Equal(1, h.Gateway.Count("heartbeat"));
    }

    [Fact]
    public async Task AReplyToADirectiveTheDelegationHasMovedPast_IsIgnored()
    {
        using var h = new DurableHarness();
        await h.Service.StartDelegationAsync(DurableHarness.Start(), default);
        await h.Service.TryHandleReplyAsync("wf-1/review", new AgentTaskResult("part", "incomplete"), default);

        // The agent's first reply, delivered twice.
        Assert.Equal(
            DurableReplyOutcome.Ignored,
            await h.Service.TryHandleReplyAsync("wf-1/review", new AgentTaskResult("part", "incomplete"), default));

        Assert.Equal(2, h.Relay.Published.Count);
        Assert.Empty(h.Gateway.Calls);
        Assert.Equal("wf-1/review/incomplete-retry-1", Assert.Single(await h.PendingAsync()).CurrentTaskId);
    }

    /// <summary>
    /// A continuation that cannot be published is kept, not turned into a failed activity: the
    /// partial answer is in the record and the next pass sends the continuation.
    /// </summary>
    [Fact]
    public async Task AContinuationThatCannotBePublished_IsSentByALaterPass()
    {
        using var h = new DurableHarness();
        await h.Service.StartDelegationAsync(DurableHarness.Start(), default);
        h.Relay.PublishFailure = new InvalidOperationException("broker away");

        Assert.Equal(
            DurableReplyOutcome.Continued,
            await h.Service.TryHandleReplyAsync("wf-1/review", new AgentTaskResult("part", "incomplete"), default));
        Assert.Single(h.Relay.Published);

        h.Relay.PublishFailure = null;
        await h.Service.TickAsync(default);

        Assert.Equal("wf-1/review/incomplete-retry-1", h.Relay.Published[1].TaskId);
        Assert.Empty(h.Gateway.Failed);
    }

    // ── Temporal away ─────────────────────────────────────────────────────────

    /// <summary>
    /// The agent answered while Temporal could not be told. The answer must not be lost: it is in
    /// the record (and so in the file), and a later pass completes the activity with it.
    /// </summary>
    [Fact]
    public async Task AnAnswerTemporalCouldNotBeTold_IsRecorded_AndDeliveredByALaterPass_EvenAfterARestart()
    {
        using var h = new DurableHarness();
        await h.Service.StartDelegationAsync(DurableHarness.Start(), default);
        h.Gateway.Complete = ActivityCallOutcome.Unavailable;

        Assert.Equal(DurableReplyOutcome.Stored, await h.Service.TryHandleReplyAsync("wf-1/review", Done, default));
        Assert.Equal("answer-recorded", Assert.Single(await h.PendingAsync()).State);

        // The redelivered copy of the same reply changes nothing.
        Assert.Equal(DurableReplyOutcome.Ignored, await h.Service.TryHandleReplyAsync("wf-1/review", Done, default));

        var restarted = h.Restart();
        await restarted.TickAsync(default);
        Assert.Single(await h.PendingAsync());

        h.Gateway.Complete = ActivityCallOutcome.Ok;
        await restarted.TickAsync(default);

        Assert.Equal(("wf-1/review", Done), Assert.Single(h.Gateway.Completed));
        Assert.Empty(await h.PendingAsync());
        // Never heartbeated or re-sent in between: the delegation has its outcome.
        Assert.Equal(0, h.Gateway.Count("heartbeat"));
        Assert.Single(h.Relay.Published);
    }

    [Fact]
    public async Task ANamespaceThatIsAway_IsAskedOncePerPass_AndOtherNamespacesAreStillHeartbeated()
    {
        using var h = new DurableHarness();
        await h.Service.StartDelegationAsync(DurableHarness.Start("wf-1/a", @namespace: "one"), default);
        await h.Service.StartDelegationAsync(DurableHarness.Start("wf-1/b", @namespace: "one"), default);
        await h.Service.StartDelegationAsync(DurableHarness.Start("wf-1/c", @namespace: "two"), default);
        h.Gateway.HeartbeatFor["wf-1/a"] = ActivityCallOutcome.Unavailable;

        await h.Service.TickAsync(default);

        Assert.Equal(["heartbeat:wf-1/a", "heartbeat:wf-1/c"], h.Gateway.Calls);
        Assert.Equal(3, (await h.PendingAsync()).Count);
    }

    // ── pass: heartbeat, re-send, budget ──────────────────────────────────────

    [Fact]
    public async Task EveryPass_HeartbeatsEveryPendingDelegation()
    {
        using var h = new DurableHarness();
        await h.Service.StartDelegationAsync(DurableHarness.Start("wf-1/a"), default);
        await h.Service.StartDelegationAsync(DurableHarness.Start("wf-1/b"), default);

        await h.Service.TickAsync(default);
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        await h.Service.TickAsync(default);

        Assert.Equal(["heartbeat:wf-1/a", "heartbeat:wf-1/b", "heartbeat:wf-1/a", "heartbeat:wf-1/b"], h.Gateway.Calls);
    }

    [Fact]
    public async Task TheDirective_IsReSentEveryFiveMinutes_AndNotBefore()
    {
        using var h = new DurableHarness();
        await h.Service.StartDelegationAsync(DurableHarness.Start(budgetSeconds: 3600), default);

        h.Clock.Advance(TimeSpan.FromMinutes(4));
        await h.Service.TickAsync(default);
        Assert.Single(h.Relay.Published);

        h.Clock.Advance(TimeSpan.FromMinutes(1));
        await h.Service.TickAsync(default);
        Assert.Equal(2, h.Relay.Published.Count);
        Assert.Equal(h.Relay.Published[0], h.Relay.Published[1]);

        h.Clock.Advance(TimeSpan.FromMinutes(4));
        await h.Service.TickAsync(default);
        Assert.Equal(2, h.Relay.Published.Count);
    }

    /// <summary>
    /// With the broker away, a pass costs one publish attempt, not one per delegation, and every
    /// delegation is still heartbeated first.
    /// </summary>
    [Fact]
    public async Task ABrokerThatIsAway_CostsAPassOnePublishAttempt_AndNoHeartbeat()
    {
        using var h = new DurableHarness();
        await h.Service.StartDelegationAsync(DurableHarness.Start("wf-1/a", budgetSeconds: 3600), default);
        await h.Service.StartDelegationAsync(DurableHarness.Start("wf-1/b", budgetSeconds: 3600), default);
        await h.Service.StartDelegationAsync(DurableHarness.Start("wf-1/c", budgetSeconds: 3600), default);
        h.Clock.Advance(TimeSpan.FromMinutes(6));
        h.Relay.PublishFailure = new InvalidOperationException("broker away");
        var attemptsBefore = h.Relay.PublishAttempts;

        await h.Service.TickAsync(default);

        Assert.Equal(3, h.Gateway.Count("heartbeat"));
        Assert.Equal(1, h.Relay.PublishAttempts - attemptsBefore);

        h.Relay.PublishFailure = null;
        await h.Service.TickAsync(default);
        Assert.Equal(6, h.Relay.Published.Count);
    }

    [Fact]
    public async Task ABudgetThatRunsOut_FailsTheActivityWithTheMeasuredTime_AndTellsTheEscalationTargetOnce()
    {
        using var h = new DurableHarness();
        await h.Service.StartDelegationAsync(DurableHarness.Start(budgetSeconds: 600), default);

        h.Clock.Advance(TimeSpan.FromSeconds(599));
        await h.Service.TickAsync(default);
        Assert.Empty(h.Gateway.Failed);

        h.Clock.Advance(TimeSpan.FromSeconds(31));
        await h.Service.TickAsync(default);

        var failed = Assert.Single(h.Gateway.Failed);
        var timeout = Assert.IsType<TimeoutException>(failed.Failure);
        Assert.Equal(
            "Agent reviewer-one did not respond — elapsed 10.5m (budget 10.0m, taskId=wf-1/review, workflowId=wf-1)",
            timeout.Message);
        Assert.Equal(("wf-1/review", timeout.Message), Assert.Single(h.Relay.TimeoutReports));
        Assert.Empty(await h.PendingAsync());
    }

    /// <summary>
    /// Temporal away at the moment of the timeout: the elapsed time stays the one measured then,
    /// and the escalation target is not told a second time.
    /// </summary>
    [Fact]
    public async Task ATimeoutTemporalCouldNotBeTold_IsRepeatedWithTheSameMessage_AndOneReport()
    {
        using var h = new DurableHarness();
        await h.Service.StartDelegationAsync(DurableHarness.Start(budgetSeconds: 60), default);
        h.Clock.Advance(TimeSpan.FromSeconds(60));
        h.Gateway.Fail = ActivityCallOutcome.Unavailable;

        await h.Service.TickAsync(default);
        Assert.Equal("timed-out", Assert.Single(await h.PendingAsync()).State);

        // A late answer does not undo a timeout the escalation target has already been told about.
        Assert.Equal(DurableReplyOutcome.Ignored, await h.Service.TryHandleReplyAsync("wf-1/review", Done, default));

        h.Clock.Advance(TimeSpan.FromMinutes(10));
        h.Gateway.Fail = ActivityCallOutcome.Ok;
        await h.Restart().TickAsync(default);

        var failed = Assert.Single(h.Gateway.Failed);
        Assert.Contains("elapsed 1.0m (budget 1.0m", failed.Failure.Message);
        Assert.Single(h.Relay.TimeoutReports);
        Assert.Single(h.Relay.Published);
        Assert.Empty(await h.PendingAsync());
    }

    // ── cancel, and an activity that is gone ──────────────────────────────────

    [Fact]
    public async Task AWorkflowCancel_IsPassedToTheAgent_AndReportedToTemporal()
    {
        using var h = new DurableHarness();
        await h.Service.StartDelegationAsync(DurableHarness.Start(), default);
        h.Gateway.Heartbeat = ActivityCallOutcome.CancelRequested;

        await h.Service.TickAsync(default);

        Assert.Equal(("reviewer-one", "wf-1/review"), Assert.Single(h.Relay.Cancels));
        Assert.Equal("wf-1/review", Assert.Single(h.Gateway.Cancelled));
        Assert.Empty(await h.PendingAsync());
    }

    /// <summary>The cancel goes to the task the agent is actually running, which after a continuation is the retry.</summary>
    [Fact]
    public async Task ACancelDuringAContinuation_NamesTheContinuationTask()
    {
        using var h = new DurableHarness();
        await h.Service.StartDelegationAsync(DurableHarness.Start(), default);
        await h.Service.TryHandleReplyAsync("wf-1/review", new AgentTaskResult("part", "incomplete"), default);
        h.Gateway.Heartbeat = ActivityCallOutcome.CancelRequested;

        await h.Service.TickAsync(default);

        Assert.Equal(("reviewer-one", "wf-1/review/incomplete-retry-1"), Assert.Single(h.Relay.Cancels));
    }

    [Fact]
    public async Task ACancellationTemporalCouldNotBeTold_IsReportedAgain_WithoutCancellingTheAgentTwice()
    {
        using var h = new DurableHarness();
        await h.Service.StartDelegationAsync(DurableHarness.Start(), default);
        h.Gateway.Heartbeat = ActivityCallOutcome.CancelRequested;
        h.Gateway.ReportCancellation = ActivityCallOutcome.Unavailable;

        await h.Service.TickAsync(default);
        Assert.Equal("cancelling", Assert.Single(await h.PendingAsync()).State);

        h.Gateway.ReportCancellation = ActivityCallOutcome.Ok;
        await h.Restart().TickAsync(default);

        Assert.Single(h.Relay.Cancels);
        Assert.Single(h.Gateway.Cancelled);
        Assert.Empty(await h.PendingAsync());
    }

    /// <summary>
    /// The decision for a record whose workflow or activity no longer exists: the agent is told to
    /// stop, the record is dropped, and one line says so with identifiers only.
    /// </summary>
    [Fact]
    public async Task ARecordWhoseActivityIsGone_IsDropped_TheAgentIsToldToStop_AndOneLineSaysSo()
    {
        using var h = new DurableHarness();
        await h.Service.StartDelegationAsync(DurableHarness.Start(), default);
        h.Gateway.Heartbeat = ActivityCallOutcome.Gone;
        h.Log.Lines.Clear();

        await h.Service.TickAsync(default);

        Assert.Empty(await h.PendingAsync());
        Assert.Equal(("reviewer-one", "wf-1/review"), Assert.Single(h.Relay.Cancels));
        Assert.Empty(h.Gateway.Completed);
        var line = Assert.Single(h.Log.Lines);
        Assert.Contains("Dropped pending delegation taskId=wf-1/review (workflow wf-1, activity 7, agent reviewer-one)", line);
        Assert.Empty(h.Restart().SnapshotAsync().Result.Pending);
    }

    [Fact]
    public async Task AnAnswerForAnActivityThatIsGone_DropsTheRecord_AndSettlesTheReply()
    {
        using var h = new DurableHarness();
        await h.Service.StartDelegationAsync(DurableHarness.Start(), default);
        h.Gateway.Complete = ActivityCallOutcome.Gone;

        Assert.Equal(DurableReplyOutcome.Completed, await h.Service.TryHandleReplyAsync("wf-1/review", Done, default));
        Assert.Empty(await h.PendingAsync());
    }

    // ── restart ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The point of the whole thing. A second service over the same file — which is all a
    /// restarted bridge has — heartbeats the delegation, keeps its budget, and completes it when
    /// the agent's reply arrives.
    /// </summary>
    [Fact]
    public async Task AfterARestart_TheDelegationIsHeartbeated_KeepsItsBudget_AndIsCompletedByTheReply()
    {
        using var h = new DurableHarness();
        await h.Service.StartDelegationAsync(DurableHarness.Start(budgetSeconds: 600, repo: "owner/name"), default);
        await h.Service.TryHandleReplyAsync("wf-1/review", new AgentTaskResult("part one", "incomplete"), default);
        h.Clock.Advance(TimeSpan.FromSeconds(100));

        var restarted = h.Restart();

        var resumed = Assert.Single(await h.PendingAsync());
        Assert.Equal("wf-1/review/incomplete-retry-1", resumed.CurrentTaskId);
        Assert.Equal(1, resumed.IncompleteAttempt);
        Assert.Equal(h.Clock.GetUtcNow().AddSeconds(500), resumed.Deadline);

        await restarted.TickAsync(default);
        Assert.Equal(["heartbeat:wf-1/review"], h.Gateway.Calls);

        await restarted.TryHandleReplyAsync("wf-1/review/incomplete-retry-1", new AgentTaskResult("part two", "completed"), default);

        Assert.Equal(new AgentTaskResult("part one\npart two", "completed"), Assert.Single(h.Gateway.Completed).Result);
        Assert.Empty(await h.PendingAsync());
    }

    /// <summary>
    /// The bridge died between recording a delegation and publishing it. The record says it was
    /// never published, so the first pass after the restart sends it.
    /// </summary>
    [Fact]
    public async Task ADirectiveRecordedButNeverPublished_IsSentByTheFirstPassAfterARestart()
    {
        using var h = new DurableHarness();
        await h.Service.StartDelegationAsync(DurableHarness.Start(), default);
        await h.Service.TryHandleReplyAsync("wf-1/review", new AgentTaskResult("part", "incomplete"), default);
        Assert.Equal(2, h.Relay.Published.Count);

        // Rewind the file to the moment before the continuation went out.
        var json = File.ReadAllText(h.StateFile);
        var published = System.Text.RegularExpressions.Regex.Match(json, "\"LastPublishedAt\": \"[^\"]+\"").Value;
        File.WriteAllText(h.StateFile, json.Replace(published, "\"LastPublishedAt\": \"0001-01-01T00:00:00+00:00\""));

        await h.Restart().TickAsync(default);

        Assert.Equal(3, h.Relay.Published.Count);
        Assert.Equal("wf-1/review/incomplete-retry-1", h.Relay.Published[2].TaskId);
    }

    /// <summary>A later attempt of the same activity owns the task id, as in the in-memory registry.</summary>
    [Fact]
    public async Task ASecondStartUnderTheSameTaskId_ReplacesTheRecord()
    {
        using var h = new DurableHarness();
        await h.Service.StartDelegationAsync(DurableHarness.Start(instruction: "first"), default);
        await h.Service.StartDelegationAsync(DurableHarness.Start(instruction: "second") with { Attempt = 2 }, default);

        Assert.Single(await h.PendingAsync());
        Assert.Equal("second", h.Relay.Published[1].Instruction);
    }

    // ── what is never written to a log or the snapshot ────────────────────────

    [Fact]
    public async Task NoInstructionAndNoAnswer_ReachesALogLineOrTheSnapshot()
    {
        const string instruction = "INSTRUCTION-SECRET-7f3a";
        const string answer = "ANSWER-SECRET-91bc";
        using var h = new DurableHarness();
        h.Gateway.Complete = ActivityCallOutcome.Unavailable;

        await h.Service.StartDelegationAsync(DurableHarness.Start(instruction: instruction, budgetSeconds: 60), default);
        await h.Service.TryHandleReplyAsync("wf-1/review", new AgentTaskResult(answer, "incomplete"), default);
        await h.Service.TryHandleReplyAsync("wf-1/review/incomplete-retry-1", new AgentTaskResult(answer, "completed"), default);
        var snapshot = System.Text.Json.JsonSerializer.Serialize(await h.Service.SnapshotAsync());
        h.Clock.Advance(TimeSpan.FromMinutes(6));
        await h.Service.TickAsync(default);

        Assert.DoesNotContain(h.Log.Lines, l => l.Contains(instruction) || l.Contains(answer));
        Assert.DoesNotContain(instruction, snapshot);
        Assert.DoesNotContain(answer, snapshot);
        Assert.DoesNotContain("AQIDBA==", snapshot); // the task token
        // The file is where they are kept, and is the only place.
        Assert.Contains(answer, File.ReadAllText(h.StateFile));
    }

    [Theory]
    [InlineData("wf/step", "wf/step")]
    [InlineData("wf/step/incomplete-retry-2", "wf/step")]
    [InlineData("wf/step~1.0/incomplete-retry-12", "wf/step~1.0")]
    [InlineData("wf/step/timeout-report", "wf/step/timeout-report")]
    [InlineData("wf/step/incomplete-retry-", "wf/step/incomplete-retry-")]
    [InlineData("wf/step/incomplete-retry-x", "wf/step/incomplete-retry-x")]
    public void RootOf_StripsOnlyAContinuationSuffix(string taskId, string expected) =>
        Assert.Equal(expected, DurableDelegationService.RootOf(taskId));
}
