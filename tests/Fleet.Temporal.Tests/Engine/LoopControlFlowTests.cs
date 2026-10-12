using Fleet.Temporal.Engine;
using Temporalio.Client;
using Temporalio.Testing;
using Temporalio.Worker;

namespace Fleet.Temporal.Tests.Engine;

public sealed class LoopControlFlowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Loop_FinalIterationContinueDoesNotSkipItsParentContinuation(bool nested)
    {
        var loop = new LoopStep { MaxIterations = 2, Steps = [new ContinueStep()] };
        StepDefinition body = nested ? new SequenceStep { Steps = [loop] } : loop;
        var root = new SequenceStep { Steps = [body, new SetVariableStep { Vars = new() { ["_result"] = "continued" } }] };
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var queue = "loop-control-" + Guid.NewGuid().ToString("N");
        using var worker = new TemporalWorker(env.Client, new TemporalWorkerOptions(queue)
            .AddWorkflow<UniversalWorkflow>().AddAllActivities(new AcceptanceEngineActivities(root)));
        await worker.ExecuteAsync(async () =>
        {
            var handle = await env.Client.StartWorkflowAsync("LoopControlTest", [], new WorkflowOptions(queue, queue));
            Assert.Equal("continued", await handle.GetResultAsync<string>());
        });
    }
}
