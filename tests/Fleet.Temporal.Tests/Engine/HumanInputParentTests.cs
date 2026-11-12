using System.Text.Json;
using Fleet.Temporal.Engine;
using Fleet.Temporal.Models;
using Temporalio.Activities;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.OperatorService.V1;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Testing;
using Temporalio.Worker;

namespace Fleet.Temporal.Tests.Engine;

public class HumanInputParentTests
{
    [Theory]
    [InlineData("text", " \"Кыргыз\"\n\\ {{config.Secret}} ")]
    [InlineData("choice", "first")]
    [InlineData("approve-reject", "rejected")]
    public async Task AwaitedChild_AllModes_ReturnOnlyToExactParent(string mode, string value)
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync(new() { Namespace = "fleet" });
        await env.Client.Connection.OperatorService.AddSearchAttributesAsync(new AddSearchAttributesRequest
        { Namespace = "fleet", SearchAttributes = { ["Phase"] = IndexedValueType.Keyword, ["GateRef"] = IndexedValueType.Keyword, ["GateVisit"] = IndexedValueType.Keyword } });
        var args = new Dictionary<string, object?> { ["Question"] = "{{input.Question}}", ["Context"] = "literal context", ["AnswerMode"] = mode };
        if (mode == "choice") args["Choices"] = JsonSerializer.SerializeToElement(new[] { new { Id = "first", Label = "First" }, new { Id = "other", Label = "Other" } });
        var parent = new WorkflowDefinitionModel { Name = "DisposableInputParent", Namespace = "fleet", TaskQueue = "fleet", Root = new SequenceStep
        { Steps = [new ChildWorkflowStep { WorkflowType = HumanInputRequestContract.WorkflowType, Args = args, OutputVar = "response" },
            new SetVariableStep { Vars = new() { ["_result"] = "{{vars.response}}" } }] } };
        var activities = new ParentActivities(parent);
        using var worker = new TemporalWorker(env.Client, new TemporalWorkerOptions("fleet").AddWorkflow<UniversalWorkflow>().AddAllActivities(activities));
        await worker.ExecuteAsync(async () =>
        {
            const string literal = " \"question\"\nКыргыз \\ {{config.Secret}} ";
            var handle = await env.Client.StartWorkflowAsync(parent.Name, [new { Question = literal }], new WorkflowOptions("parent-" + Guid.NewGuid(), "fleet"));
            WorkflowHandle? child = null;
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (child is null)
            {
                var history = await handle.FetchHistoryAsync();
                var started = history.Events.FirstOrDefault(e => e.EventType == EventType.ChildWorkflowExecutionStarted);
                if (started is not null) child = env.Client.GetWorkflowHandle(started.ChildWorkflowExecutionStartedEventAttributes.WorkflowExecution.WorkflowId,
                    runId: started.ChildWorkflowExecutionStartedEventAttributes.WorkflowExecution.RunId);
                else { if (DateTime.UtcNow > deadline) throw new TimeoutException("Child did not start"); await Task.Delay(20); }
            }
            while (!(await child.FetchHistoryAsync()).Events.Any(e => e.EventType == EventType.TimerStarted))
            { if (DateTime.UtcNow > deadline) throw new TimeoutException("Child did not park"); await Task.Delay(20); }
            await new WorkflowReplayer(new WorkflowReplayerOptions { Namespace = "fleet", TaskQueue = "fleet" }.AddWorkflow<UniversalWorkflow>())
                .ReplayWorkflowAsync(await child.FetchHistoryAsync());
            await child.SignalAsync("request-input", [new { Value = value, Ref = child.RunId, VisitId = "request-input:1" }]);
            var result = JsonSerializer.Deserialize<JsonElement>(await handle.GetResultAsync<string>().WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.Equal("parent-result", result.GetProperty("Delivery").GetString());
            Assert.Equal(literal, result.GetProperty("Request").GetProperty("Question").GetString());
            Assert.Equal(child.Id, result.GetProperty("WorkflowId").GetString());
            Assert.Equal(child.RunId, result.GetProperty("RunId").GetString());
            var origin = result.GetProperty("Request").GetProperty("Origin");
            Assert.Equal(handle.Id, origin.GetProperty("WorkflowId").GetString());
            Assert.Equal(handle.ResultRunId, origin.GetProperty("RunId").GetString());
            Assert.Equal("fleet", origin.GetProperty("Namespace").GetString());
            Assert.Equal(value, result.GetProperty("Response").GetProperty("Answer").GetProperty("Value").GetString());
            Assert.Equal(0, activities.Callbacks);
            var completed = await child.FetchHistoryAsync();
            Assert.DoesNotContain(completed.Events, e => e.EventType == EventType.SignalExternalWorkflowExecutionInitiated);
            await new WorkflowReplayer(new WorkflowReplayerOptions { Namespace = "fleet", TaskQueue = "fleet" }.AddWorkflow<UniversalWorkflow>()).ReplayWorkflowAsync(completed);
        });
    }
    [Theory]
    [InlineData("cancel", WorkflowExecutionStatus.Canceled)]
    [InlineData("terminate", WorkflowExecutionStatus.Terminated)]
    [InlineData("timeout", WorkflowExecutionStatus.TimedOut)]
    public async Task ParentLifetime_ChildNeverReturnsAnAnswerOrCallback(string close, WorkflowExecutionStatus expected)
    {
        // The Java time-skipping server does not implement parent-close termination:
        // https://github.com/temporalio/sdk-java/issues/1079
        await using var env = close == "terminate"
            ? await WorkflowEnvironment.StartLocalAsync(new() { Namespace = "fleet" })
            : await WorkflowEnvironment.StartTimeSkippingAsync(new() { Namespace = "fleet" });
        await env.Client.Connection.OperatorService.AddSearchAttributesAsync(new AddSearchAttributesRequest
        { Namespace = "fleet", SearchAttributes = { ["Phase"] = IndexedValueType.Keyword, ["GateRef"] = IndexedValueType.Keyword, ["GateVisit"] = IndexedValueType.Keyword } });
        var parent = new WorkflowDefinitionModel { Name = "DisposableClosingParent", Namespace = "fleet", TaskQueue = "fleet",
            Root = new ChildWorkflowStep { WorkflowType = HumanInputRequestContract.WorkflowType,
                Args = new() { ["Question"] = "q", ["Context"] = "c", ["AnswerMode"] = "text" },
                ExecutionTimeoutMinutes = close == "timeout" ? 1 : null, OutputVar = "_result" } };
        var activities = new ParentActivities(parent);
        using var worker = new TemporalWorker(env.Client, new TemporalWorkerOptions("fleet").AddWorkflow<UniversalWorkflow>().AddAllActivities(activities));
        await worker.ExecuteAsync(async () =>
        {
            var handle = await env.Client.StartWorkflowAsync(parent.Name, [], new WorkflowOptions("parent-" + Guid.NewGuid(), "fleet"));
            WorkflowHandle? child = null;
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (child is null || !(await child.FetchHistoryAsync()).Events.Any(e => e.EventType == EventType.TimerStarted))
            {
                var started = (await handle.FetchHistoryAsync()).Events.FirstOrDefault(e => e.EventType == EventType.ChildWorkflowExecutionStarted);
                if (started is not null) child = env.Client.GetWorkflowHandle(started.ChildWorkflowExecutionStartedEventAttributes.WorkflowExecution.WorkflowId,
                    runId: started.ChildWorkflowExecutionStartedEventAttributes.WorkflowExecution.RunId);
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Child did not park");
                await Task.Delay(20);
            }
            if (close == "cancel") await handle.CancelAsync();
            if (close == "terminate") await handle.TerminateAsync("Synthetic parent close");
            await Assert.ThrowsAsync<Temporalio.Exceptions.WorkflowFailedException>(() => handle.GetResultAsync<string>().WaitAsync(TimeSpan.FromSeconds(30)));
            await Assert.ThrowsAsync<Temporalio.Exceptions.WorkflowFailedException>(() => child.GetResultAsync<string>().WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.Equal(expected, (await child.DescribeAsync()).Status);
            Assert.Equal(0, activities.Callbacks);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FireAndForget_LiteralAndTemplate_RefusedBeforeChildCommand(bool template)
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync(new() { Namespace = "fleet" });
        var parent = new WorkflowDefinitionModel { Name = "DisposableForbiddenParent", Namespace = "fleet", TaskQueue = "fleet",
            Root = new SequenceStep { IgnoreFailure = true, Steps = [new FireAndForgetStep { WorkflowType = template ? "{{input.Type}}" : HumanInputRequestContract.WorkflowType, IgnoreFailure = true }] } };
        var activities = new ParentActivities(parent);
        using var worker = new TemporalWorker(env.Client, new TemporalWorkerOptions("fleet").AddWorkflow<UniversalWorkflow>().AddAllActivities(activities));
        await worker.ExecuteAsync(async () =>
        {
            var handle = await env.Client.StartWorkflowAsync(parent.Name, [new { Type = HumanInputRequestContract.WorkflowType }], new WorkflowOptions("parent-" + Guid.NewGuid(), "fleet"));
            await Assert.ThrowsAsync<Temporalio.Exceptions.WorkflowFailedException>(() => handle.GetResultAsync<string>().WaitAsync(TimeSpan.FromSeconds(20)));
            Assert.DoesNotContain((await handle.FetchHistoryAsync()).Events, e => e.EventType == EventType.StartChildWorkflowExecutionInitiated);
            Assert.Equal(0, activities.Callbacks);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AwaitedChild_ForgedOriginOrWrongRouting_StartsNothing(bool forgeOrigin)
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync(new() { Namespace = "fleet" });
        var args = new Dictionary<string, object?> { ["Question"] = "q", ["Context"] = "c", ["AnswerMode"] = "text" };
        if (forgeOrigin) args["Origin"] = new { Kind = "agent", RequesterAgent = "other" };
        var parent = new WorkflowDefinitionModel { Name = "DisposableInvalidParent", Namespace = "fleet", TaskQueue = "fleet",
            Root = new ChildWorkflowStep { WorkflowType = HumanInputRequestContract.WorkflowType, Namespace = forgeOrigin ? "fleet" : "other", Args = args } };
        var activities = new ParentActivities(parent);
        using var worker = new TemporalWorker(env.Client, new TemporalWorkerOptions("fleet").AddWorkflow<UniversalWorkflow>().AddAllActivities(activities));
        await worker.ExecuteAsync(async () =>
        {
            var handle = await env.Client.StartWorkflowAsync(parent.Name, [], new WorkflowOptions("parent-" + Guid.NewGuid(), "fleet"));
            await Assert.ThrowsAsync<Temporalio.Exceptions.WorkflowFailedException>(() => handle.GetResultAsync<string>().WaitAsync(TimeSpan.FromSeconds(20)));
            Assert.DoesNotContain((await handle.FetchHistoryAsync()).Events, e => e.EventType == EventType.StartChildWorkflowExecutionInitiated);
            Assert.Equal(0, activities.Callbacks);
        });
    }

    public sealed class ParentActivities(WorkflowDefinitionModel parent)
    {
        public int Callbacks;
        [Activity("LoadWorkflowDefinition")] public WorkflowDefinitionModel Load(string name) => name == parent.Name ? parent :
            new() { Name = HumanInputRequestContract.WorkflowType, Namespace = "fleet", TaskQueue = "fleet", Root = SeedHarness.Parse(HumanInputRequestContract.WorkflowType) };
        [Activity("LoadWorkflowConfig")] public JsonElement Config() => JsonSerializer.SerializeToElement(new { });
        [Activity("DelegateToAgent")] public string Callback(string target, string instruction, string taskId, bool retry, int maxRetries)
        { Callbacks++; throw new InvalidOperationException("Parent requests must not delegate."); }
    }
}
