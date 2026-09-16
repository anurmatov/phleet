using System.Text.Json;
using Fleet.Temporal.Engine;
using Fleet.Temporal.Models;
using Temporalio.Activities;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Converters;
using Temporalio.Testing;
using Temporalio.Worker;
using Temporalio.Workflows;

namespace Fleet.Temporal.Tests.Engine;

/// <summary>
/// #280 D-3 — the patch gate, and the only test that can actually prove it does anything.
///
/// <para>
/// Buffering a signal emits no workflow command and is replay-neutral. CONSUMING a buffered entry
/// is not: an execution already in flight when this deploys dropped its signal under the old code
/// and recorded a <c>TimerStarted</c> for the wait that followed. Replay that history against the
/// new engine without a gate and the engine issues no timer — a divergent command sequence, which
/// Temporal reports as a non-determinism failure and which wedges the workflow.
/// </para>
///
/// <para>
/// Proving that needs a history recorded by code that never called <see cref="Workflow.Patched"/>.
/// <see cref="LegacyUniversalWorkflowDouble"/> produces one. It is deliberately a copy rather than
/// a reuse: the moment it shares the gate with production it stops being a legacy history.
/// </para>
/// </summary>
public sealed class SignalBufferPatchReplayTests
{
    private const string SignalName = "blocker-resolved";

    /// <summary>
    /// T18. A NEW execution records the patch marker and takes the fast path: the signal that
    /// arrived while the workflow was mid-activity is consumed at the wait, so no timer is started
    /// at all.
    /// </summary>
    [Fact]
    public async Task ANewExecution_RecordsTheMarkerAndTakesTheFastPath()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var (taskQueue, workflowId) = Ids("patch-new");
        var activities = new ReplayTestActivities(Definition());

        using var worker = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions(taskQueue)
                .AddWorkflow<UniversalWorkflow>()
                .AddAllActivities(activities.GetType(), activities));

        await worker.ExecuteAsync(async () =>
        {
            var handle = await env.Client.StartWorkflowAsync(
                "example-workflow",
                Array.Empty<object?>(),
                new WorkflowOptions(id: workflowId, taskQueue: taskQueue));

            // Deliver while the config-load activity is still running: no waiter is registered yet.
            await activities.ConfigLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await handle.SignalAsync(SignalName, [Payload()]);
            activities.ReleaseConfigLoad();

            await handle.GetResultAsync();
        });

        var history = await env.Client.GetWorkflowHandle(workflowId).FetchHistoryAsync();

        Assert.Contains(
            history.Events.Where(e => e.MarkerRecordedEventAttributes is not null)
                          .Select(e => e.MarkerRecordedEventAttributes.MarkerName),
            name => name == "core_patch");

        // The fast path: consumed from the buffer, so the wait never parked.
        Assert.DoesNotContain(history.Events, e => e.EventType == EventType.TimerStarted);
    }

    /// <summary>
    /// T17 / F17. The gate's whole purpose. A history recorded by the pre-#280 engine — signal
    /// dropped, timer started — must replay against the CURRENT engine with no non-determinism
    /// error, which it can only do by taking the legacy branch.
    ///
    /// Remove the <see cref="Workflow.Patched"/> gate and this test fails: the current code would
    /// consume the buffered signal, skip the timer, and diverge from the recorded command sequence.
    /// </summary>
    [Fact]
    public async Task APrePatchHistory_ReplaysCleanlyAgainstTheCurrentEngine()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var (taskQueue, workflowId) = Ids("patch-legacy");
        var activities = new ReplayTestActivities(Definition());

        using var legacyWorker = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions(taskQueue)
                .AddWorkflow<LegacyUniversalWorkflowDouble>()
                .AddAllActivities(activities.GetType(), activities));

        await legacyWorker.ExecuteAsync(async () =>
        {
            var handle = await env.Client.StartWorkflowAsync(
                "example-workflow",
                Array.Empty<object?>(),
                new WorkflowOptions(id: workflowId, taskQueue: taskQueue));

            await activities.ConfigLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await handle.SignalAsync(SignalName, [Payload()]);
            activities.ReleaseConfigLoad();

            await handle.GetResultAsync();
        });

        var history = await env.Client.GetWorkflowHandle(workflowId).FetchHistoryAsync();

        // Preconditions: this really is a legacy history — no marker, and the signal was dropped,
        // so the wait parked and its timer is in the record.
        Assert.DoesNotContain(
            history.Events.Where(e => e.MarkerRecordedEventAttributes is not null)
                          .Select(e => e.MarkerRecordedEventAttributes.MarkerName),
            name => name == "core_patch");
        Assert.Contains(history.Events, e => e.EventType == EventType.TimerStarted);
        Assert.Contains(history.Events, e => e.EventType == EventType.WorkflowExecutionSignaled);

        // The assertion: the current engine replays it without diverging.
        var replayer = new WorkflowReplayer(
            new WorkflowReplayerOptions().AddWorkflow<UniversalWorkflow>());
        await replayer.ReplayWorkflowAsync(history);
    }

    // ── harness ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A single finite park. No <c>phase</c> and no <c>notifyStep</c>: both emit extra commands,
    /// and the legacy double only reproduces the wait itself.
    /// </summary>
    private static WorkflowDefinitionModel Definition() => new()
    {
        Name = "example-workflow",
        Namespace = "default",
        TaskQueue = "test",
        Root = new WaitForSignalStep
        {
            Name = "park_on_gate",
            SignalName = SignalName,
            OutputVar = "resume",
            TimeoutMinutes = 60,
            MaxReminders = 0,
            AutoCompleteOnTimeout = true,
        },
    };

    private static object Payload() =>
        JsonSerializer.SerializeToElement(new { decision = "approved", blockerRef = "run-a" });

    private static (string TaskQueue, string WorkflowId) Ids(string label) =>
        ($"{label}-{Guid.NewGuid():N}", $"{label}-{Guid.NewGuid():N}");
}

