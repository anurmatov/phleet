namespace Fleet.Shared.Security;

/// <summary>Receiver-created identity, never populated from caller attribution or tool arguments.</summary>
public sealed record VerifiedAgentIdentity(string AgentName, string GatewayId, string SandboxId,
    Guid LaunchId, Guid IdentityEpoch, string Service, string Route);

public sealed record FleetRequestBinding(string Method, string RawTarget, string BodySha256, string SessionHash)
{
    // SHA256 of zero bytes. Never an empty claim, JSON null, or hash of a sentinel word.
    public const string AbsentSessionHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
}
