using System.Text.RegularExpressions;
using Fleet.Temporal.Activities;
using Fleet.Temporal.Configuration;
using Fleet.Temporal.Engine;
using Fleet.Temporal.Models;
using Fleet.Temporal.Services;
using Fleet.Temporal.Tests.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RabbitMQ.Client;
using Temporalio.Activities;
using Temporalio.Api.Common.V1;
using Temporalio.Converters;
using Temporalio.Exceptions;
using Temporalio.Testing;

namespace Fleet.Temporal.Tests.Services.DurableDelegation;

/// <summary>
/// <c>noRedelivery</c> on the durable path: at most one attempt is ever made to publish the
/// directive of an opted-in delegation, and none unless the attempt was first marked in the state
/// file. The delegation is started through the real <see cref="DelegateToAgentActivity"/>; the
/// relay and Temporal are the fakes the other durable tests use, the clock is the test's, and a
/// restart is a second service over the same file.
///
/// <para>
/// What is counted is <see cref="FakeRelay.PublishAttempts"/>: every call the service made to
/// publish a directive, delivered or not. The guarantee is about attempts.
/// </para>
///
/// <para>
/// The <c>FieldAbsent_</c> tests are the other half: a delegation that does not opt in is
/// re-sent, continued, recovered after a restart and written to the file exactly as before.
/// </para>
/// </summary>
public sealed class DurableNoRedeliveryTests
{
    private const string Agent = "agent-a";
    private const string TaskId = "wf-1/merge";
    private const int BudgetSeconds = 1800;
    private static readonly AgentTaskResult Done = new("merged", "completed");

    // ── harness ───────────────────────────────────────────────────────────────

