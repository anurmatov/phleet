using System.Data;
using System.Text.Json;
using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;
using Openshell.V1;

namespace Fleet.Orchestrator.Services;

/// <summary>Stop/deprovision only. No creation, activation, legacy fallback or automatic replay.</summary>
public sealed class OpenShellRetirementService(Func<OrchestratorDbContext> createDb, IServiceScopeFactory scopes)
{
    public sealed record Intent(Guid RequestId, string GatewayId, string Workspace, string SandboxId,
        string Name, ulong ResourceVersion)
    {
        public OpenShellSandboxSnapshot Target() => new(GatewayId, Workspace, SandboxId, Name,
            SandboxPhase.Unspecified, "", 0, new Dictionary<string, string>()) { ResourceVersion = ResourceVersion };
    }

    public Task<ProvisionResult> StopAsync(string name, CancellationToken ct = default) => RunAsync(name, false, ct);
    public Task<ProvisionResult> DeprovisionAsync(string name, CancellationToken ct = default) => RunAsync(name, true, ct);

    private async Task<ProvisionResult> RunAsync(string name, bool delete, CancellationToken callerToken)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        operation.CancelAfter(TimeSpan.FromSeconds(120));
        var ct = operation.Token;
        using var scope = scopes.CreateScope();
        var gateway = scope.ServiceProvider.GetService<IOpenShellGatewayClient>();
        var reader = scope.ServiceProvider.GetService<IOpenShellRuntimeEvidenceReader>();
        if (gateway is null || reader is null)
            return ProvisionResult.Fail(name, "compatibility_blocked: trusted retirement peers are unavailable", 503);
        Guid? submitted = null;
        try
        {
            await using var db = createDb();
            if (db.Database.CreateExecutionStrategy().RetriesOnFailure)
                return ProvisionResult.Fail(name, "non-retrying lifecycle storage is required", 503);
            var launch = await FindAsync(db, name, ct);
            if (launch is null) return ProvisionResult.Fail(name, "exact launch ownership is unavailable", 409);
            if (launch.ActiveAgentId is not null && launch.ActiveAgentId != launch.AgentId
                || launch.Phase is "stopped" or "retired" && launch.RevokedAt is null)
                return ProvisionResult.Fail(name, "launch ownership or revocation is inconsistent", 409);
            if (launch.Phase is "stop_pending" or "cleanup_pending")
                return ProvisionResult.Fail(name, $"retirement pending for launch {launch.Id:D}; reconcile the persisted intent without resending", 409);
            if (launch.Phase is "stopped" or "retired")
            {
                if (!await db.OpenShellRetiredIds.AnyAsync(x => x.GatewayId == launch.GatewayId && x.SandboxId == launch.SandboxId, ct))
                    throw new OpenShellStateConflictException("Retirement tombstone is missing.");
                await VerifyAsync(reader, launch, launch.Phase == "retired", ct);
            }
            else
            {
                if (launch.RevokedAt is not null || launch.Phase is not ("activated" or "created"))
                    return ProvisionResult.Fail(name, "launch phase does not permit retirement", 409);
                var target = await gateway.ReadExactAsync(launch.SandboxId!, ct);
                if (target is null) return ProvisionResult.Fail(name, "runtime observation is unavailable; reconciliation required", 503);
                var intent = await PrepareAsync(db, launch.Id, target, delete: false, ct);
                submitted = intent.RequestId;
                await gateway.StopExactAsync(intent.Target(), intent.RequestId, ct);
                await FinishStopAsync(db, launch.Id, intent.RequestId, reader, ct);
            }
            if (!delete) return ProvisionResult.Ok(name, "sandbox stopped and intake drain verified; retained ID cannot resume");
            launch = await FindAsync(db, name, ct) ?? throw new OpenShellStateConflictException("Launch disappeared.");
            if (launch.Phase == "retired")
                return ProvisionResult.Ok(name, "sandbox deprovisioned; absence and drained intake reverified");
            var observed = await gateway.ReadExactAsync(launch.SandboxId!, ct);
            if (observed is null) return ProvisionResult.Fail(name, "delete requires reconciliation of missing runtime", 409);
            var deletion = await PrepareAsync(db, launch.Id, observed, delete: true, ct);
            submitted = deletion.RequestId;
            await gateway.DeleteExactAsync(deletion.Target(), deletion.RequestId, allowMissing: true, ct);
            // Completion is based on fresh exact-runtime and intake evidence, never the RPC acknowledgement.
            await new OpenShellLaunchStore(db).CompleteCleanupAsync(launch.Id, DateTime.UtcNow, reader, ct, deletion.RequestId);
            return ProvisionResult.Ok(name, "sandbox deprovisioned after absence and intake drain verification; config retained");
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested) { throw; }
        catch (OpenShellStateConflictException)
        {
            return ProvisionResult.Fail(name, "retirement state conflict; reconciliation required", 409);
        }
        catch (Exception)
        {
            // Do not log driver messages, tokens, connection strings or response bodies.
            return ProvisionResult.Fail(name, submitted is { } requestId
                ? $"retirement unconfirmed for request {requestId:D}; preserve recovery state and reconcile without replay"
                : "retirement unavailable; preserve recovery state and reconcile without replay", submitted is not null ? 202 : 503);
        }
    }

    private static Task<OpenShellLaunch?> FindAsync(OrchestratorDbContext db, string name, CancellationToken ct) =>
        db.OpenShellLaunches.AsNoTracking().Where(l => l.Agent.Name == name && l.Agent.ExecutionShell == "openshell"
            && l.Agent.OpenShellMigrationState == "active" && l.GatewayId != null && l.SandboxId != null && l.Workspace != null)
            .OrderByDescending(l => l.ActiveAgentId != null).ThenByDescending(l => l.CreatedAt).FirstOrDefaultAsync(ct);

    private static async Task<Intent> PrepareAsync(OrchestratorDbContext db, Guid id,
        OpenShellSandboxSnapshot target, bool delete, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var row = await db.OpenShellLaunches.SingleAsync(l => l.Id == id, ct);
        await db.Entry(row).ReloadAsync(ct);
        var agent = await db.Agents.AsNoTracking().SingleAsync(a => a.Id == row.AgentId, ct);
        if (agent.ExecutionShell != "openshell" || agent.OpenShellMigrationState != "active" || row.ActiveAgentId != row.AgentId
            || row.GatewayId != target.GatewayId || row.SandboxId != target.Id || row.Workspace != target.Workspace
            || target.ResourceVersion == 0 || (delete ? row.Phase != "stopped" || row.DeleteIntentJson is not null
                : row.Phase is not ("activated" or "created") || row.StopIntentJson is not null || row.RevokedAt is not null))
            throw new OpenShellStateConflictException("Exact launch state changed before retirement.");
        var now = DateTime.UtcNow;
        var lease = await db.OpenShellLeases.SingleOrDefaultAsync(l => l.AgentId == row.AgentId, ct);
        if (lease is not null)
        {
            await db.Entry(lease).ReloadAsync(ct);
            if (lease.ExpiresAt > now) throw new OpenShellStateConflictException("Lifecycle lease is busy.");
        }
        var intent = new Intent(Guid.NewGuid(), target.GatewayId, target.Workspace, target.Id, target.Name, target.ResourceVersion);
        var json = JsonSerializer.Serialize(intent);
        if (json.Length > 2048) throw new OpenShellStateConflictException("Mutation intent is too large.");
        if (lease is null) { lease = new() { AgentId = row.AgentId }; db.OpenShellLeases.Add(lease); }
        // An expired lease gets a fresh operation owner, never a renewal of its old owner.
        lease.Owner = intent.RequestId; lease.ExpiresAt = now.AddSeconds(120);
        if (delete) row.DeleteIntentJson = json;
        else row.StopIntentJson = json;
        row.Phase = delete ? "cleanup_pending" : "stop_pending";
        row.RevokedAt ??= now;
        row.UpdatedAt = now;
        if (!await db.OpenShellRetiredIds.AnyAsync(x => x.GatewayId == row.GatewayId && x.SandboxId == row.SandboxId, ct))
            db.OpenShellRetiredIds.Add(new() { GatewayId = row.GatewayId!, SandboxId = row.SandboxId!, RetiredAt = now });
        // Commit the complete immutable payload and revoke before sending even one mutation RPC.
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return intent;
    }

    private static async Task FinishStopAsync(OrchestratorDbContext db, Guid id, Guid owner,
        IOpenShellRuntimeEvidenceReader reader, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var launch = await db.OpenShellLaunches.SingleAsync(l => l.Id == id, ct);
        await db.Entry(launch).ReloadAsync(ct);
        var lease = await db.OpenShellLeases.SingleAsync(l => l.AgentId == launch.AgentId, ct);
        await db.Entry(lease).ReloadAsync(ct);
        if (launch.Phase != "stop_pending" || launch.RevokedAt is null || lease.Owner != owner || lease.ExpiresAt <= DateTime.UtcNow)
            throw new OpenShellStateConflictException("Stop ownership changed before completion.");
        await VerifyAsync(reader, launch, false, ct);
        if (lease.ExpiresAt <= DateTime.UtcNow)
            throw new OpenShellStateConflictException("Stop lease expired during verification.");
        launch.Phase = "stopped";
        db.OpenShellLeases.Remove(lease);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private static async Task VerifyAsync(IOpenShellRuntimeEvidenceReader reader, OpenShellLaunch launch,
        bool absence, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var target = new OpenShellRuntimeTarget(launch.Id, launch.IdentityEpoch, launch.GatewayId, launch.SandboxId, absence);
        var proof = await reader.ReadAsync(target, deadline.Token).WaitAsync(deadline.Token);
        var now = DateTime.UtcNow;
        if (proof.LaunchId != target.LaunchId || proof.IdentityEpoch != target.IdentityEpoch
            || proof.GatewayId != target.GatewayId || proof.RuntimeId != target.RuntimeId
            || !proof.Stopped || absence && !proof.Absent || proof.Consumers != 0 || proof.Pollers != 0
            || proof.ObservedAt.Kind != DateTimeKind.Utc || proof.ObservedAt > now || now - proof.ObservedAt > TimeSpan.FromSeconds(5))
            throw new OpenShellStateConflictException("Fresh stopped/absent and drained-intake proof is required.");
    }
}
