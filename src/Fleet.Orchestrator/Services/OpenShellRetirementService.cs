using System.Collections.Concurrent;
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
            if (launch is null || !Bound(launch)) return ProvisionResult.Fail(name, "exact launch ownership is unavailable", 409);
            if (launch.ActiveAgentId is not null && launch.ActiveAgentId != launch.AgentId
                || launch.Phase is "stopped" or "retired" && launch.RevokedAt is null)
                return ProvisionResult.Fail(name, "launch ownership or revocation is inconsistent", 409);
            if (launch.Phase is "stop_pending" or "cleanup_pending")
            {
                var pending = JsonSerializer.Deserialize<Intent>((launch.Phase == "stop_pending"
                    ? launch.StopIntentJson : launch.DeleteIntentJson) ?? "null");
                return ProvisionResult.Fail(name, $"retirement pending for launch {launch.Id:D}, request {pending?.RequestId:D}; use explicit operator reconciliation", 409);
            }
            if (launch.Phase is "stopped" or "retired")
            {
                if (!await db.OpenShellRetiredIds.AnyAsync(x => x.GatewayId == launch.GatewayId && x.SandboxId == launch.SandboxId, ct))
                    throw new OpenShellStateConflictException("Retirement tombstone is missing.");
                await VerifyTerminalAsync(db, reader, launch.Id, ct);
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
            if (!Bound(launch)) throw new OpenShellStateConflictException("Current launch mapping is incomplete.");
            if (launch.Phase == "retired")
            {
                await VerifyArtifactRetirementAsync(launch, scope.ServiceProvider.GetService<IOpenShellArtifactCleanup>(), ct);
                return ProvisionResult.Ok(name, "sandbox deprovisioned; absence and drained intake reverified");
            }
            var observed = await gateway.ReadExactAsync(launch.SandboxId!, ct);
            if (observed is null) return ProvisionResult.Fail(name, "delete requires reconciliation of missing runtime", 409);
            var deletion = await PrepareAsync(db, launch.Id, observed, delete: true, ct);
            submitted = deletion.RequestId;
            await gateway.DeleteExactAsync(deletion.Target(), deletion.RequestId, allowMissing: true, ct);
            // Completion is based on fresh exact-runtime and intake evidence, never the RPC acknowledgement.
            await new OpenShellLaunchStore(db, artifacts: scope.ServiceProvider.GetService<IOpenShellArtifactCleanup>()).CompleteCleanupAsync(launch.Id, DateTime.UtcNow, reader, ct, deletion.RequestId);
            return ProvisionResult.Ok(name, "sandbox deprovisioned after absence and intake drain verification; config retained");
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested) { throw; }
        catch (OpenShellStateConflictException)
        {
            return ProvisionResult.Fail(name, submitted is { } requestId
                ? $"retirement state conflict for request {requestId:D}; reconciliation required"
                : "retirement state conflict; reconciliation required", 409);
        }
        catch (Exception)
        {
            // Do not log driver messages, tokens, connection strings or response bodies.
            return ProvisionResult.Fail(name, submitted is { } requestId
                ? $"retirement unconfirmed for request {requestId:D}; preserve recovery state and use explicit operator reconciliation"
                : "retirement unavailable; preserve recovery state and use explicit operator reconciliation", 503);
        }
    }

    /// <summary>Explicit outside-operator recovery only; never creates a new mutation payload or resumes an ID.</summary>
    public async Task<ProvisionResult> ReconcileAsync(string name, Guid expectedLaunchId, Guid expectedRequestId,
        bool replay, CancellationToken callerToken = default)
    {
        if (expectedLaunchId == Guid.Empty || expectedRequestId == Guid.Empty)
            return ProvisionResult.Fail(name, "expected launch and request IDs are required", 400);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        operation.CancelAfter(TimeSpan.FromSeconds(120));
        var ct = operation.Token;
        using var scope = scopes.CreateScope();
        var gateway = scope.ServiceProvider.GetService<IOpenShellGatewayClient>();
        var reader = scope.ServiceProvider.GetService<IOpenShellRuntimeEvidenceReader>();
        if (gateway is null || reader is null)
            return ProvisionResult.Fail(name, "compatibility_blocked: trusted retirement peers are unavailable", 503);
        try
        {
            await using var db = createDb();
            if (db.Database.CreateExecutionStrategy().RetriesOnFailure)
                return ProvisionResult.Fail(name, "non-retrying lifecycle storage is required", 503);
            var (intent, delete) = await ClaimReconciliationAsync(db, name, expectedLaunchId, expectedRequestId, ct);
            // Replay is separately explicit. Do not look up a new name/version or mint a request UUID.
            // The pinned gateway must deduplicate the original complete payload; a refusal stays pending.
            if (replay)
            {
                if (delete) await gateway.DeleteExactAsync(intent.Target(), intent.RequestId, allowMissing: true, ct);
                else await gateway.StopExactAsync(intent.Target(), intent.RequestId, ct);
            }
            if (delete) await new OpenShellLaunchStore(db, artifacts: scope.ServiceProvider.GetService<IOpenShellArtifactCleanup>()).CompleteCleanupAsync(expectedLaunchId, DateTime.UtcNow, reader, ct, intent.RequestId);
            else await FinishStopAsync(db, expectedLaunchId, intent.RequestId, reader, ct);
            return ProvisionResult.Ok(name, $"request {intent.RequestId:D} reconciled with fresh runtime and drained-intake proof");
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested) { throw; }
        catch (OpenShellStateConflictException)
        {
            return ProvisionResult.Fail(name, $"request {expectedRequestId:D} conflicts with current ownership or proof; recovery state retained", 409);
        }
        catch (Exception)
        {
            return ProvisionResult.Fail(name, $"request {expectedRequestId:D} remains unconfirmed; recovery state retained", 503);
        }
    }

    /// <summary>
    /// Explicit outside-operator recovery for a PENDING PREPARATION launch. The provisioning failure
    /// path produces these and they are never visible to the selection above: migration state stays
    /// "pending", the held-cleanup lease owner is the launch itself (reserved in
    /// <see cref="OpenShellLaunchStore"/>), not a request UUID, and whether the held cleanup is a
    /// Stop or a Delete is decided solely by the persisted intents — never by the phase, because
    /// <c>cleanup_pending</c> is set before either intent exists. The original mutation identity is
    /// replayed verbatim; a Delete continuation acquires its own fresh identity only after fresh
    /// stop proof; the never-issued mutation is never replayed under a re-minted identity, no
    /// metadata is promoted and Create is never retried. Anything unmatched leaves the launch pending.
    /// </summary>
    public async Task<ProvisionResult> ReconcilePreparationAsync(string name, Guid expectedLaunchId,
        bool replay, CancellationToken callerToken = default)
    {
        if (expectedLaunchId == Guid.Empty)
            return ProvisionResult.Fail(name, "expected launch ID is required", 400);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        operation.CancelAfter(TimeSpan.FromSeconds(120));
        var ct = operation.Token;
        using var scope = scopes.CreateScope();
        var gateway = scope.ServiceProvider.GetService<IOpenShellGatewayClient>();
        var reader = scope.ServiceProvider.GetService<IOpenShellRuntimeEvidenceReader>();
        if (gateway is null || reader is null)
            return ProvisionResult.Fail(name, "compatibility_blocked: trusted retirement peers are unavailable", 503);
        try
        {
            await using var db = createDb();
            if (db.Database.CreateExecutionStrategy().RetriesOnFailure)
                return ProvisionResult.Fail(name, "non-retrying lifecycle storage is required", 503);
            var row = await ClaimPreparationReconciliationAsync(db, name, expectedLaunchId, replay, ct);
            var store = new OpenShellLaunchStore(db, artifacts: scope.ServiceProvider.GetService<IOpenShellArtifactCleanup>());
            var stop = row.StopIntentJson is null ? null : JsonSerializer.Deserialize<Intent>(row.StopIntentJson);
            var delete = row.DeleteIntentJson is null ? null : JsonSerializer.Deserialize<Intent>(row.DeleteIntentJson);
            if (delete is not null)
            {
                // Pending Delete lost its reply: replay only the original delete identity; completion
                // is always the fresh exact absence/drain proof, never the acknowledgement.
                if (replay) await gateway.DeleteExactAsync(delete.Target(), delete.RequestId, allowMissing: true, ct);
                await store.CompleteCleanupAsync(row.Id, DateTime.UtcNow, reader, ct);
                return ProvisionResult.Ok(name, $"preparation launch {row.Id:D} retired with fresh absence and drained-intake proof after delete {delete.RequestId:D}");
            }
            if (stop is not null)
            {
                // Pending Stop lost its reply: replay only the original stop identity, then the
                // never-issued Delete of the same held-cleanup chain acquires its own identity —
                // only after fresh stop proof, and the stop identity is never re-minted.
                if (replay) await gateway.StopExactAsync(stop.Target(), stop.RequestId, ct);
                var observed = await gateway.ReadExactAsync(row.SandboxId!, ct)
                    ?? throw new OpenShellStateConflictException("preparation_recovery_runtime_missing");
                // A refreshed observation only describes the launch if its gateway, workspace and
                // ID still equal the persisted values: drift means a replacement under the old ID,
                // which earns no proof, no intent record and no mutation. Checked before all three.
                if (observed.GatewayId != row.GatewayId || observed.Workspace != row.Workspace
                    || observed.Id != row.SandboxId)
                    throw new OpenShellStateConflictException("preparation_recovery_runtime_drift");
                await ProvePreparationStoppedAsync(reader, row, observed, ct);
                var deletion = new Intent(Guid.NewGuid(), observed.GatewayId, observed.Workspace, observed.Id,
                    observed.Name, observed.ResourceVersion);
                row.DeleteIntentJson = JsonSerializer.Serialize(deletion);
                await db.SaveChangesAsync(ct);
                await gateway.DeleteExactAsync(observed, deletion.RequestId, allowMissing: true, ct);
                await store.CompleteCleanupAsync(row.Id, DateTime.UtcNow, reader, ct);
                return ProvisionResult.Ok(name, $"preparation launch {row.Id:D} retired after fresh absence proof following stop {stop.RequestId:D}");
            }
            // The preparation failed before any gateway mutation was persisted (for example an
            // ambiguous Create): nothing exists to replay; only a complete authenticated
            // launch-owned inventory can prove nothing owned survived, and then the slot releases.
            await store.CompleteCleanupAsync(row.Id, DateTime.UtcNow, reader, ct);
            return ProvisionResult.Ok(name, $"preparation launch {row.Id:D} retired after fresh absence and drained-intake proof; migration state unchanged");
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested) { throw; }
        catch (OpenShellStateConflictException)
        {
            return ProvisionResult.Fail(name, $"preparation launch {expectedLaunchId:D} conflicts with current ownership or proof; recovery state retained", 409);
        }
        catch (Exception)
        {
            return ProvisionResult.Fail(name, $"preparation launch {expectedLaunchId:D} remains unconfirmed; recovery state retained", 503);
        }
    }

    private static async Task<OpenShellLaunch> ClaimPreparationReconciliationAsync(OrchestratorDbContext db,
        string name, Guid expectedLaunchId, bool replay, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var agent = await db.Agents.AsNoTracking().SingleOrDefaultAsync(a => a.Name == name, ct);
        if (agent is null || agent.ExecutionShell != "openshell")
            throw new OpenShellStateConflictException("preparation_recovery_agent_unqualified");
        // Preparation never promotes migration state; once it is not "pending" the launch belongs to
        // the normal retirement paths above and this route must not compete with them.
        if (agent.OpenShellMigrationState != "pending")
            throw new OpenShellStateConflictException("preparation_recovery_state_promoted");
        var row = await db.OpenShellLaunches.SingleOrDefaultAsync(l => l.Id == expectedLaunchId, ct);
        if (row is null || row.AgentId != agent.Id)
            throw new OpenShellStateConflictException("preparation_recovery_wrong_owner");
        await db.Entry(row).ReloadAsync(ct);
        if (await db.OpenShellLaunches.AnyAsync(l => l.AgentId == agent.Id && l.Id != row.Id
                && (l.ActiveAgentId != null || l.Phase != "retired" || l.RevokedAt == null), ct))
            throw new OpenShellStateConflictException("preparation_recovery_ambiguous_ownership");
        // Producer contract (provisioning catch → OpenShellLaunchStore.RetireAsync): revoked, still
        // holding the active slot, phase cleanup_pending.
        if (row.RevokedAt is null || row.ActiveAgentId != row.AgentId || row.Phase != "cleanup_pending")
            throw new OpenShellStateConflictException("preparation_recovery_not_pending");
        if ((row.GatewayId is null) != (row.SandboxId is null))
            throw new OpenShellStateConflictException("preparation_recovery_mapping_inconsistent");
        var stop = row.StopIntentJson is null ? null : JsonSerializer.Deserialize<Intent>(row.StopIntentJson);
        var delete = row.DeleteIntentJson is null ? null : JsonSerializer.Deserialize<Intent>(row.DeleteIntentJson);
        if (row.SandboxId is null && (stop is not null || delete is not null))
            throw new OpenShellStateConflictException("preparation_recovery_intent_unbound");
        if (replay && stop is null && delete is null)
            throw new OpenShellStateConflictException("preparation_recovery_nothing_to_replay");
        foreach (var intent in new[] { stop, delete })
            if (intent is not null && (intent.RequestId == Guid.Empty || intent.GatewayId != row.GatewayId
                    || intent.Workspace != row.Workspace || intent.SandboxId != row.SandboxId
                    || string.IsNullOrWhiteSpace(intent.Name) || intent.ResourceVersion == 0))
                throw new OpenShellStateConflictException("preparation_recovery_intent_inconsistent");
        if (row.SandboxId is not null && !await db.OpenShellRetiredIds.AnyAsync(
                x => x.GatewayId == row.GatewayId && x.SandboxId == row.SandboxId, ct))
            throw new OpenShellStateConflictException("preparation_recovery_tombstone_missing");
        var lease = await db.OpenShellLeases.SingleOrDefaultAsync(l => l.AgentId == row.AgentId, ct);
        if (lease is null)
            // The preparation lease spans reserve through held cleanup; gone means another operation
            // owns this agent's lifecycle now and recovery must not steal it.
            throw new OpenShellStateConflictException("preparation_recovery_lease_missing");
        await db.Entry(lease).ReloadAsync(ct);
        // Preparation lease ownership is the LAUNCH itself (Reserve) — unlike retirement, which
        // reassigns the owner to the intent request UUID.
        if (lease.Owner != row.Id)
            throw new OpenShellStateConflictException("preparation_recovery_foreign_lease");
        var now = DateTime.UtcNow;
        if (lease.ExpiresAt > now)
            throw new OpenShellStateConflictException("preparation_recovery_lease_live");
        // Only the original owner is re-armed, and only with a fresh expiry; identity never changes.
        lease.ExpiresAt = now.AddSeconds(120);
        row.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return row;
    }

    private static async Task ProvePreparationStoppedAsync(IOpenShellRuntimeEvidenceReader reader,
        OpenShellLaunch launch, OpenShellSandboxSnapshot target, CancellationToken ct)
    {
        // The same stopped-and-drained shape the provisioning held cleanup requires before Delete.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var proof = await reader.ReadAsync(new(launch.Id, launch.IdentityEpoch, target.GatewayId, target.Id, false),
            deadline.Token).WaitAsync(deadline.Token);
        var now = DateTime.UtcNow;
        if (proof.LaunchId != launch.Id || proof.IdentityEpoch != launch.IdentityEpoch
            || proof.GatewayId != target.GatewayId || proof.RuntimeId != target.Id || !proof.Stopped
            || proof.Consumers != 0 || proof.Pollers != 0 || proof.ObservedAt.Kind != DateTimeKind.Utc
            || proof.ObservedAt > now || now - proof.ObservedAt > TimeSpan.FromSeconds(5))
            throw new OpenShellStateConflictException("preparation_recovery_stop_unconfirmed");
    }

    private static async Task<(Intent Intent, bool Delete)> ClaimReconciliationAsync(OrchestratorDbContext db,
        string name, Guid expectedLaunchId, Guid expectedRequestId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var selected = await FindAsync(db, name, ct);
        if (selected is null || selected.Id != expectedLaunchId)
            throw new OpenShellStateConflictException("The expected launch is not the current owner.");
        var row = await db.OpenShellLaunches.SingleAsync(l => l.Id == selected.Id, ct);
        await db.Entry(row).ReloadAsync(ct);
        if (!Bound(row) || row.ActiveAgentId != row.AgentId || row.RevokedAt is null
            || row.Phase is not ("stop_pending" or "cleanup_pending"))
            throw new OpenShellStateConflictException("Reconciliation requires a bound, revoked, pending current launch.");
        var delete = row.Phase == "cleanup_pending";
        var json = delete ? row.DeleteIntentJson : row.StopIntentJson;
        var intent = json is null ? null : JsonSerializer.Deserialize<Intent>(json);
        if (intent is null || intent.RequestId != expectedRequestId || intent.RequestId == Guid.Empty
            || intent.GatewayId != row.GatewayId || intent.Workspace != row.Workspace
            || intent.SandboxId != row.SandboxId || string.IsNullOrWhiteSpace(intent.Name) || intent.ResourceVersion == 0
            || !await db.OpenShellRetiredIds.AnyAsync(x => x.GatewayId == row.GatewayId && x.SandboxId == row.SandboxId, ct))
            throw new OpenShellStateConflictException("The persisted mutation identity is unavailable or inconsistent.");
        var lease = await db.OpenShellLeases.SingleOrDefaultAsync(l => l.AgentId == row.AgentId, ct);
        if (lease is null) throw new OpenShellStateConflictException("The pending operation lease is missing.");
        await db.Entry(lease).ReloadAsync(ct);
        var now = DateTime.UtcNow;
        if (lease.Owner != intent.RequestId || lease.ExpiresAt > now)
            throw new OpenShellStateConflictException("Another or still-live operation owns the lease.");
        // Only the original expired pending operation can be renewed by this explicit recovery path.
        lease.ExpiresAt = now.AddSeconds(120);
        row.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (intent, delete);
    }

    /// <summary>Delete configuration only after deprovision, rechecking every retained launch under the deletion transaction.</summary>
    public async Task<(ProvisionResult Result, bool AclChanged)> CompleteAgentDeletionAsync(string name, int expectedAgentId, CancellationToken callerToken = default)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        operation.CancelAfter(TimeSpan.FromSeconds(120));
        var ct = operation.Token;
        using var scope = scopes.CreateScope();
        var reader = scope.ServiceProvider.GetService<IOpenShellRuntimeEvidenceReader>();
        if (reader is null) return (ProvisionResult.Fail(name, "compatibility_blocked: trusted deletion evidence is unavailable", 503), false);
        try
        {
            await using var db = createDb();
            if (db.Database.CreateExecutionStrategy().RetriesOnFailure)
                return (ProvisionResult.Fail(name, "non-retrying lifecycle storage is required", 503), false);
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var agent = await db.Agents.SingleOrDefaultAsync(x => x.Name == name, ct);
            if (agent is null) return (ProvisionResult.Fail(name, "agent not found in DB", 404), false);
            var launches = await db.OpenShellLaunches.Where(x => x.AgentId == agent.Id).ToListAsync(ct);
            if (agent.Id != expectedAgentId || agent.ExecutionShell != "openshell" || agent.OpenShellMigrationState != "active" || launches.Count == 0
                || launches.Any(x => x.Phase != "retired" || x.RevokedAt is null || x.ActiveAgentId is not null)
                || await db.OpenShellLeases.AnyAsync(x => x.AgentId == agent.Id, ct))
                throw new OpenShellStateConflictException("Every launch must be retired with no active slot or lifecycle owner.");
            foreach (var launch in launches)
            {
                if (launch.SandboxId is not null && (!Bound(launch) || !await db.OpenShellRetiredIds.AnyAsync(x =>
                        x.GatewayId == launch.GatewayId && x.SandboxId == launch.SandboxId, ct)))
                    throw new OpenShellStateConflictException("Deletion cannot discard incomplete mapping or missing tombstone.");
            }
            foreach (var launch in launches)
                await VerifyArtifactRetirementAsync(launch, scope.ServiceProvider.GetService<IOpenShellArtifactCleanup>(), ct);
            // Complete mapping checks before parallel I/O; never share the DbContext with workers.
            // Bounded workers avoid one task per historical launch. A single cohort deadline also
            // bounds queued reads, rather than serially spending five seconds per retired sandbox.
            using var proofDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            proofDeadline.CancelAfter(TimeSpan.FromSeconds(5));
            var observations = new ConcurrentBag<DateTime>();
            await Parallel.ForEachAsync(launches, new ParallelOptions
            {
                MaxDegreeOfParallelism = 8, CancellationToken = proofDeadline.Token,
            }, async (launch, token) => observations.Add(await VerifyAsync(reader, launch, true, token)));
            var oldestProof = observations.Min();
            var ids = launches.Select(x => x.Id).ToHashSet();
            // Keep the SQL membership test relational, not a captured array/span expression.
            var ownedIds = db.OpenShellLaunches.Where(x => x.AgentId == agent.Id).Select(x => x.Id);
            var sessions = await db.OpenShellIdentitySessions.Where(x => x.AgentId == agent.Id || ownedIds.Contains(x.LaunchId)).ToListAsync(ct);
            if (sessions.Any(x => x.AgentId != agent.Id || !ids.Contains(x.LaunchId)))
                throw new OpenShellStateConflictException("Session ownership is inconsistent; preserve recovery state.");
            // Remove only this agent's references, never shared credential files or owned workspace bytes.
            db.OpenShellRequestNonces.RemoveRange(await db.OpenShellRequestNonces.Where(x => ownedIds.Contains(x.LaunchId)).ToListAsync(ct));
            db.OpenShellIdentitySessions.RemoveRange(sessions);
            db.OpenShellSecretBindings.RemoveRange(await db.OpenShellSecretBindings.Where(x => x.AgentId == agent.Id).ToListAsync(ct));
            db.OpenShellLaunches.RemoveRange(launches);
            var aclChanged = await AgentProjectAccessSync.StageAssignmentsAsync(db, name, [], ct);
            db.Agents.Remove(agent); // Existing FK cascades remove configuration links, not their shared resources.
            if (DateTime.UtcNow - oldestProof > TimeSpan.FromSeconds(5))
                throw new OpenShellStateConflictException("Deletion proof aged out before configuration cleanup.");
            await db.SaveChangesAsync(ct);
            if (DateTime.UtcNow - oldestProof > TimeSpan.FromSeconds(5))
                throw new OpenShellStateConflictException("Deletion proof aged out before commit.");
            await tx.CommitAsync(ct);
            return (ProvisionResult.Ok(name, "agent configuration deleted after fresh absence and intake-drain proof"), aclChanged);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested) { throw; }
        catch (OpenShellStateConflictException)
        {
            return (ProvisionResult.Fail(name, "agent deletion blocked by current ownership or proof; recovery state retained", 409), false);
        }
        catch (Exception)
        {
            return (ProvisionResult.Fail(name, "agent deletion unconfirmed; inspect authoritative state before retrying", 503), false);
        }
    }

    private static async Task VerifyArtifactRetirementAsync(OpenShellLaunch launch, IOpenShellArtifactCleanup? artifacts, CancellationToken ct)
    {
        if (launch.CapabilitySecretRef is null) return;
        var preparation = launch.PreparationJson is null ? null : JsonSerializer.Deserialize<OpenShellPreparationScope>(launch.PreparationJson);
        if (artifacts is null || preparation is null || preparation.PlanHash != preparation.Plan.Hash)
            throw new OpenShellStateConflictException("bootstrap_retirement_unverified");
        await artifacts.VerifyRetiredAsync(new(launch.Id, launch.IdentityEpoch, preparation.PlanHash, launch.CapabilitySecretRef), ct).WaitAsync(ct);
    }

    private static Task<OpenShellLaunch?> FindAsync(OrchestratorDbContext db, string name, CancellationToken ct) =>
        db.OpenShellLaunches.AsNoTracking().Where(l => l.Agent.Name == name && l.Agent.ExecutionShell == "openshell"
            && l.Agent.OpenShellMigrationState == "active")
            .OrderByDescending(l => l.ActiveAgentId != null).ThenByDescending(l => l.CreatedAt).FirstOrDefaultAsync(ct);

    private static bool Bound(OpenShellLaunch launch) => !string.IsNullOrWhiteSpace(launch.GatewayId)
        && !string.IsNullOrWhiteSpace(launch.SandboxId) && !string.IsNullOrWhiteSpace(launch.Workspace);

    private static async Task VerifyTerminalAsync(OrchestratorDbContext db, IOpenShellRuntimeEvidenceReader reader,
        Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var row = await db.OpenShellLaunches.SingleAsync(l => l.Id == id, ct);
        await db.Entry(row).ReloadAsync(ct);
        if (!Bound(row) || row.RevokedAt is null || row.Phase is not ("stopped" or "retired")
            || (row.Phase == "stopped" ? row.ActiveAgentId != row.AgentId : row.ActiveAgentId is not null)
            || await db.OpenShellLaunches.AnyAsync(l => l.AgentId == row.AgentId && l.Id != row.Id
                && (l.ActiveAgentId != null || l.Phase != "retired"), ct))
            throw new OpenShellStateConflictException("Current or ambiguous launch ownership prevents historical success.");
        await VerifyAsync(reader, row, row.Phase == "retired", ct);
        await tx.CommitAsync(ct);
    }

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

    private static async Task<DateTime> VerifyAsync(IOpenShellRuntimeEvidenceReader reader, OpenShellLaunch launch,
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
            || absence && target.RuntimeId is null && (!proof.LaunchInventoryComplete || proof.OwnedSandboxCount != 0)
            || proof.ObservedAt.Kind != DateTimeKind.Utc || proof.ObservedAt > now || now - proof.ObservedAt > TimeSpan.FromSeconds(5))
            throw new OpenShellStateConflictException("Fresh stopped/absent and drained-intake proof is required.");
        return proof.ObservedAt;
    }
}
