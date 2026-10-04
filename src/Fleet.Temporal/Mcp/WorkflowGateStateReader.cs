using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Common;

namespace Fleet.Temporal.Mcp;

/// <summary>
/// Reads what a workflow reports about its current gate: its execution status and the
/// <c>Phase</c> keyword search attribute that a <c>wait_for_signal</c> step sets when it parks.
///
/// <para>
/// Used only by the CTO <c>design-approval</c> feedback path (#430). The engine buffers a signal
/// that arrives while nothing waits for it and hands it to the next wait of that name, so a
/// feedback sent off-gate would silently consume the next gate visit before the CEO sees it.
/// Checking the reported phase first is what keeps that feedback at the gate it was meant for.
/// </para>
/// </summary>
public interface IWorkflowGateStateReader
{
    Task<(WorkflowExecutionStatus Status, string? Phase)> ReadAsync(
        WorkflowHandle handle,
        CancellationToken cancellationToken = default);
}

/// <summary>Default reader: one read-only describe on the client the signal would use.</summary>
public sealed class WorkflowGateStateReader : IWorkflowGateStateReader
{
    private static readonly SearchAttributeKey<string> PhaseKey = SearchAttributeKey.CreateKeyword("Phase");

    public async Task<(WorkflowExecutionStatus Status, string? Phase)> ReadAsync(
        WorkflowHandle handle,
        CancellationToken cancellationToken = default)
    {
        var description = await handle.DescribeAsync(
            new WorkflowDescribeOptions { Rpc = new RpcOptions { CancellationToken = cancellationToken } });

        var phase = description.TypedSearchAttributes.TryGetValue(PhaseKey, out var value) ? value : null;
        return (description.Status, phase);
    }
}
