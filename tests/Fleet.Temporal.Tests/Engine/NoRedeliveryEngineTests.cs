using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Fleet.Temporal.Activities;
using Fleet.Temporal.Engine;
using Fleet.Temporal.Models;
using NSubstitute;
using Temporalio.Activities;
using Temporalio.Api.Common.V1;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Converters;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Temporalio.Worker;
using Temporalio.Worker.Interceptors;
using Temporalio.Workflows;

namespace Fleet.Temporal.Tests.Engine;

/// <summary>
/// <c>noRedelivery</c> in the engine and the definition validator, with hand-built definitions on
/// a time-skipping test server.
///
/// <para>
/// What the engine adds for such a step: a cancelled run is noticed before the directive is
/// scheduled, a cancellation at the step ends the run instead of being swallowed by the step's own
/// <c>ignoreFailure</c>, and the flag reaches the activity as an eighth argument. What it must not
/// change: every other failure still follows <c>ignoreFailure</c>, a step without the field
/// schedules the arguments it always did, and recorded histories replay.
/// </para>
///
/// <para>
/// The two <c>WithoutNoRedelivery_</c> tests are controls. They pin what the engine does for a
/// step that has not opted in, which is also what it did for every step before the field: a
/// cancel that arrives during an <c>ignoreFailure</c> delegate is swallowed, nothing further is
/// scheduled, and the run carries on and ends <c>Completed</c>.
/// </para>
/// </summary>
public sealed class NoRedeliveryEngineTests
{
    private const string Type = "ExampleWorkflow";

    // ── definitions ───────────────────────────────────────────────────────────

    private static DelegateStep Once(string name = "merge", bool ignoreFailure = true, string? repo = null) => new()
    {
        Name = name,
        Target = "agent-a",
        Instruction = "merge the change",
        TimeoutMinutes = 10,
        RetryOnIncomplete = false,
        MaxIncompleteRetries = 0,
        NoRedelivery = true,
        IgnoreFailure = ignoreFailure,
        StatusVar = name + "_status",
        Repo = repo,
    };

    private static DelegateStep Plain(string name, bool ignoreFailure = true, string? repo = null) => new()
    {
        Name = name,
        Target = "agent-a",
        Instruction = $"do {name}",
        IgnoreFailure = ignoreFailure,
        Repo = repo,
    };

    private static WorkflowDefinitionModel Definition(params StepDefinition[] steps) => new()
    {
        Name = Type,
        Namespace = "default",
        TaskQueue = "test",
        Root = new SequenceStep { Steps = steps },
    };

    private static SetVariableStep Result(string value = "done") => new() { Vars = new() { ["_result"] = value } };

    // ── cancellation ──────────────────────────────────────────────────────────

    /// <summary>
    /// The run is cancelled while an earlier <c>ignoreFailure</c> step is out, and that step
    /// swallows the cancellation. The noRedelivery step is entered next: it sees the cancelled
    /// run before scheduling anything, and the run ends <c>Canceled</c>.
    /// </summary>
    [Fact]
    public async Task CancelSwallowedInEarlierIgnoreFailureStep_NoRedeliveryEntryThrows_Canceled_NothingScheduled()
    {
        var run = await RunAndCancelDuringAsync("pin", Definition(Plain("pin"), Once(), Result()));

        Assert.Equal(WorkflowExecutionStatus.Canceled, run.Status);
        Assert.Null(run.Result);
        // The step that was out when the cancel arrived, and nothing after it.
        Assert.Equal(["pin"], run.ScheduledDelegates);
        Assert.Equal(["pin"], run.StartedDelegates);

        await ReplayAsync(run.History);
    }

