using Temporalio.Client;

namespace Fleet.Temporal.Mcp;

/// <summary>
/// Production implementation of <see cref="IWorkflowDispatcher"/> that dispatches
/// <c>FireAndForgetTaskWorkflow</c> via the Temporal client obtained from
/// <see cref="TemporalClientFactory"/>.
/// </summary>
internal sealed class TemporalWorkflowDispatcher(TemporalClientFactory clientFactory) : IWorkflowDispatcher
{
    private const string FireAndForgetWorkflowType = "FireAndForgetTaskWorkflow";
    private const string FleetNamespace = "fleet";

    public async Task<string> FireAndForgetAsync(
        string targetAgent,
        string taskDescription,
        CancellationToken ct = default)
    {
        var client = await clientFactory.GetClientAsync(FleetNamespace);
        var workflowId = $"notify-cto-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

        var handle = await client.StartWorkflowAsync(
            FireAndForgetWorkflowType,
            [new { TargetAgent = targetAgent, TaskDescription = taskDescription }],
            new WorkflowOptions(id: workflowId, taskQueue: FleetNamespace));

        return handle.Id;
    }
}
