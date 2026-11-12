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
        [Activity("LoadWorkflowDefinition")] public WorkflowDefinitionModel Load(string _) => definition;
        [Activity("LoadWorkflowConfig")] public JsonElement Config() => JsonSerializer.SerializeToElement(new { });
        [Activity("DelegateToAgent")]
        public AgentTaskResult Notify(string target, string instruction, string taskId, bool retry, int maxRetries)
        {
            Calls.Enqueue((target, instruction, taskId));
            Assert.False(retry);
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
