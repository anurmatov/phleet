using Temporalio.Client;

namespace Fleet.Temporal.Mcp;

/// <summary>
/// Production implementation of <see cref="IWorkflowDispatcher"/> that dispatches
/// <c>NotifyCtoWorkflow</c> via the Temporal client obtained from
/// <see cref="ITemporalClientFactory"/>.
/// </summary>
internal sealed class TemporalWorkflowDispatcher(ITemporalClientFactory clientFactory) : IWorkflowDispatcher
{
    internal const string NotifyCtoWorkflowType = "NotifyCtoWorkflow";
    internal const string FleetNamespace = "fleet";

    public async Task<string> FireAndForgetAsync(
        string targetAgent,
        string taskDescription,
        CancellationToken ct = default)
    {
        var client = await clientFactory.GetClientAsync(FleetNamespace);
        var workflowId = $"notify-cto-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}";

        var handle = await client.StartWorkflowAsync(
            NotifyCtoWorkflowType,
            [new { TargetAgent = targetAgent, TaskDescription = taskDescription }],
            new WorkflowOptions(id: workflowId, taskQueue: FleetNamespace));

        return handle.Id;
    }
}
