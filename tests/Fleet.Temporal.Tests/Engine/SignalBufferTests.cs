using System.Text.Json;
using Fleet.Temporal.Engine;
using Fleet.Temporal.Models;
using Temporalio.Activities;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Testing;
using Temporalio.Worker;

namespace Fleet.Temporal.Tests.Engine;

/// <summary>
/// #280 — a signal that arrives while the workflow is busy elsewhere must not be lost, and a
/// wakeup must not be able to terminate a workflow it was not addressed to.
///
/// <para>
/// These drive the PRODUCTION <see cref="UniversalWorkflow"/> inside a time-skipping
/// <see cref="WorkflowEnvironment"/> with stub activities, following the pattern established by
/// <c>SleepStepTests</c>. Asserting on workflow history rather than on wall-clock timing is what
/// makes them deterministic: "no timer was started" is a fact about the recorded commands, and it
/// is the only way to tell "resumed immediately from the buffer" apart from "parked and got lucky".
/// </para>
///
/// <para>
/// Everything here uses generic placeholder names. The driver definition that motivated the issue
/// is deployment-private and is not reproduced.
/// </para>
/// </summary>
public sealed class SignalBufferTests
{
    private const string SignalName = "blocker-resolved";

    // ── T3 / F1 / F2: a signal that arrives before the wait is honoured ──────

    /// <summary>
    /// T3. The headline defect. The signal arrives while the workflow is inside an activity — no
    /// waiter registered — and before #280 was accepted by Temporal, written to history, and then
    /// dropped on the floor. The wait that followed would park until its timeout.
    ///
    /// Asserted on the absence of a TimerStarted command: a finite wait that actually parks starts
    /// a durable timer, so "no timer" is proof the buffered entry was consumed at the head of the
    /// step rather than the park merely being short.
    /// </summary>
    [Fact]
    public async Task SignalArrivingBeforeTheWait_ResumesImmediatelyWithoutParking()
    {
        var definition = Definition(new SequenceStep
        {
            Steps =
            [
                // A step that gives the test a window in which no waiter is registered.
                new DelegateStep { Name = "tick", Target = "example-agent", Instruction = "work", OutputVar = "tick" },
                Park(outputVar: "resume"),
            ],
        });

        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var (taskQueue, workflowId) = Ids("buffer-before-wait");
        var activities = new EngineTestActivities(definition);

        using var worker = Worker(env, taskQueue, activities);
        await worker.ExecuteAsync(async () =>
        {
            var handle = await Start(env, taskQueue, workflowId);

            // Deliver while the delegate activity is still running: the workflow is mid-step and
            // has no waiter for this signal name.
            await activities.DelegateStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await handle.SignalAsync(SignalName, [Payload("approved", "run-a")]);
            activities.ReleaseDelegate();

            await handle.GetResultAsync();
        });

        var history = await env.Client.GetWorkflowHandle(workflowId).FetchHistoryAsync();
        Assert.DoesNotContain(history.Events, e => e.EventType == EventType.TimerStarted);
    }

    /// <summary>
    /// T4. Three identical wakeups produce one resumed turn. The first resolves the waiter; the
    /// rest fall through to the buffer, where at most one entry per signal name survives.
    /// </summary>
    [Fact]
    public async Task DuplicateWakeups_ResumeTheTurnOnce()
    {
        var definition = Definition(new SequenceStep
        {
            Steps =
            [
                Park(outputVar: "resume"),
                new DelegateStep { Name = "after_resume", Target = "example-agent", Instruction = "continue", OutputVar = "after" },
            ],
        });

        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var (taskQueue, workflowId) = Ids("duplicate-wakeups");
        var activities = new EngineTestActivities(definition) { AutoReleaseDelegate = true };

        using var worker = Worker(env, taskQueue, activities);
        await worker.ExecuteAsync(async () =>
        {
            var handle = await Start(env, taskQueue, workflowId);
            await WaitForParkAsync(handle);

            for (var i = 0; i < 3; i++)
                await handle.SignalAsync(SignalName, [Payload("approved", "run-a")]);

            await handle.GetResultAsync();
        });

        // Exactly one continuation ran. A second resume would show as a second delegate.
        Assert.Equal(1, activities.DelegateCalls.Count(name => name == "after_resume"));
    }

