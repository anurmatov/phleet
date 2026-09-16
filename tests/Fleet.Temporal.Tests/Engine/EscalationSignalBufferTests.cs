using System.Text.Json;
using Fleet.Temporal.Engine;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.OperatorService.V1;
using Fleet.Temporal.Models;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Testing;
using Temporalio.Worker;

namespace Fleet.Temporal.Tests.Engine;

/// <summary>
/// #280 D-10 — the SECOND wait, the one that is easy to forget.
///
/// <para>
/// <c>escalation-decision</c> is not served by the <c>wait_for_signal</c> step: it is an
/// independent inline wait inside the delegate-with-escalation retry loop, sharing only the waiter
/// dictionary. A fix confined to the step would leave it untouched — and because its wait is
/// INDEFINITE, a dropped signal there is a permanent hang rather than a timeout.
/// </para>
///
/// <para>
/// Its correlation is by arrival ordinal rather than payload, because the documented reply schema
/// carries no correlation value and widening it would break every operator who has learned it.
/// The epoch is snapshotted before each attempt: the operator who resolves an escalation is
/// usually reacting to the step already being in trouble, i.e. while the delegate is still
/// running, so a snapshot taken inside the catch would decline the very reply it exists to accept.
/// </para>
/// </summary>
public sealed class EscalationSignalBufferTests
{
    private const string EscalationSignal = "escalation-decision";

    /// <summary>
    /// T19 / F18. The reply arrives while the delegate is still running and about to fail. Its
    /// arrival ordinal is greater than the epoch snapshotted before the attempt, so the escalation
    /// consumes it on entry — and sends NO notification, because there is nothing to ask.
    ///
    /// The notification count is the assertion that matters: a design that notified first and then
    /// checked the buffer would pester a human who has already answered.
    /// </summary>
    [Fact]
    public async Task AReplyArrivingDuringTheAttempt_ResolvesWithoutNotifying()
    {
        var definition = Definition(new DelegateWithEscalationStep
        {
            Name = "flaky_step",
            Target = "example-agent",
            Instruction = "do the thing",
            OutputVar = "step_out",
        });

        await using var env = await StartEnvAsync();
        var (taskQueue, workflowId) = Ids("escalation-early");
        var activities = new EscalationTestActivities(definition) { FailFirstAttempt = true };

        using var worker = Worker(env, taskQueue, activities);
        await worker.ExecuteAsync(async () =>
        {
            var handle = await Start(env, taskQueue, workflowId);

            // The delegate is running and will fail. Reply now — before the escalation exists.
            await activities.DelegateStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await handle.SignalAsync(EscalationSignal, [Decision("skip")]);
            activities.ReleaseDelegate();

            await handle.GetResultAsync();
        });

        // No escalation notification was sent: the decision was already in hand.
        Assert.DoesNotContain(activities.DelegateCalls, name => name.Contains("escalation_notify"));
    }

    /// <summary>
    /// T20 / F19. An entry that arrived BEFORE this attempt is stale — it belongs to an earlier
    /// step or an earlier retry round — so the escalation declines it, notifies, and waits
    /// normally.
    ///
    /// And it is left in the buffer, not deleted. That half is asserted by parking on the same
    /// signal name afterwards and completing from it: deleting a declined entry would be a silent
    /// discard, which is the defect class this whole mechanism exists to remove (MUST NOT 10).
    /// </summary>
    [Fact]
    public async Task AStaleReply_IsDeclinedAndLeftInTheBuffer()
    {
        var definition = Definition(new SequenceStep
        {
            Steps =
            [
                // Runs first, before the escalation exists, and gives the test a window in which
                // the stale reply can arrive with nothing waiting.
                new DelegateStep { Name = "warmup", Target = "example-agent", Instruction = "warm", OutputVar = "warm" },
                new DelegateWithEscalationStep
                {
                    Name = "flaky_step",
                    Target = "example-agent",
                    Instruction = "do the thing",
                    OutputVar = "step_out",
                },
                // Consumes whatever is still buffered under the same name.
                new WaitForSignalStep
                {
                    Name = "drain_stale_entry",
                    SignalName = EscalationSignal,
                    OutputVar = "drained",
                    TimeoutMinutes = 60,
                    AutoCompleteOnTimeout = true,
                },
                new SetVariableStep
                {
                    Vars = new() { ["_result"] = "{{vars.drained.Decision | default: 'nothing-buffered'}}" },
                },
            ],
        });

        await using var env = await StartEnvAsync();
        var (taskQueue, workflowId) = Ids("escalation-stale");
        var activities = new EscalationTestActivities(definition) { FailFirstAttempt = true };

        string? result = null;
        using var worker = Worker(env, taskQueue, activities);
        await worker.ExecuteAsync(async () =>
        {
            var handle = await Start(env, taskQueue, workflowId);

            // Stale: delivered during the WARMUP step, i.e. before the failing attempt begins.
            await activities.DelegateStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await handle.SignalAsync(EscalationSignal, [Decision("stale-entry")]);
            activities.ReleaseDelegate();

            // The escalation must not consume it, so it notifies and waits. Answer that properly.
            // "continue" rather than "skip": skip sets the engine's skip-remaining flag and the
            // drain step below would never run, leaving the second half of the claim untested.
            await activities.EscalationNotified.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await handle.SignalAsync(EscalationSignal, [Decision("continue")]);

            result = await handle.GetResultAsync<string>();
        });

        // The escalation DID notify — proof the stale entry was declined rather than consumed.
        Assert.Contains(activities.DelegateCalls, name => name.Contains("escalation_notify"));

        // ...and the declined entry was still there afterwards. Deleting it when declining would
        // be a silent discard, and this is the assertion that catches it.
        Assert.Equal("stale-entry", result);
    }

