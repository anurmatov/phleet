using Openshell.V1;

namespace Fleet.Orchestrator.Services;

public sealed record OpenShellSandboxSnapshot(string GatewayId, string Workspace, string Id, string Name,
    SandboxPhase Phase, string SupervisorInstanceId, uint PolicyVersion,
    IReadOnlyDictionary<string, string> Labels);

/// <summary>Authenticated observations only; named mutation RPCs are not exact-ID authority.</summary>
public interface IOpenShellGatewayClient
{
    Task<IReadOnlyList<OpenShellSandboxSnapshot>> InventoryAsync(CancellationToken ct = default);
    Task<OpenShellSandboxSnapshot?> ReadExactAsync(string sandboxId, CancellationToken ct = default);
}