    /// <summary>
    /// "Before the directive is scheduled" means before the step is looked at at all. Here the
    /// noRedelivery step cannot even be resolved (it has no instruction), which without the check
    /// would be an ordinary failure for its <c>ignoreFailure</c> to swallow, and the run would
    /// carry on. The cancellation is found first.
    /// </summary>
    [Fact]
    public async Task CancelSwallowedInEarlierIgnoreFailureStep_NoRedeliveryEntry_IsCheckedBeforeTheStepIsResolved()
    {
        var run = await RunAndCancelDuringAsync(
            "pin", Definition(Plain("pin"), Once() with { Instruction = null }, Result()));

        Assert.Equal(WorkflowExecutionStatus.Canceled, run.Status);
        Assert.Null(run.Result);
        Assert.Equal(["pin"], run.ScheduledDelegates);
    }

    /// <summary>
    /// The control for the tests above: the second step has not opted in, so the cancellation the
    /// first one swallowed is swallowed again (the server refuses to schedule an activity for a
    /// cancelled run, and that refusal is a failure like any other) and the run ends
    /// <c>Completed</c>.
    /// </summary>
    [Fact]
    public async Task WithoutNoRedelivery_ACancelSwallowedByIgnoreFailure_SchedulesNothingMore_AndTheRunCompletes()
    {
        var run = await RunAndCancelDuringAsync("pin", Definition(Plain("pin"), Plain("merge"), Result()));

        Assert.Equal(WorkflowExecutionStatus.Completed, run.Status);
        Assert.Equal("done", run.Result);
        Assert.Equal(["pin"], run.ScheduledDelegates);
    }

    /// <summary>
    /// The run is cancelled while the noRedelivery step itself is out. Its <c>ignoreFailure</c>
    /// does not swallow that: the run ends <c>Canceled</c>, the read-back after it never runs and
    /// no result is set.
    ///
    /// <para>
    /// The steps after it are the ones the control below runs through to <c>Completed</c>, and
    /// deliberately nothing else: a <c>sleep</c> between them would end a cancelled run by itself
    /// and hide whether this step did.
    /// </para>
    /// </summary>
    [Fact]
    public async Task CancelDuringNoRedeliveryStep_Canceled_NextStepsNotRun()
    {
        var run = await RunAndCancelDuringAsync("merge", Definition(Once(), Plain("readback"), Result()));

        Assert.Equal(WorkflowExecutionStatus.Canceled, run.Status);
        Assert.Null(run.Result);
        Assert.Equal(["merge"], run.ScheduledDelegates);
        Assert.Equal(["merge"], run.StartedDelegates);
        // The activity was told to stop, which is how the agent comes to be told.
        Assert.Contains(run.History.Events, e => e.EventType == EventType.ActivityTaskCancelRequested);

        await ReplayAsync(run.History);
    }

    /// <summary>
    /// The step's activity fails for a reason of its own, and the run is cancelled before the
    /// workflow has seen that failure: the two arrive together. A failure is what
    /// <c>ignoreFailure</c> is for, but the run was cancelled while the step was out, so the step
    /// ends as the cancellation and the run ends <c>Canceled</c>, not at the read-back.
    ///
    /// <para>
    /// The coincidence is made on purpose: an interceptor holds the activity's failure back until
    /// the run's cancellation has been requested, and only then lets the engine see it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ACancelThatArrivesWithTheStepsOwnFailure_StillEndsTheRunCanceled()
    {
        var definition = Definition(Once(), Plain("readback"), Result());
        var activities = new StubActivities(definition)
        {
            Behaviour = step => step == "merge"
                ? throw new ApplicationFailureException("the step failed on purpose", nonRetryable: true)
                : Task.FromResult(new AgentTaskResult($"{step} done", "completed")),
        };

        var run = await ExecuteAsync(
            definition, activities, input: null, cancelDuring: "merge",
            interceptor: new HoldADelegateFailureUntilTheRunIsCancelled(),
            beforeCancel: async handle =>
            {
                // The failure is recorded, and held: the run is still open.
                var deadline = Environment.TickCount64 + 30_000;
                while (!(await handle.FetchHistoryAsync()).Events.Any(e => e.EventType == EventType.ActivityTaskFailed))
                {
                    Assert.True(Environment.TickCount64 < deadline, "the step's activity never failed");
                    await Task.Delay(20);
                }
            });

        Assert.Contains(run.History.Events, e => e.EventType == EventType.ActivityTaskFailed);
        Assert.Equal(WorkflowExecutionStatus.Canceled, run.Status);
        Assert.Null(run.Result);
        Assert.Equal(["merge"], run.ScheduledDelegates);
    }

