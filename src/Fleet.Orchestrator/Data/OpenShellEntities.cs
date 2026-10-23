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

/// <summary>Independent of agent/launch foreign keys; deleting either cannot resurrect an ID.</summary>
public sealed class OpenShellRetiredId
{
    public required string GatewayId { get; set; }
    public required string SandboxId { get; set; }
    public DateTime RetiredAt { get; set; }
}
