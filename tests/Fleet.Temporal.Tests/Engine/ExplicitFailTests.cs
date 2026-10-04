using System.Collections.Concurrent;
using System.Text.Json;
using Fleet.Temporal.Engine;
using Fleet.Temporal.Models;
using Temporalio.Activities;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Temporalio.Worker;

namespace Fleet.Temporal.Tests.Engine;

/// <summary>
/// #424 — the <c>fail</c> step: a deliberate failure that no <c>ignoreFailure</c> in the same run
/// can swallow, at any depth, while ordinary failures stay suppressible and failure stays per run.
///
/// <para>
/// Same harness as <c>SleepStepTests</c>: the production <see cref="UniversalWorkflow"/> in a
/// time-skipping environment, with the definition load mocked per workflow type so a child run can
/// get its own definition. <c>set_variable</c> leaves no trace in history, so every nested case also
/// puts a delegate after the parent: that delegate never being scheduled is the observable proof
/// that nothing after the parent ran.
/// </para>
/// </summary>
public sealed class ExplicitFailTests
{
    private const string RootType = "example-workflow";
    private const string ChildType = "example-child";

    // ── AC5: top-level fail ──────────────────────────────────────────────────

    [Fact]
    public async Task ATopLevelFail_FailsTheRun_WithTheResolvedMessage_AndSchedulesNothing()
    {
        var run = await RunAsync(
            new() { [RootType] = new FailStep { Name = "stop", Message = "stopped because {{input.Reason}}" } },
            input: new { Reason = "broken" });

        var failure = AssertExplicitFail(run);
        Assert.True(failure.NonRetryable);
        Assert.Equal("stopped because broken", failure.Message);

        var scheduled = run.History.Events
            .Where(e => e.EventType == EventType.ActivityTaskScheduled)
            .Select(e => e.ActivityTaskScheduledEventAttributes.ActivityType.Name)
            .ToHashSet();
        Assert.Subset(new HashSet<string> { "LoadWorkflowDefinition", "LoadWorkflowConfig" }, scheduled);
        Assert.DoesNotContain(run.History.Events, e => e.EventType == EventType.TimerStarted);
    }

    // ── AC6: nested, ignoreFailure on every ancestor ─────────────────────────

    public static TheoryData<string> NestedCases => new() { "a", "b", "c", "e", "f" };

    [Theory]
    [MemberData(nameof(NestedCases))]
    public async Task AFailUnderIgnoreFailureAncestors_StillFailsTheRun(string @case)
    {
        StepDefinition parent = @case switch
        {
            // (a) sequence → fail
            "a" => Seq(Fail()),
            // (b) branch (taken case) → sequence → fail
            "b" => Branch(Seq(Fail())),
            // (c) loop → fail on iteration 1; the body delegate counts iterations
            "c" => new LoopStep { IgnoreFailure = true, MaxIterations = 3, Steps = [Delegate("loop_body"), Fail()] },
            // (e) two levels: sequence → branch → fail
            "e" => Seq(Branch(Fail())),
            // (f) three levels: sequence → loop → sequence → fail
            "f" => Seq(new LoopStep { IgnoreFailure = true, MaxIterations = 3, Steps = [Seq(Fail())] }),
            _ => throw new ArgumentOutOfRangeException(nameof(@case)),
        };

        var run = await RunAsync(new() { [RootType] = WithSentinelsAfter(parent) });

        AssertExplicitFail(run);
        Assert.DoesNotContain("post_parent", run.Calls);
        if (@case == "c")
            Assert.Equal(1, run.Calls.Count(c => c == "loop_body"));
    }

    /// <summary>
    /// (d) static <c>parallel</c>: branch 1 fails, branch 2 is a delegate. Branch 2 is scheduled and
    /// completes — started siblings are not cancelled — and only then does the run fail.
    /// </summary>
    [Fact]
    public async Task AFailInAParallelBranch_LetsStartedSiblingsFinish_ThenFailsTheRun()
    {
        var parallel = new ParallelStep { IgnoreFailure = true, Steps = [Fail(), Delegate("sibling")] };

        var run = await RunAsync(new() { [RootType] = WithSentinelsAfter(parallel) });

        AssertExplicitFail(run);
        Assert.Contains("sibling", run.Calls);
        Assert.Single(run.History.Events, e => e.EventType == EventType.ActivityTaskCompleted && IsDelegate(run, e.ActivityTaskCompletedEventAttributes.ScheduledEventId));
        Assert.DoesNotContain("post_parent", run.Calls);
    }