    /// <summary>The control: the same cancel at a step that has not opted in is swallowed.</summary>
    [Fact]
    public async Task WithoutNoRedelivery_ACancelDuringAnIgnoreFailureStep_IsSwallowed_AndTheRunCompletes()
    {
        var run = await RunAndCancelDuringAsync("merge", Definition(Plain("merge"), Plain("readback"), Result()));

        Assert.Equal(WorkflowExecutionStatus.Completed, run.Status);
        Assert.Equal("done", run.Result);
        Assert.Equal(["merge"], run.ScheduledDelegates);
    }

    // ── every other failure still follows ignoreFailure ───────────────────────

    public enum Failure { Throws, BudgetTimeout }

    /// <summary>
    /// No cancellation: the step throws, or reports a spent budget the way the real activity does
    /// (a <see cref="TimeoutException"/>). With <c>ignoreFailure</c> the run goes on to the
    /// read-back and completes; without it the run fails, and is not turned into a cancellation.
    /// </summary>
    [Theory]
    [InlineData(Failure.Throws, true)]
    [InlineData(Failure.BudgetTimeout, true)]
    [InlineData(Failure.Throws, false)]
    [InlineData(Failure.BudgetTimeout, false)]
    public async Task NoRedeliveryStepTimeoutOrThrowWithoutCancel_FollowsIgnoreFailure(Failure failure, bool ignoreFailure)
    {
        var definition = Definition(
            Once(ignoreFailure: ignoreFailure), Plain("readback"), Result("{{vars.merge_status}}|done"));
        var activities = new StubActivities(definition)
        {
            Behaviour = step =>
            {
                if (step != "merge") return Task.FromResult(new AgentTaskResult($"{step} done", "completed"));
                if (failure == Failure.Throws)
                    throw new ApplicationFailureException("the step failed on purpose", nonRetryable: true);
                throw new TimeoutException("Agent agent-a did not respond — elapsed 10.0m (budget 10.0m)");
            },
        };

        var run = await RunAsync(definition, activities);

        if (ignoreFailure)
        {
            Assert.Equal(WorkflowExecutionStatus.Completed, run.Status);
            // The status was reset before the attempt and the attempt never returned one.
            Assert.Equal("unknown|done", run.Result);
            Assert.Equal(["merge", "readback"], run.ScheduledDelegates);
        }
        else
        {
            Assert.Equal(WorkflowExecutionStatus.Failed, run.Status);
            Assert.Equal(["merge"], run.ScheduledDelegates);
        }

        // Tried once either way: the engine schedules a delegate with one attempt.
        Assert.Equal(1, activities.Calls.Count(c => c == "merge"));
    }

    /// <summary>An answer is not a failure at all: <c>failed</c> and <c>incomplete</c> are statuses the definition reads.</summary>
    [Theory]
    [InlineData("failed")]
    [InlineData("incomplete")]
    public async Task NoRedeliveryStep_AFailedOrIncompleteAnswer_IsReturnedOnce_AndTheRunGoesOn(string status)
    {
        var definition = Definition(Once(), Plain("readback"), Result("{{vars.merge_status}}|done"));
        var activities = new StubActivities(definition)
        {
            Behaviour = step => Task.FromResult(new AgentTaskResult($"{step} said so", step == "merge" ? status : "completed")),
        };

        var run = await RunAsync(definition, activities);

        Assert.Equal(WorkflowExecutionStatus.Completed, run.Status);
        Assert.Equal($"{status}|done", run.Result);
        Assert.Equal(["merge", "readback"], run.ScheduledDelegates);
        Assert.Equal(1, activities.Calls.Count(c => c == "merge"));
    }

    // ── the activity input ────────────────────────────────────────────────────

