using Openshell.V1;

namespace Fleet.Orchestrator.Services;

public sealed record OpenShellSandboxSnapshot(string GatewayId, string Workspace, string Id, string Name,
    SandboxPhase Phase, string SupervisorInstanceId, uint PolicyVersion,
    IReadOnlyDictionary<string, string> Labels)
{
    public ulong ResourceVersion { get; init; }
}

/// <summary>Acknowledgement only, not proof of intake or external cleanup.</summary>
public sealed record OpenShellDeleteAcknowledgement(string SandboxId, DeletionOutcome Outcome);

/// <summary>Authenticated observations and capability-gated expected-ID/version mutations.</summary>
public interface IOpenShellGatewayClient
{
    Task<OpenShellSandboxSnapshot> CreateAsync(OpenShellLaunchPlan plan, Guid launchId, Guid epoch,
        Guid requestId, CancellationToken ct = default) => throw new NotSupportedException("create_unqualified");
    Task<OpenShellSupervisorProof> ReadCurrentSupervisorAsync(OpenShellSandboxSnapshot target, string provider,
        CancellationToken ct = default) => throw new NotSupportedException("supervisor_unqualified");
    Task<IReadOnlyList<OpenShellSandboxSnapshot>> InventoryAsync(CancellationToken ct = default);
    Task<OpenShellSandboxSnapshot?> ReadExactAsync(string sandboxId, CancellationToken ct = default);
    Task<OpenShellSandboxSnapshot> StopExactAsync(OpenShellSandboxSnapshot target, Guid requestId, CancellationToken ct = default);
    Task<OpenShellDeleteAcknowledgement> DeleteExactAsync(OpenShellSandboxSnapshot target, Guid requestId,
        bool allowMissing, CancellationToken ct = default);
}
