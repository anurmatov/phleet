namespace Fleet.Orchestrator.Services;

/// <summary>Called only after authoritative revoked-runtime absence/drain and lease ownership proof.</summary>
public interface IOpenShellArtifactCleanup
{
    Task VerifyRetiredAsync(OpenShellArtifactCleanupTarget target, CancellationToken ct)
        => throw new NotSupportedException("bootstrap_retirement_verifier_unqualified");
    Task RetireAsync(OpenShellArtifactCleanupTarget target, CancellationToken ct);
}
public sealed record OpenShellArtifactCleanupTarget(Guid LaunchId, Guid IdentityEpoch, string PlanHash, string Reference);