    /// <summary>
    /// The flag is an eighth positional argument, after an explicit budget of 0 and the repo (or
    /// null), and it is there only when the step sets it to true. A step without the field, and
    /// one that spells out <c>false</c>, schedule five arguments, or seven with a repo.
    /// </summary>
    [Fact]
    public async Task NoRedelivery_ActivityArgumentAppendedOnlyWhenTrue()
    {
        var definition = Definition(
            Plain("plain"),
            Plain("plain_repo", repo: "org/app"),
            Plain("explicit_false") with { NoRedelivery = false },
            Once("once"),
            Once("once_repo", repo: "{{input.Repo}}"),
            Once("once_blank_repo", repo: "{{input.Missing}}"),
            Result());

        var run = await RunAsync(definition, new StubActivities(definition), new { Repo = "org/app" });

        Assert.Equal(WorkflowExecutionStatus.Completed, run.Status);
        Assert.Equal(
            ["plain", "plain_repo", "explicit_false", "once", "once_repo", "once_blank_repo"], run.ScheduledDelegates);

        var inputs = DelegateHistoryScenario.DelegateInputs(run.History);
        Assert.Equal([5, 7, 5, 8, 8, 8], inputs.Select(p => p.Payloads_.Count).ToArray());

        // Seven, as before the field: the budget slot and the repo, and nothing after them.
        Assert.Equal(0, Decode<int>(inputs[1].Payloads_[5]));
        Assert.Equal("org/app", Decode<string>(inputs[1].Payloads_[6]));

        foreach (var (input, repo) in new[] { (inputs[3], (string?)null), (inputs[4], "org/app"), (inputs[5], null) })
        {
            Assert.Equal("agent-a", Decode<string>(input.Payloads_[0]));
            Assert.Equal("merge the change", Decode<string>(input.Payloads_[1]));
            Assert.False(Decode<bool>(input.Payloads_[3]));          // retryOnIncomplete
            Assert.Equal(0, Decode<int>(input.Payloads_[4]));        // maxIncompleteRetries
            Assert.Equal(0, Decode<int>(input.Payloads_[5]));        // agentBudgetSeconds: not explicit
            Assert.Equal(repo, Decode<string?>(input.Payloads_[6])); // repo, or null to reach the next slot
            Assert.True(Decode<bool>(input.Payloads_[7]));           // noRedelivery
        }

        Assert.Equal($"{run.WorkflowId}/once", Decode<string>(inputs[3].Payloads_[2]));

        await ReplayAsync(run.History);
    }

    /// <summary>
    /// The eight payloads bind to the real activity: its definition takes up to eight and still
    /// requires only the first three, so the five- and seven-payload callers bind as they did.
    /// </summary>
    [Fact]
    public void TheRealActivity_AcceptsTheEightPayloadsANoRedeliveryStepSchedules()
    {
        var method = typeof(DelegateToAgentActivity).GetMethod(nameof(DelegateToAgentActivity.DelegateToAgentAsync))!;
        var definition = ActivityDefinition.Create(method, _ => null);

        Assert.Equal(DelegateToAgentActivity.ActivityName, definition.Name);
        Assert.Equal(8, definition.ParameterTypes.Count);
        Assert.Equal(typeof(bool), definition.ParameterTypes.ElementAt(7));
        Assert.Equal(3, definition.RequiredParameterCount);
    }

    // ── recorded histories ────────────────────────────────────────────────────

