using System.Text.Json;
using Fleet.Temporal.Engine;
using Fleet.Temporal.Models;
using Temporalio.Activities;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.OperatorService.V1;
using Temporalio.Client;
using Temporalio.Testing;
using Temporalio.Worker;

namespace Fleet.Temporal.Tests.Engine;

/// <summary>
/// #280 §10 T1, T2, T5, T6, T8, T9, T21 — the behaviours a DRIVER definition depends on, exercised
/// through a generic driver fixture rather than through engine primitives in isolation.
///
/// <para>
/// The distinction matters. Every other test in this suite asks whether one engine rule works; a
/// driver is the assembly of about eight of them — extraction, a computed phase, a bound park, the
/// correlation verdict, the decision table, loop control — and the original failure was not any one
/// rule being wrong. It was the assembly having no way to park and wake at all. A fixture shaped
/// like a real driver is the only place that composition is under test.
/// </para>
///
/// <para>
/// <see cref="DriverFixture"/> is public-safe and generic: a tick agent, a status line, a blocker
/// line. The deployment's own driver definition is private and is not reproduced here.
/// </para>
/// </summary>
public sealed class ParkedDriverTests
{
    private const string SignalName = "blocker-resolved";
    private const string BlockerRef = "gated-run-a";

    // ── T1 / T2: parking is free, and a wakeup resumes exactly one tick ──────

    /// <summary>
    /// T1. While parked, the driver consumes NO agent turns and sends exactly ONE notification.
    ///
    /// This is the whole reason the park is a signal wait rather than a sleep-and-re-tick loop: a
    /// human gate can legitimately stand open for days, and a polling driver would burn a turn and
    /// emit a notification per probe. Asserted on the delegate count, which is what an agent turn
    /// actually costs.
    /// </summary>
    [Fact]
    public async Task WhileParked_NoAgentTurnsAreConsumedAndOneNotificationIsSent()
    {
        await using var env = await StartEnvAsync();
        var (taskQueue, workflowId) = Ids("driver-park");
        var activities = new DriverActivities(DriverFixture.Definition());
        activities.Ticks.Enqueue($"STATUS: blocked\nBLOCKER: {BlockerRef}");

        using var worker = Worker(env, taskQueue, activities);
        await worker.ExecuteAsync(async () =>
        {
            var handle = await Start(env, taskQueue, workflowId);
            await WaitForParkAsync(handle);

            // Parked. Give the runtime room to do something wrong — a poller would tick here.
            await Task.Delay(500);
            Assert.Equal(1, activities.CountOf("tick"));
            Assert.Equal(1, activities.CountOf("park_notify"));

            activities.Ticks.Enqueue("STATUS: done");
            await handle.SignalAsync(SignalName, [Payload("approved", BlockerRef)]);
            await handle.GetResultAsync<string>().WaitAsync(TimeSpan.FromSeconds(30));
        });

        // Still one notification after the resume: the park notifies on entry, never again.
        Assert.Equal(1, activities.CountOf("park_notify"));
    }

    /// <summary>
    /// T2. One wakeup produces exactly one resumed tick — not zero (dropped) and not two (the
    /// buffer replaying an entry it should have consumed).
    /// </summary>
    [Fact]
    public async Task AnApproval_ResumesExactlyOneTick()
    {
        await using var env = await StartEnvAsync();
        var (taskQueue, workflowId) = Ids("driver-resume");
        var activities = new DriverActivities(DriverFixture.Definition());
        activities.Ticks.Enqueue($"STATUS: blocked\nBLOCKER: {BlockerRef}");

        string? result = null;
        using var worker = Worker(env, taskQueue, activities);
        await worker.ExecuteAsync(async () =>
        {
            var handle = await Start(env, taskQueue, workflowId);
            await WaitForParkAsync(handle);

            activities.Ticks.Enqueue("STATUS: done");
            await handle.SignalAsync(SignalName, [Payload("approved", BlockerRef)]);
            result = await handle.GetResultAsync<string>().WaitAsync(TimeSpan.FromSeconds(30));
        });

        Assert.Equal("DONE", result);
        Assert.Equal(2, activities.CountOf("tick"));   // the original tick, and one resumed tick
    }

    // ── T5 / T6: the terminal decisions, each distinguishable ────────────────

    /// <summary>
    /// T5. A rejection addressed to THIS blocker terminates the driver explicitly, with a
    /// notification and no further tick.
    ///
    /// "Explicitly" is the load-bearing word: the failure mode this replaces is a driver that
    /// simply stops, which is indistinguishable from one that is still working.
    /// </summary>
    [Fact]
    public Task ARejectionForTheBoundBlocker_TerminatesWithItsOwnOutcome()
        => AssertTerminalAsync("driver-rejected", Payload("rejected", BlockerRef), "REJECTED");