    private static (DelegateToAgentActivity Activity, List<string> PublishedByTheActivityItself) Activity(DurableHarness h)
    {
        // The activity's own broker connection. On the durable path nothing may go through it.
        var published = new List<string>();
        var channel = Substitute.For<IChannel>();
        channel.BasicPublishAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(),
                Arg.Any<BasicProperties>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                lock (published) published.Add(System.Text.Encoding.UTF8.GetString(call.ArgAt<ReadOnlyMemory<byte>>(4).Span));
                return ValueTask.CompletedTask;
            });
        var connection = Substitute.For<IConnection>();
        connection.CreateChannelAsync(Arg.Any<CreateChannelOptions?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(channel));
        var connectionFactory = Substitute.For<IConnectionFactory>();
        connectionFactory.CreateConnectionAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(connection));

        var activity = new DelegateToAgentActivity(
            Options.Create(new RabbitMqOptions()),
            Options.Create(new TemporalBridgeOptions { AgentTimeoutSeconds = BudgetSeconds }),
            new TaskCompletionRegistry(NullLogger<TaskCompletionRegistry>.Instance),
            connectionFactory, Substitute.For<IHttpClientFactory>(),
            NullLogger<DelegateToAgentActivity>.Instance, h.Service);
        return (activity, published);
    }

    /// <summary>
    /// Runs the real activity for the delegation and returns what it ended with:
    /// <see cref="CompleteAsyncException"/> when the delegation was handed over, the failure
    /// otherwise. Then lets the relay lane finish whatever the start asked of it.
    /// </summary>
    private static async Task<Exception> StartThroughTheActivityAsync(
        DurableHarness h, bool noRedelivery = true, bool retryOnIncomplete = false, int maxIncompleteRetries = 0)
    {
        var (activity, publishedByTheActivityItself) = Activity(h);
        var env = new ActivityEnvironment { Info = ActivityEnvironment.DefaultInfo with { TaskToken = [1, 2, 3, 4] } };

        var ended = await Assert.ThrowsAnyAsync<Exception>(() => env.RunAsync(() =>
            activity.DelegateToAgentAsync(
                Agent, "merge the change", TaskId,
                retryOnIncomplete: retryOnIncomplete, maxIncompleteRetries: maxIncompleteRetries,
                noRedelivery: noRedelivery)));
        await h.SettleAsync();

        Assert.Empty(publishedByTheActivityItself);
        return ended;
    }

    /// <summary>
    /// Leaves the task id under a hold, so the next delegation started under it is recorded with
    /// its directive owed to the relay lane instead of published by its activity: an earlier
    /// attempt is cancelled, and its cancel ends without the bridge knowing whether it will still
    /// arrive. Returns the publish attempts made so far (the earlier attempt's one).
    /// </summary>
    private static async Task<int> HoldTheTaskIdAsync(DurableHarness h)
    {
        await h.StartAsync(DurableHarness.Start(TaskId, Agent, "an earlier attempt", budgetSeconds: BudgetSeconds));
        h.Gateway.Heartbeat = ActivityCallOutcome.CancelRequested;
        h.Relay.BeforeSend = (kind, _, _) =>
            kind == "cancel" ? Task.FromCanceled(new CancellationToken(canceled: true)) : Task.CompletedTask;
        await h.TickAsync();
        Assert.Empty(await h.PendingAsync());

        h.Gateway.Heartbeat = ActivityCallOutcome.Ok;
        h.Relay.BeforeSend = null;
        lock (h.Gateway.Calls)
        {
            h.Gateway.Calls.Clear();
            h.Gateway.Cancelled.Clear();
        }

        return h.Relay.PublishAttempts;
    }

    /// <summary>The hold a cancel of unknown outcome leaves, and a little more.</summary>
    private static readonly TimeSpan PastTheHold = TimeSpan.FromSeconds(31);

    /// <summary>
    /// Makes every write of the state file fail from now on: a directory stands where the
    /// store's temp file goes. The file itself stays as it was last written.
    /// </summary>
    private static void BreakWrites(DurableHarness h)
    {
        var blocker = h.StateFile + ".tmp";
        Directory.CreateDirectory(blocker);
        File.WriteAllText(Path.Combine(blocker, "in-the-way"), "");
    }

    private static void RepairWrites(DurableHarness h) => Directory.Delete(h.StateFile + ".tmp", recursive: true);

    private static ApplicationFailureException AssertDirectiveNotSent(Exception failure)
    {
        var notSent = Assert.IsType<ApplicationFailureException>(failure);
        Assert.Equal(DurableDelegationService.DirectiveNotSentErrorType, notSent.ErrorType);
        // A retried attempt would be a second publish.
        Assert.True(notSent.NonRetryable);
        Assert.Contains(TaskId, notSent.Message);
        return notSent;
    }

    /// <summary>Passes a minute apart, each followed by everything the relay lane then sends.</summary>
    private static async Task PassesAsync(DurableHarness h, int minutes)
    {
        for (var minute = 0; minute < minutes; minute++)
        {
            h.Clock.Advance(TimeSpan.FromMinutes(1));
            await h.TickAsync();
        }
    }

    // ── one attempt ───────────────────────────────────────────────────────────

    [Fact]
    public async Task NoRedelivery_Durable_LostAnswer_OnePublish()
    {
        using var h = new DurableHarness();

        Assert.IsType<CompleteAsyncException>(await StartThroughTheActivityAsync(h));
        Assert.Equal(1, h.Relay.PublishAttempts);

        // Twelve passes over twelve minutes: past the re-sends at five and at ten.
        await PassesAsync(h, 12);

        Assert.Equal(1, h.Relay.PublishAttempts);
        Assert.Equal(12, h.Gateway.Count("heartbeat"));
        Assert.Equal("waiting", Assert.Single(await h.PendingAsync()).State);

        // The budget runs out as it does for any delegation: the ordinary timeout, one notice.
        h.Clock.Advance(TimeSpan.FromSeconds(BudgetSeconds));
        await h.TickAsync();

        Assert.IsType<TimeoutException>(Assert.Single(h.Gateway.Failed).Failure);
        Assert.Single(h.Relay.TimeoutReports);
        Assert.Equal(1, h.Relay.PublishAttempts);
        Assert.Empty(await h.PendingAsync());
    }

    /// <summary>The control: the same twelve minutes without the flag re-send at five and at ten.</summary>
    [Fact]
    public async Task WithoutNoRedelivery_Durable_TheSameLostAnswer_IsReSent()
    {
        using var h = new DurableHarness();

        Assert.IsType<CompleteAsyncException>(await StartThroughTheActivityAsync(h, noRedelivery: false));
        await PassesAsync(h, 12);

        Assert.Equal(3, h.Relay.PublishAttempts);
    }

    /// <summary>
    /// The marker is in the file before the directive is handed to the relay, on both paths a
    /// first directive can take: the one the activity publishes itself, and the one the lane
    /// publishes because it was held. Read from the file at the moment the relay is called.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoRedelivery_Durable_TheMarkerIsOnDiskBeforeTheDirectiveLeaves(bool held)
    {
        using var h = new DurableHarness();
        if (held) await HoldTheTaskIdAsync(h);

        var onDiskAtPublish = new List<string>();
        h.Relay.BeforeSend = (kind, _, _) =>
        {
            if (kind == "publish") lock (onDiskAtPublish) onDiskAtPublish.Add(File.ReadAllText(h.StateFile));
            return Task.CompletedTask;
        };

        Assert.IsType<CompleteAsyncException>(await StartThroughTheActivityAsync(h));
        if (held)
        {
            // Recorded, unmarked, nothing sent yet.
            Assert.Empty(onDiskAtPublish);
            Assert.Contains("\"PublishOnce\": true", File.ReadAllText(h.StateFile));
            Assert.DoesNotContain("PublishAttempted", File.ReadAllText(h.StateFile));

            h.Clock.Advance(PastTheHold);
            await h.TickAsync();
        }

        var state = Assert.Single(onDiskAtPublish);
        Assert.Contains("\"PublishOnce\": true", state);
        Assert.Contains("\"PublishAttempted\": true", state);
        Assert.Single(h.Relay.Published, p => p.Instruction == "merge the change");

        // And it stays the only one.
        var attempts = h.Relay.PublishAttempts;
        await PassesAsync(h, 12);
        Assert.Equal(attempts, h.Relay.PublishAttempts);
    }

    [Fact]
    public async Task NoRedelivery_Durable_IncompleteAnswer_ZeroContinuationDirectives()
    {
        using var h = new DurableHarness();

        // Retries left on, on purpose: the flag alone has to be enough.
        Assert.IsType<CompleteAsyncException>(
            await StartThroughTheActivityAsync(h, retryOnIncomplete: true, maxIncompleteRetries: 3));

        var outcome = await h.ReplyAsync(TaskId, new AgentTaskResult("half of it", "incomplete"));

        Assert.Equal(DurableReplyOutcome.Completed, outcome);
        Assert.Equal(new AgentTaskResult("half of it", "incomplete"), Assert.Single(h.Gateway.Completed).Result);
        Assert.Equal(1, h.Relay.PublishAttempts);
        Assert.Empty(await h.PendingAsync());
    }

    // ── no confirmed marker, no directive ─────────────────────────────────────

    [Fact]
    public async Task NoRedelivery_Durable_NoStatePath_ZeroPublishes_FailureReturned()
    {
        using var h = new DurableHarness(configure: o => o.StateFile = "");

        AssertDirectiveNotSent(await StartThroughTheActivityAsync(h));

        Assert.Equal(0, h.Relay.PublishAttempts);
        Assert.Empty(await h.PendingAsync());

        await PassesAsync(h, 12);
        Assert.Equal(0, h.Relay.PublishAttempts);
        Assert.Empty(h.Gateway.Calls);
    }

    [Fact]
    public async Task NoRedelivery_Durable_MarkerWriteFails_ZeroPublishes_FailureReturned_AtStart()
    {
        using var h = new DurableHarness();
        BreakWrites(h);

        AssertDirectiveNotSent(await StartThroughTheActivityAsync(h));

        Assert.Equal(0, h.Relay.PublishAttempts);
        Assert.Empty(await h.PendingAsync());
        Assert.False(File.Exists(h.StateFile));

        await PassesAsync(h, 12);
        Assert.Equal(0, h.Relay.PublishAttempts);
        Assert.Empty(h.Gateway.Calls);
    }

    /// <summary>
    /// The directive was held, so its record is in the file unmarked. When the hold is over the
    /// lane has to mark the attempt first, and the file can no longer be written: nothing is sent,
    /// then or later, and the next pass fails the activity.
    /// </summary>
    [Fact]
    public async Task NoRedelivery_Durable_MarkerWriteFails_ZeroPublishes_FailureReturned_InTheLane()
    {
        using var h = new DurableHarness();
        var before = await HoldTheTaskIdAsync(h);
        Assert.IsType<CompleteAsyncException>(await StartThroughTheActivityAsync(h));
        Assert.Equal(before, h.Relay.PublishAttempts);

        BreakWrites(h);
        h.Clock.Advance(PastTheHold);
        await h.TickAsync();

        Assert.Equal(before, h.Relay.PublishAttempts);
        Assert.Empty(h.Gateway.Failed);

        await h.TickAsync();

        AssertDirectiveNotSent(Assert.Single(h.Gateway.Failed).Failure);
        Assert.Empty(await h.PendingAsync());
        Assert.Empty(h.Relay.TimeoutReports);

        // Not when the file can be written again either.
        RepairWrites(h);
        await PassesAsync(h, 12);
        Assert.Equal(before, h.Relay.PublishAttempts);
    }

    [Fact]
    public async Task NoRedelivery_MarkerWriteFails_CallerFails_Restart_ZeroPublishesBeforeAndAfter()
    {
        using var h = new DurableHarness();
        BreakWrites(h);

        AssertDirectiveNotSent(await StartThroughTheActivityAsync(h));
        Assert.Equal(0, h.Relay.PublishAttempts);

        // The bridge comes back with a file it can write.
        RepairWrites(h);
        h.Restart();
        Assert.Empty(await h.PendingAsync());
        await PassesAsync(h, 12);

        Assert.Equal(0, h.Relay.PublishAttempts);
        Assert.Empty(h.Gateway.Calls);
    }

    /// <summary>
    /// The same failure on the lane's path, with the restart in between: the file still holds the
    /// record as it was confirmed at the start, unmarked, and the new process fails it without
    /// sending.
    /// </summary>
    [Fact]
    public async Task NoRedelivery_MarkerWriteFailsInTheLane_Restart_ZeroPublishesBeforeAndAfter()
    {
        using var h = new DurableHarness();
        var before = await HoldTheTaskIdAsync(h);
        Assert.IsType<CompleteAsyncException>(await StartThroughTheActivityAsync(h));

        BreakWrites(h);
        h.Clock.Advance(PastTheHold);
        await h.TickAsync();
        Assert.Equal(before, h.Relay.PublishAttempts);

        RepairWrites(h);
        h.Restart();
        Assert.Single(await h.PendingAsync());
        await PassesAsync(h, 12);

        Assert.Equal(before, h.Relay.PublishAttempts);
        AssertDirectiveNotSent(Assert.Single(h.Gateway.Failed).Failure);
        Assert.Empty(await h.PendingAsync());
    }

    // ── a publish that throws ─────────────────────────────────────────────────

    /// <summary>
    /// The activity's own publish fails: the activity fails with that, as it always did, the
    /// record is gone, and nothing is tried again by a later pass or after a restart.
    /// </summary>
    [Fact]
    public async Task NoRedelivery_Durable_PublishThrows_NotRetried()
    {
        using var h = new DurableHarness();
        h.Relay.PublishFailure = new InvalidOperationException("broker away");

        var failure = await StartThroughTheActivityAsync(h);

        Assert.Equal("broker away", Assert.IsType<InvalidOperationException>(failure).Message);
        Assert.Equal(1, h.Relay.PublishAttempts);
        Assert.Empty(await h.PendingAsync());

        h.Relay.PublishFailure = null;
        await PassesAsync(h, 12);
        h.Restart();
        await PassesAsync(h, 12);

        Assert.Equal(1, h.Relay.PublishAttempts);
        Assert.Empty(h.Relay.Published);
    }

    /// <summary>
    /// The lane's publish of a held directive fails. Whether anything left the bridge is not
    /// known, so it is not tried again: the record waits for an answer, a cancel or its budget,
    /// and an answer that does arrive completes it.
    /// </summary>
    [Fact]
    public async Task NoRedelivery_Durable_PublishThrowsInTheLane_NotRetried()
    {
        using var h = new DurableHarness();
        var before = await HoldTheTaskIdAsync(h);
        Assert.IsType<CompleteAsyncException>(await StartThroughTheActivityAsync(h));

        h.Relay.PublishFailure = new InvalidOperationException("broker away");
        h.Clock.Advance(PastTheHold);
        await h.TickAsync();
        Assert.Equal(before + 1, h.Relay.PublishAttempts);

        // The broker is back, and stays back for twelve minutes of passes.
        h.Relay.PublishFailure = null;
        await PassesAsync(h, 12);

        Assert.Equal(before + 1, h.Relay.PublishAttempts);
        Assert.Empty(h.Gateway.Failed);
        Assert.Equal("waiting", Assert.Single(await h.PendingAsync()).State);

        Assert.Equal(DurableReplyOutcome.Completed, await h.ReplyAsync(TaskId, Done));
        Assert.Equal((TaskId, Done), Assert.Single(h.Gateway.Completed));
    }

    // ── after a restart ───────────────────────────────────────────────────────

    /// <summary>
    /// The bridge stopped while the directive was still held: recorded, never attempted. The new
    /// process does not send it. There is no answer to wait for, so the activity is failed at
    /// once and the workflow can go on to read back what state things are in.
    /// </summary>
    [Fact]
    public async Task NoRedelivery_RestoredUnattemptedRecord_NoDirective_FailureForReadback()
    {
        using var h = new DurableHarness();
        var before = await HoldTheTaskIdAsync(h);
        Assert.IsType<CompleteAsyncException>(await StartThroughTheActivityAsync(h));
        Assert.Equal(before, h.Relay.PublishAttempts);

        h.Restart();
        Assert.Single(await h.PendingAsync());
        h.Clock.Advance(PastTheHold);
        await h.TickAsync();

        AssertDirectiveNotSent(Assert.Single(h.Gateway.Failed).Failure);
        Assert.Equal(before, h.Relay.PublishAttempts);
        Assert.Empty(await h.PendingAsync());
        // Not a timeout: the escalation target is not told that an agent did not respond.
        Assert.Empty(h.Relay.TimeoutReports);
        Assert.Empty(h.Relay.Cancels);

        await PassesAsync(h, 12);
        Assert.Equal(before, h.Relay.PublishAttempts);
    }

    /// <summary>
    /// The relay lane can run in a new process before its first pass has: another delegation
    /// starting asks for a relay pass, and so does a reply. It finds the restored record with a
    /// directive it never sent and a hold that is over, and must leave it for the pass to fail.
    /// </summary>
    [Fact]
    public async Task NoRedelivery_RestoredUnattemptedRecord_TheLaneRunsBeforeTheFirstPass_NoDirective()
    {
        using var h = new DurableHarness();
        var before = await HoldTheTaskIdAsync(h);
        Assert.IsType<CompleteAsyncException>(await StartThroughTheActivityAsync(h));

        h.Restart();
        h.Clock.Advance(PastTheHold);
        await h.StartAsync(DurableHarness.Start("wf-2/other", Agent, "something else", budgetSeconds: BudgetSeconds));

        // The other delegation's directive, and only that.
        Assert.Equal(before + 1, h.Relay.PublishAttempts);
        Assert.DoesNotContain(h.Relay.Published, p => p.Instruction == "merge the change");

        await h.TickAsync();

        AssertDirectiveNotSent(Assert.Single(h.Gateway.Failed).Failure);
        Assert.Equal(before + 1, h.Relay.PublishAttempts);
        Assert.Equal("wf-2/other", Assert.Single(await h.PendingAsync()).TaskId);
    }

    public enum Ending { Answer, Cancel, Budget }

    /// <summary>
    /// The bridge stopped after the attempt. The new process never sends the directive again, not
    /// at five minutes and not at ten, and everything else is what it is for any restored
    /// delegation: the agent's answer completes the activity, a workflow cancel reaches the agent
    /// and is reported, and a spent budget is a timeout with its one notice.
    /// </summary>
    [Theory]
    [InlineData(Ending.Answer)]
    [InlineData(Ending.Cancel)]
    [InlineData(Ending.Budget)]
    public async Task NoRedelivery_RestoredAttemptedRecord_NoDirective_AnswerCancelCompletionAsToday(Ending ending)
    {
        using var h = new DurableHarness();
        Assert.IsType<CompleteAsyncException>(await StartThroughTheActivityAsync(h));
        Assert.Equal(1, h.Relay.PublishAttempts);

        h.Restart();
        await PassesAsync(h, 12);

        Assert.Equal(1, h.Relay.PublishAttempts);
        Assert.Equal(12, h.Gateway.Count("heartbeat"));
        Assert.Empty(h.Gateway.Failed);

        switch (ending)
        {
            case Ending.Answer:
                Assert.Equal(DurableReplyOutcome.Completed, await h.ReplyAsync(TaskId, Done));
                Assert.Equal((TaskId, Done), Assert.Single(h.Gateway.Completed));
                break;

            case Ending.Cancel:
                h.Gateway.Heartbeat = ActivityCallOutcome.CancelRequested;
                await h.TickAsync();
                Assert.Equal((Agent, TaskId), Assert.Single(h.Relay.Cancels));
                Assert.Equal(TaskId, Assert.Single(h.Gateway.Cancelled));
                break;

            case Ending.Budget:
                h.Clock.Advance(TimeSpan.FromSeconds(BudgetSeconds));
                await h.TickAsync();
                Assert.IsType<TimeoutException>(Assert.Single(h.Gateway.Failed).Failure);
                Assert.Single(h.Relay.TimeoutReports);
                break;
        }

        Assert.Equal(1, h.Relay.PublishAttempts);
        Assert.Empty(await h.PendingAsync());
    }

    /// <summary>
    /// The bridge died between writing the marker and handing the directive to the relay. The
    /// file says an attempt was made and that nothing was published. A record without the marker
    /// in that state is sent by the first pass after the restart; this one is not.
    /// </summary>
    [Fact]
    public async Task NoRedelivery_CrashBetweenTheMarkerAndThePublish_Restart_NoDirective()
    {
        using var h = new DurableHarness();
        Assert.IsType<CompleteAsyncException>(await StartThroughTheActivityAsync(h));

        // Rewind the file to the moment the marker was written.
        var json = File.ReadAllText(h.StateFile);
        var published = Regex.Match(json, "\"LastPublishedAt\": \"[^\"]+\"").Value;
        File.WriteAllText(h.StateFile, json.Replace(published, "\"LastPublishedAt\": \"0001-01-01T00:00:00+00:00\""));
        Assert.Contains("\"PublishAttempted\": true", File.ReadAllText(h.StateFile));

        h.Restart();
        await PassesAsync(h, 12);

        Assert.Equal(1, h.Relay.PublishAttempts);
        Assert.Equal("waiting", Assert.Single(await h.PendingAsync()).State);
        Assert.Empty(h.Gateway.Failed);
    }

    // ── a delegation that does not opt in ─────────────────────────────────────

    /// <summary>
    /// A state file as the bridge wrote it before the field existed: one delegation recorded and
    /// never published. The first pass after the restart sends it, it is re-sent five minutes
    /// later, and an incomplete answer is continued.
    /// </summary>
    [Fact]
    public async Task FieldAbsent_RestoredRecord_LegacyRecoveryUnchanged()
    {
        using var h = new DurableHarness();
        File.WriteAllText(h.StateFile, StateFileBeforeTheField.Replace(
            "\"LastPublishedAt\": \"2026-01-01T00:00:00+00:00\"", "\"LastPublishedAt\": \"0001-01-01T00:00:00+00:00\""));

        h.Restart();
        await h.TickAsync();

        Assert.Equal(("reviewer-one", "wf-1/review", "review the change", "owner/name"), Assert.Single(h.Relay.Published));

        h.Clock.Advance(TimeSpan.FromMinutes(5));
        await h.TickAsync();
        Assert.Equal(2, h.Relay.Published.Count);

        var outcome = await h.Service.TryHandleReplyAsync(
            "wf-1/review", new AgentTaskResult("part one", "incomplete"), default, FixedExecutionId);
        await h.SettleAsync();

        Assert.Equal(DurableReplyOutcome.Continued, outcome);
        Assert.Equal("wf-1/review/incomplete-retry-1", h.Relay.Published[2].TaskId);
        Assert.Empty(h.Gateway.Failed);
    }

    /// <summary>
    /// No state file configured and no flag: the delegation lives in memory and still works as it
    /// did. Published at the start, re-sent at five minutes, continued, completed.
    /// </summary>
    [Fact]
    public async Task FieldAbsent_MemoryOnlyStore_LegacyBehaviourUnchanged()
    {
        using var h = new DurableHarness(configure: o => o.StateFile = "");

        Assert.IsType<CompleteAsyncException>(
            await StartThroughTheActivityAsync(h, noRedelivery: false, retryOnIncomplete: true, maxIncompleteRetries: 3));

        Assert.Equal(1, h.Relay.PublishAttempts);
        Assert.False((await h.Service.SnapshotAsync()).Durable);

        h.Clock.Advance(TimeSpan.FromMinutes(5));
        await h.TickAsync();
        Assert.Equal(2, h.Relay.PublishAttempts);

        Assert.Equal(DurableReplyOutcome.Continued, await h.ReplyAsync(TaskId, new AgentTaskResult("part one", "incomplete")));
        Assert.Equal(TaskId + "/incomplete-retry-1", h.Relay.Published[2].TaskId);

        Assert.Equal(
            DurableReplyOutcome.Completed,
            await h.ReplyAsync(TaskId + "/incomplete-retry-1", new AgentTaskResult("part two", "completed")));
        Assert.Equal(new AgentTaskResult("part one\npart two", "completed"), Assert.Single(h.Gateway.Completed).Result);
    }

    /// <summary>
    /// Two things a delegation without the field must leave exactly as they were.
    ///
    /// <para>
    /// The state file: a record written by the current service is compared, byte for byte, with
    /// <see cref="StateFileBeforeTheField"/>, which is what the service wrote for the same
    /// delegation before the field existed. The execution id is minted at random and is replaced
    /// by a fixed one before comparing; nothing else is touched.
    /// </para>
    ///
    /// <para>
    /// The activity input: the engine schedules five arguments for a step without a repo and
    /// seven for one with, payload for payload what the recorded pre-change history holds
    /// (<c>Fixtures/uwe-delegate-5arg-history.json</c>).
    /// </para>
    /// </summary>
    [Fact]
    public async Task FieldAbsent_ActivityInputAndStateFileBytesUnchanged()
    {
        using var h = new DurableHarness();
        await h.StartAsync(DurableHarness.Start(repo: "owner/name"));

        var written = Regex.Replace(
            File.ReadAllText(h.StateFile), "\"ExecutionId\": \"[0-9a-f]{32}\"", $"\"ExecutionId\": \"{FixedExecutionId}\"");
        Assert.Equal(StateFileBeforeTheField, written);

        var recorded = DelegateHistoryScenario.DelegateInputs(Temporalio.Common.WorkflowHistory.FromJson(
            DelegateHistoryScenario.WorkflowId,
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, DelegateHistoryScenario.FixturePath))));

        var withoutRepo = DelegateHistoryScenario.DelegateInputs(await DelegateHistoryScenario.RunAsync(
            DelegateHistoryScenario.Definition(), DelegateHistoryScenario.Input()));
        Assert.Equal([5, 5, 5, 5], withoutRepo.Select(p => p.Payloads_.Count).ToArray());
        Assert.Equal(recorded, withoutRepo);

        var baseline = (SequenceStep)DelegateHistoryScenario.Definition().Root;
        var withRepo = DelegateHistoryScenario.DelegateInputs(await DelegateHistoryScenario.RunAsync(
            DelegateHistoryScenario.Definition(
                (DelegateStep)baseline.Steps[0] with { Repo = "{{input.Repo}}" },
                (DelegateWithEscalationStep)baseline.Steps[1] with { Repo = "{{input.Repo}}" }),
            DelegateHistoryScenario.Input(new { Task = "the thing", Repo = "org/app" })));
        Assert.Equal([7, 7, 5, 7], withRepo.Select(p => p.Payloads_.Count).ToArray());
        for (var i = 0; i < withRepo.Count; i++)
            Assert.Equal(recorded[i].Payloads_, withRepo[i].Payloads_.Take(5));
        foreach (var attempt in new[] { withRepo[0], withRepo[1], withRepo[3] })
        {
            Assert.Equal(0, Decode<int>(attempt.Payloads_[5]));
            Assert.Equal("org/app", Decode<string>(attempt.Payloads_[6]));
        }
    }

    private static T Decode<T>(Payload payload) => DataConverter.Default.PayloadConverter.ToValue<T>(payload);

    private const string FixedExecutionId = "0123456789abcdef0123456789abcdef";

    /// <summary>
    /// The state file the service wrote for <c>DurableHarness.Start(repo: "owner/name")</c> before
    /// <c>PublishOnce</c> and <c>PublishAttempted</c> existed, captured from that code with the
    /// execution id replaced by <see cref="FixedExecutionId"/>. It must never be regenerated from
    /// the current code: it would then compare the service with itself.
    /// </summary>
    private const string StateFileBeforeTheField =
        """
        [
          {
            "TaskId": "wf-1/review",
            "CurrentTaskId": "wf-1/review",
            "Namespace": "fleet",
            "WorkflowId": "wf-1",
            "RunId": "run-1",
            "WorkflowType": "example-workflow",
            "ActivityId": "7",
            "Attempt": 1,
            "TaskToken": "AQIDBA==",
            "Agent": "reviewer-one",
            "ExecutionId": "0123456789abcdef0123456789abcdef",
            "CurrentInstruction": "review the change",
            "Repo": "owner/name",
            "BudgetSeconds": 600,
            "RetryOnIncomplete": true,
            "MaxIncompleteRetries": 3,
            "IncompleteAttempt": 0,
            "AccumulatedText": "",
            "CreatedAt": "2026-01-01T00:00:00+00:00",
            "WaitStartedAt": "2026-01-01T00:00:00+00:00",
            "LastPublishedAt": "2026-01-01T00:00:00+00:00",
            "ResultStatus": null,
            "ResultText": null,
            "TimeoutMessage": null,
            "TimeoutReported": false,
            "CancelRequested": false,
            "CancelSent": false,
            "PriorCancels": [],
            "PublishNotBefore": "0001-01-01T00:00:00+00:00",
            "TemporalSettled": false
          }
        ]
        """;
}