    /// <summary>
    /// A consumed entry is REMOVED. Left in place it would resume the next wait on the same signal
    /// name too, turning one wakeup into an unbounded supply of them — and each spurious resume
    /// costs a real agent turn.
    ///
    /// Two parks on one buffered signal: the first consumes it, the second must actually park.
    /// </summary>
    [Fact]
    public async Task AConsumedEntry_IsRemovedFromTheBuffer()
    {
        var definition = Definition(new SequenceStep
        {
            Steps =
            [
                new DelegateStep { Name = "tick", Target = "example-agent", Instruction = "work", OutputVar = "tick" },
                Park(outputVar: "first"),
                Park(outputVar: "second"),
            ],
        });

        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var (taskQueue, workflowId) = Ids("consume-removes");
        var activities = new EngineTestActivities(definition);

        using var worker = Worker(env, taskQueue, activities);
        await worker.ExecuteAsync(async () =>
        {
            var handle = await Start(env, taskQueue, workflowId);
            await activities.DelegateStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await handle.SignalAsync(SignalName, [Payload("approved", "run-a")]);
            activities.ReleaseDelegate();
            await handle.GetResultAsync();
        });

        var history = await env.Client.GetWorkflowHandle(workflowId).FetchHistoryAsync();

        // Exactly one timer: the first park consumed the entry and started none, the second found
        // nothing and parked. Leaving the entry in place would produce zero.
        Assert.Single(history.Events, e => e.EventType == EventType.TimerStarted);
    }

    // ── T10 / F16: the buffer is bounded, and eviction is loud ───────────────

    /// <summary>
    /// T10, the retained half. Seventeen distinct signal names arrive while the workflow is
    /// mid-step with nothing waiting. The cap is 16, so the NEWEST is retained: a later wait for it
    /// resumes immediately, with no timer.
    ///
    /// Paired with the eviction test below, this is what distinguishes a correctly bounded buffer
    /// from one that stores nothing at all — a buffer that dropped everything would also pass
    /// "the evicted entry does not resume", and the cap would be untested.
    /// </summary>
    [Fact]
    public async Task AMiddleEntry_SurvivesEvictionAndResumesALaterWait()
    {
        // Deliberately NOT the newest. Eviction chooses among entries ALREADY in the buffer, so an
        // evict-newest policy would still retain the incoming 17th — parking on it would pass
        // under either policy and prove nothing. surplus-15 is retained only by evict-oldest.
        const string newest = "surplus-15";
        var definition = Definition(new SequenceStep
        {
            Steps =
            [
                new DelegateStep { Name = "tick", Target = "example-agent", Instruction = "work", OutputVar = "tick" },
                new WaitForSignalStep
                {
                    Name = "park_on_retained",
                    SignalName = newest,
                    OutputVar = "resume",
                    TimeoutMinutes = 60,
                    AutoCompleteOnTimeout = true,
                },
            ],
        });

        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var (taskQueue, workflowId) = Ids("buffer-retained");
        var activities = new EngineTestActivities(definition);

        using var worker = Worker(env, taskQueue, activities);
        await worker.ExecuteAsync(async () =>
        {
            var handle = await Start(env, taskQueue, workflowId);
            await activities.DelegateStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            for (var i = 0; i <= UniversalWorkflow.MaxBufferedSignals; i++)
                await handle.SignalAsync($"surplus-{i}", [Payload("approved", "run-a")]);

            activities.ReleaseDelegate();
            await handle.GetResultAsync();
        });

        var history = await env.Client.GetWorkflowHandle(workflowId).FetchHistoryAsync();
        Assert.DoesNotContain(history.Events, e => e.EventType == EventType.TimerStarted);
    }