// ---------------------------------------------------------------------------
// Legacy double
// ---------------------------------------------------------------------------

/// <summary>
/// The pre-#280 command sequence for one finite <c>wait_for_signal</c>, registered as a dynamic
/// workflow so the history it produces is indistinguishable from one recorded by the deployed
/// engine before this change.
///
/// Three properties are load-bearing and must not be "tidied":
/// <list type="number">
/// <item>it never calls <see cref="Workflow.Patched"/>, so the history carries no marker;</item>
/// <item>its signal handler has no <c>else</c> — a signal with no waiter registered is dropped,
/// which is the defect being reproduced;</item>
/// <item>it runs the same two load activities in the same order as the engine, so replay lines up
/// on everything except the behaviour under test.</item>
/// </list>
/// </summary>
[Workflow(Dynamic = true)]
public class LegacyUniversalWorkflowDouble
{
    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _signalWaiters = new();

    [WorkflowRun]
    public async Task<object?> RunAsync(IRawValue[] args)
    {
        var definition = await Workflow.ExecuteActivityAsync<WorkflowDefinitionModel>(
            "LoadWorkflowDefinition",
            [Workflow.Info.WorkflowType],
            new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) });

        await Workflow.ExecuteActivityAsync<JsonElement>(
            "LoadWorkflowConfig",
            Array.Empty<object?>(),
            new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(10) });

        var step = (WaitForSignalStep)definition.Root;

        // Legacy ExecuteWaitForSignalAsync, finite branch, no reminder interval: register, then a
        // single wait slice equal to the total timeout.
        var tcs = new TaskCompletionSource<JsonElement>();
        _signalWaiters[step.SignalName] = tcs;

        var totalTimeout = TimeSpan.FromMinutes(step.TimeoutMinutes!.Value);
        await Workflow.WaitConditionAsync(() => tcs.Task.IsCompleted, totalTimeout);

        _signalWaiters.Remove(step.SignalName);
        return tcs.Task.IsCompleted
            ? tcs.Task.Result
            : JsonSerializer.SerializeToElement(new { Decision = "timeout" });
    }

    /// <summary>The pre-#280 handler: no else branch, and the TrySetResult result discarded.</summary>
    [WorkflowSignal(Dynamic = true)]
    public Task HandleSignalAsync(string signalName, IRawValue[] args)
    {
        var payload = args.Length > 0
            ? Workflow.PayloadConverter.ToValue<JsonElement>(args[0])
            : default;

        if (_signalWaiters.TryGetValue(signalName, out var tcs))
            tcs.TrySetResult(payload);

        return Task.CompletedTask;
    }
}

// ---------------------------------------------------------------------------
// Test-only activity stubs
// ---------------------------------------------------------------------------

/// <summary>
/// Holds the config-load activity open so a test can deliver a signal into a window where no
/// waiter is registered — the window in which the pre-#280 engine silently lost it.
/// </summary>
file sealed class ReplayTestActivities(WorkflowDefinitionModel definition)
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource ConfigLoadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void ReleaseConfigLoad() => _release.TrySetResult();

    [Activity("LoadWorkflowDefinition")]
    public WorkflowDefinitionModel LoadDefinition(string _) => definition;

    [Activity("LoadWorkflowConfig")]
    public async Task<JsonElement> LoadConfigAsync()
    {
        ConfigLoadStarted.TrySetResult();
        await _release.Task.WaitAsync(TimeSpan.FromSeconds(30));
        return JsonSerializer.SerializeToElement(new { });
    }
}
