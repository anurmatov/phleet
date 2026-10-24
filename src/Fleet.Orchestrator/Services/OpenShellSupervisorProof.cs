using Openshell.V1;

namespace Fleet.Orchestrator.Services;

/// <summary>Live gateway evaluation, not durable operation success. Session and process are distinct identities.</summary>
public sealed record OpenShellSupervisorProof(ProviderDesiredIdentity Desired, string SessionId,
    string ProcessInstanceId, string NetworkInstanceId, ulong Sequence, DateTime EvaluatedAt)
{
    public static OpenShellSupervisorProof Verify(OpenShellSandboxSnapshot target, string provider,
        ProviderReadinessStatus status, DateTime now)
    {
        var desired = status.Receipt?.Desired;
        var observed = status.Observed;
        var evaluated = status.EvaluatedTime?.ToDateTime();
        var observation = status.ObservedTime?.ToDateTime();
        if (now.Kind != DateTimeKind.Utc || target.Phase != SandboxPhase.Ready || target.ResourceVersion == 0
            || status.State != ProviderReadinessState.Ready || desired is null || observed is null
            || status.Receipt!.Provider != provider || status.Receipt.Workspace != target.Workspace
            || desired.SandboxId != target.Id || desired.Sandbox != target.Name
            || string.IsNullOrWhiteSpace(desired.AttachmentEpoch) || string.IsNullOrWhiteSpace(desired.ProviderId)
            || desired.ProviderResourceVersion == 0 || desired.PolicyHash.Length != 64
            || desired.PolicyHash.Any(c => !char.IsAsciiHexDigitLower(c))
            || string.IsNullOrWhiteSpace(observed.SessionId) || observed.Sequence == 0
            || string.IsNullOrWhiteSpace(observed.ProcessInstanceId) || observed.ProcessInstanceId != target.SupervisorInstanceId
            || string.IsNullOrWhiteSpace(status.NetworkInstanceId)
            || observed.AttachmentEpoch != desired.AttachmentEpoch || observed.ProviderEnvRevision != desired.ProviderEnvRevision
            || observed.ConfigRevision != desired.ConfigRevision || observed.PolicyHash != desired.PolicyHash
            || !observed.CredentialsInstalled || !observed.PolicyActive || !observed.LaunchEnvironmentInstalled
            || evaluated is null || evaluated > now || now - evaluated > TimeSpan.FromSeconds(5)
            || observation is null || observation > evaluated)
            throw new OpenShellStateConflictException("current_supervisor_unverified");
        // The gateway separately evaluates the live session lease. An old accepted observation
        // is valid while that same session/process remains live; do not substitute an invented TTL.
        return new(desired.Clone(), observed.SessionId, observed.ProcessInstanceId, status.NetworkInstanceId,
            observed.Sequence, evaluated.Value);
    }
}