    /// <summary>
    /// T22. The epoch comparison is strictly greater, never <c>&gt;=</c>.
    ///
    /// A reply delivered before the attempt starts has <c>Seq &lt;= epoch</c> and must be declined;
    /// with <c>&gt;=</c> the arrival that immediately preceded the snapshot would be accepted, which
    /// is precisely a stale reply resolving the wrong escalation. Observable as: the escalation
    /// notified instead of resolving silently.
    /// </summary>
    [Fact]
    public async Task AReplyDeliveredBeforeTheAttempt_IsDeclined()
    {
        var definition = Definition(new SequenceStep
        {
            Steps =
            [
                new DelegateStep { Name = "warmup", Target = "example-agent", Instruction = "warm", OutputVar = "warm" },
                new DelegateWithEscalationStep
                {
                    Name = "flaky_step",
                    Target = "example-agent",
                    Instruction = "do the thing",
                    OutputVar = "step_out",
                },
            ],
        });

        await using var env = await StartEnvAsync();
        var (taskQueue, workflowId) = Ids("escalation-strict");
        var activities = new EscalationTestActivities(definition) { FailFirstAttempt = true };

        using var worker = Worker(env, taskQueue, activities);
        await worker.ExecuteAsync(async () =>
        {
            var handle = await Start(env, taskQueue, workflowId);

            await activities.DelegateStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await handle.SignalAsync(EscalationSignal, [Decision("skip")]);
            activities.ReleaseDelegate();

            await activities.EscalationNotified.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await handle.SignalAsync(EscalationSignal, [Decision("skip")]);

            await handle.GetResultAsync();
        });

        Assert.Contains(activities.DelegateCalls, name => name.Contains("escalation_notify"));
    }

    /// <summary>
    /// T21. A <c>retry</c> decision starts a fresh window: the epoch is re-read at the top of each
    /// loop iteration, so a leftover reply from the PREVIOUS round cannot resolve the next
    /// escalation.
    ///
    /// Without the per-iteration snapshot, one operator reply would silently answer every
    /// subsequent failure of the same step — the step would appear to retry forever with a human
    /// apparently approving each round, when nobody was asked after the first.
    ///
    /// Observable as: the second failure notifies AGAIN rather than resolving from the leftover.
    /// </summary>
    [Fact]
    public async Task AfterARetry_ALeftoverReplyDoesNotResolveTheNextEscalation()
    {
        var definition = Definition(new DelegateWithEscalationStep
        {
            Name = "flaky_step",
            Target = "example-agent",
            Instruction = "do the thing",
            OutputVar = "step_out",
        });

        await using var env = await StartEnvAsync();
        var (taskQueue, workflowId) = Ids("escalation-retry-epoch");
        var activities = new EscalationTestActivities(definition)
        {
            FailFirstAttempt = true,
            FailingAttempts = 2,      // fail, retry, fail again
        };

        using var worker = Worker(env, taskQueue, activities);
        await worker.ExecuteAsync(async () =>
        {
            var handle = await Start(env, taskQueue, workflowId);
            await activities.DelegateStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            activities.ReleaseDelegate();

            // Round 1: the escalation asks, the operator answers "retry".
            await activities.EscalationNotified.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await handle.SignalAsync(EscalationSignal, [Decision("retry")]);

            // Round 2 fails too. If the round-1 reply were still consumable, it would resolve this
            // escalation silently and the notification count would stay at one.
            await activities.WaitForNotificationsAsync(2);
            await handle.SignalAsync(EscalationSignal, [Decision("continue")]);

            await handle.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(30));
        });

