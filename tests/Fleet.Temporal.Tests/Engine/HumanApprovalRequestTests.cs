using System.Collections.Concurrent;
using System.Text.Json;
using Fleet.Temporal.Engine;
using Fleet.Temporal.Models;
using Temporalio.Activities;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.OperatorService.V1;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Converters;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Temporalio.Worker;

namespace Fleet.Temporal.Tests.Engine;

public class HumanApprovalRequestTests
{
    private const string Type = HumanApprovalRequestContract.WorkflowType;
    private const string Signal = HumanApprovalRequestContract.Signal;
    private const string Literal = " \"quoted\"\nКыргыз \\ {{config.Secret}} ";

    [Fact]
    public void Seed_ExistingPrimitivesAndRequiredGuard_Validate()
    {
        var steps = WorkflowDefinitionValidator.Walk(SeedHarness.Parse(Type)).ToList();
        var wait = Assert.Single(steps.OfType<WaitForSignalStep>());
        Assert.Equal("approval_visit", wait.VisitVar);
        Assert.Equal("Ref", wait.DelegatedGuard!.Marker);
        Assert.Equal("{{vars.approval_visit}}", wait.DelegatedGuard.Require!["VisitId"]);
        Assert.Null(wait.NotifyStep);
        Assert.Equal(1440, wait.TimeoutMinutes);
        var callback = Assert.Single(steps.OfType<DelegateStep>());
        Assert.Equal("{{input.RequesterAgent}}", callback.Target);
        Assert.False(callback.RetryOnIncomplete);
        Assert.True(callback.IgnoreFailure);
    }

    [Theory]
    [InlineData("approved", "matching", "acknowledged")]
    [InlineData("rejected", "matching", "acknowledged")]
    [InlineData("approved", "wrong", "unknown")]
    [InlineData("approved", "failure", "unknown")]
    [InlineData("approved", "missing", "unknown")]
    [InlineData("approved", "incomplete", "unknown")]
    [InlineData("approved", "extra-line", "unknown")]
    [InlineData("malformed", "matching", "acknowledged")]
    [InlineData("case-variant", "matching", "acknowledged")]
    public async Task Decision_SeedReturnsStringAndOneReportOnlyCallback(string decision, string receipt, string delivery)
    {
        var run = await Run(receipt, async (env, handle, activities) =>
        {
            await Parked(handle);
            await new WorkflowReplayer(new WorkflowReplayerOptions { Namespace = "fleet", TaskQueue = "fleet" }
                .AddWorkflow<UniversalWorkflow>()).ReplayWorkflowAsync(await handle.FetchHistoryAsync());
            await Assert.ThrowsAsync<WorkflowAlreadyStartedException>(() => env.Client.StartWorkflowAsync(Type, [Input()],
                new WorkflowOptions(handle.Id, "fleet") { IdReusePolicy = WorkflowIdReusePolicy.RejectDuplicate }));
            // A stale gate/visit cannot consume or reset the single wait.
            await handle.SignalAsync(Signal, [JsonSerializer.SerializeToElement(new { Decision = "approved", Ref = "old", VisitId = "old" })]);
            object payload = decision switch
            {
                "malformed" => new { Decision = 7, Ref = handle.ResultRunId, VisitId = Signal + ":1" },
                "case-variant" => new { decision = "approved", Ref = handle.ResultRunId, VisitId = Signal + ":1" },
                _ => new { Decision = decision, Comment = Literal, Ref = handle.ResultRunId, VisitId = Signal + ":1" }
            };
            await handle.SignalAsync(Signal, [JsonSerializer.SerializeToElement(payload)]);
            // First valid decision only; conflicting buffered data must not cause a second callback.
            await handle.SignalAsync(Signal, [JsonSerializer.SerializeToElement(new { Decision = "rejected", Ref = handle.ResultRunId, VisitId = Signal + ":1" })]);
        });
        var expected = decision is "malformed" or "case-variant" ? "invalid" : decision;
        var result = JsonSerializer.Deserialize<JsonElement>(run.Result);
        Assert.Equal(expected, result.GetProperty("Decision").GetString());
        Assert.Equal(delivery, result.GetProperty("Delivery").GetString());
        Assert.Equal(Literal, result.GetProperty("Request").GetProperty("Action").GetString());
        Assert.Equal(Literal, result.GetProperty("Request").GetProperty("Scope").GetString());
        Assert.Equal(run.Id, result.GetProperty("WorkflowId").GetString());
        Assert.Equal(run.RunId, result.GetProperty("RunId").GetString());
        if (expected is "approved" or "rejected") Assert.Equal(Literal, result.GetProperty("DecisionPayload").GetProperty("Comment").GetString());
        var callback = Assert.Single(run.Activities.Calls);
        Assert.Equal("agent1", callback.Target);
        Assert.EndsWith("/notify-requester", callback.TaskId);
        Assert.Contains("Report only", callback.Instruction);
        var snapshot = JsonSerializer.Deserialize<JsonElement>(callback.Instruction.Split('\n').Last());
        Assert.Equal("unknown", snapshot.GetProperty("Delivery").GetString());
        Assert.Equal(expected, snapshot.GetProperty("Decision").GetString());
        var timers = run.History.Events.Where(e => e.EventType == EventType.TimerStarted).ToArray();
        Assert.NotEmpty(timers);
        var end = timers[0].EventTime.ToDateTime() + timers[0].TimerStartedEventAttributes.StartToFireTimeout.ToTimeSpan();
        Assert.All(timers, e => Assert.InRange(
            (e.EventTime.ToDateTime() + e.TimerStartedEventAttributes.StartToFireTimeout.ToTimeSpan() - end).TotalSeconds, -1, 1));
        await new WorkflowReplayer(new WorkflowReplayerOptions { Namespace = "fleet", TaskQueue = "fleet" }.AddWorkflow<UniversalWorkflow>()).ReplayWorkflowAsync(run.History);
    }