    /// <summary>
    /// The eviction half, isolated: 17 names buffered with nothing waiting, then a wait for the
    /// oldest. It must NOT resume from the buffer — that entry was evicted — so the wait parks and
    /// starts a timer.
    /// </summary>
    [Fact]
    public async Task AnEvictedEntry_DoesNotResumeALaterWait()
    {
        var definition = Definition(new SequenceStep
        {
            Steps =
            [
                new DelegateStep { Name = "tick", Target = "example-agent", Instruction = "work", OutputVar = "tick" },
                new WaitForSignalStep
                {
                    Name = "park_on_evicted",
                    SignalName = "surplus-0",
                    OutputVar = "resume",
                    TimeoutMinutes = 60,
                    AutoCompleteOnTimeout = true,
                },
            ],
        });

        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var (taskQueue, workflowId) = Ids("buffer-evict");
        var activities = new EngineTestActivities(definition);

        using var worker = Worker(env, taskQueue, activities);
        await worker.ExecuteAsync(async () =>
        {
            var handle = await Start(env, taskQueue, workflowId);
            await activities.DelegateStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            for (var i = 0; i < UniversalWorkflow.MaxBufferedSignals + 1; i++)
                await handle.SignalAsync($"surplus-{i}", [Payload("approved", "run-a")]);

            activities.ReleaseDelegate();
            await handle.GetResultAsync();
        });

        var history = await env.Client.GetWorkflowHandle(workflowId).FetchHistoryAsync();

        // Evicted: the wait had nothing to consume, so it parked and its timer fired.
        Assert.Contains(history.Events, e => e.EventType == EventType.TimerStarted);
    }

    // ── T7 / F7: the park's own timeout stays terminal under bindTo ──────────

    /// <summary>
    /// T7. The evaluation-order trap, and the reason D-6 rule 1 exists. The engine's
    /// <c>autoCompleteOnTimeout</c> payload is <c>{Decision:"timeout"}</c> — it carries no
    /// correlation field at all, so comparing it would score the park's OWN timeout as a mismatch,
    /// degrade it to a resume, and re-park forever instead of terminating.
    /// </summary>
    [Fact]
    public async Task ParkTimeout_ScoresMatch_AndReportsTimeout()
    {
        var definition = Definition(new SequenceStep
        {
            Steps =
            [
                Park(outputVar: "resume", bindTo: "run-a"),
                new SetVariableStep
                {
                    Vars = new()
                    {
                        ["_result"] = "{{vars.resume_bindMatch}}|{{vars.resume.Decision | default: 'none'}}",
                    },
                },
            ],
        });

        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var (taskQueue, workflowId) = Ids("park-timeout");
        var activities = new EngineTestActivities(definition) { AutoReleaseDelegate = true };

        string? result = null;
        using var worker = Worker(env, taskQueue, activities);
        await worker.ExecuteAsync(async () =>
        {
            var handle = await Start(env, taskQueue, workflowId);
            // No signal is ever sent: the time-skipping environment runs the 60-minute timer out.
            result = await handle.GetResultAsync<string>();
        });

        Assert.Equal("match|timeout", result);
    }

    // ── T12 / T14 / T15 / T13: correlation verdicts ──────────────────────────

    /// <summary>
    /// T12 / F6. A decision addressed to a DIFFERENT blocker scores <c>mismatch</c>. The workflow
    /// still resumes — a mismatch may never drop a wakeup — but the definition is then free to
    /// refuse to act on it, which is what stops a stale <c>rejected</c> from terminating an epic
    /// nobody rejected.
    /// </summary>
    [Fact]
    public async Task ADecisionForAnotherBlocker_ScoresMismatch()
        => Assert.Equal("mismatch|rejected", await RunBindScenarioAsync(
            "bind-mismatch", bindTo: "run-a", payload: Payload("rejected", "some-other-run")));

    [Fact]
    public async Task ADecisionForTheBoundBlocker_ScoresMatch()
        => Assert.Equal("match|rejected", await RunBindScenarioAsync(
            "bind-match", bindTo: "run-a", payload: Payload("rejected", "run-a")));

    /// <summary>
    /// T14 / F8. A relayed signal arrives as a plain JSON STRING — that is how the relay listener
    /// sends one — so the correlation read must tolerate a payload with no properties at all.
    /// Mismatch, logged, resume-only. Never an exception: a throw here would fail the workflow on
    /// the one path that exists to keep it alive.
    /// </summary>
    [Fact]
    public async Task ANonObjectPayload_ScoresMismatchWithoutThrowing()
        => Assert.Equal("mismatch|none", await RunBindScenarioAsync(
            "bind-string-payload", bindTo: "run-a", payload: "just a string"));

