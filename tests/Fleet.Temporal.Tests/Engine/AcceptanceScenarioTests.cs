using System.Collections.Concurrent;
using System.Text.Json;
using Fleet.Temporal.Activities;
using Fleet.Temporal.Engine;
using Fleet.Temporal.Models;
using Temporalio.Activities;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Temporalio.Worker;

namespace Fleet.Temporal.Tests.Engine;

// The production dynamic engine interprets the shipped seed; only owner/status IO is stubbed.
public sealed partial class AcceptanceScenarioTests
{
    private static readonly string Head = new('a', 40);

    [Theory]
    [InlineData("done", "passed", "none")]
    [InlineData("product_fail", "failed", "product_fail")]
    [InlineData("harness_fail", "failed", "harness_fail")]
    [InlineData("env_blocked", "failed", "env_blocked")]
    [InlineData("head_changed", "head_changed", "none")]
    public async Task AcceptanceChild_StageOutcomeAlwaysCleansAndPreservesEvidence(string stage, string outcome, string report)
    {
        var result = await Run("UwePrAcceptanceWorkflow", Input(), c => c.Step switch
        {
            "pre_stage" => new(Stage(stage), "completed"),
            "cleanup" => new("CLEANUP: verified\nRECEIPT: https://example.com/cleanup", "completed"),
            _ => new("CHILD: running", "completed"),
        });
        Assert.Contains($"ACCEPTANCE: {outcome}", result.Text);
        Assert.Contains("CLEANUP: verified", result.Text);
        Assert.Contains($"OWNER_REPORT: {report}", result.Text);
        Assert.Single(result.Calls, c => c.Step == "pre_stage");
        Assert.Single(result.Calls, c => c.Step == "cleanup");
        Assert.DoesNotContain(result.Calls, c => c.Step == "post_stage");
        var backstop = Assert.Single(result.History.Events.Where(e => e.EventType == EventType.StartChildWorkflowExecutionInitiated));
        Assert.Equal(ParentClosePolicy.Abandon, backstop.StartChildWorkflowExecutionInitiatedEventAttributes.ParentClosePolicy);
        var firstOwner = result.History.Events.First(e => e.EventType == EventType.ActivityTaskScheduled
            && e.ActivityTaskScheduledEventAttributes.ActivityType.Name == DelegateToAgentActivity.ActivityName);
        Assert.True(backstop.EventId < firstOwner.EventId);
        Assert.Equal(1, firstOwner.ActivityTaskScheduledEventAttributes.RetryPolicy.MaximumAttempts);
        Assert.Equal(62 * 60, firstOwner.ActivityTaskScheduledEventAttributes.StartToCloseTimeout.Seconds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptanceChild_ThrownOrFailedOwnerIsNeverResentOrSentCleanup(bool throws)
    {
        var result = await Run("UwePrAcceptanceWorkflow", Input(), c =>
        {
            if (c.Step == "pre_stage")
            {
                if (throws) throw new ApplicationFailureException("owned fixture failure", errorType: "SyntheticFailure", nonRetryable: true);
                return new(Stage("done"), "failed"); // Even a pass-looking body cannot override failure status.
            }
            return new("CHILD: running", "completed");
        });
        Assert.Contains("ACCEPTANCE: blocked", result.Text);
        Assert.Contains("REASON: owner_timeout", result.Text);
        Assert.Contains("CLEANUP: deferred", result.Text);
        Assert.Single(result.Calls, c => c.Step == "pre_stage");
        Assert.DoesNotContain(result.Calls, c => c.Step is "post_stage" or "cleanup");
        Assert.Single(result.History.Events.Where(e => e.EventType == EventType.StartChildWorkflowExecutionInitiated));
    }

    [Fact]
    public async Task AcceptanceChild_RealActivityTimeoutDefersCleanupWithoutAnotherOwnerCall()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var owner = new HangingOwnerActivities();
        using var worker = new TemporalWorker(env.Client, new TemporalWorkerOptions("fleet")
            .AddWorkflow<UniversalWorkflow>().AddAllActivities(new ScenarioActivities()).AddAllActivities(owner));
        await worker.ExecuteAsync(async () =>
        {
            var handle = await env.Client.StartWorkflowAsync("UwePrAcceptanceWorkflow", [Input()],
                new WorkflowOptions($"owner-timeout-{Guid.NewGuid():N}", "fleet"));
            await owner.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // Time skipping is locked while an activity runs; exercise the real two-minute
            // heartbeat deadline in bounded foreground time, not a fabricated failure.
            var result = await handle.GetResultAsync<string>().WaitAsync(TimeSpan.FromSeconds(150));
            Assert.Contains("REASON: owner_timeout", result);
            Assert.Contains("CLEANUP: deferred", result);
            Assert.Equal(["pre_stage"], owner.Calls.ToArray());
            var history = await handle.FetchHistoryAsync();
            Assert.Single(history.Events.Where(e => e.EventType == EventType.ActivityTaskTimedOut));
            Assert.Single(history.Events.Where(e => e.EventType == EventType.StartChildWorkflowExecutionInitiated));
        });
    }

    [Theory]
    [InlineData("residue", "blocked")]
    [InlineData("unknown", "blocked")]
    [InlineData("ALREADY_CLEAN", "passed")]
    public async Task AcceptanceChild_CleanupMustBeVerifiedOrAlreadyClean(string cleanup, string outcome)
    {
        var result = await Run("UwePrAcceptanceWorkflow", Input(), c => c.Step switch
        {
            "pre_stage" => new(Stage("done"), "completed"),
            "cleanup" => new(cleanup == "ALREADY_CLEAN" ? cleanup : $"CLEANUP: {cleanup}", "completed"),
            _ => new("CHILD: running", "completed"),
        });
        Assert.Contains($"ACCEPTANCE: {outcome}", result.Text);
        if (outcome == "blocked") Assert.Contains("REASON: cleanup_residue", result.Text);
    }

    [Fact]
    public async Task AcceptanceChild_DurableWaitAndPrePostStageBoundsComeFromInput()
    {
        var input = Input(); input["WaitSeconds"] = 60; input["PostStages"] = 1;
        var result = await Run("UwePrAcceptanceWorkflow", input, c => new(
            c.Step == "cleanup" ? "CLEANUP: verified" : Stage("pass"), "completed"));
        Assert.Contains("ACCEPTANCE: passed", result.Text);
        Assert.Equal(4, result.Calls.Count(c => c.Step == "pre_stage"));
        Assert.Equal(4, result.Calls.Count(c => c.Step == "post_stage"));
        var timer = Assert.Single(result.History.Events.Where(e => e.EventType == EventType.TimerStarted));
        Assert.Equal(60, timer.TimerStartedEventAttributes.StartToFireTimeout.Seconds);
    }

    [Theory]
    [InlineData("running", "completed", 3)]
    [InlineData("closed", "failed", 1)]
    [InlineData("unknown", "completed", 1)]
    public async Task CleanupBackstop_RunningUnknownOrFailedStatusNeverDispatchesCleanup(string child, string status, int checks)
    {
        var result = await Run("UweAcceptanceCleanupWorkflow", Input(), c => new($"CHILD: {child}", status));
        Assert.Equal(checks, result.Calls.Count(c => c.Step == "child_status"));
        Assert.DoesNotContain(result.Calls, c => c.Step == "backstop_cleanup");
        Assert.Single(result.Calls, c => c.Step == "notify_live_or_unknown_child");
        var timers = result.History.Events.Where(e => e.EventType == EventType.TimerStarted).ToList();
        Assert.Equal(checks, timers.Count);
        Assert.Equal(5400, timers[0].TimerStartedEventAttributes.StartToFireTimeout.Seconds);
        Assert.All(timers.Skip(1), e => Assert.Equal(1800, e.TimerStartedEventAttributes.StartToFireTimeout.Seconds));
    }

    [Theory]
    [InlineData("CLEANUP: verified", "completed", false)]
    [InlineData("ALREADY_CLEAN", "completed", false)]
    [InlineData("CLEANUP: residue", "completed", true)]
    [InlineData("CLEANUP: verified", "failed", true)]
    public async Task CleanupBackstop_ConfirmedClosedChildGetsOneIdempotentCleanup(string cleanup, string status, bool notified)
    {
        var result = await Run("UweAcceptanceCleanupWorkflow", Input(), c => c.Step == "child_status"
            ? new("CHILD: closed", "completed") : new(cleanup, status));
        Assert.Single(result.Calls, c => c.Step == "backstop_cleanup");
        Assert.Equal(notified, result.Calls.Any(c => c.Target == "agent1" && c.Step != "child_status"));
    }

    private static string Stage(string result) =>
        $"ATTEMPT: attempt-example\nHEAD: {Head}\nSTAGE_RESULT: {result}\nREASON: synthetic\nRECEIPT: https://example.com/stage";

    private static Dictionary<string, object?> Input() => new()
    {
        ["Repo"] = "example-org/example-repo",
        ["PrNumber"] = 7,
        ["HeadOid"] = Head,
        ["Owner"] = "agent3",
        ["AttemptId"] = "attempt-example",
        ["PinnedAt"] = "2026-01-01T00:00:00Z",
        ["PlanSha256"] = new string('b', 64),
        ["MaxDurationSeconds"] = 3600,
        ["WaitSeconds"] = 0,
        ["PostStages"] = 0,
        ["HarnessRetry"] = false,
        ["ChildWorkflowId"] = "child-example",
        ["SshTargets"] = "none",
        ["CapabilityMapCommit"] = "none",
        ["CapabilityMapSha256"] = "none",
    };

    private static async Task<ScenarioResult> Run(string name, Dictionary<string, object?> input, Func<Call, AgentTaskResult> respond)
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var calls = new ConcurrentQueue<Call>();
        var loader = new ScenarioActivities();
        var stub = ActivityDefinition.Create(DelegateToAgentActivity.ActivityName, typeof(AgentTaskResult),
            [typeof(string), typeof(string), typeof(string), typeof(bool), typeof(int), typeof(int), typeof(string)], 3,
            args =>
            {
                var call = new Call(((string)args[2]!).Split('/').Last(), (string)args[0]!, (string)args[1]!);
                calls.Enqueue(call);
                Assert.False((bool)args[3]!); Assert.Equal(0, (int)args[4]!);
                return respond(call);
            });
        using var worker = new TemporalWorker(env.Client, new TemporalWorkerOptions("fleet")
            .AddWorkflow<UniversalWorkflow>().AddAllActivities(loader).AddActivity(stub));
        ScenarioResult? result = null;
        await worker.ExecuteAsync(async () =>
        {
            var handle = await env.Client.StartWorkflowAsync(name, [input], new WorkflowOptions($"acceptance-{Guid.NewGuid():N}", "fleet"));
            var text = await handle.GetResultAsync<string?>().WaitAsync(TimeSpan.FromSeconds(30));
            result = new(text ?? "", await handle.FetchHistoryAsync(), calls.ToList());
        });
        return result!;
    }

    private sealed class HangingOwnerActivities
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<string> Calls { get; } = new();
        [Activity("DelegateToAgent")]
        public async Task<AgentTaskResult> Delegate(string target, string instruction, string taskId,
            bool retryOnIncomplete, int maxIncompleteRetries, int agentBudgetSeconds = 0, string? repo = null)
        {
            var step = taskId.Split('/').Last();
            if (target != "agent3") return new(step == "child_status" ? "CHILD: running" : "ok", "completed");
            Calls.Enqueue(step);
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ActivityExecutionContext.Current.CancellationToken);
            throw new InvalidOperationException("unreachable fixture completion");
        }
    }

    private sealed record ScenarioResult(string Text, Temporalio.Common.WorkflowHistory History, List<Call> Calls);
    private sealed class ScenarioActivities
    {
        [Activity("LoadWorkflowDefinition")]
        public WorkflowDefinitionModel Load(string name) => new() { Name = name, Namespace = "default", TaskQueue = "fleet", Root = SeedHarness.Parse(name) };
        [Activity("LoadWorkflowConfig")]
        public JsonElement Config() => JsonSerializer.SerializeToElement(new { CtoAgent = "agent1" });
    }
}
