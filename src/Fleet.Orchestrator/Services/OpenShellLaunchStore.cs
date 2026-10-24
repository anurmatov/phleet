using System.Data;
using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;

namespace Fleet.Orchestrator.Services;

public sealed class OpenShellStateConflictException(string message) : InvalidOperationException(message);

/// <summary>
/// Serialized launch intent, lease fencing and append-only retirement through this store. No gateway calls, driver
/// retries, activation or inferred runtime state. Lifecycle callers must use a non-retrying DB
/// context: an ambiguous mutation must be reconciled, never replayed by EF's execution strategy.
/// </summary>
public sealed class OpenShellLaunchStore(OrchestratorDbContext db, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    public async Task<OpenShellLaunch> ReserveAsync(int agentId, string? expectedOldRuntimeId,
        DateTime now, TimeSpan duration, CancellationToken ct = default, OpenShellPreparationScope? preparation = null)
    {
        ValidateTime(now, duration);
        if (agentId <= 0) throw new ArgumentOutOfRangeException(nameof(agentId));
        if (expectedOldRuntimeId is not null) ValidateId(expectedOldRuntimeId);
        if (preparation is not null && (preparation.AgentId != agentId || preparation.ExpectedOldRuntimeId != expectedOldRuntimeId
            || preparation.PlanHash != preparation.Plan.Hash || preparation.Deadline <= now || preparation.Deadline < now + duration))
            throw new OpenShellStateConflictException("preparation_scope_conflict");
        RequireNonRetryingContext();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        if (await db.OpenShellLaunches.AnyAsync(x => x.ActiveAgentId == agentId
            || preparation != null && x.AgentId == agentId && (x.Phase != "retired" || x.RevokedAt == null), ct))
            throw new OpenShellStateConflictException("An active or cleanup-pending launch requires reconciliation.");
        var lease = await db.OpenShellLeases.SingleOrDefaultAsync(x => x.AgentId == agentId, ct);
        if (lease is not null)
        {
            await db.Entry(lease).ReloadAsync(ct);
            if (db.Entry(lease).State == EntityState.Detached) lease = null;
        }
        if (lease is not null && lease.ExpiresAt > now)
            throw new OpenShellStateConflictException("The lifecycle lease is busy.");
        var launch = new OpenShellLaunch
        {
            Id = Guid.NewGuid(), AgentId = agentId, ActiveAgentId = agentId,
            ExpectedOldRuntimeId = expectedOldRuntimeId, IdentityEpoch = Guid.NewGuid(), CreatedAt = now,
            PreparationJson = preparation is null ? null : System.Text.Json.JsonSerializer.Serialize(preparation),
            PreparationDeadline = preparation?.Deadline, CreateRequestId = preparation is null ? null : Guid.NewGuid(),
            Workspace = preparation?.Plan.Workspace, PolicyHash = preparation?.Plan.PolicyHash, ImageHash = preparation?.Plan.ImageHash,
        };
        if (lease is null)
        {
            lease = new() { AgentId = agentId, Owner = launch.Id, ExpiresAt = now + duration };
            db.OpenShellLeases.Add(lease);
        }
        else { lease.Owner = launch.Id; lease.ExpiresAt = now + duration; }
        db.OpenShellLaunches.Add(launch);
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            // A failed/ambiguous transaction must not leave writes queued for a later SaveChanges.
            // Detach only this reservation's entities; never clear unrelated tracked work.
            db.Entry(launch).State = EntityState.Detached;
            db.Entry(lease).State = EntityState.Detached;
            if (ex is DbUpdateException { InnerException: MySqlException { Number: 1062 } })
                throw new OpenShellStateConflictException("The lifecycle slot was reserved concurrently; reconcile before retrying.");
            throw;
        }
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
        var launch = await ReloadLaunchAsync(launchId, ct);
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
            if (launch.Phase != "legacy_stopped")
                throw new OpenShellStateConflictException("Legacy stop must be confirmed before recording a created sandbox.");
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
        var launch = await ReloadLaunchAsync(launchId, ct);
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

    public async Task ConfirmLegacyStoppedAsync(Guid launchId, DateTime now,
        IOpenShellRuntimeEvidenceReader reader, CancellationToken ct = default)
    {
        ValidateTime(now, TimeSpan.FromSeconds(1));
        RequireNonRetryingContext();
        ArgumentNullException.ThrowIfNull(reader);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var launch = await ReloadLaunchAsync(launchId, ct);
        if (launch.RevokedAt is not null || launch.Phase is not ("prepared" or "legacy_stopped")
            || !await db.OpenShellLeases.AnyAsync(x => x.AgentId == launch.AgentId && x.Owner == launchId && x.ExpiresAt > now, ct))
            throw new OpenShellStateConflictException("Legacy-stop confirmation requires the current prepared launch and lease.");
        await VerifyEvidenceAsync(reader, launch, cleanup: false, ct);
        launch.Phase = "legacy_stopped";
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task CompleteCleanupAsync(Guid launchId, DateTime now,
        IOpenShellRuntimeEvidenceReader reader, CancellationToken ct = default, Guid? retirementOwner = null)
    {
        ValidateTime(now, TimeSpan.FromSeconds(1));
        RequireNonRetryingContext();
        ArgumentNullException.ThrowIfNull(reader);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var launch = await ReloadLaunchAsync(launchId, ct);
        if (retirementOwner is not null && (launch.DeleteIntentJson is null
            || System.Text.Json.JsonSerializer.Deserialize<OpenShellRetirementService.Intent>(launch.DeleteIntentJson)?.RequestId != retirementOwner))
            throw new OpenShellStateConflictException("Retirement operation does not own the persisted intent.");
        if (launch.RevokedAt is null || launch.Phase is not ("cleanup_pending" or "retired"))
            throw new OpenShellStateConflictException("Cleanup completion requires a revoked launch.");
        await VerifyEvidenceAsync(reader, launch, cleanup: true, ct);
        if (launch.SandboxId is not null && !await db.OpenShellRetiredIds.AnyAsync(x =>
                x.GatewayId == launch.GatewayId && x.SandboxId == launch.SandboxId, ct))
            throw new OpenShellStateConflictException("The retirement tombstone is missing.");
        var lease = await db.OpenShellLeases.SingleOrDefaultAsync(x => x.AgentId == launch.AgentId, ct);
        if (lease is not null)
        {
            await db.Entry(lease).ReloadAsync(ct);
            if (lease.Owner != (retirementOwner ?? launchId))
                throw new OpenShellStateConflictException("A different lifecycle owner holds the lease.");
            db.OpenShellLeases.Remove(lease);
        }
        launch.ActiveAgentId = null;
        launch.Phase = "retired";
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task RecordPreparationAttemptAsync(Guid id, bool create, CancellationToken ct = default)
    {
        RequireNonRetryingContext();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var launch = await ReloadLaunchAsync(id, ct);
        await RequirePreparationOwnerAsync(launch, ct);
        var now = _clock.GetUtcNow().UtcDateTime;
        if (create)
        {
            if (launch.Phase != "legacy_stopped" || launch.CreateAttemptedAt is not null || launch.CreateRequestId is null)
                throw new OpenShellStateConflictException("create_attempt_already_owned_or_out_of_order");
            launch.CreateAttemptedAt = now;
        }
        else
        {
            if (launch.Phase != "prepared" || launch.StopAttemptedAt is not null)
                throw new OpenShellStateConflictException("stop_attempt_already_owned_or_out_of_order");
            launch.StopAttemptedAt = now;
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task RecordSecurityReadyAsync(Guid id, OpenShellSandboxSnapshot target,
        IReadOnlyList<OpenShellSupervisorProof> proofs, CancellationToken ct = default)
    {
        RequireNonRetryingContext();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var launch = await ReloadLaunchAsync(id, ct);
        await RequirePreparationOwnerAsync(launch, ct);
        var scope = System.Text.Json.JsonSerializer.Deserialize<OpenShellPreparationScope>(launch.PreparationJson!)!;
        var providers = scope.Plan.ReadSpec().Providers;
        var expected = scope.Plan.ReadExpectedProviders();
        var now = _clock.GetUtcNow().UtcDateTime;
        if (launch.Phase != "created" || launch.GatewayId != target.GatewayId || launch.SandboxId != target.Id
            || launch.Workspace != target.Workspace || target.Phase != Openshell.V1.SandboxPhase.Ready || target.ResourceVersion == 0
            || proofs.Count != providers.Count || proofs.Count == 0
            || expected.Length != providers.Count || expected.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != expected.Length
            || proofs.Select(p => p.Provider).Distinct(StringComparer.Ordinal).Count() != proofs.Count
            || expected.Any(p => !providers.Contains(p.Name))
            || proofs.Any(p => !expected.Any(e => e.Name == p.Provider && e.Matches(p.Desired)))
            || proofs.Any(p => string.IsNullOrWhiteSpace(p.SessionId) || string.IsNullOrWhiteSpace(p.NetworkInstanceId) || p.Sequence == 0
                || p.Desired.SandboxId != target.Id || p.Desired.Sandbox != target.Name
                || p.ProcessInstanceId != target.SupervisorInstanceId || p.EvaluatedAt > now
                || now - p.EvaluatedAt > TimeSpan.FromSeconds(5))
            || proofs.Any(p => p.SessionId != proofs[0].SessionId || p.ProcessInstanceId != proofs[0].ProcessInstanceId
                || p.NetworkInstanceId != proofs[0].NetworkInstanceId || p.Desired.AttachmentEpoch != proofs[0].Desired.AttachmentEpoch))
            throw new OpenShellStateConflictException("security_ready_proof_conflict");
        launch.SecurityReadyJson = System.Text.Json.JsonSerializer.Serialize(proofs);
        launch.Phase = "security_ready";
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private async Task RequirePreparationOwnerAsync(OpenShellLaunch launch, CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        if (launch.PreparationJson is null || launch.RevokedAt is not null || launch.ActiveAgentId != launch.AgentId
            || launch.PreparationDeadline is null || launch.PreparationDeadline <= now
            || launch.StopAttemptedAt is { } stopped && stopped.AddSeconds(120) <= now
            || launch.CreateAttemptedAt is { } created && created.AddSeconds(60) <= now
            || !await db.OpenShellLeases.AnyAsync(l => l.AgentId == launch.AgentId && l.Owner == launch.Id && l.ExpiresAt > now, ct))
            throw new OpenShellStateConflictException("preparation_owner_or_deadline_unavailable");
    }

    private async Task<OpenShellLaunch> ReloadLaunchAsync(Guid launchId, CancellationToken ct)
    {
        var launch = await db.OpenShellLaunches.SingleAsync(x => x.Id == launchId, ct);
        // A tracking query does not refresh an existing instance. Reload within the serializable
        // transaction before making any decision about mapping, revocation, phase or ownership.
        await db.Entry(launch).ReloadAsync(ct);
        if (db.Entry(launch).State == EntityState.Detached)
            throw new OpenShellStateConflictException("The launch disappeared; reconciliation is required.");
        return launch;
    }

    private async Task VerifyEvidenceAsync(IOpenShellRuntimeEvidenceReader reader, OpenShellLaunch launch,
        bool cleanup, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var runtimeId = cleanup ? launch.SandboxId : launch.ExpectedOldRuntimeId;
        var gatewayId = cleanup ? launch.GatewayId : null;
        var target = new OpenShellRuntimeTarget(launch.Id, launch.IdentityEpoch, gatewayId, runtimeId, cleanup);
        var evidence = await reader.ReadAsync(target, deadline.Token).WaitAsync(deadline.Token);
        // Judge freshness at completion, not the caller's pre-I/O timestamp. Give the adapter
        // an immutable target, never the mutable tracked entity used for the transaction.
        var completedAt = _clock.GetUtcNow().UtcDateTime;
        if (evidence.LaunchId != launch.Id || evidence.IdentityEpoch != launch.IdentityEpoch
            || evidence.RuntimeId != runtimeId || evidence.GatewayId != gatewayId
            || evidence.ObservedAt.Kind != DateTimeKind.Utc || evidence.ObservedAt > completedAt
            || completedAt - evidence.ObservedAt > TimeSpan.FromSeconds(5)
            || !evidence.Stopped || (cleanup && !evidence.Absent)
            // No bound receipt does not prove create never happened. Require a complete,
            // authenticated inventory by launch/epoch, including ambiguous create results.
            || (cleanup && runtimeId is null && (!evidence.LaunchInventoryComplete || evidence.OwnedSandboxCount != 0))
            || evidence.Consumers != 0 || evidence.Pollers != 0)
            throw new OpenShellStateConflictException("Current exact-runtime stop/absence and drained intake evidence is required.");
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