    [Fact]
    public async Task Expiry_OneDurableDeadlineAndOneExpiredCallback()
    {
        var run = await Run("matching", (_, _, _) => Task.CompletedTask);
        var result = JsonSerializer.Deserialize<JsonElement>(run.Result);
        Assert.Equal("expired", result.GetProperty("Decision").GetString());
        Assert.Equal("timeout", result.GetProperty("DecisionPayload").GetProperty("Decision").GetString());
        Assert.Single(run.Activities.Calls);
        var timer = Assert.Single(run.History.Events, e => e.EventType == EventType.TimerStarted);
        Assert.Equal(TimeSpan.FromDays(1), timer.TimerStartedEventAttributes.StartToFireTimeout.ToTimeSpan());
        Assert.DoesNotContain(run.History.Events, e => e.EventType == EventType.SignalExternalWorkflowExecutionInitiated);
    }

    [Fact]
    public async Task AlternateStart_WithoutMemo_FailsBeforeAnyActivityOrGate()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync(new() { Namespace = "fleet" });
        using var worker = new TemporalWorker(env.Client, new TemporalWorkerOptions("fleet").AddWorkflow<UniversalWorkflow>());
        await worker.ExecuteAsync(async () =>
        {
            var handle = await env.Client.StartWorkflowAsync(Type, [Input()], new WorkflowOptions(Id(), "fleet"));
            await Assert.ThrowsAsync<WorkflowFailedException>(() => handle.GetResultAsync<string>());
            var history = await handle.FetchHistoryAsync();
            Assert.DoesNotContain(history.Events, e => e.EventType is EventType.ActivityTaskScheduled or EventType.UpsertWorkflowSearchAttributes);
        });
    }

    [Fact]
    public async Task Cancellation_WhileParked_NoRequesterCallback()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync(new() { Namespace = "fleet" });
        await env.Client.Connection.OperatorService.AddSearchAttributesAsync(new AddSearchAttributesRequest
        { Namespace = "fleet", SearchAttributes = { ["Phase"] = IndexedValueType.Keyword, ["GateRef"] = IndexedValueType.Keyword, ["GateVisit"] = IndexedValueType.Keyword } });
        var activities = new ApprovalActivities(new WorkflowDefinitionModel
        { Name = Type, Namespace = "fleet", TaskQueue = "fleet", Root = SeedHarness.Parse(Type) }, "matching");
        using var worker = new TemporalWorker(env.Client, new TemporalWorkerOptions("fleet").AddWorkflow<UniversalWorkflow>().AddAllActivities(activities));
        await worker.ExecuteAsync(async () =>
        {
            var handle = await env.Client.StartWorkflowAsync(Type, [Input()], new WorkflowOptions(Id(), "fleet")
            { Memo = new Dictionary<string, object> { [HumanApprovalRequestContract.RequesterMemo] = "agent1" } });
            await Parked(handle);
            await handle.CancelAsync();
            await Assert.ThrowsAsync<WorkflowFailedException>(() => handle.GetResultAsync<string>());
            Assert.Equal(WorkflowExecutionStatus.Canceled, (await handle.DescribeAsync()).Status);
            Assert.Empty(activities.Calls);
        });
    }

    [Theory]
    [InlineData("approved", "approved")]
    [InlineData("rejected", "rejected")]
    [InlineData("stale", "expired")]
    public async Task Decision_CoBatchedWithFiredTimer_UsesInheritedGuardOrdering(string decision, string expected)
    {
        var run = await RunWithWorkerStopped(async (env, handle) =>
        {
            await handle.SignalAsync(Signal, [JsonSerializer.SerializeToElement(new
            {
                Decision = decision == "stale" ? "approved" : decision,
                Ref = handle.ResultRunId, VisitId = decision == "stale" ? "old-visit" : Signal + ":1"
            })]);
        }, fireTimer: true);
        var timer = Assert.Single(run.History.Events, e => e.EventType == EventType.TimerFired);
        var signal = Assert.Single(run.History.Events, e => e.EventType == EventType.WorkflowExecutionSignaled);
        Assert.True(timer.EventId < signal.EventId);
        // No worker consumed the timer before the decision arrived: both enter the next task.
        Assert.DoesNotContain(run.History.Events, e => e.EventType == EventType.WorkflowTaskStarted &&
            e.EventId > timer.EventId && e.EventId < signal.EventId);
        Assert.Equal(expected, JsonSerializer.Deserialize<JsonElement>(run.Result!).GetProperty("Decision").GetString());
        Assert.Single(run.Activities.Calls);
        await Replay(run.History);
    }

    [Fact]
    public async Task Decision_AfterRecordedExpiry_CannotReplaceExpiredResultOrCallback()
    {
        var run = await Run("matching", async (env, handle, activities) =>
        {
            activities.HoldNotification = true;
            try
            {
                await Parked(handle);
                var advance = env.DelayAsync(TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1));
                await activities.Notifying.Task.WaitAsync(TimeSpan.FromSeconds(20));
                var history = await handle.FetchHistoryAsync();
                Assert.Contains(history.Events, e => e.EventType == EventType.TimerFired);
                var callback = Assert.Single(activities.Calls);
                Assert.Equal("expired", JsonSerializer.Deserialize<JsonElement>(callback.Instruction.Split('\n').Last()).GetProperty("Decision").GetString());
                await handle.SignalAsync(Signal, [JsonSerializer.SerializeToElement(new
                { Decision = "approved", Ref = handle.ResultRunId, VisitId = Signal + ":1" })]);
                activities.ReleaseNotification.TrySetResult();
                await advance.WaitAsync(TimeSpan.FromSeconds(20));
            }
            finally { activities.ReleaseNotification.TrySetResult(); }
        });
        Assert.Equal("expired", JsonSerializer.Deserialize<JsonElement>(run.Result).GetProperty("Decision").GetString());
        Assert.Single(run.Activities.Calls);
        var fired = Assert.Single(run.History.Events, e => e.EventType == EventType.TimerFired);
        var late = Assert.Single(run.History.Events, e => e.EventType == EventType.WorkflowExecutionSignaled);
        Assert.True(late.EventId > fired.EventId);
        await Replay(run.History);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Cancellation_CoBatchedWithDecision_NoApprovalOrRequesterCallback(bool cancelFirst, bool fireTimer)
    {
        var run = await RunWithWorkerStopped(async (_, handle) =>
        {
            if (cancelFirst) await handle.CancelAsync();
            await handle.SignalAsync(Signal, [JsonSerializer.SerializeToElement(new
            { Decision = "approved", Ref = handle.ResultRunId, VisitId = Signal + ":1" })]);
            if (!cancelFirst) await handle.CancelAsync();
        }, canceled: true, fireTimer: fireTimer);
        var canceled = Assert.Single(run.History.Events, e => e.EventType == EventType.WorkflowExecutionCancelRequested);
        var signal = Assert.Single(run.History.Events, e => e.EventType == EventType.WorkflowExecutionSignaled);
        Assert.Equal(cancelFirst, canceled.EventId < signal.EventId);
        // Check the actual event batch before interpreting the terminal outcome. In particular,
        // a result assertion in the fixture must not hide the history of a failed CI race.
        var activation = Assert.Single(run.History.Events.Where(e =>
            e.EventType == EventType.WorkflowTaskStarted && e.EventId > Math.Min(canceled.EventId, signal.EventId)).Take(1));
        Assert.True(activation.EventId > Math.Max(canceled.EventId, signal.EventId), Describe(run));
        if (fireTimer)
        {
            var timer = Assert.Single(run.History.Events, e => e.EventType == EventType.TimerFired);
            Assert.True(timer.EventId < Math.Min(canceled.EventId, signal.EventId), Describe(run));
        }
        Assert.DoesNotContain(run.History.Events, e => e.EventType == EventType.ActivityTaskScheduled &&
            e.ActivityTaskScheduledEventAttributes.ActivityType.Name == "DelegateToAgent");
        Assert.True(run.Failure is WorkflowFailedException, Describe(run));
        Assert.Null(run.Result);
        Assert.Empty(run.Activities.Calls);
        Assert.Equal(EventType.WorkflowExecutionCanceled, run.History.Events.Last().EventType);
        await Replay(run.History);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancellation_AfterDecisionBeforeCallbackStarts_ApprovalCancelsButLegacyStillCompletes(bool approvalRequest)
    {
        var run = await RunWithWorkerStopped(async (_, handle) =>
        {
            await handle.SignalAsync(Signal, [JsonSerializer.SerializeToElement(new
            { Decision = "approved", Ref = handle.ResultRunId, VisitId = Signal + ":1" })]);
        }, canceled: approvalRequest, workflowType: approvalRequest ? Type : "LegacyCallbackCancellationControl",
            pollActivities: false, afterResume: async handle =>
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!(await handle.FetchHistoryAsync()).Events.Any(e =>
                e.EventType == EventType.ActivityTaskScheduled &&
                e.ActivityTaskScheduledEventAttributes.ActivityType.Name == "DelegateToAgent"))
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Callback was not scheduled");
                await Task.Delay(20);
            }
            await handle.CancelAsync();
        });
        var signal = Assert.Single(run.History.Events, e => e.EventType == EventType.WorkflowExecutionSignaled);
        var callback = Assert.Single(run.History.Events, e => e.EventType == EventType.ActivityTaskScheduled &&
            e.ActivityTaskScheduledEventAttributes.ActivityType.Name == "DelegateToAgent");
        var canceled = Assert.Single(run.History.Events, e => e.EventType == EventType.WorkflowExecutionCancelRequested);
        Assert.True(signal.EventId < callback.EventId && callback.EventId < canceled.EventId, Describe(run));
        Assert.Contains(run.History.Events, e => e.EventType == EventType.ActivityTaskCanceled &&
            e.ActivityTaskCanceledEventAttributes.ScheduledEventId == callback.EventId);
        Assert.DoesNotContain(run.History.Events, e => e.EventType == EventType.ActivityTaskStarted &&
            e.ActivityTaskStartedEventAttributes.ScheduledEventId == callback.EventId);
        Assert.Empty(run.Activities.Calls);
        if (approvalRequest)
        {
            Assert.True(run.Failure is WorkflowFailedException, Describe(run));
            Assert.Null(run.Result);
            Assert.Equal(EventType.WorkflowExecutionCanceled, run.History.Events.Last().EventType);
        }
        else
        {
            Assert.Null(run.Failure);
            Assert.Equal("approved", JsonSerializer.Deserialize<JsonElement>(run.Result!).GetProperty("Decision").GetString());
            Assert.Equal(EventType.WorkflowExecutionCompleted, run.History.Events.Last().EventType);
        }
        await Replay(run.History);
    }

    private static Task Replay(WorkflowHistory history) => new WorkflowReplayer(
        new WorkflowReplayerOptions { Namespace = "fleet", TaskQueue = "fleet" }.AddWorkflow<UniversalWorkflow>()).ReplayWorkflowAsync(history);

    private sealed record BatchedRun(string? Result, WorkflowHistory History, ApprovalActivities Activities, Exception? Failure);
    private static string Describe(BatchedRun run) =>
        $"Result={run.Result}; failure={run.Failure}; history=" +
        string.Join(",", run.History.Events.Select(e => $"{e.EventId}:{e.EventType}"));
    private static async Task<BatchedRun> RunWithWorkerStopped(Func<WorkflowEnvironment, WorkflowHandle, Task> submit, bool canceled = false, bool fireTimer = false, bool pollActivities = true, Func<WorkflowHandle, Task>? afterResume = null, string workflowType = Type)
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync(new() { Namespace = "fleet" });
        await env.Client.Connection.OperatorService.AddSearchAttributesAsync(new AddSearchAttributesRequest
        { Namespace = "fleet", SearchAttributes = { ["Phase"] = IndexedValueType.Keyword, ["GateRef"] = IndexedValueType.Keyword, ["GateVisit"] = IndexedValueType.Keyword } });
        var activities = new ApprovalActivities(new WorkflowDefinitionModel
        { Name = workflowType, Namespace = "fleet", TaskQueue = "fleet", Root = SeedHarness.Parse(Type) }, "matching");
        WorkflowHandle handle = null!;
        // Stop the worker, not the workflow, so external events cannot be consumed separately.
        // Disable sticky queues so the stopped worker cannot retain the next activation.
        using (var worker = new TemporalWorker(env.Client, new TemporalWorkerOptions("fleet") { MaxCachedWorkflows = 0 }.AddWorkflow<UniversalWorkflow>().AddAllActivities(activities)))
            await worker.ExecuteAsync(async () =>
            {
                handle = await env.Client.StartWorkflowAsync(workflowType, [Input()], new WorkflowOptions(Id(), "fleet")
                { Memo = new Dictionary<string, object> { [HumanApprovalRequestContract.RequesterMemo] = "agent1" } });
                await Parked(handle);
            });
        Task? advance = null;
        if (fireTimer)
        {
            // Time skipping pauses at a pending workflow task. Keep the bounded advance alive
            // until the stopped worker resumes, rather than awaiting past that task here.
            advance = env.DelayAsync(TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1));
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!(await handle.FetchHistoryAsync()).Events.Any(e => e.EventType == EventType.TimerFired))
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Approval timer did not fire");
                await Task.Delay(20);
            }
        }
        await submit(env, handle);
        string? result = null;
        Exception? failure = null;
        var resumedOptions = new TemporalWorkerOptions("fleet") { MaxCachedWorkflows = 0 }.AddWorkflow<UniversalWorkflow>();
        if (pollActivities) resumedOptions.AddAllActivities(activities);
        using (var resumed = new TemporalWorker(env.Client, resumedOptions))
            await resumed.ExecuteAsync(async () =>
            {
                if (afterResume is not null) await afterResume(handle);
                // Capture failure AND history before the caller asserts cancellation. A thrown
                // terminal assertion used to discard the only evidence needed to diagnose CI.
                failure = await Record.ExceptionAsync(async () =>
                    result = await handle.GetResultAsync<string>().WaitAsync(TimeSpan.FromSeconds(40)));
                if (advance is not null) await advance.WaitAsync(TimeSpan.FromSeconds(20));
            });
        var run = new BatchedRun(result, await handle.FetchHistoryAsync(), activities, failure);
        Assert.True(canceled || failure is null, Describe(run));
        return run;
    }

    private static string Id() => "human-approval-" + Guid.NewGuid();
    private static JsonElement Input() => JsonSerializer.SerializeToElement(new { Action = Literal, Scope = Literal, RequesterAgent = "agent1" });
    private sealed record ApprovalRun(string Id, string RunId, string ResultText, WorkflowHistory History, ApprovalActivities Activities)
    { public string Result => ResultText; }
    private static async Task<ApprovalRun> Run(string receipt, Func<WorkflowEnvironment, WorkflowHandle, ApprovalActivities, Task> drive)
    {
        var definition = new WorkflowDefinitionModel { Name = Type, Namespace = "fleet", TaskQueue = "fleet", Root = SeedHarness.Parse(Type) };
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync(new() { Namespace = "fleet" });
        await env.Client.Connection.OperatorService.AddSearchAttributesAsync(new AddSearchAttributesRequest
        { Namespace = "fleet", SearchAttributes = { ["Phase"] = IndexedValueType.Keyword, ["GateRef"] = IndexedValueType.Keyword, ["GateVisit"] = IndexedValueType.Keyword } });
        var activities = new ApprovalActivities(definition, receipt);
        using var worker = new TemporalWorker(env.Client, new TemporalWorkerOptions("fleet").AddWorkflow<UniversalWorkflow>().AddAllActivities(activities));
        return await worker.ExecuteAsync(async () =>
        {
            var handle = await env.Client.StartWorkflowAsync(Type, [Input()], new WorkflowOptions(Id(), "fleet")
            { IdReusePolicy = WorkflowIdReusePolicy.RejectDuplicate, Memo = new Dictionary<string, object> { [HumanApprovalRequestContract.RequesterMemo] = "agent1" } });
            await drive(env, handle, activities);
            var result = await handle.GetResultAsync<string>().WaitAsync(TimeSpan.FromSeconds(40));
            var history = await handle.FetchHistoryAsync();
            await Assert.ThrowsAsync<WorkflowAlreadyStartedException>(() => env.Client.StartWorkflowAsync(Type, [Input()],
                new WorkflowOptions(handle.Id, "fleet") { IdReusePolicy = WorkflowIdReusePolicy.RejectDuplicate }));
            return new ApprovalRun(handle.Id, handle.ResultRunId!, result, history, activities);
        });
    }
    private static async Task Parked(WorkflowHandle handle)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!(await handle.FetchHistoryAsync()).Events.Any(e => e.EventType == EventType.TimerStarted))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Request did not park");
            await Task.Delay(20);
        }
    }
    public sealed class ApprovalActivities(WorkflowDefinitionModel definition, string receipt)
    {
        public ConcurrentQueue<(string Target, string Instruction, string TaskId)> Calls { get; } = new();
        public bool HoldNotification { get; set; }
        public TaskCompletionSource Notifying { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseNotification { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        [Activity("LoadWorkflowDefinition")] public WorkflowDefinitionModel Load(string _) => definition;
        [Activity("LoadWorkflowConfig")] public JsonElement Config() => JsonSerializer.SerializeToElement(new { });
        [Activity("DelegateToAgent")]
        public async Task<AgentTaskResult> Notify(string target, string instruction, string taskId, bool retry, int maxRetries)
        {
            Calls.Enqueue((target, instruction, taskId));
            Assert.False(retry);
            Notifying.TrySetResult();
            if (HoldNotification) await ReleaseNotification.Task.WaitAsync(ActivityExecutionContext.Current.CancellationToken);
            var snapshot = JsonSerializer.Deserialize<JsonElement>(instruction.Split('\n').Last());
            var expected = $"APPROVAL_RECEIVED:{snapshot.GetProperty("WorkflowId").GetString()}:{snapshot.GetProperty("RunId").GetString()}:{snapshot.GetProperty("Decision").GetString()}";
            return receipt switch
            {
                "failure" => throw new InvalidOperationException("synthetic callback failure"),
                "matching" => new(" \n" + expected + "\n ", "completed"),
                "incomplete" => new(expected, "incomplete"),
                "missing" => new("", "completed"),
                "extra-line" => new(expected + "\nextra", "completed"),
                _ => new("APPROVAL_RECEIVED:other:run:approved", "completed")
            };
        }
    }
}