    /// <summary>
    /// T6. Cancellation terminates too, and reports a DIFFERENT outcome. Collapsing the two would
    /// lose the distinction between "a human said no" and "the gate ran out of time" — which is
    /// the only signal anyone has for whether to re-open the work.
    /// </summary>
    [Fact]
    public Task ACancellationForTheBoundBlocker_TerminatesWithItsOwnOutcome()
        => AssertTerminalAsync("driver-cancelled", Payload("cancelled", BlockerRef), "CANCELLED");

    /// <summary>
    /// The pairing that makes T5/T6 worth having: the SAME rejection addressed to a different
    /// blocker does not terminate anything. It resumes and reconciles, because a decision about
    /// someone else's gate is not a decision about this one.
    /// </summary>
    [Fact]
    public async Task ARejectionForAnotherBlocker_ResumesInsteadOfTerminating()
    {
        await using var env = await StartEnvAsync();
        var (taskQueue, workflowId) = Ids("driver-stale-reject");
        var activities = new DriverActivities(DriverFixture.Definition());
        activities.Ticks.Enqueue($"STATUS: blocked\nBLOCKER: {BlockerRef}");

        string? result = null;
        using var worker = Worker(env, taskQueue, activities);
        await worker.ExecuteAsync(async () =>
        {
            var handle = await Start(env, taskQueue, workflowId);
            await WaitForParkAsync(handle);

            activities.Ticks.Enqueue("STATUS: done");
            await handle.SignalAsync(SignalName, [Payload("rejected", "some-other-gated-run")]);
            result = await handle.GetResultAsync<string>().WaitAsync(TimeSpan.FromSeconds(30));
        });

        Assert.Equal("DONE", result);          // not REJECTED
        Assert.Equal(2, activities.CountOf("tick"));
    }

    // ── T8: the wait survives replay ─────────────────────────────────────────

    /// <summary>
    /// T8. The recorded history of a park-and-resume replays against the current engine with no
    /// non-determinism error, and carries exactly one park notification and one resumed tick —
    /// so a worker restart mid-park cannot produce a duplicate of either.
    ///
    /// Replay is the real test of "durable across worker restart": it re-runs the workflow code
    /// against the recorded command sequence, which is precisely what a restarted worker does.
    /// </summary>
    [Fact]
    public async Task AParkAndResume_ReplaysWithoutDuplicatingTheNotificationOrTheTick()
    {
        await using var env = await StartEnvAsync();
        var (taskQueue, workflowId) = Ids("driver-replay");
        var activities = new DriverActivities(DriverFixture.Definition());
        activities.Ticks.Enqueue($"STATUS: blocked\nBLOCKER: {BlockerRef}");

        using var worker = Worker(env, taskQueue, activities);
        await worker.ExecuteAsync(async () =>
        {
            var handle = await Start(env, taskQueue, workflowId);
            await WaitForParkAsync(handle);
            activities.Ticks.Enqueue("STATUS: done");
            await handle.SignalAsync(SignalName, [Payload("approved", BlockerRef)]);
            await handle.GetResultAsync<string>().WaitAsync(TimeSpan.FromSeconds(30));
        });

        var history = await env.Client.GetWorkflowHandle(workflowId).FetchHistoryAsync();

        var scheduled = history.Events
            .Where(e => e.EventType == EventType.ActivityTaskScheduled)
            .Select(e => e.ActivityTaskScheduledEventAttributes.ActivityType.Name)
            .Count(name => name == "DelegateToAgent");

        // Two ticks and one notification, once each.
        Assert.Equal(3, scheduled);
        Assert.Equal(1, activities.CountOf("park_notify"));

        var replayer = new WorkflowReplayer(
            new WorkflowReplayerOptions().AddWorkflow<UniversalWorkflow>());
        await replayer.ReplayWorkflowAsync(history);
    }

    // ── T9: a driver with no blocker is unchanged ────────────────────────────