    [Fact]
    public async Task APayloadMissingTheBindField_ScoresMismatch()
        => Assert.Equal("mismatch|approved", await RunBindScenarioAsync(
            "bind-missing-field", bindTo: "run-a",
            payload: JsonSerializer.SerializeToElement(new { decision = "approved" })));

    /// <summary>
    /// T15. <c>bindTo</c> present but resolving to empty — an UNBOUND park, which is what a tick
    /// that could not name its blocker produces. Every wakeup scores mismatch, so the park can be
    /// woken but never terminated by a decision that might belong to something else.
    /// </summary>
    [Fact]
    public async Task AnEmptyBindTo_ScoresMismatchOnEveryWakeup()
        => Assert.Equal("mismatch|rejected", await RunBindScenarioAsync(
            "bind-empty", bindTo: "{{vars.never_set | default: ''}}", payload: Payload("rejected", "run-a")));

    /// <summary>
    /// T13. <c>bindTo</c> omitted writes NO sibling variable. This is what keeps every pre-#280
    /// definition byte-identical: writing it unconditionally would add a variable to the vars scope
    /// of every existing wait in the fleet.
    /// </summary>
    [Fact]
    public async Task OmittedBindTo_WritesNoSiblingVariable()
        => Assert.Equal("ABSENT|approved", await RunBindScenarioAsync(
            "bind-omitted", bindTo: null, payload: Payload("approved", "run-a")));

    /// <summary>
    /// T27, the half that keeps an unbound park from being a dead end. A park whose correlation
    /// value is empty can never accept a terminal decision — but its OWN timeout still terminates,
    /// because the engine generated that payload and rule 1 scores it <c>match</c> without
    /// comparison.
    ///
    /// Without this the degraded park would be unterminable: mismatch on every wakeup AND mismatch
    /// on its own timeout, so it would resume, re-park, and repeat forever.
    /// </summary>
    [Fact]
    public async Task AnUnboundPark_StillTerminatesOnItsOwnTimeout()
    {
        var definition = Definition(new SequenceStep
        {
            Steps =
            [
                Park(outputVar: "resume", bindTo: "{{vars.never_set | default: ''}}"),
                new SetVariableStep
                {
                    Vars = new()
                    {
                        ["_result"] = "{{vars.resume_bindMatch}}|{{vars.resume.Decision | default: 'none'}}",
                    },
                },
            ],
        });

        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var (taskQueue, workflowId) = Ids("unbound-timeout");
        var activities = new EngineTestActivities(definition) { AutoReleaseDelegate = true };

        string? result = null;
        using var worker = Worker(env, taskQueue, activities);
        await worker.ExecuteAsync(async () =>
        {
            var handle = await Start(env, taskQueue, workflowId);
            result = await handle.GetResultAsync<string>();
        });

        Assert.Equal("match|timeout", result);
    }

    /// <summary>
    /// The extraction mechanism an unbound park depends on, pinned at the level a definition uses
    /// it. A tick reports its blocker on a line of its response; the three cases the driver must
    /// tell apart are an id, a literal "none", and the line being absent entirely.
    ///
    /// <c>extract:</c> returns empty on no match and <c>default:</c> treats empty as absent, so the
    /// distinction needs no new primitive — but it also means a definition that omits the default
    /// silently produces an empty ref, which is why this is asserted rather than assumed.
    /// </summary>
    [Theory]
    [InlineData("done\nBLOCKER: some-run-id", "some-run-id")]
    [InlineData("done\nBLOCKER: none", "none")]
    [InlineData("done, nothing to report", "ABSENT")]
    public async Task BlockerExtraction_DistinguishesIdNoneAndMissing(string tickOutput, string expected)
    {
        var definition = Definition(new SequenceStep
        {
            Steps =
            [
                new SetVariableStep { Vars = new() { ["tick_result"] = tickOutput } },
                new SetVariableStep
                {
                    Vars = new()
                    {
                        ["blocker_ref"] = "{{vars.tick_result | extract: 'BLOCKER:\\s*(\\S+)' | default: 'ABSENT'}}",
                    },
                },
                new SetVariableStep { Vars = new() { ["_result"] = "{{vars.blocker_ref}}" } },
            ],
        });

        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var (taskQueue, workflowId) = Ids("blocker-extract");
        var activities = new EngineTestActivities(definition) { AutoReleaseDelegate = true };

        string? result = null;
        using var worker = Worker(env, taskQueue, activities);
        await worker.ExecuteAsync(async () =>
        {
            var handle = await Start(env, taskQueue, workflowId);
            result = await handle.GetResultAsync<string>();
        });

        Assert.Equal(expected, result);
    }

