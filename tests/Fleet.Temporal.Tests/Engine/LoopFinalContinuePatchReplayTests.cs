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

public sealed class LoopFinalContinuePatchReplayTests
{
    private const string PatchId = "uwe-loop-final-continue-v1";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Loop_FinalContinue_ReplaysLegacyAndPatchedCommandSequences(bool legacy)
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var queue = "loop-patch-" + Guid.NewGuid().ToString("N");
        var options = new TemporalWorkerOptions(queue).AddAllActivities(new LoopPatchActivities(Definition()));
        if (legacy) options.AddWorkflow<LegacyLoopFinalContinueWorkflowDouble>();
        else options.AddWorkflow<UniversalWorkflow>();
        using var worker = new TemporalWorker(env.Client, options);
        await worker.ExecuteAsync(async () =>
        {
            var handle = await env.Client.StartWorkflowAsync("LoopPatchExample", [], new WorkflowOptions(queue, queue));
            Assert.Equal("done", await handle.GetResultAsync<string>());
        });
        var history = await env.Client.GetWorkflowHandle(queue).FetchHistoryAsync();
        Assert.Equal(legacy ? new long[] { 3, 4 } : [1, 3, 4], history.Events
            .Where(e => e.EventType == EventType.TimerStarted)
            .Select(e => e.TimerStartedEventAttributes.StartToFireTimeout.Seconds));
        var markers = history.Events.Where(e => e.EventType == EventType.MarkerRecorded)
            .SelectMany(e => e.MarkerRecordedEventAttributes.Details.Values)
            .SelectMany(p => p.Payloads_).Select(p => p.Data.ToStringUtf8()).ToList();
        Assert.Contains(markers, data => data.Contains(UniversalWorkflow.SignalBufferPatchId, StringComparison.Ordinal));
        Assert.Equal(!legacy, markers.Any(data => data.Contains(PatchId, StringComparison.Ordinal)));
        // Both histories replay through the actual engine. Without the gate the legacy replay
        // emits the 1-second timer where history already records the 3-second timer.
        var replayer = new WorkflowReplayer(new WorkflowReplayerOptions().AddWorkflow<UniversalWorkflow>());
        await replayer.ReplayWorkflowAsync(history);
    }

    private static WorkflowDefinitionModel Definition() => new()
    {
        Name = "LoopPatchExample", Namespace = "default", TaskQueue = "test",
        Root = new SequenceStep { Steps = [
            new LoopStep { Name = "outer", MaxIterations = 2, Steps = [
                new BranchStep { On = "{{vars.outer_iteration}}", Cases = new() {
                    ["0"] = new SequenceStep { Steps = [
                        new LoopStep { MaxIterations = 1, Steps = [new ContinueStep()] },
                        new SleepStep { Seconds = 1 }
                    ] }
                }, Default = new SleepStep { Seconds = 3 } }
            ] },
            new SleepStep { Seconds = 4 },
            new SetVariableStep { Vars = new() { ["_result"] = "done" } }
        ] }
    };
}

// The exact pre-fix command sequence, independent of the production loop implementation:
// the first outer iteration consumes the leaked inner continue, skipping its 1-second timer.
// The second iteration and parent continuation still run, leaving the history replay-sensitive.
[Workflow(Dynamic = true)]
public sealed class LegacyLoopFinalContinueWorkflowDouble
{
    [WorkflowRun]
    public async Task<object?> RunAsync(IRawValue[] args)
    {
        await Workflow.ExecuteActivityAsync<WorkflowDefinitionModel>("LoadWorkflowDefinition",
            [Workflow.Info.WorkflowType], new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) });
        await Workflow.ExecuteActivityAsync<JsonElement>("LoadWorkflowConfig", [],
            new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(10) });
        Workflow.Patched(UniversalWorkflow.SignalBufferPatchId);
        // No loop patch marker: calling production code here would invalidate the legacy fixture.
        await Workflow.DelayAsync(TimeSpan.FromSeconds(3));
        await Workflow.DelayAsync(TimeSpan.FromSeconds(4));
        return "done";
    }
}

file sealed class LoopPatchActivities(WorkflowDefinitionModel definition)
{
    [Activity("LoadWorkflowDefinition")]
    public WorkflowDefinitionModel Definition(string _) => definition;
    [Activity("LoadWorkflowConfig")]
    public JsonElement Config() => JsonSerializer.SerializeToElement(new { });
}