    /// <summary>
    /// T9. A driver that never reports blocked keeps its exact cadence: the same ticks, no timer,
    /// no signal command, no park notification. The park machinery must be invisible to a run that
    /// never parks.
    ///
    /// One event IS new on every UWE execution: the <c>core_patch</c> marker the replay gate
    /// records. That is the price of being able to deploy this without wedging in-flight
    /// workflows, it costs one history event, and it is asserted here rather than left for someone
    /// to discover while diffing histories.
    /// </summary>
    [Fact]
    public async Task ADriverThatNeverBlocks_KeepsItsCadence()
    {
        await using var env = await StartEnvAsync();
        var (taskQueue, workflowId) = Ids("driver-no-blocker");
        var activities = new DriverActivities(DriverFixture.Definition());
        activities.Ticks.Enqueue("STATUS: working");
        activities.Ticks.Enqueue("STATUS: working");
        activities.Ticks.Enqueue("STATUS: done");

        string? result = null;
        using var worker = Worker(env, taskQueue, activities);
        await worker.ExecuteAsync(async () =>
        {
            var handle = await Start(env, taskQueue, workflowId);
            result = await handle.GetResultAsync<string>().WaitAsync(TimeSpan.FromSeconds(30));
        });

        Assert.Equal("DONE", result);
        Assert.Equal(3, activities.CountOf("tick"));
        Assert.Equal(0, activities.CountOf("park_notify"));

        var history = await env.Client.GetWorkflowHandle(workflowId).FetchHistoryAsync();
        Assert.DoesNotContain(history.Events, e => e.EventType == EventType.TimerStarted);
        Assert.DoesNotContain(history.Events, e => e.EventType == EventType.WorkflowExecutionSignaled);
        Assert.DoesNotContain(history.Events,
            e => e.EventType == EventType.SignalExternalWorkflowExecutionInitiated);

        // The one new event, named explicitly.
        Assert.Single(history.Events.Where(e => e.MarkerRecordedEventAttributes is not null));
    }

    // ── shared ───────────────────────────────────────────────────────────────

    private static async Task AssertTerminalAsync(string label, object payload, string expected)
    {
        await using var env = await StartEnvAsync();
        var (taskQueue, workflowId) = Ids(label);
        var activities = new DriverActivities(DriverFixture.Definition());
        activities.Ticks.Enqueue($"STATUS: blocked\nBLOCKER: {BlockerRef}");

        string? result = null;
        using var worker = Worker(env, taskQueue, activities);
        await worker.ExecuteAsync(async () =>
        {
            var handle = await Start(env, taskQueue, workflowId);
            await WaitForParkAsync(handle);

            // Queued but never consumed: a terminal decision must not tick again.
            activities.Ticks.Enqueue("STATUS: done");
            await handle.SignalAsync(SignalName, [payload]);
            result = await handle.GetResultAsync<string>().WaitAsync(TimeSpan.FromSeconds(30));
        });

        Assert.Equal(expected, result);
        Assert.Equal(1, activities.CountOf("tick"));              // no further tick
        Assert.Equal(1, activities.CountOf("terminal_notify"));   // and the human was told
    }

    /// <summary>
    /// The driver parks with a <c>Phase</c>, and upserting an UNREGISTERED search attribute fails
    /// the workflow task server-side where the engine's try/catch cannot see it. Production
    /// registers Phase per namespace at bridge startup; a bare test server does not.
    /// </summary>
    private static async Task<WorkflowEnvironment> StartEnvAsync()
    {
        var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        await env.Client.Connection.OperatorService.AddSearchAttributesAsync(
            new AddSearchAttributesRequest
            {
                Namespace = env.Client.Options.Namespace,
                SearchAttributes = { ["Phase"] = IndexedValueType.Keyword },
            });
        return env;
    }

    private static object Payload(string decision, string blockerRef) =>
        JsonSerializer.SerializeToElement(new { decision, blockerRef });

    private static (string TaskQueue, string WorkflowId) Ids(string label) =>
        ($"{label}-{Guid.NewGuid():N}", $"{label}-{Guid.NewGuid():N}");

    private static TemporalWorker Worker(WorkflowEnvironment env, string taskQueue, object activities) =>
        new(env.Client,
            new TemporalWorkerOptions(taskQueue)
                .AddWorkflow<UniversalWorkflow>()
                .AddAllActivities(activities.GetType(), activities));

    private static Task<WorkflowHandle> Start(WorkflowEnvironment env, string taskQueue, string workflowId) =>
        env.Client.StartWorkflowAsync(
            "example-driver",
            Array.Empty<object?>(),
            new WorkflowOptions(id: workflowId, taskQueue: taskQueue));

    /// <summary>Waits until the driver is genuinely parked — the park's timer is in history.</summary>
    private static async Task WaitForParkAsync(WorkflowHandle handle)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            var history = await handle.FetchHistoryAsync();
            if (history.Events.Any(e => e.EventType == EventType.TimerStarted))
                return;
            await Task.Delay(50, cts.Token);
        }
    }
}
