namespace Fleet.Shared.Security;

/// <summary>Receiver-created identity, never populated from caller attribution or tool arguments.</summary>
public sealed record VerifiedAgentIdentity(string AgentName, string GatewayId, string SandboxId,
    Guid LaunchId, Guid IdentityEpoch, string Service, string Route);

public sealed record FleetRequestBinding(string Method, string RawTarget, string BodySha256, string SessionHash);
