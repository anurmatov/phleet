namespace Fleet.Orchestrator.Services;

/// <summary>
/// Trusted lifecycle adapter boundary, not an upstream wire message. An implementation must obtain
/// current authenticated runtime and poller/consumer observations, or throw on unknown state.
/// No production implementation is registered yet: these transitions cannot admit a workload.
/// </summary>
public interface IOpenShellRuntimeEvidenceReader
{
    Task<OpenShellRuntimeEvidence> ReadAsync(OpenShellRuntimeTarget target, CancellationToken cancellationToken);
}

public sealed record OpenShellRuntimeTarget(Guid LaunchId, Guid IdentityEpoch, string? GatewayId,
    string? RuntimeId, bool RequireAbsence);

public sealed record OpenShellRuntimeEvidence(Guid LaunchId, Guid IdentityEpoch, string? GatewayId,
    string? RuntimeId, bool Stopped, bool Absent, int Consumers, int Pollers, DateTime ObservedAt);