    /// <summary>
    /// Every history this repository keeps from before the field replays against the current
    /// engine: none of them has a noRedelivery step, so none of the new code changes a command.
    /// </summary>
    [Theory]
    [InlineData(DelegateHistoryScenario.WorkflowId, DelegateHistoryScenario.FixturePath)]
    [InlineData(DelegateStatusVarHistoryScenario.WorkflowId, DelegateStatusVarHistoryScenario.FixturePath)]
    [InlineData(ParallelDelegateHistoryScenario.WorkflowId, ParallelDelegateHistoryScenario.FixturePath)]
    [InlineData(GateWaitHistoryScenario.WorkflowId, GateWaitHistoryScenario.FixturePath)]
    [InlineData(
        CrossNamespaceIdHistoryScenario.WorkflowId, CrossNamespaceIdHistoryScenario.FixturePath,
        CrossNamespaceIdHistoryScenario.Namespace, CrossNamespaceIdHistoryScenario.TaskQueue)]
    public async Task ARecordedHistory_StillReplays(
        string workflowId, string fixturePath, string? @namespace = null, string? taskQueue = null)
    {
        var history = WorkflowHistory.FromJson(
            workflowId, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, fixturePath)));

        // The one recording that was not made in the default namespace is replayed in its own.
        var options = new WorkflowReplayerOptions();
        if (@namespace is not null) options.Namespace = @namespace;
        if (taskQueue is not null) options.TaskQueue = taskQueue;

        await new WorkflowReplayer(options.AddWorkflow<UniversalWorkflow>()).ReplayWorkflowAsync(history);
    }

    // ── validator ─────────────────────────────────────────────────────────────

    private static InvalidOperationException Refused(StepDefinition root) =>
        Assert.Throws<InvalidOperationException>(() => WorkflowDefinitionValidator.Validate(root, Type));

    [Fact]
    public void Validator_NoRedelivery_RefusedOnEscalationStep()
    {
        var root = new SequenceStep
        {
            Steps =
            [
                new DelegateWithEscalationStep
                {
                    Name = "merge",
                    Target = "agent-a",
                    Instruction = "merge the change",
                    RetryOnIncomplete = false,
                    MaxIncompleteRetries = 0,
                    NoRedelivery = true,
                },
            ],
        };

        var ex = Refused(root);

        Assert.Contains($"'{Type}'", ex.Message);
        Assert.Contains("'merge'", ex.Message);
        Assert.Contains("delegate_with_escalation", ex.Message);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 3)]
    [InlineData(true, 3)]
    [InlineData(false, 1)]
    public void Validator_NoRedelivery_RefusedWithRetriesOn(bool retryOnIncomplete, int maxIncompleteRetries)
    {
        var root = Once() with { RetryOnIncomplete = retryOnIncomplete, MaxIncompleteRetries = maxIncompleteRetries };

        var ex = Refused(root);

        Assert.Contains($"'{Type}'", ex.Message);
        Assert.Contains("'merge'", ex.Message);
        Assert.Contains("\"retryOnIncomplete\": false", ex.Message);
        Assert.Contains("\"maxIncompleteRetries\": 0", ex.Message);
    }

    /// <summary>Both retry fields default to on, so a step that only sets the flag is refused.</summary>
    [Fact]
    public void Validator_NoRedelivery_RefusedWithRetriesOn_WhenTheyAreLeftAtTheirDefaults()
    {
        var ex = Refused(new DelegateStep { Name = "merge", Target = "agent-a", Instruction = "merge", NoRedelivery = true });

        Assert.Contains("retryOnIncomplete: true, maxIncompleteRetries: 3", ex.Message);
    }

    public static TheoryData<string, StepDefinition> UnderAnIgnoringAncestor() => new()
    {
        { "sequence", new SequenceStep { Name = "outer", IgnoreFailure = true, Steps = [Once()] } },
        { "loop", new LoopStep { Name = "outer", IgnoreFailure = true, Steps = [Once()] } },
        {
            "branch",
            new BranchStep { Name = "outer", IgnoreFailure = true, On = "{{vars.x}}", Cases = new() { ["a"] = Once() } }
        },
        {
            "branch",
            new BranchStep { Name = "outer", IgnoreFailure = true, On = "{{vars.x}}", Cases = new(), Default = Once() }
        },
        { "parallel", new ParallelStep { Name = "outer", IgnoreFailure = true, Steps = [Once(), new NoopStep()] } },
        {
            "parallel",
            new ParallelStep { Name = "outer", IgnoreFailure = true, ForEach = "{{input.Items}}", ItemVar = "item", Step = Once() }
        },
        {
            // Not the parent: two levels up, with steps that do not ignore in between.
            "sequence",
            new SequenceStep
            {
                Name = "outer",
                IgnoreFailure = true,
                Steps =
                [
                    new LoopStep
                    {
                        Name = "middle",
                        Steps = [new BranchStep { Name = "inner", On = "{{vars.x}}", Cases = new() { ["a"] = Once() } }],
                    },
                ],
            }
        },
    };

    [Theory]
    [MemberData(nameof(UnderAnIgnoringAncestor))]
    public void Validator_NoRedelivery_RefusedUnderIgnoreFailureAncestor(string ancestorType, StepDefinition root)
    {
        var ex = Refused(root);

        Assert.Contains($"'{Type}'", ex.Message);
        Assert.Contains("'merge'", ex.Message);
        Assert.Contains($"{ancestorType} step 'outer'", ex.Message);
        Assert.Contains("ignoreFailure", ex.Message);
    }

    /// <summary>
    /// A wait's notification is sent again on every reminder, and the wait handles its failure
    /// itself, so the promise cannot be kept there.
    /// </summary>
    [Fact]
    public void Validator_NoRedelivery_RefusedOnAWaitsNotifyStep()
    {
        var root = new WaitForSignalStep { Name = "gate", SignalName = "go", NotifyStep = Once("tell") };

        var ex = Refused(root);

        Assert.Contains("'tell'", ex.Message);
        Assert.Contains("notifyStep", ex.Message);
    }

    public static TheoryData<string, StepDefinition> Accepted() => new()
    {
        { "a plain delegate with retries off", Once(ignoreFailure: false) },
        { "its own ignoreFailure is allowed: only a cancellation is kept from it", Once(ignoreFailure: true) },
        {
            "under composites that do not ignore, beside a sibling that does",
            new SequenceStep
            {
                Steps =
                [
                    Plain("pin", ignoreFailure: true),
                    new LoopStep
                    {
                        Steps = [new BranchStep { On = "{{vars.x}}", Cases = new() { ["a"] = Once() }, Default = new NoopStep() }],
                    },
                    new SequenceStep { IgnoreFailure = true, Steps = [Plain("readback")] },
                ],
            }
        },
        {
            "the field absent or false: none of the rules apply",
            new SequenceStep
            {
                IgnoreFailure = true,
                Steps =
                [
                    new DelegateStep { Name = "a", Target = "agent-a", Instruction = "work" },
                    new DelegateStep { Name = "b", Target = "agent-a", Instruction = "work", NoRedelivery = false },
                    new DelegateWithEscalationStep { Name = "c", Target = "agent-a", Instruction = "work" },
                    new WaitForSignalStep
                    {
                        SignalName = "go",
                        NotifyStep = new DelegateStep { Target = "agent-a", Instruction = "waiting", IgnoreFailure = true },
                    },
                ],
            }
        },
    };

    [Theory]
    [MemberData(nameof(Accepted))]
    public void Validator_NoRedelivery_AcceptsWhatItCanKeep(string why, StepDefinition root)
    {
        Assert.NotNull(why);
        WorkflowDefinitionValidator.Validate(root, Type);
    }

    /// <summary>
    /// The rules run where every other one does, against the JSON a definition is stored as: the
    /// camel-cased field is read, a definition that can keep the promise loads with it set, and
    /// one that cannot fails to load.
    /// </summary>
    [Fact]
    public async Task ADefinitionWithNoRedelivery_IsReadFromJson_AndValidatedAtLoad()
    {
        const string good = """
            {"type":"sequence","steps":[
              {"type":"delegate","name":"merge","target":"agent-a","instruction":"merge the change",
               "noRedelivery":true,"retryOnIncomplete":false,"maxIncompleteRetries":0,"ignoreFailure":true}
            ]}
            """;

        var loaded = await LoadActivity(good).LoadAsync(Type);
        var merge = Assert.IsType<DelegateStep>(Assert.Single(Assert.IsType<SequenceStep>(loaded.Root).Steps));
        Assert.True(merge.NoRedelivery);

        var underAnIgnoringSequence = good.Replace("{\"type\":\"sequence\",", "{\"type\":\"sequence\",\"name\":\"outer\",\"ignoreFailure\":true,");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => LoadActivity(underAnIgnoringSequence).LoadAsync(Type));
        Assert.Contains("'merge'", ex.Message);
        Assert.Contains("'outer'", ex.Message);

        var asAnEscalation = good.Replace("\"type\":\"delegate\"", "\"type\":\"delegate_with_escalation\"");
        ex = await Assert.ThrowsAsync<InvalidOperationException>(() => LoadActivity(asAnEscalation).LoadAsync(Type));
        Assert.Contains("delegate_with_escalation", ex.Message);
    }

    // ── harness ───────────────────────────────────────────────────────────────

    private sealed record Run(
        string WorkflowId,
        WorkflowExecutionStatus Status,
        string? Result,
        WorkflowHistory History,
        IReadOnlyList<string> StartedDelegates)
    {
        /// <summary>The step name of every <c>DelegateToAgent</c> the run scheduled, in order.</summary>
        public IReadOnlyList<string> ScheduledDelegates =>
            [.. DelegateHistoryScenario.DelegateInputs(History).Select(p => Decode<string>(p.Payloads_[2]).Split('/')[^1])];
    }

    private static Task<Run> RunAsync(WorkflowDefinitionModel definition, StubActivities activities, object? input = null) =>
        ExecuteAsync(definition, activities, input, cancelDuring: null);

    /// <summary>
    /// Starts the run, waits until the named step's activity is executing, and cancels the
    /// workflow. That activity does not return until the run has closed: the engine has to act on
    /// the cancellation itself, not on the activity's result.
    /// </summary>
    private static Task<Run> RunAndCancelDuringAsync(string step, WorkflowDefinitionModel definition)
    {
        var activities = new StubActivities(definition);
        activities.Behaviour = async name =>
        {
            if (name == step) await activities.Released.WaitAsync(TimeSpan.FromSeconds(60));
            return new AgentTaskResult($"{name} done", "completed");
        };
        return ExecuteAsync(definition, activities, input: null, cancelDuring: step);
    }

    private static async Task<Run> ExecuteAsync(
        WorkflowDefinitionModel definition, StubActivities activities, object? input, string? cancelDuring,
        IWorkerInterceptor? interceptor = null, Func<WorkflowHandle, Task>? beforeCancel = null)
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var id = "no-redelivery-" + Guid.NewGuid().ToString("N");

        var options = new TemporalWorkerOptions(id)
            .AddWorkflow<UniversalWorkflow>()
            .AddAllActivities(activities.GetType(), activities);
        if (interceptor is not null) options.Interceptors = [interceptor];
        using var worker = new TemporalWorker(env.Client, options);

        string? result = null;
        await worker.ExecuteAsync(async () =>
        {
            try
            {
                var handle = await env.Client.StartWorkflowAsync(
                    Type,
                    new object?[] { JsonSerializer.SerializeToElement(input ?? new { }) },
                    new WorkflowOptions(id: id, taskQueue: id));

                if (cancelDuring is not null)
                {
                    await activities.StartedAsync(cancelDuring);
                    if (beforeCancel is not null) await beforeCancel(handle);
                    await handle.CancelAsync();
                }

                try
                {
                    result = await handle.GetResultAsync<string>().WaitAsync(TimeSpan.FromSeconds(60));
                }
                catch (WorkflowFailedException)
                {
                    // How it closed is read from the server, below.
                }
            }
            finally
            {
                activities.Release();
            }
        });

        var closed = env.Client.GetWorkflowHandle(id);
        return new Run(
            id,
            (await closed.DescribeAsync()).Status,
            result,
            await closed.FetchHistoryAsync(),
            activities.Calls);
    }

    private static async Task ReplayAsync(WorkflowHistory history)
    {
        var replayer = new WorkflowReplayer(new WorkflowReplayerOptions().AddWorkflow<UniversalWorkflow>());
        await replayer.ReplayWorkflowAsync(history);
    }

    private static T Decode<T>(Payload payload) => DataConverter.Default.PayloadConverter.ToValue<T>(payload);

    /// <summary>
    /// The activities a UWE run needs, with the delegate taking the real activity's full optional
    /// tail so it binds five, seven and eight payloads alike. What the engine scheduled is read
    /// from the history, not from here.
    /// </summary>
    private sealed class StubActivities(WorkflowDefinitionModel definition)
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _started = new();
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<string> _calls = new();

        /// <summary>What a delegation to the named step answers. Completes at once by default.</summary>
        public Func<string, Task<AgentTaskResult>> Behaviour { get; set; } =
            step => Task.FromResult(new AgentTaskResult($"{step} done", "completed"));

        /// <summary>The step name of every delegation that reached the activity, in order.</summary>
        public IReadOnlyList<string> Calls => [.. _calls];

        public Task Released => _released.Task;

        public void Release() => _released.TrySetResult();

        public Task StartedAsync(string step) => Signal(step).Task.WaitAsync(TimeSpan.FromSeconds(30));

        private TaskCompletionSource Signal(string step) =>
            _started.GetOrAdd(step, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        [Activity("LoadWorkflowDefinition")]
        public WorkflowDefinitionModel LoadDefinition(string _) => definition;

        [Activity("LoadWorkflowConfig")]
        public JsonElement LoadConfig() => JsonSerializer.SerializeToElement(new { });

        [Activity(DelegateToAgentActivity.ActivityName)]
        public async Task<AgentTaskResult> DelegateAsync(
            string target,
            string instruction,
            string taskId,
            bool retryOnIncomplete,
            int maxIncompleteRetries,
            int agentBudgetSeconds = 0,
            string? repo = null,
            bool noRedelivery = false)
        {
            var step = taskId.Split('/')[^1];
            _calls.Enqueue(step);
            Signal(step).TrySetResult();
            return await Behaviour(step);
        }
    }

    /// <summary>
    /// Makes a delegate's failure and the run's cancellation arrive together: the failure of a
    /// <c>DelegateToAgent</c> activity is not passed on to the workflow until the workflow's
    /// cancellation has been requested. Nothing else is touched.
    /// </summary>
    private sealed class HoldADelegateFailureUntilTheRunIsCancelled : IWorkerInterceptor
    {
        public WorkflowInboundInterceptor InterceptWorkflow(WorkflowInboundInterceptor nextInterceptor) =>
            new Inbound(nextInterceptor);

        private sealed class Inbound(WorkflowInboundInterceptor next) : WorkflowInboundInterceptor(next)
        {
            public override void Init(WorkflowOutboundInterceptor outbound) => base.Init(new Outbound(outbound));
        }

        private sealed class Outbound(WorkflowOutboundInterceptor next) : WorkflowOutboundInterceptor(next)
        {
            public override async Task<TResult> ScheduleActivityAsync<TResult>(ScheduleActivityInput input)
            {
                try
                {
                    return await base.ScheduleActivityAsync<TResult>(input);
                }
                catch (ActivityFailureException) when (
                    input.Activity == DelegateToAgentActivity.ActivityName && !Workflow.CancellationToken.IsCancellationRequested)
                {
                    // Woken by the token itself. (A wait condition on the token's state is not
                    // re-evaluated when the cancellation arrives.)
                    var cancelled = new TaskCompletionSource();
                    using (Workflow.CancellationToken.Register(() => cancelled.TrySetResult()))
                        await cancelled.Task;
                    throw;
                }
            }
        }
    }

    private static LoadWorkflowDefinitionActivity LoadActivity(string definitionJson)
    {
        var body = JsonSerializer.Serialize(new
        {
            name = Type,
            @namespace = "default",
            taskQueue = "test",
            definition = definitionJson,
            version = 1,
        });

        var client = new HttpClient(new StubHandler(body)) { BaseAddress = new Uri("http://orchestrator.invalid/") };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("orchestrator").Returns(client);
        return new LoadWorkflowDefinitionActivity(factory);
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}
