namespace Fleet.Orchestrator.Data;

/// <summary>Durable intent/ownership only. A created row is never a readiness or activation receipt.</summary>
public sealed class OpenShellLaunch
{
    public Guid Id { get; set; }
    public int AgentId { get; set; }
    public Agent Agent { get; set; } = null!;
    // Nullable unique slot: retain while cleanup is ambiguous; terminal cleanup will release it.
    public int? ActiveAgentId { get; set; }
    public string? ExpectedOldRuntimeId { get; set; }
    public string? GatewayId { get; set; }
    public string? SandboxId { get; set; }
    public string Phase { get; set; } = "prepared";
    public string? Workspace { get; set; }
    public string? PolicyHash { get; set; }
    public string? ImageHash { get; set; }
    public string? DestinationHash { get; set; }
    public string? ControlUrl { get; set; }
    // Reference only. Never persist the capability, credentials or raw driver errors here.
    public string? CapabilitySecretRef { get; set; }
    // Nonsecret immutable request payloads, committed before gateway mutations.
    public string? StopIntentJson { get; set; }
    public string? DeleteIntentJson { get; set; }
    public string? Error { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public Guid? BotPollerLeaseOwner { get; set; }
    public DateTime? BotPollerLeaseExpiresAt { get; set; }
    public long? TelegramOffset { get; set; }
    public Guid IdentityEpoch { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}

public sealed class OpenShellLease
{
    public int AgentId { get; set; }
    public Agent Agent { get; set; } = null!;
    public Guid Owner { get; set; }
    public DateTime ExpiresAt { get; set; }
}

/// <summary>Independent of agent/launch foreign keys. Append-only through tracked writes; privileged bulk/SQL writes bypass this guard.</summary>
public sealed class OpenShellRetiredId
{
    public required string GatewayId { get; set; }
    public required string SandboxId { get; set; }
    public DateTime RetiredAt { get; set; }
}

/// <summary>Inventory metadata only. Classification never grants runtime access.</summary>
public sealed class OpenShellSecretBinding
{
    public int AgentId { get; set; }
    public Agent Agent { get; set; } = null!;
    public required string Reference { get; set; }
    public string Classification { get; set; } = "unclassified";
    public required string SourceKind { get; set; }
    public string? Provider { get; set; }
    public string? Alias { get; set; }
    public string? ProfileHash { get; set; }
    public string? EndpointHash { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public string? RiskApprovalRef { get; set; }
    public long Version { get; set; }
}

/// <summary>Hashed session custody. No session/assertion bytes or implicit admission.</summary>
public sealed class OpenShellIdentitySession
{
    public required string SessionHash { get; set; }
    public required string Service { get; set; }
    public required string Route { get; set; }
    public int AgentId { get; set; }
    public Agent Agent { get; set; } = null!;
    public Guid LaunchId { get; set; }
    public OpenShellLaunch Launch { get; set; } = null!;
    public Guid IdentityEpoch { get; set; }
    public DateTime ExpiresAt { get; set; }
}

public sealed class OpenShellRequestNonce
{
    public required string Jti { get; set; }
    public Guid LaunchId { get; set; }
    public OpenShellLaunch Launch { get; set; } = null!;
    public DateTime ExpiresAt { get; set; }
}
