using System.Text.Json;
using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Services;

/// <summary>Static external qualification, before old-stop: artifacts, owned mounts, hook Describe/ValidateConfig and trust.</summary>
public interface IOpenShellPreparationPreflight
{
    Task VerifyAsync(OpenShellPreparationScope selection, CancellationToken ct);
    // Must establish complete exclusive runtime/process/broker ownership BEFORE stopping legacy.
    Task VerifyLegacyOwnershipAsync(OpenShellPreparationScope selection, CancellationToken ct);
    Task StopLegacyExactAsync(string expectedId, CancellationToken ct);
}

/// <summary>The bootstrap process, not the orchestrator, sends the outside-identity probe. No activation API.</summary>
public interface IOpenShellBootstrapProbe
{
    Task VerifyAsync(Guid launchId, Guid epoch, OpenShellSandboxSnapshot target,
        IReadOnlyList<OpenShellSupervisorProof> proofs, CancellationToken ct);
}

/// <summary>
/// Shared source preparation driver. Optional peers are deliberately NOT registered until their concrete
/// qualification is implemented. Missing prerequisites fail before reserve/stop/create, never simulate readiness.
/// </summary>
public sealed class OpenShellProvisioningService(Func<OrchestratorDbContext> createDb,
    IOpenShellPreparationAuthority authority, IServiceScopeFactory scopes)
{
    public async Task<ProvisionResult?> TryPrepareAsync(string name, string intent, bool overrides,
        CancellationToken callerToken)
    {
        OpenShellPreparationScope? selection;
        try { selection = await authority.SelectAsync(name, intent, callerToken); }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested) { throw; }
        catch { return ProvisionResult.Fail(name, "preparation_authority_unavailable", 503); }
        if (selection is null) return null; // No source authority is inferred from agent metadata.
        if (overrides || selection.Intent != intent || selection.AgentId <= 0 || selection.Plan.Hash != selection.PlanHash
            || selection.Deadline.Kind != DateTimeKind.Utc || selection.Deadline <= DateTime.UtcNow.AddSeconds(10)
            || selection.Deadline > DateTime.UtcNow.AddSeconds(120))
            return ProvisionResult.Fail(name, "preparation_scope_conflict", 409);
        using var dependencies = scopes.CreateScope();
        var gateway = dependencies.ServiceProvider.GetService<IOpenShellGatewayClient>();
        var reader = dependencies.ServiceProvider.GetService<IOpenShellRuntimeEvidenceReader>();
        var preflight = dependencies.ServiceProvider.GetService<IOpenShellPreparationPreflight>();
        var probe = dependencies.ServiceProvider.GetService<IOpenShellBootstrapProbe>();
        if (gateway is null || reader is null || preflight is null || probe is null)
            return ProvisionResult.Fail(name, "compatibility_blocked: preparation peers unqualified", 503);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        operation.CancelAfter(selection.Deadline - DateTime.UtcNow - TimeSpan.FromSeconds(10));
        var ct = operation.Token;
        OpenShellLaunch? launch = null;
        var cleanupAttempted = false;
        var safetyDeadline = selection.Deadline;
        try
        {
            var compiled = new OpenShellPolicyCompiler().Compile(selection.Plan.GatewayId, selection.Plan.Workspace,
                selection.Plan.ReadSpec(), selection.Plan.InputHash);
            if (compiled != selection.Plan) throw new OpenShellStateConflictException("preparation_plan_changed");
            await preflight.VerifyAsync(selection, ct).WaitAsync(ct);
            await preflight.VerifyLegacyOwnershipAsync(selection, ct).WaitAsync(ct);
            await using var db = createDb();
            var agent = await db.Agents.AsNoTracking().SingleOrDefaultAsync(a => a.Name == name, ct);
            if (agent?.Id != selection.AgentId || agent.ExecutionShell != "openshell"
                || agent.OpenShellMigrationState != "pending"
                || await db.OpenShellLaunches.AnyAsync(l => l.AgentId == agent.Id, ct))
                throw new OpenShellStateConflictException("preparation_owner_changed");
            var store = new OpenShellLaunchStore(db);
            launch = await store.ReserveAsync(agent.Id, selection.ExpectedOldRuntimeId, DateTime.UtcNow,
                selection.Deadline - DateTime.UtcNow, ct, selection);
            if (selection.ExpectedOldRuntimeId is not null)
            {
                // Persist before the attempt, never reset by lease renewal or a later RPC.
                await store.RecordPreparationAttemptAsync(launch.Id, create: false, ct);
                await preflight.StopLegacyExactAsync(selection.ExpectedOldRuntimeId, ct).WaitAsync(ct);
            }
            await store.ConfirmLegacyStoppedAsync(launch.Id, DateTime.UtcNow, reader, ct);
            await store.RecordPreparationAttemptAsync(launch.Id, create: true, ct);
            safetyDeadline = new DateTime(Math.Min(selection.Deadline.Ticks, launch.CreateAttemptedAt!.Value.AddSeconds(60).Ticks), DateTimeKind.Utc);
            operation.CancelAfter(safetyDeadline - DateTime.UtcNow - TimeSpan.FromSeconds(10));
            var created = await gateway.CreateAsync(selection.Plan, launch.Id, launch.IdentityEpoch,
                launch.CreateRequestId!.Value, ct).WaitAsync(ct);
            await store.BindCreatedSandboxAsync(launch.Id, created.GatewayId, created.Id, DateTime.UtcNow, ct);
            // A single current read is intentional. Pending is not Ready and is never a reason to
            // resend Create or retain an unbounded bootstrap process; follow-up reconciliation is explicit.
            var current = await gateway.ReadExactAsync(created.Id, ct) ?? throw new OpenShellStateConflictException("created_runtime_missing");
            var proofs = new List<OpenShellSupervisorProof>();
            foreach (var provider in compiled.ReadSpec().Providers)
                proofs.Add(await gateway.ReadCurrentSupervisorAsync(current, provider, ct));
            await store.RecordSecurityReadyAsync(launch.Id, current, proofs, ct);
            await probe.VerifyAsync(launch.Id, launch.IdentityEpoch, current, proofs.AsReadOnly(), ct).WaitAsync(ct);
            // Re-evaluate every required provider after the probe, not a stored operation receipt.
            foreach (var (provider, proof) in compiled.ReadSpec().Providers.Zip(proofs))
            {
                var fresh = await gateway.ReadCurrentSupervisorAsync(current, provider, ct);
                if (!fresh.Desired.Equals(proof.Desired) || fresh.SessionId != proof.SessionId
                    || fresh.ProcessInstanceId != proof.ProcessInstanceId || fresh.NetworkInstanceId != proof.NetworkInstanceId)
                    throw new OpenShellStateConflictException("supervisor_changed_after_probe");
            }
            cleanupAttempted = true;
            await RetireHeldAsync(db, store, launch, gateway, reader, ct);
            return ProvisionResult.Fail(name, $"preparation_verified_activation_held: launch {launch.Id:D}; safely retired", 409);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Cancellation is not permission to skip revocation. Persist local retirement using a
            // separate short bound; do not resend an ambiguous Create or a failed mutation.
            if (launch is not null)
            {
                using var revoke = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await using var recovery = createDb();
                    var recoveryStore = new OpenShellLaunchStore(recovery);
                    await recoveryStore.RetireAsync(launch.Id, DateTime.UtcNow, revoke.Token);
                    var owned = await recovery.OpenShellLaunches.AsNoTracking().SingleAsync(l => l.Id == launch.Id, revoke.Token);
                    // Unbound/ambiguous Create requires authoritative complete ownership reconciliation.
                    // Never guess from labels or resend a mutation whose acknowledgement was lost.
                    if (!cleanupAttempted && owned.SandboxId is not null && owned.StopIntentJson is null && owned.DeleteIntentJson is null)
                    {
                        using var safety = new CancellationTokenSource(TimeSpan.FromTicks(Math.Max(1,
                            Math.Min(TimeSpan.FromSeconds(8).Ticks, (safetyDeadline - DateTime.UtcNow).Ticks))));
                        var tracked = await recovery.OpenShellLaunches.SingleAsync(l => l.Id == launch.Id, safety.Token);
                        await RetireHeldAsync(recovery, recoveryStore, tracked, gateway, reader, safety.Token);
                    }
                }
                catch { /* Pending immutable ownership remains; no success or fallback. */ }
            }
            return ProvisionResult.Fail(name, launch is null ? "preparation_refused_before_launch"
                : $"preparation_unconfirmed: launch {launch.Id:D}; preserve pending ownership", ex is OpenShellStateConflictException ? 409 : 503);
        }
    }

    private static async Task RetireHeldAsync(OrchestratorDbContext db, OpenShellLaunchStore store,
        OpenShellLaunch launch, IOpenShellGatewayClient gateway, IOpenShellRuntimeEvidenceReader reader, CancellationToken ct)
    {
        await store.RetireAsync(launch.Id, DateTime.UtcNow, ct);
        var target = await gateway.ReadExactAsync(launch.SandboxId!, ct)
            ?? throw new OpenShellStateConflictException("held_cleanup_requires_reconciliation");
        launch.StopIntentJson = JsonSerializer.Serialize(new OpenShellRetirementService.Intent(
            Guid.NewGuid(), target.GatewayId, target.Workspace, target.Id, target.Name, target.ResourceVersion));
        await db.SaveChangesAsync(ct);
        var stop = JsonSerializer.Deserialize<OpenShellRetirementService.Intent>(launch.StopIntentJson)!;
        await gateway.StopExactAsync(target, stop.RequestId, ct);
        target = await gateway.ReadExactAsync(target.Id, ct)
            ?? throw new OpenShellStateConflictException("held_cleanup_requires_reconciliation");
        var stopped = await reader.ReadAsync(new(launch.Id, launch.IdentityEpoch, target.GatewayId, target.Id, false), ct);
        if (stopped.LaunchId != launch.Id || stopped.IdentityEpoch != launch.IdentityEpoch
            || stopped.GatewayId != target.GatewayId || stopped.RuntimeId != target.Id || !stopped.Stopped
            || stopped.Consumers != 0 || stopped.Pollers != 0 || stopped.ObservedAt.Kind != DateTimeKind.Utc
            || stopped.ObservedAt > DateTime.UtcNow || DateTime.UtcNow - stopped.ObservedAt > TimeSpan.FromSeconds(5))
            throw new OpenShellStateConflictException("held_stop_unconfirmed");
        launch.DeleteIntentJson = JsonSerializer.Serialize(new OpenShellRetirementService.Intent(
            Guid.NewGuid(), target.GatewayId, target.Workspace, target.Id, target.Name, target.ResourceVersion));
        await db.SaveChangesAsync(ct);
        var delete = JsonSerializer.Deserialize<OpenShellRetirementService.Intent>(launch.DeleteIntentJson)!;
        await gateway.DeleteExactAsync(target, delete.RequestId, true, ct);
        await store.CompleteCleanupAsync(launch.Id, DateTime.UtcNow, reader, ct);
    }
}