        Assert.Equal(2, activities.DelegateCalls.Count(name => name.Contains("escalation_notify")));
    }

    /// <summary>
    /// T23 / F20. The reply that lands while the escalation NOTIFICATION activity is still in
    /// flight still resolves that escalation — via the waiter, which is registered before the
    /// notification is sent. That ordering predates #280 and must stay: the epoch covers a
    /// different window, and neither mechanism subsumes the other.
    /// </summary>
    [Fact]
    public async Task AReplyDuringTheNotification_StillResolvesTheEscalation()
    {
        var definition = Definition(new DelegateWithEscalationStep
        {
            Name = "flaky_step",
            Target = "example-agent",
            Instruction = "do the thing",
            OutputVar = "step_out",
        });

        await using var env = await StartEnvAsync();
        var (taskQueue, workflowId) = Ids("escalation-notify-race");
        var activities = new EscalationTestActivities(definition)
        {
            FailFirstAttempt = true,
            HoldEscalationNotification = true,
        };

        using var worker = Worker(env, taskQueue, activities);
        await worker.ExecuteAsync(async () =>
        {
            var handle = await Start(env, taskQueue, workflowId);
            await activities.DelegateStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            activities.ReleaseDelegate();

            // The notification activity is running and blocked. Reply into that window.
            await activities.EscalationNotified.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await handle.SignalAsync(EscalationSignal, [Decision("skip")]);
            activities.ReleaseEscalationNotification();

            // Resolves rather than hanging: the waiter was registered before the notification.
            await handle.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(30));
        });
    }

    // ── harness ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The escalation path upserts the <c>Phase</c> search attribute, and an upsert of an
    /// UNREGISTERED attribute fails the workflow TASK server-side — which the engine's try/catch
    /// cannot intercept, because the failure happens when the task is completed rather than when
    /// the call is made. Production registers Phase per namespace at bridge startup; a bare test
    /// server does not, so these tests register it too.
    ///
    /// Without this the escalation branch never gets past its first command and every test here
    /// times out on a workflow that is silently retrying a poisoned task.
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

    private static object Decision(string decision) =>
        JsonSerializer.SerializeToElement(new { Decision = decision });

    private static WorkflowDefinitionModel Definition(StepDefinition root) => new()
    {
        Name = "example-workflow",
        Namespace = "default",
        TaskQueue = "test",
        Root = root,
    };

    private static (string TaskQueue, string WorkflowId) Ids(string label) =>
        ($"{label}-{Guid.NewGuid():N}", $"{label}-{Guid.NewGuid():N}");

    private static TemporalWorker Worker(WorkflowEnvironment env, string taskQueue, object activities) =>
        new(env.Client,
            new TemporalWorkerOptions(taskQueue)
                .AddWorkflow<UniversalWorkflow>()
                .AddAllActivities(activities.GetType(), activities));

    private static Task<WorkflowHandle> Start(WorkflowEnvironment env, string taskQueue, string workflowId) =>
        env.Client.StartWorkflowAsync(
            "example-workflow",
            Array.Empty<object?>(),
            new WorkflowOptions(id: workflowId, taskQueue: taskQueue));
}

// ---------------------------------------------------------------------------
// Test-only activity stubs
// ---------------------------------------------------------------------------

/// <summary>
/// A delegate stub that fails on cue so the escalation path runs, and that can hold the escalation
/// notification open so a test can deliver a reply into that exact window.
/// </summary>
file sealed class EscalationTestActivities(WorkflowDefinitionModel definition)
{
    private readonly TaskCompletionSource _releaseDelegate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseNotification = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<string> _calls = [];
    private int _attempts;

    public TaskCompletionSource DelegateStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource EscalationNotified { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Fail the first non-warmup, non-notify delegate so the escalation branch runs.</summary>
    public bool FailFirstAttempt { get; init; }

    /// <summary>How many attempts fail when <see cref="FailFirstAttempt"/> is set. Default 1.</summary>
    public int FailingAttempts { get; init; } = 1;

    public bool HoldEscalationNotification { get; init; }

    public IReadOnlyList<string> DelegateCalls
    {
        get { lock (_calls) return _calls.ToList(); }
    }

    public void ReleaseDelegate() => _releaseDelegate.TrySetResult();

    /// <summary>Waits until at least <paramref name="count"/> escalation notifications have run.</summary>
    public async Task WaitForNotificationsAsync(int count)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (DelegateCalls.Count(n => n.Contains("escalation_notify")) < count)
            await Task.Delay(25, cts.Token);
    }
    public void ReleaseEscalationNotification() => _releaseNotification.TrySetResult();

    [Activity("LoadWorkflowDefinition")]
    public WorkflowDefinitionModel LoadDefinition(string _) => definition;

    [Activity("LoadWorkflowConfig")]
    public JsonElement LoadConfig() =>
        JsonSerializer.SerializeToElement(new { EscalationTarget = "example-escalation-target" });

    [Activity("DelegateToAgent")]
    public async Task<AgentTaskResult> DelegateAsync(
        string target, string instruction, string taskId, bool retryOnIncomplete, int maxRetries)
    {
        var name = taskId.Split('/').LastOrDefault() ?? taskId;
        lock (_calls) _calls.Add(name);

        if (name.Contains("escalation_notify"))
        {
            EscalationNotified.TrySetResult();
            if (HoldEscalationNotification)
                await _releaseNotification.Task.WaitAsync(TimeSpan.FromSeconds(30));
            return new AgentTaskResult("notified", "completed");
        }

        if (name == "warmup")
        {
            DelegateStarted.TrySetResult();
            await _releaseDelegate.Task.WaitAsync(TimeSpan.FromSeconds(30));
            return new AgentTaskResult("warm", "completed");
        }

        DelegateStarted.TrySetResult();
        await _releaseDelegate.Task.WaitAsync(TimeSpan.FromSeconds(30));

        if (FailFirstAttempt && Interlocked.Increment(ref _attempts) <= FailingAttempts)
            throw new ApplicationException("step failed on purpose");

        return new AgentTaskResult("done", "completed");
    }
}