    /// <summary>
    /// A parallel whose EARLIER branch fails ordinarily (no ignoreFailure on it) and whose later
    /// branch fails explicitly. WhenAll rethrows the first faulted branch, so without picking the
    /// unsuppressible failure the parallel's own ignoreFailure would swallow the explicit fail.
    /// Static and dynamic fan-out share the same await.
    /// </summary>
    [Theory]
    [InlineData("static")]
    [InlineData("dynamic")]
    public async Task AnOrdinaryFailureInAnEarlierBranch_DoesNotHideAFailInALaterOne(string shape)
    {
        var ordinaryFailure = new SleepStep { Name = "bad_sleep", Seconds = 0 };
        var parallel = shape == "static"
            ? new ParallelStep { IgnoreFailure = true, Steps = [ordinaryFailure, Fail()] }
            : new ParallelStep
            {
                IgnoreFailure = true,
                ForEach = "first,second",
                ItemVar = "item",
                Step = new BranchStep
                {
                    On = "{{vars.item}}",
                    Cases = new() { ["first"] = ordinaryFailure, ["second"] = Fail() },
                },
            };

        var run = await RunAsync(new() { [RootType] = WithSentinelsAfter(parallel) });

        AssertExplicitFail(run);
        Assert.DoesNotContain("post_parent", run.Calls);
    }

    // ── AC7: ordinary failures stay suppressible ─────────────────────────────

    [Fact]
    public async Task AnOrdinaryNonRetryableFailure_IsStillSuppressedByIgnoreFailure()
    {
        var run = await RunAsync(new()
        {
            [RootType] = new SequenceStep
            {
                Steps =
                [
                    new SequenceStep { IgnoreFailure = true, Steps = [new SleepStep { Name = "bad_sleep", Seconds = 0 }] },
                    new SetVariableStep { Vars = new() { ["_result"] = "reached_success" } },
                ],
            },
        });

        Assert.Null(run.Failure);
        Assert.Equal("reached_success", run.Result);
    }

    // ── AC8: failure is per run ──────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AFailInAChild_FailsTheChild_AndTheParentHandlesItAsBefore(bool parentIgnoresFailure)
    {
        var run = await RunAsync(new()
        {
            [RootType] = new SequenceStep
            {
                Steps =
                [
                    new ChildWorkflowStep { Name = "child", WorkflowType = ChildType, IgnoreFailure = parentIgnoresFailure },
                    new SetVariableStep { Vars = new() { ["_result"] = "reached_success" } },
                ],
            },
            [ChildType] = new FailStep { Name = "child_stop", Message = "child failed on purpose" },
        });

        // The child closed Failed with ExplicitFail.
        var childId = run.History.Events
            .Single(e => e.EventType == EventType.ChildWorkflowExecutionStarted)
            .ChildWorkflowExecutionStartedEventAttributes.WorkflowExecution.WorkflowId;
        var childClose = run.ChildHistories[childId].Events.Last();
        Assert.Equal(EventType.WorkflowExecutionFailed, childClose.EventType);
        Assert.Equal(UniversalWorkflow.ExplicitFailErrorType,
            childClose.WorkflowExecutionFailedEventAttributes.Failure.ApplicationFailureInfo.Type);

        if (parentIgnoresFailure)
        {
            Assert.Null(run.Failure);
            Assert.Equal("reached_success", run.Result);
        }
        else
        {
            // Existing behaviour: the parent fails with the SDK's child failure, not ExplicitFail.
            var failed = Assert.IsType<WorkflowFailedException>(run.Failure);
            Assert.IsType<ChildWorkflowFailureException>(failed.InnerException);
        }
    }

    // ── harness ──────────────────────────────────────────────────────────────

    private static FailStep Fail() => new() { Name = "stop", Message = "stopped on purpose" };

    private static SequenceStep Seq(params StepDefinition[] steps) => new() { IgnoreFailure = true, Steps = steps };

    private static BranchStep Branch(StepDefinition taken) => new()
    {
        IgnoreFailure = true,
        On = "go",
        Cases = new() { ["go"] = taken },
    };