    // ── T24 / F15: an absent waiter id is a skip ─────────────────────────────

    /// <summary>
    /// T24 / F15. A gated run started without a waiter id resolves the template to empty. The step
    /// must SKIP — no signal command in history — so the run behaves exactly as it did before the
    /// step was added. Throwing here would newly break every gate owner started by hand.
    /// </summary>
    [Fact]
    public async Task SignalWorkflow_WithNoTargetId_SkipsWithoutSignalling()
    {
        var definition = Definition(new SignalWorkflowStep
        {
            Name = "wake_parked_waiter",
            WorkflowId = "{{input.WaiterWorkflowId | default: ''}}",
            SignalName = SignalName,
            Payload = new() { ["decision"] = "approved" },
        });

        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var (taskQueue, workflowId) = Ids("signal-skip");
        var activities = new EngineTestActivities(definition) { AutoReleaseDelegate = true };

        using var worker = Worker(env, taskQueue, activities);
        await worker.ExecuteAsync(async () =>
        {
            var handle = await Start(env, taskQueue, workflowId);
            await handle.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(30));
        });

        var history = await env.Client.GetWorkflowHandle(workflowId).FetchHistoryAsync();
        Assert.DoesNotContain(history.Events,
            e => e.EventType == EventType.SignalExternalWorkflowExecutionInitiated);
    }

    /// <summary>
    /// The skip is independent of <c>ignoreFailure</c>. With <c>ignoreFailure: false</c> an empty
    /// target must STILL complete normally — "no waiter was registered" is not a failure, it is the
    /// documented shape of a gated run nobody is waiting on.
    ///
    /// This is what distinguishes a real skip from a throw that <c>ignoreFailure</c> happens to
    /// swallow: with the default of true the two are indistinguishable, and the difference only
    /// shows on the path where a definition has explicitly asked for delivery failures to be fatal.
    /// </summary>
    [Fact]
    public async Task SignalWorkflow_WithNoTargetId_SkipsEvenWhenFailuresAreFatal()
    {
        var definition = Definition(new SignalWorkflowStep
        {
            Name = "wake_parked_waiter",
            WorkflowId = "{{input.WaiterWorkflowId | default: ''}}",
            SignalName = SignalName,
            IgnoreFailure = false,
        });

        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var (taskQueue, workflowId) = Ids("signal-skip-fatal");
        var activities = new EngineTestActivities(definition) { AutoReleaseDelegate = true };

        using var worker = Worker(env, taskQueue, activities);
        await worker.ExecuteAsync(async () =>
        {
            var handle = await Start(env, taskQueue, workflowId);
            // Bounded deliberately: the failure mode this guards against is a step that THROWS
            // here, which the .NET SDK turns into a failed workflow TASK and retries forever. An
            // unbounded await would hang the run instead of failing it.
            await handle.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(30));
        });

        var history = await env.Client.GetWorkflowHandle(workflowId).FetchHistoryAsync();
        Assert.DoesNotContain(history.Events,
            e => e.EventType == EventType.SignalExternalWorkflowExecutionInitiated);
    }

    /// <summary>
    /// The positive control for the skip: with a target id the step really does emit the external
    /// signal command. Without this, a step that silently did nothing would pass the test above.
    /// </summary>
    [Fact]
    public async Task SignalWorkflow_WithATargetId_EmitsTheExternalSignal()
    {
        var definition = Definition(new SignalWorkflowStep
        {
            Name = "wake_parked_waiter",
            WorkflowId = "{{input.WaiterWorkflowId | default: ''}}",
            SignalName = SignalName,
            Payload = new() { ["decision"] = "approved", ["blockerRef"] = "{{workflow.id}}" },
        });

        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var (taskQueue, workflowId) = Ids("signal-send");
        var activities = new EngineTestActivities(definition) { AutoReleaseDelegate = true };

        using var worker = Worker(env, taskQueue, activities);
        await worker.ExecuteAsync(async () =>
        {
            var handle = await env.Client.StartWorkflowAsync(
                "example-workflow",
                new object?[] { new { WaiterWorkflowId = "some-other-workflow" } },
                new WorkflowOptions(id: workflowId, taskQueue: taskQueue));
            await handle.GetResultAsync();
        });

        var history = await env.Client.GetWorkflowHandle(workflowId).FetchHistoryAsync();

        // ignoreFailure defaults to true, so signalling a workflow that does not exist completes
        // the sender normally (T11) — and the command is still recorded.
        Assert.Contains(history.Events,
            e => e.EventType == EventType.SignalExternalWorkflowExecutionInitiated);
    }

    /// <summary>
    /// T11. The same missing target with <c>ignoreFailure: false</c> FAILS the sender. Paired with
    /// the test above, this is what proves the default is doing the work rather than the failure
    /// being impossible.
    /// </summary>
    [Fact]
    public async Task SignalWorkflow_ToAMissingTarget_FailsWhenIgnoreFailureIsOff()
    {
        var definition = Definition(new SignalWorkflowStep
        {
            Name = "wake_parked_waiter",
            WorkflowId = "{{input.WaiterWorkflowId}}",
            SignalName = SignalName,
            IgnoreFailure = false,
        });

        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var (taskQueue, workflowId) = Ids("signal-fail");
        var activities = new EngineTestActivities(definition) { AutoReleaseDelegate = true };

        using var worker = Worker(env, taskQueue, activities);
        await worker.ExecuteAsync(async () =>
        {
            var handle = await env.Client.StartWorkflowAsync(
                "example-workflow",
                new object?[] { new { WaiterWorkflowId = "no-such-workflow-" + Guid.NewGuid().ToString("N") } },
                new WorkflowOptions(id: workflowId, taskQueue: taskQueue)
                {
                    RetryPolicy = new() { MaximumAttempts = 1 },
                });

            await Assert.ThrowsAsync<Temporalio.Exceptions.WorkflowFailedException>(
                () => handle.GetResultAsync());
        });
    }

    // ── T25: the workflow scope ──────────────────────────────────────────────

    /// <summary>
    /// T25. <c>{{workflow.id}}</c> resolves to this execution's own id, and a <c>set_variable</c>
    /// writing a variable called <c>workflow</c> cannot shadow it — <c>set_variable</c> writes into
    /// <c>vars</c>, so the two are different scopes. A definition able to overwrite the scope could
    /// redirect every wakeup it sends (MUST NOT 27).
    /// </summary>
    [Fact]
    public async Task WorkflowScope_ResolvesAndCannotBeShadowed()
    {
        var definition = Definition(new SequenceStep
        {
            Steps =
            [
                new SetVariableStep { Vars = new() { ["workflow"] = "spoofed" } },
                new SetVariableStep
                {
                    Vars = new() { ["_result"] = "{{workflow.id}}|{{vars.workflow}}|{{workflow.type}}" },
                },
            ],
        });

        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var (taskQueue, workflowId) = Ids("workflow-scope");
        var activities = new EngineTestActivities(definition) { AutoReleaseDelegate = true };

        string? result = null;
        using var worker = Worker(env, taskQueue, activities);
        await worker.ExecuteAsync(async () =>
        {
            var handle = await Start(env, taskQueue, workflowId);
            result = await handle.GetResultAsync<string>();
        });

        Assert.Equal($"{workflowId}|spoofed|example-workflow", result);
    }

    // ── shared scenario ──────────────────────────────────────────────────────

    /// <summary>
    /// Parks, delivers one signal, and returns "<c>{bindMatch}|{decision}</c>" — the two things a
    /// definition actually branches on. <c>ABSENT</c> means the sibling variable was never written.
    /// </summary>
    private static async Task<string?> RunBindScenarioAsync(string label, string? bindTo, object payload)
    {
        var definition = Definition(new SequenceStep
        {
            Steps =
            [
                Park(outputVar: "resume", bindTo: bindTo),
                new SetVariableStep
                {
                    Vars = new()
                    {
                        ["_result"] = "{{vars.resume_bindMatch | default: 'ABSENT'}}|{{vars.resume.decision | default: 'none'}}",
                    },
                },
            ],
        });

        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var (taskQueue, workflowId) = Ids(label);
        var activities = new EngineTestActivities(definition) { AutoReleaseDelegate = true };

        string? result = null;
        using var worker = Worker(env, taskQueue, activities);
        await worker.ExecuteAsync(async () =>
        {
            var handle = await Start(env, taskQueue, workflowId);
            await WaitForParkAsync(handle);
            await handle.SignalAsync(SignalName, [payload]);
            result = await handle.GetResultAsync<string>();
        });

        return result;
    }

    // ── harness ──────────────────────────────────────────────────────────────

    private static WaitForSignalStep Park(string outputVar, string? bindTo = null) => new()
    {
        Name = "park_on_gate",
        SignalName = SignalName,
        OutputVar = outputVar,
        BindTo = bindTo,
        TimeoutMinutes = 60,
        MaxReminders = 0,
        AutoCompleteOnTimeout = true,
    };

    private static WorkflowDefinitionModel Definition(StepDefinition root) => new()
    {
        Name = "example-workflow",
        Namespace = "default",
        TaskQueue = "test",
        Root = root,
    };

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
            "example-workflow",
            Array.Empty<object?>(),
            new WorkflowOptions(id: workflowId, taskQueue: taskQueue));

    /// <summary>
    /// Waits until the workflow is actually parked on the signal.
    ///
    /// Signalling before the waiter is registered would exercise the BUFFER path instead of the
    /// waiter path — which is a different test, and one that would pass for the wrong reason here.
    /// The park is observable in history: the finite-timeout branch starts a timer as it parks.
    /// </summary>
    private static async Task WaitForParkAsync(WorkflowHandle handle)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            var history = await handle.FetchHistoryAsync();
            if (history.Events.Any(e => e.EventType == EventType.TimerStarted))
                return;
            await Task.Delay(50, cts.Token);
        }
    }
}

