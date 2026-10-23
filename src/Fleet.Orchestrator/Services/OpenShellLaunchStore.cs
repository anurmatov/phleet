using System.Data;
using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Services;

public sealed class OpenShellStateConflictException(string message) : InvalidOperationException(message);

/// <summary>
/// Serialized launch intent, lease fencing and permanent ID retirement. No gateway calls, driver
/// retries, activation or inferred runtime state. Lifecycle callers must use a non-retrying DB
/// context: an ambiguous mutation must be reconciled, never replayed by EF's execution strategy.
/// </summary>
public sealed class OpenShellLaunchStore(OrchestratorDbContext db)
{
    public async Task<OpenShellLaunch> ReserveAsync(int agentId, string? expectedOldRuntimeId,
        DateTime now, TimeSpan duration, CancellationToken ct = default)
    {
        ValidateTime(now, duration);
        if (agentId <= 0) throw new ArgumentOutOfRangeException(nameof(agentId));
        if (expectedOldRuntimeId is not null) ValidateId(expectedOldRuntimeId);
        RequireNonRetryingContext();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        if (await db.OpenShellLaunches.AnyAsync(x => x.ActiveAgentId == agentId, ct))
            throw new OpenShellStateConflictException("An active or cleanup-pending launch requires reconciliation.");
        var lease = await db.OpenShellLeases.SingleOrDefaultAsync(x => x.AgentId == agentId, ct);
        if (lease is not null && lease.ExpiresAt > now)
            throw new OpenShellStateConflictException("The lifecycle lease is busy.");
        var launch = new OpenShellLaunch
        {
            Id = Guid.NewGuid(), AgentId = agentId, ActiveAgentId = agentId,
            ExpectedOldRuntimeId = expectedOldRuntimeId, IdentityEpoch = Guid.NewGuid(), CreatedAt = now,
        };
        if (lease is null)
            db.OpenShellLeases.Add(new() { AgentId = agentId, Owner = launch.Id, ExpiresAt = now + duration });
        else { lease.Owner = launch.Id; lease.ExpiresAt = now + duration; }
        db.OpenShellLaunches.Add(launch);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return launch;
    }

    public async Task<bool> RenewAsync(int agentId, Guid owner, DateTime now, TimeSpan duration,
        CancellationToken ct = default)
    {
        ValidateTime(now, duration);
        RequireNonRetryingContext();
        // An expired lease is never revived, and a stale owner cannot renew a replacement lease.
        var changed = await db.OpenShellLeases.Where(x => x.AgentId == agentId && x.Owner == owner && x.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, now + duration), ct);
        return changed == 1;
    }

    public async Task BindCreatedSandboxAsync(Guid launchId, string gatewayId, string sandboxId,
        DateTime now, CancellationToken ct = default)
    {
        ValidateId(gatewayId); ValidateId(sandboxId);
        ValidateTime(now, TimeSpan.FromSeconds(1));
        RequireNonRetryingContext();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var launch = await db.OpenShellLaunches.SingleAsync(x => x.Id == launchId, ct);
        if (launch.RevokedAt is not null || !await db.OpenShellLeases.AnyAsync(x =>
                x.AgentId == launch.AgentId && x.Owner == launchId && x.ExpiresAt > now, ct))
            throw new OpenShellStateConflictException("The launch is revoked or its lease is unavailable.");
        if (launch.GatewayId is not null || launch.SandboxId is not null)
        {
            if (launch.GatewayId != gatewayId || launch.SandboxId != sandboxId)
                throw new OpenShellStateConflictException("A sandbox ID belongs to exactly one immutable launch.");
        }
        else
        {
            if (await db.OpenShellRetiredIds.AnyAsync(x => x.GatewayId == gatewayId && x.SandboxId == sandboxId, ct)
                || await db.OpenShellLaunches.AnyAsync(x => x.GatewayId == gatewayId && x.SandboxId == sandboxId, ct))
                throw new OpenShellStateConflictException("The sandbox ID was already used.");
            launch.GatewayId = gatewayId; launch.SandboxId = sandboxId; launch.Phase = "created";
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task RetireAsync(Guid launchId, DateTime now, CancellationToken ct = default)
    {
        ValidateTime(now, TimeSpan.FromSeconds(1));
        RequireNonRetryingContext();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var launch = await db.OpenShellLaunches.SingleAsync(x => x.Id == launchId, ct);
        if (launch.RevokedAt is null)
        {
            launch.RevokedAt = now;
            launch.Phase = "cleanup_pending";
            if (launch.GatewayId is not null && launch.SandboxId is not null)
                db.OpenShellRetiredIds.Add(new() { GatewayId = launch.GatewayId, SandboxId = launch.SandboxId, RetiredAt = now });
            await db.SaveChangesAsync(ct);
        }
        // Keep the active slot, including on repeated retirement. Only a future verified stop/delete
        // transition may release it; revocation alone is not proof of consumer/poller absence.
        await tx.CommitAsync(ct);
    }

    private void RequireNonRetryingContext()
    {
        if (db.Database.CreateExecutionStrategy().RetriesOnFailure)
            throw new InvalidOperationException("OpenShell lifecycle storage requires a non-retrying context.");
    }

    private static void ValidateTime(DateTime now, TimeSpan duration)
    {
        if (now.Kind != DateTimeKind.Utc) throw new ArgumentException("A UTC timestamp is required.", nameof(now));
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromSeconds(120))
            throw new ArgumentOutOfRangeException(nameof(duration));
    }

    private static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new ArgumentException("A canonical opaque runtime ID is required.", nameof(id));
    }
}