    private static DelegateStep Delegate(string name) => new() { Name = name, Target = "agent1", Instruction = "work" };

    /// <summary>The parent, then the sentinel and the post-parent delegate, all under an ignoreFailure root.</summary>
    private static SequenceStep WithSentinelsAfter(StepDefinition parent) => new()
    {
        IgnoreFailure = true,
        Steps =
        [
            parent,
            new SetVariableStep { Vars = new() { ["reached_success"] = "true" } },
            Delegate("post_parent"),
        ],
    };

    private static ApplicationFailureException AssertExplicitFail(Run run)
    {
        var failed = Assert.IsType<WorkflowFailedException>(run.Failure);
        var cause = Assert.IsType<ApplicationFailureException>(failed.InnerException);
        Assert.Equal(UniversalWorkflow.ExplicitFailErrorType, cause.ErrorType);
        return cause;
    }

    private static bool IsDelegate(Run run, long scheduledEventId) =>
        run.History.Events.Any(e => e.EventId == scheduledEventId
            && e.ActivityTaskScheduledEventAttributes?.ActivityType?.Name == "DelegateToAgent");

    private sealed record Run(
        string? Result,
        Exception? Failure,
        WorkflowHistory History,
        IReadOnlyDictionary<string, WorkflowHistory> ChildHistories,
        IReadOnlyList<string> Calls);

    private static async Task<Run> RunAsync(Dictionary<string, StepDefinition> definitions, object? input = null)
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var taskQueue = $"explicit-fail-{Guid.NewGuid():N}";
        var workflowId = $"explicit-fail-{Guid.NewGuid():N}";
        var activities = new ExplicitFailActivities(definitions);

        string? result = null;
        Exception? failure = null;

        using var worker = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions(taskQueue)
                .AddWorkflow<UniversalWorkflow>()
                .AddAllActivities(activities.GetType(), activities));

        await worker.ExecuteAsync(async () =>
        {
            var handle = await env.Client.StartWorkflowAsync(
                RootType,
                input is null ? Array.Empty<object?>() : [JsonSerializer.SerializeToElement(input)],
                new WorkflowOptions(workflowId, taskQueue)
                {
                    RetryPolicy = new Temporalio.Common.RetryPolicy { MaximumAttempts = 1 },
                });
            try
            {
                result = await handle.GetResultAsync<string?>().WaitAsync(TimeSpan.FromSeconds(60));
            }
            catch (WorkflowFailedException ex)
            {
                failure = ex;
            }
        });

        var history = await env.Client.GetWorkflowHandle(workflowId).FetchHistoryAsync();
        var children = new Dictionary<string, WorkflowHistory>();
        foreach (var started in history.Events.Where(e => e.EventType == EventType.ChildWorkflowExecutionStarted))
        {
            var childId = started.ChildWorkflowExecutionStartedEventAttributes.WorkflowExecution.WorkflowId;
            children[childId] = await env.Client.GetWorkflowHandle(childId).FetchHistoryAsync();
        }

        return new Run(result, failure, history, children, activities.Calls);
    }
}

// ---------------------------------------------------------------------------
// Test-only activity stubs
// ---------------------------------------------------------------------------

/// <summary>
/// Returns a definition per workflow type, so a child run loads its own, and records every
/// delegation by step name. Delegations always complete.
/// </summary>
file sealed class ExplicitFailActivities(Dictionary<string, StepDefinition> definitions)
{
    private readonly ConcurrentQueue<string> _calls = new();

    public IReadOnlyList<string> Calls => _calls.ToList();

    [Activity("LoadWorkflowDefinition")]
    public WorkflowDefinitionModel LoadDefinition(string workflowType) => new()
    {
        Name = workflowType,
        Namespace = "default",
        TaskQueue = "test",
        Root = definitions[workflowType],
    };

    [Activity("LoadWorkflowConfig")]
    public JsonElement LoadConfig() => JsonSerializer.SerializeToElement(new { });

    [Activity("DelegateToAgent")]
    public AgentTaskResult Delegate(string target, string instruction, string taskId, bool retryOnIncomplete, int maxIncompleteRetries)
    {
        _calls.Enqueue(taskId.Split('/').LastOrDefault() ?? taskId);
        return new AgentTaskResult("done", "completed");
    }
}