// ---------------------------------------------------------------------------
// Test-only activity stubs
// ---------------------------------------------------------------------------

/// <summary>
/// Stubs the two determinism-safe activities the engine always runs, plus the agent delegation.
/// <see cref="DelegateStarted"/> / <see cref="ReleaseDelegate"/> give a test a window during which
/// the workflow is provably mid-step with no signal waiter registered.
/// </summary>
file sealed class EngineTestActivities(WorkflowDefinitionModel definition)
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<string> _delegateCalls = [];

    public TaskCompletionSource DelegateStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>When true the delegate returns immediately instead of waiting to be released.</summary>
    public bool AutoReleaseDelegate { get; init; }

    public IReadOnlyList<string> DelegateCalls
    {
        get { lock (_delegateCalls) return _delegateCalls.ToList(); }
    }

    public void ReleaseDelegate() => _release.TrySetResult();

    [Activity("LoadWorkflowDefinition")]
    public WorkflowDefinitionModel LoadDefinition(string _) => definition;

    [Activity("LoadWorkflowConfig")]
    public JsonElement LoadConfig() => JsonSerializer.SerializeToElement(new { });

    [Activity("DelegateToAgent")]
    public async Task<AgentTaskResult> DelegateAsync(
        string target, string instruction, string taskId, bool retryOnIncomplete, int maxRetries)
    {
        lock (_delegateCalls) _delegateCalls.Add(TaskName(taskId));
        DelegateStarted.TrySetResult();
        if (!AutoReleaseDelegate)
            await _release.Task.WaitAsync(TimeSpan.FromSeconds(30));
        return new AgentTaskResult("done", "completed");
    }

    // DelegateToAgentActivity builds taskId as "{workflowId}/{stepName}".
    private static string TaskName(string taskId) =>
        taskId.Split('/').LastOrDefault() ?? taskId;
}
