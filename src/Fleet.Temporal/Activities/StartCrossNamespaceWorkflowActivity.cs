using Fleet.Temporal.Mcp;
using Temporalio.Activities;
using Temporalio.Client;

namespace Fleet.Temporal.Activities;

/// <summary>
/// Activity that starts a workflow in a different Temporal namespace.
/// Used when a workflow in one namespace needs to spawn work in another
/// namespace where the required activities are registered.
/// </summary>
public sealed class StartCrossNamespaceWorkflowActivity(TemporalClientFactory clientFactory)
{
    [Activity("StartCrossNamespaceWorkflow")]
    public async Task<string> StartAsync(
        string targetNamespace,
        string workflowType,
        string workflowId,
        string taskQueue,
        object? input)
    {
        var client = await clientFactory.GetClientAsync(targetNamespace);

        var handle = await client.StartWorkflowAsync(
            workflowType,
            input is not null ? [input] : Array.Empty<object>(),
            new WorkflowOptions(id: workflowId, taskQueue: taskQueue));

        return handle.Id;
    }
}
