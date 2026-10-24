using System.Globalization;
using System.Text.Json;
using Fleet.Orchestrator.Configuration;
using Fleet.Orchestrator.Data;
using Fleet.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Fleet.Orchestrator.Services;

// Workflow failure accountability (#476): the observer's persistence. The observer is the only
// writer of run observation fields, observations, checkpoints (including the fenced lease row) and
// observer health rows. Every write runs in one transaction that first re-checks the fenced lease,
// so a lost lease or a DB error never advances a cursor or half-stores a claim.

/// <summary>The fenced lease is held by someone else (or expired): stop new work.</summary>
public sealed class WorkflowOutcomeLeaseLostException(string message) : Exception(message);

/// <summary>A held observer lease. <see cref="Epoch"/> is the fencing token.</summary>
public sealed record WorkflowOutcomeLease(string Owner, long Epoch);

public static class WorkflowOutcomeWindowKinds
{
    public const string Recent = "recent";
    public const string Full = "full";
    public const string Enrollment = "enrollment";
    public const string Lease = "lease";
}

/// <summary>Health incident components (deduplicated by component + outage key).</summary>
public static class WorkflowOutcomeHealthComponents
{
    public const string Temporal = "temporal";
    public const string Visibility = "visibility";
    public const string History = "history";
    public const string Coverage = "coverage";
    public const string Capacity = "capacity";
    public const string Keyring = "keyring";
    public const string Correlation = "correlation";
    public const string CauseReview = "cause_review";
}

/// <summary>A scan window: one checkpoint row. The query and its hash are fixed for a pass.</summary>
public sealed record WorkflowOutcomeWindow(string Namespace, string Kind, string Key, DateTime? Start, DateTime? End, string Query)
{
    public string QueryHash => WorkflowOutcomeHashes.Sha256Hex(Query);
}

/// <summary>A run row the reconciler still has to Describe or classify.</summary>
public sealed record WorkflowOutcomeKnownRun(
    Guid Id, string Namespace, string WorkflowId, string RunId, string WorkflowType, string Status,
    DateTime? StartTime, DateTime? CloseTime, DateTime DetectedAt);

public enum WorkflowOutcomeStoreOutcome { Inserted, Updated, Unchanged, OperatorAuthoritative, Quarantined, Collision }

public sealed record WorkflowOutcomeStoreResult(
    WorkflowOutcomeStoreOutcome Outcome, Guid? RunRowId, int Version, bool VersionBumped,
    string? Classification, string? EvidenceState, DateTime? DetectedAt, DateTime? StoredAt, bool KeyringFailed = false);

public sealed record WorkflowOutcomeNamespaceState(
    DateTime? EnrolledAt, long? RetentionSeconds, string? ArchivalState, string? PolicyHashes,
    DateTime? LastFullPassStartedAt, DateTime? LastFullPassCompletedAt);

public sealed class WorkflowOutcomeStore(
    IServiceScopeFactory scopes,
    IWorkflowOutcomePayloadProtector protector,
    IOptions<WorkflowOutcomeOptions> options,
    TimeProvider clock,
    ILogger<WorkflowOutcomeStore> logger)
{
    public const string LeaseKey = "lease";
    public const string PayloadPurpose = "run-evidence.v1";
    public const string OperatorClassificationEvent = "operator_classification";
    public const string SuccessorOutcomeEvent = "successor_outcome";
    public static readonly TimeSpan LeaseTtl = TimeSpan.FromSeconds(90);

    public static readonly IReadOnlyList<string> NonterminalStatuses = new List<string> { "Running", "Paused", "Unspecified" };

    // Lists, not arrays: with C# 14 an array's Contains binds to the span overload, which EF Core
    // cannot translate inside a query.

    /// <summary>Classifications whose metadata expires; failures, cause gaps and pending rows never do.</summary>
    private static readonly List<string> ExpiringClassifications =
        [WorkflowOutcomeClassifications.ExcludedNonfailure, WorkflowOutcomeClassifications.IntentionalNonfailure, WorkflowOutcomeClassifications.InsufficientEvidence];

    /// <summary>Classifications whose evidence is kept encrypted for review.</summary>
    private static readonly List<string> EvidenceClassifications =
        [WorkflowOutcomeClassifications.ConfirmedFailure, WorkflowOutcomeClassifications.CauseUnresolved, WorkflowOutcomeClassifications.ClassificationPending];

    private const int MaxGapRefs = 100;
    private const int MaxChainDepth = 20;

    // One writer at a time: EF contexts are not thread-safe and the reads that feed a write must
    // see the previous write. Reads to Temporal stay concurrent; only the DB step is serialized.
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    // ─── Lease ──────────────────────────────────────────────────────────────

    /// <summary>Acquires or renews the fenced lease; null when another live owner holds it or the race was lost.</summary>
    public async Task<WorkflowOutcomeLease?> AcquireLeaseAsync(string owner, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope();
            var db = Db(scope);
            var now = Now;
            var row = await db.WorkflowOutcomeCheckpoints.SingleOrDefaultAsync(c => c.WindowKey == LeaseKey, ct);
            if (row is null)
            {
                row = new WorkflowOutcomeCheckpoint
                {
                    Namespace = "",
                    WindowKind = WorkflowOutcomeWindowKinds.Lease,
                    WindowKey = LeaseKey,
                    QueryHash = "",
                    LeaseOwner = owner,
                    LeaseEpoch = 1,
                    LeaseExpiresAt = now + LeaseTtl,
                    UpdatedAt = now,
                };
                db.WorkflowOutcomeCheckpoints.Add(row);
            }
            else if (row.LeaseOwner == owner && row.LeaseExpiresAt >= now)
            {
                row.LeaseExpiresAt = now + LeaseTtl;
                row.UpdatedAt = now;
            }
            else if (row.LeaseOwner is not null && row.LeaseOwner != owner && row.LeaseExpiresAt >= now)
            {
                return null;
            }
            else
            {
                // Expired (or our own expired lease): take it with a new epoch, so anything the
                // previous holder still tries to commit fails its fence.
                row.LeaseOwner = owner;
                row.LeaseEpoch++;
                row.LeaseExpiresAt = now + LeaseTtl;
                row.UpdatedAt = now;
            }

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                return null; // concurrent insert or epoch change: someone else won
            }
            return new WorkflowOutcomeLease(owner, row.LeaseEpoch);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ─── Checkpoints ────────────────────────────────────────────────────────

    public async Task<WorkflowOutcomeCheckpoint?> GetCheckpointAsync(string windowKey, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        return await Db(scope).WorkflowOutcomeCheckpoints.AsNoTracking().SingleOrDefaultAsync(c => c.WindowKey == windowKey, ct);
    }

    public static string EnrollmentKey(string ns) => ns + "|enrollment";
    public static string FullKey(string ns) => ns + "|full";
    public static string RecentKey(string ns, DateTime dayStart) => ns + "|recent|" + dayStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Records enrollment (first time only) and the latest DescribeNamespace facts.</summary>
    public Task UpsertEnrollmentAsync(WorkflowOutcomeLease lease, string ns, WorkflowOutcomeNamespaceInfo info, string? coordinator,
        string policyFingerprint, CancellationToken ct) =>
        FencedAsync(lease, async (db, now) =>
        {
            var key = EnrollmentKey(ns);
            var cp = await db.WorkflowOutcomeCheckpoints.SingleOrDefaultAsync(c => c.WindowKey == key, ct);
            if (cp is null)
            {
                cp = new WorkflowOutcomeCheckpoint
                {
                    Namespace = ns, WindowKind = WorkflowOutcomeWindowKinds.Enrollment, WindowKey = key, QueryHash = "",
                    EnrolledAt = now,
                };
                db.WorkflowOutcomeCheckpoints.Add(cp);
            }
            cp.EnrolledAt ??= now;
            cp.EnrolledCoordinator = coordinator;
            cp.PolicyHashes = policyFingerprint;
            cp.RetentionSeconds = info.RetentionSeconds;
            cp.ArchivalState = Truncate("history:" + info.HistoryArchivalState + ";visibility:" + info.VisibilityArchivalState, 32);
            cp.LastError = null;
            cp.UpdatedAt = now;
            return true;
        }, ct);

    public async Task<WorkflowOutcomeNamespaceState> GetNamespaceStateAsync(string ns, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = Db(scope);
        var enrollmentKey = EnrollmentKey(ns);
        var fullKey = FullKey(ns);
        var rows = await db.WorkflowOutcomeCheckpoints.AsNoTracking()
            .Where(c => c.WindowKey == enrollmentKey || c.WindowKey == fullKey).ToListAsync(ct);
        var enrollment = rows.FirstOrDefault(r => r.WindowKey == enrollmentKey);
        var full = rows.FirstOrDefault(r => r.WindowKey == fullKey);
        return new WorkflowOutcomeNamespaceState(enrollment?.EnrolledAt, enrollment?.RetentionSeconds, enrollment?.ArchivalState,
            enrollment?.PolicyHashes, full?.PassStartedAt, full?.PassCompletedAt);
    }

    /// <summary>Starts a new pass of a window: no token, nothing seen, incomplete.</summary>
    public Task BeginPassAsync(WorkflowOutcomeLease lease, WorkflowOutcomeWindow window, CancellationToken ct) =>
        FencedAsync(lease, async (db, now) =>
        {
            var cp = await WindowRowAsync(db, window, ct);
            cp.PageToken = null;
            cp.SeenCount = 0;
            cp.Complete = false;
            cp.PassStartedAt = now;
            cp.LastError = null;
            cp.UpdatedAt = now;
            return true;
        }, ct);

    /// <summary>
    /// Commits one listed page: every listed run id and the next page token in ONE transaction.
    /// A null <paramref name="nextToken"/> completes the pass.
    /// </summary>
    public Task<int> CommitPageAsync(WorkflowOutcomeLease lease, WorkflowOutcomeWindow window,
        IReadOnlyList<WorkflowOutcomeListedRun> runs, byte[]? nextToken, CancellationToken ct) =>
        FencedAsync(lease, async (db, now) =>
        {
            var cp = await WindowRowAsync(db, window, ct);
            var inserted = await UpsertDiscoveredAsync(db, window.Namespace, runs, now, ct);
            cp.PageToken = nextToken is null ? null : Convert.ToBase64String(nextToken);
            cp.SeenCount += runs.Count;
            cp.Complete = nextToken is null;
            if (cp.Complete) cp.PassCompletedAt = now;
            cp.LastError = null;
            cp.UpdatedAt = now;
            return inserted;
        }, ct);

    /// <summary>Records a window error; the page token is left exactly as last committed.</summary>
    public Task RecordWindowErrorAsync(WorkflowOutcomeLease lease, WorkflowOutcomeWindow window, string error, bool restart, CancellationToken ct) =>
        FencedAsync(lease, async (db, now) =>
        {
            var cp = await WindowRowAsync(db, window, ct);
            cp.Complete = false;
            cp.LastError = Truncate(error, 200);
            // An invalid token restarts the same window from the beginning (rows dedup on identity).
            if (restart) cp.PageToken = null;
            cp.UpdatedAt = now;
            return true;
        }, ct);

    private static async Task<WorkflowOutcomeCheckpoint> WindowRowAsync(OrchestratorDbContext db, WorkflowOutcomeWindow window, CancellationToken ct)
    {
        var cp = await db.WorkflowOutcomeCheckpoints.SingleOrDefaultAsync(c => c.WindowKey == window.Key, ct);
        if (cp is null)
        {
            cp = new WorkflowOutcomeCheckpoint
            {
                Namespace = window.Namespace, WindowKind = window.Kind, WindowKey = window.Key, QueryHash = window.QueryHash,
            };
            db.WorkflowOutcomeCheckpoints.Add(cp);
        }
        cp.WindowStart = window.Start;
        cp.WindowEnd = window.End;
        cp.QueryHash = window.QueryHash;
        return cp;
    }

    // ─── Discovery ──────────────────────────────────────────────────────────

    private async Task<int> UpsertDiscoveredAsync(OrchestratorDbContext db, string ns, IReadOnlyList<WorkflowOutcomeListedRun> runs,
        DateTime now, CancellationToken ct)
    {
        var byHash = new Dictionary<string, WorkflowOutcomeListedRun>(StringComparer.Ordinal);
        foreach (var run in runs)
            byHash.TryAdd(WorkflowOutcomeHashes.IdentityHash(ns, run.WorkflowId, run.RunId), run);
        var hashes = byHash.Keys.ToList();
        var existing = await db.WorkflowOutcomeRuns.Where(r => hashes.Contains(r.IdentityHash)).ToDictionaryAsync(r => r.IdentityHash, ct);

        var inserted = 0;
        foreach (var (hash, run) in byHash)
        {
            if (existing.TryGetValue(hash, out var row))
            {
                if (!SameIdentity(row, ns, run.WorkflowId, run.RunId))
                {
                    await CollisionAsync(db, ns, run.WorkflowId, run.RunId, now, ct);
                    continue;
                }
                // Visibility lags: it may move a nonterminal row to a closed status, never back.
                if (row.TerminalEventId is null && !row.Quarantined && IsNonterminal(row.Status) && !IsNonterminal(run.Status))
                {
                    row.Status = run.Status;
                    row.CloseTime ??= run.CloseTime;
                    row.DetectedAt = now;
                    row.UpdatedAt = now;
                }
                row.StartTime ??= run.StartTime;
                continue;
            }

            db.WorkflowOutcomeRuns.Add(DiscoveryRow(ns, run, hash, now));
            inserted++;
        }
        return inserted;
    }

    private static WorkflowOutcomeRun DiscoveryRow(string ns, WorkflowOutcomeListedRun run, string hash, DateTime now)
    {
        var row = new WorkflowOutcomeRun
        {
            Id = Guid.NewGuid(),
            Namespace = ns,
            WorkflowId = run.WorkflowId,
            RunId = run.RunId,
            IdentityHash = hash,
            ParentWorkflowId = run.ParentWorkflowId,
            ParentRunId = run.ParentRunId,
            WorkflowType = Truncate(run.WorkflowType, 255),
            StartTime = run.StartTime,
            CloseTime = run.CloseTime,
            Status = run.Status,
            // Discovery bookkeeping only: pending with no reason code until the exact history is read.
            Classification = WorkflowOutcomeClassifications.ClassificationPending,
            EvidenceState = WorkflowOutcomeEvidenceStates.Pending,
            Version = 1,
            DetectedAt = now,
            StoredAt = now,
            UpdatedAt = now,
        };
        if (string.IsNullOrEmpty(run.RunId))
        {
            // No exact identity: never merged with anything, never classified.
            row.ReasonCode = WorkflowOutcomeReasonCodes.CorrelationUnverified;
            row.EvidenceState = WorkflowOutcomeEvidenceStates.Unverified;
            row.Quarantined = true;
            row.QuarantineReason = "empty_run_id";
            row.ClassificationPendingSince = now;
        }
        return row;
    }

    /// <summary>Marks a known nonterminal run closed (Describe answered with a closed status).</summary>
    public Task MarkClosedAsync(WorkflowOutcomeLease lease, Guid runRowId, string status, DateTime? closeTime, CancellationToken ct) =>
        FencedAsync(lease, async (db, now) =>
        {
            var row = await db.WorkflowOutcomeRuns.SingleOrDefaultAsync(r => r.Id == runRowId, ct);
            if (row is null || row.TerminalEventId is not null || !IsNonterminal(row.Status)) return false;
            row.Status = status;
            row.CloseTime ??= closeTime;
            row.DetectedAt = now;
            row.UpdatedAt = now;
            return true;
        }, ct);

    /// <summary>Ensures a successor/parent run is tracked so it is Described before any logical-failure claim.</summary>
    public Task EnsureTrackedAsync(WorkflowOutcomeLease lease, string ns, string workflowId, string runId, string workflowType,
        string? previousRunId, CancellationToken ct) =>
        FencedAsync(lease, async (db, now) =>
        {
            var hash = WorkflowOutcomeHashes.IdentityHash(ns, workflowId, runId);
            if (await db.WorkflowOutcomeRuns.AnyAsync(r => r.IdentityHash == hash, ct)) return false;
            var row = DiscoveryRow(ns, new WorkflowOutcomeListedRun(workflowId, runId, workflowType, "Running", null, null, null, null), hash, now);
            row.PreviousRunId = previousRunId;
            db.WorkflowOutcomeRuns.Add(row);
            return true;
        }, ct);

    /// <summary>One keyset page (by row id) of known nonterminal runs; page with the last id until empty.</summary>
    public async Task<IReadOnlyList<WorkflowOutcomeKnownRun>> ListNonterminalAsync(string ns, Guid? afterId, int max, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var query = Db(scope).WorkflowOutcomeRuns.AsNoTracking()
            .Where(r => r.Namespace == ns && !r.Quarantined && r.TerminalEventId == null && NonterminalStatuses.Contains(r.Status));
        if (afterId is { } after) query = query.Where(r => r.Id.CompareTo(after) > 0);
        return await query.OrderBy(r => r.Id).Take(max)
            .Select(r => new WorkflowOutcomeKnownRun(r.Id, r.Namespace, r.WorkflowId, r.RunId, r.WorkflowType, r.Status, r.StartTime, r.CloseTime, r.DetectedAt))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Closed runs whose exact history must be read: never classified yet, classified under a
    /// different classifier policy while still pending, or stored while the keyring was failing.
    /// </summary>
    public async Task<IReadOnlyList<WorkflowOutcomeKnownRun>> ListAwaitingHistoryAsync(string ns, string classifierHash, int max, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        return await Db(scope).WorkflowOutcomeRuns.AsNoTracking()
            .Where(r => r.Namespace == ns && !r.Quarantined && !NonterminalStatuses.Contains(r.Status)
                && ((r.TerminalEventId == null && r.ReasonCode == null)
                    || (r.Classification == WorkflowOutcomeClassifications.ClassificationPending
                        && (r.ReasonCode == WorkflowOutcomeReasonCodes.ClassifierMissing || r.ReasonCode == WorkflowOutcomeReasonCodes.ClassifierUncoveredType)
                        && (r.ClassifierHash == null || r.ClassifierHash != classifierHash))
                    || (r.TerminalEventId != null && r.EvidenceState == WorkflowOutcomeEvidenceStates.Pending && r.PayloadCiphertext == null
                        && EvidenceClassifications.Contains(r.Classification))))
            .OrderBy(r => r.DetectedAt).Take(max)
            .Select(r => new WorkflowOutcomeKnownRun(r.Id, r.Namespace, r.WorkflowId, r.RunId, r.WorkflowType, r.Status, r.StartTime, r.CloseTime, r.DetectedAt))
            .ToListAsync(ct);
    }

    /// <summary>The verified ownership of a parent row derived under <paramref name="ownershipHash"/>, or null.</summary>
    public async Task<WorkflowOutcomeParentOwnership?> GetOwnershipAsync(string ns, string workflowId, string runId, string ownershipHash, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var hash = WorkflowOutcomeHashes.IdentityHash(ns, workflowId, runId);
        var row = await Db(scope).WorkflowOutcomeRuns.AsNoTracking().SingleOrDefaultAsync(r => r.IdentityHash == hash, ct);
        if (row is null || !SameIdentity(row, ns, workflowId, runId) || row.Quarantined || row.OwnershipPolicyHash != ownershipHash || row.Coordinator is null)
            return null;
        return new WorkflowOutcomeParentOwnership(ns, workflowId, runId, row.ProjectName, row.Coordinator, Verified: true);
    }

    /// <summary>Stores ownership derived from a still-running run's Started event (so its children can inherit it).</summary>
    public Task StoreOwnershipAsync(WorkflowOutcomeLease lease, WorkflowOutcomeRunFacts facts, WorkflowOutcomeOwnershipResult ownership, CancellationToken ct) =>
        FencedAsync(lease, async (db, now) =>
        {
            var hash = WorkflowOutcomeHashes.IdentityHash(facts.Namespace, facts.WorkflowId, facts.RunId);
            var row = await db.WorkflowOutcomeRuns.SingleOrDefaultAsync(r => r.IdentityHash == hash, ct);
            if (row is null)
            {
                row = DiscoveryRow(facts.Namespace, new WorkflowOutcomeListedRun(facts.WorkflowId, facts.RunId, facts.WorkflowType, "Running",
                    facts.StartTime, null, facts.ParentWorkflowId, facts.ParentRunId), hash, now);
                db.WorkflowOutcomeRuns.Add(row);
            }
            else if (!SameIdentity(row, facts.Namespace, facts.WorkflowId, facts.RunId))
            {
                await CollisionAsync(db, facts.Namespace, facts.WorkflowId, facts.RunId, now, ct);
                return false;
            }
            ApplyOwnership(row, facts, ownership);
            row.UpdatedAt = now;
            return true;
        }, ct);

    // ─── Classification ─────────────────────────────────────────────────────

    /// <summary>
    /// Stores the classification of one exact closed run with append-only observations, monotonic
    /// version (+1 only on a material failure change of an already-classified run), encrypted
    /// evidence (fail closed), nonfailure TTL and retry-chain resolution — all in one transaction.
    /// An operator classification for the run is authoritative and never overwritten.
    /// </summary>
    public Task<WorkflowOutcomeStoreResult> StoreOutcomeAsync(WorkflowOutcomeLease lease, WorkflowOutcomeRunFacts facts,
        WorkflowOutcomeVerdict verdict, WorkflowOutcomeOwnershipResult ownership, long? retentionSeconds, CancellationToken ct) =>
        FencedAsync(lease, async (db, now) =>
        {
            var terminal = facts.Terminal ?? throw new ArgumentException("facts have no terminal event", nameof(facts));
            var hash = WorkflowOutcomeHashes.IdentityHash(facts.Namespace, facts.WorkflowId, facts.RunId);
            var row = await db.WorkflowOutcomeRuns.SingleOrDefaultAsync(r => r.IdentityHash == hash, ct);
            if (row is not null && !SameIdentity(row, facts.Namespace, facts.WorkflowId, facts.RunId))
            {
                await CollisionAsync(db, facts.Namespace, facts.WorkflowId, facts.RunId, now, ct);
                return new WorkflowOutcomeStoreResult(WorkflowOutcomeStoreOutcome.Collision, null, 0, false, null, null, null, null);
            }
            if (string.IsNullOrEmpty(facts.RunId))
                throw new ArgumentException("an empty RunId is never classified", nameof(facts));

            var inserted = row is null;
            if (row is null)
            {
                row = DiscoveryRow(facts.Namespace, new WorkflowOutcomeListedRun(facts.WorkflowId, facts.RunId, facts.WorkflowType,
                    terminal.Status, facts.StartTime, terminal.EventTime, facts.ParentWorkflowId, facts.ParentRunId), hash, now);
                db.WorkflowOutcomeRuns.Add(row);
            }

            var observations = inserted
                ? []
                : await db.WorkflowOutcomeObservations.Where(o => o.RunRowId == row.Id).ToListAsync(ct);

            // The terminal event of an exact run is immutable: another id/type is a contradiction.
            if (row.TerminalEventId is { } storedId && (storedId != terminal.EventId || row.TerminalEventType != terminal.EventType))
            {
                if (!observations.Any(o => o.EventId == terminal.EventId && o.EventType == terminal.EventType && o.EventHash == terminal.EventSha256))
                    db.WorkflowOutcomeObservations.Add(Observation(row.Id, terminal, verdict, contradicting: true, now));
                row.Quarantined = true;
                row.QuarantineReason = "contradicting_terminal";
                row.EvidenceState = WorkflowOutcomeEvidenceStates.Quarantined;
                row.UpdatedAt = now;
                await RaiseInTxAsync(db, WorkflowOutcomeHealthComponents.Correlation, "contradicting_terminal:" + facts.Namespace,
                    "contradicting terminal observations quarantined", facts.Namespace, null, null, 1, false, [OutcomeRef(row.Id)], now, ct);
                return new WorkflowOutcomeStoreResult(WorkflowOutcomeStoreOutcome.Quarantined, row.Id, row.Version, false,
                    row.Classification, row.EvidenceState, row.DetectedAt, row.StoredAt);
            }

            if (!observations.Any(o => o.EventId == terminal.EventId && o.EventType == terminal.EventType && o.EventHash == terminal.EventSha256))
                db.WorkflowOutcomeObservations.Add(Observation(row.Id, terminal, verdict, contradicting: false, now));
            else if (row.TerminalEventHash is not null && row.TerminalEventHash != terminal.EventSha256)
                logger.LogWarning("workflow_outcome_terminal_hash_changed outcome={OutcomeId} event={EventId}", row.Id, terminal.EventId);

            // Facts that are not classification: identity links, definition snapshot, correlation.
            ApplyRunFacts(row, facts, terminal);
            ApplyOwnership(row, facts, ownership);

            if (observations.Any(o => o.EventType == OperatorClassificationEvent))
            {
                row.UpdatedAt = now;
                return new WorkflowOutcomeStoreResult(WorkflowOutcomeStoreOutcome.OperatorAuthoritative, row.Id, row.Version, false,
                    row.Classification, row.EvidenceState, row.DetectedAt, row.StoredAt);
            }

            var hadTerminal = row.TerminalEventId is not null;
            var before = Material(row);
            var previousClassification = row.Classification;
            row.TerminalEventId = terminal.EventId;
            row.TerminalEventType = terminal.EventType;
            row.TerminalEventHash ??= terminal.EventSha256;
            row.Classification = verdict.Classification;
            row.ReasonCode = verdict.ReasonCode;
            row.CauseRefsJson = JsonSerializer.Serialize(verdict.CauseRefs);
            row.ClassifierId = verdict.ClassifierId;
            row.ClassifierHash = verdict.ClassifierHash;
            row.TaskOwner = verdict.TaskOwner;
            row.SourceTaskRefsJson = verdict.SourceTaskRefs.Count == 0 ? null : JsonSerializer.Serialize(verdict.SourceTaskRefs);
            var changed = !Equals(before, Material(row));
            var bumped = hadTerminal && changed;
            if (bumped) row.Version++;

            row.EvidenceState = verdict.EvidenceState;
            if (changed || !hadTerminal) row.StoredAt = now;
            if (verdict.Classification is WorkflowOutcomeClassifications.CauseUnresolved or WorkflowOutcomeClassifications.ClassificationPending)
                row.ClassificationPendingSince = previousClassification == verdict.Classification && row.ClassificationPendingSince is not null
                    ? row.ClassificationPendingSince : now;
            else
                row.ClassificationPendingSince = null;

            if (verdict.Classification == WorkflowOutcomeClassifications.ConfirmedFailure && row.FailureIncidentId is null)
                row.FailureIncidentId = await ChainIncidentAsync(db, row, ct) ?? Guid.NewGuid();

            var keyringFailed = !ProtectEvidence(row, facts, verdict);
            row.MetadataExpiresAt = ExpiresAt(row, observations, retentionSeconds);
            row.UpdatedAt = now;

            if (verdict.GapReason == WorkflowOutcomeClassifier.UncoveredTypeGap)
                // Durable per type: it stays open after these TTL'd runs expire, until rules cover the type.
                await RaiseInTxAsync(db, WorkflowOutcomeHealthComponents.Coverage, UncoveredTypeKey(facts.Namespace, facts.WorkflowType),
                    "no classifier rules for workflow type " + Truncate(facts.WorkflowType, 300) + ": its business failures are not detected",
                    facts.Namespace, facts.StartTime, terminal.EventTime, 1, false, [OutcomeRef(row.Id)], now, ct);
            else if (verdict.GapReason is not null)
                await RaiseInTxAsync(db, WorkflowOutcomeHealthComponents.Coverage, verdict.GapReason + ":" + facts.Namespace,
                    "classification pending: " + verdict.GapReason, facts.Namespace, facts.StartTime, terminal.EventTime, 1, false, [OutcomeRef(row.Id)], now, ct);

            await ResolveChainAsync(db, row, verdict, now, ct);

            return new WorkflowOutcomeStoreResult(inserted ? WorkflowOutcomeStoreOutcome.Inserted : changed ? WorkflowOutcomeStoreOutcome.Updated : WorkflowOutcomeStoreOutcome.Unchanged,
                row.Id, row.Version, bumped, row.Classification, row.EvidenceState, row.DetectedAt, row.StoredAt, keyringFailed);
        }, ct);

    /// <summary>Exact history is gone (retention/NotFound/archived): durable gap with the known id, never a failure claim.</summary>
    public Task MarkHistoryMissingAsync(WorkflowOutcomeLease lease, Guid runRowId, string reason, CancellationToken ct) =>
        FencedAsync(lease, async (db, now) =>
        {
            var row = await db.WorkflowOutcomeRuns.SingleOrDefaultAsync(r => r.Id == runRowId, ct);
            if (row is null || row.TerminalEventId is not null) return false;
            row.Classification = WorkflowOutcomeClassifications.ClassificationPending;
            row.ReasonCode = WorkflowOutcomeReasonCodes.HistoryIncomplete;
            row.EvidenceState = WorkflowOutcomeEvidenceStates.Missing;
            row.ClassificationPendingSince ??= now;
            row.MetadataExpiresAt = null;
            row.UpdatedAt = now;
            await RaiseInTxAsync(db, WorkflowOutcomeHealthComponents.Coverage, "history_missing:" + row.Namespace,
                "history missing: " + reason, row.Namespace, row.StartTime ?? row.CloseTime, row.CloseTime ?? row.StartTime, 1, false,
                [OutcomeRef(row.Id)], now, ct);
            return true;
        }, ct);

    /// <summary>History that cannot be interpreted (event-id gap, raw blobs): visible, unverified, not retried.</summary>
    public Task MarkHistoryUnverifiedAsync(WorkflowOutcomeLease lease, Guid runRowId, string reason, CancellationToken ct) =>
        FencedAsync(lease, async (db, now) =>
        {
            var row = await db.WorkflowOutcomeRuns.SingleOrDefaultAsync(r => r.Id == runRowId, ct);
            if (row is null || row.TerminalEventId is not null) return false;
            row.Classification = WorkflowOutcomeClassifications.ClassificationPending;
            row.ReasonCode = WorkflowOutcomeReasonCodes.HistoryIncomplete;
            row.EvidenceState = WorkflowOutcomeEvidenceStates.Unverified;
            row.ClassificationPendingSince ??= now;
            row.UpdatedAt = now;
            await RaiseInTxAsync(db, WorkflowOutcomeHealthComponents.History, "history_unverified:" + row.Namespace,
                "history unreadable: " + reason, row.Namespace, null, null, 1, false, [OutcomeRef(row.Id)], now, ct);
            return true;
        }, ct);

    // ─── Health ─────────────────────────────────────────────────────────────

    /// <summary>Opens (or refreshes) one deduplicated incident. Gap counts and refs accumulate.</summary>
    public Task RaiseAsync(WorkflowOutcomeLease lease, string component, string outageKey, string reason, string? ns,
        DateTime? gapStart = null, DateTime? gapEnd = null, long knownRuns = 0, bool unknownRuns = false,
        IReadOnlyList<string>? refs = null, CancellationToken ct = default) =>
        FencedAsync(lease, async (db, now) =>
        {
            await RaiseInTxAsync(db, component, outageKey, reason, ns, gapStart, gapEnd, knownRuns, unknownRuns, refs ?? [], now, ct);
            return true;
        }, ct);

    /// <summary>Resolves an open incident when its condition has cleared. Coverage gaps are facts and are never auto-resolved.</summary>
    public Task ResolveAsync(WorkflowOutcomeLease lease, string component, string outageKey, CancellationToken ct) =>
        FencedAsync(lease, async (db, now) =>
        {
            var row = await db.WorkflowOutcomeHealth.SingleOrDefaultAsync(h => h.Component == component && h.OutageKey == outageKey, ct);
            if (row is null || row.State != "open") return false;
            row.State = "resolved";
            row.ResolvedAt = now;
            row.LastSeenAt = now;
            return true;
        }, ct);

    /// <summary>Sets an aggregate incident to exactly the given counts/refs (cause-review alerts); resolves it when zero.</summary>
    public Task SetAggregateAsync(WorkflowOutcomeLease lease, string component, string outageKey, string reason, string ns,
        long count, IReadOnlyList<string> refs, CancellationToken ct) =>
        FencedAsync(lease, async (db, now) =>
        {
            var row = await db.WorkflowOutcomeHealth.SingleOrDefaultAsync(h => h.Component == component && h.OutageKey == outageKey, ct);
            if (count == 0)
            {
                if (row is { State: "open" }) { row.State = "resolved"; row.ResolvedAt = now; row.LastSeenAt = now; }
                return true;
            }
            if (row is null)
            {
                row = new WorkflowOutcomeHealth { Component = component, OutageKey = outageKey, State = "open", Reason = reason, FirstSeenAt = now };
                db.WorkflowOutcomeHealth.Add(row);
            }
            else if (row.State != "open") { row.State = "open"; row.FirstSeenAt = now; row.ResolvedAt = null; }
            row.Reason = Truncate(reason, 500);
            row.Namespace = ns;
            row.KnownRunCount = count;
            row.CauseReviewRefsJson = JsonSerializer.Serialize(refs.Take(MaxGapRefs));
            row.LastSeenAt = now;
            return true;
        }, ct);

    private static async Task RaiseInTxAsync(OrchestratorDbContext db, string component, string outageKey, string reason, string? ns,
        DateTime? gapStart, DateTime? gapEnd, long knownRuns, bool unknownRuns, IReadOnlyList<string> refs, DateTime now, CancellationToken ct)
    {
        outageKey = Truncate(outageKey, 200);
        var row = db.WorkflowOutcomeHealth.Local.FirstOrDefault(h => h.Component == component && h.OutageKey == outageKey)
            ?? await db.WorkflowOutcomeHealth.SingleOrDefaultAsync(h => h.Component == component && h.OutageKey == outageKey, ct);
        if (row is null)
        {
            row = new WorkflowOutcomeHealth
            {
                Component = component, OutageKey = outageKey, State = "open", Reason = Truncate(reason, 500), FirstSeenAt = now,
            };
            db.WorkflowOutcomeHealth.Add(row);
        }
        else if (row.State != "open")
        {
            row.State = "open";
            row.FirstSeenAt = now;
            row.ResolvedAt = null;
        }
        row.Reason = Truncate(reason, 500);
        row.Namespace = ns;
        row.LastSeenAt = now;
        if (gapStart is not null && (row.GapStart is null || gapStart < row.GapStart)) row.GapStart = gapStart;
        if (gapEnd is not null && (row.GapEnd is null || gapEnd > row.GapEnd)) row.GapEnd = gapEnd;
        row.UnknownRunCount |= unknownRuns;
        if (refs.Count > 0 || knownRuns > 0)
        {
            var existing = row.CauseReviewRefsJson is null ? [] : JsonSerializer.Deserialize<List<string>>(row.CauseReviewRefsJson) ?? [];
            var added = refs.Where(r => !existing.Contains(r, StringComparer.Ordinal)).ToArray();
            // Known-id counts grow only with new ids, so a repeated observation never inflates them.
            row.KnownRunCount += refs.Count > 0 ? added.Length : knownRuns;
            existing.AddRange(added);
            row.CauseReviewRefsJson = JsonSerializer.Serialize(existing.Take(MaxGapRefs));
        }
    }

    // ─── Queries for alerts and cleanup ─────────────────────────────────────

    /// <summary>Closed runs currently pending because no pinned classifier covers their loaded definition.</summary>
    public async Task<int> ClassifierGapCountAsync(string ns, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        return await Db(scope).WorkflowOutcomeRuns.AsNoTracking().CountAsync(r => r.Namespace == ns
            && r.Classification == WorkflowOutcomeClassifications.ClassificationPending && r.ReasonCode == WorkflowOutcomeReasonCodes.ClassifierMissing, ct);
    }

    /// <summary>Workflow types of <paramref name="ns"/> with a run still pending on a classifier gap or an uncovered type.</summary>
    public async Task<IReadOnlyList<string>> PendingClassificationTypesAsync(string ns, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        return await Db(scope).WorkflowOutcomeRuns.AsNoTracking()
            .Where(r => r.Namespace == ns && r.Classification == WorkflowOutcomeClassifications.ClassificationPending
                && (r.ReasonCode == WorkflowOutcomeReasonCodes.ClassifierMissing || r.ReasonCode == WorkflowOutcomeReasonCodes.ClassifierUncoveredType))
            .Select(r => r.WorkflowType).Distinct().ToListAsync(ct);
    }

    /// <summary>Unresolved causes / pending classifications older than <paramref name="olderThan"/>.</summary>
    public async Task<(int Count, IReadOnlyList<string> Refs)> StalePendingAsync(string ns, DateTime olderThan, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var query = Db(scope).WorkflowOutcomeRuns.AsNoTracking()
            .Where(r => r.Namespace == ns && r.ClassificationPendingSince != null && r.ClassificationPendingSince < olderThan
                && (r.Classification == WorkflowOutcomeClassifications.CauseUnresolved
                    || (r.Classification == WorkflowOutcomeClassifications.ClassificationPending && r.ReasonCode != WorkflowOutcomeReasonCodes.ClassifierUncoveredType)));
        var count = await query.CountAsync(ct);
        var ids = await query.OrderBy(r => r.ClassificationPendingSince).Take(20).Select(r => r.Id).ToListAsync(ct);
        return (count, ids.Select(OutcomeRef).ToArray());
    }

    /// <summary>
    /// Deletes at most <paramref name="max"/> expired nonfailure metadata rows (and their
    /// observations). Rows that ever were a failure version, have batch membership or an operator
    /// classification are kept.
    /// </summary>
    public Task<int> CleanupExpiredAsync(WorkflowOutcomeLease lease, int max, CancellationToken ct) =>
        FencedAsync(lease, async (db, now) =>
        {
            var candidates = await db.WorkflowOutcomeRuns
                .Where(r => r.MetadataExpiresAt != null && r.MetadataExpiresAt < now && r.Version == 1 && !r.Quarantined
                    && (ExpiringClassifications.Contains(r.Classification)
                        || (r.Classification == WorkflowOutcomeClassifications.ClassificationPending && r.ReasonCode == WorkflowOutcomeReasonCodes.ClassifierUncoveredType)))
                .OrderBy(r => r.MetadataExpiresAt).Take(max).ToListAsync(ct);
            if (candidates.Count == 0) return 0;
            var ids = candidates.Select(c => c.Id).ToList();
            var batched = await db.WorkflowOutcomeBatchItems.Where(i => ids.Contains(i.OutcomeId)).Select(i => i.OutcomeId).ToListAsync(ct);
            var attested = await db.WorkflowOutcomeObservations.Where(o => ids.Contains(o.RunRowId) && o.EventType == OperatorClassificationEvent)
                .Select(o => o.RunRowId).ToListAsync(ct);
            var deletable = candidates.Where(c => !batched.Contains(c.Id) && !attested.Contains(c.Id)).ToList();
            var deletableIds = deletable.Select(d => d.Id).ToList();
            db.WorkflowOutcomeObservations.RemoveRange(await db.WorkflowOutcomeObservations.Where(o => deletableIds.Contains(o.RunRowId)).ToListAsync(ct));
            db.WorkflowOutcomeRuns.RemoveRange(deletable);
            return deletable.Count;
        }, ct);

    // ─── Internals ──────────────────────────────────────────────────────────

    private async Task<T> FencedAsync<T>(WorkflowOutcomeLease lease, Func<OrchestratorDbContext, DateTime, Task<T>> work, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope();
            var db = Db(scope);
            var strategy = db.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                var now = Now;
                var fence = await db.WorkflowOutcomeCheckpoints.SingleOrDefaultAsync(c => c.WindowKey == LeaseKey, ct);
                if (fence is null || fence.LeaseOwner != lease.Owner || fence.LeaseEpoch != lease.Epoch || fence.LeaseExpiresAt < now)
                    throw new WorkflowOutcomeLeaseLostException("observer lease not held");
                // Touching the lease row makes the commit conditional on the epoch (concurrency token).
                fence.LeaseExpiresAt = now + LeaseTtl;
                fence.UpdatedAt = now;
                var result = await work(db, now);
                try
                {
                    await db.SaveChangesAsync(ct);
                }
                catch (DbUpdateConcurrencyException)
                {
                    throw new WorkflowOutcomeLeaseLostException("observer lease epoch changed");
                }
                await tx.CommitAsync(ct);
                return result;
            });
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static OrchestratorDbContext Db(IServiceScope scope) =>
        scope.ServiceProvider.GetService<OrchestratorDbContext>() ?? throw new InvalidOperationException("workflow outcome database unavailable");

    private static bool SameIdentity(WorkflowOutcomeRun row, string ns, string workflowId, string runId) =>
        string.Equals(row.Namespace, ns, StringComparison.Ordinal)
        && string.Equals(row.WorkflowId, workflowId, StringComparison.Ordinal)
        && string.Equals(row.RunId, runId, StringComparison.Ordinal);

    internal static bool IsNonterminal(string status) => NonterminalStatuses.Contains(status, StringComparer.Ordinal);

    private Task CollisionAsync(OrchestratorDbContext db, string ns, string workflowId, string runId, DateTime now, CancellationToken ct)
    {
        // Same identity hash, different originals: never merged. Only the incident records it.
        logger.LogError("workflow_outcome_identity_collision namespace={Namespace}", ns);
        var refHash = WorkflowOutcomeHashes.Sha256Hex(ns + "\u001f" + workflowId + "\u001f" + runId);
        return RaiseInTxAsync(db, WorkflowOutcomeHealthComponents.Correlation, "identity_collision:" + ns,
            "identity hash collision: correlation_unverified, quarantined", ns, null, null, 1, false, ["run-hash:" + refHash], now, ct);
    }

    private static (string, string?, long?, string?, string?) Material(WorkflowOutcomeRun row) =>
        (row.Classification, row.ReasonCode, row.TerminalEventId, row.TerminalEventType, row.CauseRefsJson);

    private static WorkflowOutcomeObservation Observation(Guid runRowId, WorkflowOutcomeTerminal terminal, WorkflowOutcomeVerdict verdict, bool contradicting, DateTime now) => new()
    {
        RunRowId = runRowId,
        EventId = terminal.EventId,
        EventType = terminal.EventType,
        EventHash = terminal.EventSha256,
        Status = terminal.Status,
        Classification = verdict.Classification,
        ReasonCode = verdict.ReasonCode,
        Contradicting = contradicting,
        ObservedAt = now,
    };

    private static void ApplyRunFacts(WorkflowOutcomeRun row, WorkflowOutcomeRunFacts facts, WorkflowOutcomeTerminal terminal)
    {
        row.Status = terminal.Status;
        row.WorkflowType = Truncate(facts.WorkflowType.Length > 0 ? facts.WorkflowType : row.WorkflowType, 255);
        row.StartTime = facts.StartTime ?? row.StartTime;
        row.CloseTime = terminal.EventTime ?? row.CloseTime;
        row.FirstExecutionRunId = facts.FirstExecutionRunId;
        row.PreviousRunId = facts.ContinuedExecutionRunId;
        row.NextRunId = terminal.NewExecutionRunId;
        row.ParentWorkflowId = facts.ParentWorkflowId;
        row.ParentRunId = facts.ParentRunId;
        row.DefinitionName = facts.DefinitionName is null ? null : Truncate(facts.DefinitionName, 255);
        row.DefinitionVersion = facts.DefinitionVersion;
        row.DefinitionHash = facts.DefinitionHash;
        row.InputHash = facts.Input?.Sha256;
        ApplyCorrelation(row, facts);
    }

    private static void ApplyOwnership(WorkflowOutcomeRun row, WorkflowOutcomeRunFacts facts, WorkflowOutcomeOwnershipResult ownership)
    {
        row.ProjectName = ownership.ProjectName;
        row.ProjectEvidenceJson = ownership.EvidenceJson();
        row.OwnershipPolicyHash = ownership.PolicyHash;
        row.Coordinator = ownership.Coordinator;
        row.Repo = ownership.Repo;
        row.ParentWorkflowId ??= facts.ParentWorkflowId;
        row.ParentRunId ??= facts.ParentRunId;
        row.FirstExecutionRunId ??= facts.FirstExecutionRunId;
        row.InputHash ??= facts.Input?.Sha256;
        ApplyCorrelation(row, facts);
    }

    private static void ApplyCorrelation(WorkflowOutcomeRun row, WorkflowOutcomeRunFacts facts)
    {
        if (facts.Input is not { Supported: true, Json: { } json }) return;
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
        row.IssueNumber = EpicJson.PositiveInteger(doc.RootElement, "IssueNumber");
        row.PrNumber = EpicJson.PositiveInteger(doc.RootElement, "PrNumber");
    }

    /// <summary>The incident of the failed predecessor this run retries/continues, if any.</summary>
    private static async Task<Guid?> ChainIncidentAsync(OrchestratorDbContext db, WorkflowOutcomeRun row, CancellationToken ct)
    {
        if (row.PreviousRunId is null) return null;
        var hash = WorkflowOutcomeHashes.IdentityHash(row.Namespace, row.WorkflowId, row.PreviousRunId);
        var previous = await db.WorkflowOutcomeRuns.SingleOrDefaultAsync(r => r.IdentityHash == hash, ct);
        return previous is { Classification: WorkflowOutcomeClassifications.ConfirmedFailure } && SameIdentity(previous, row.Namespace, row.WorkflowId, row.PreviousRunId)
            ? previous.FailureIncidentId
            : null;
    }

    /// <summary>
    /// Resolves retry chains in both directions: this run's successor_pending predecessors (walking
    /// back over continue-as-new links) and, when this run is itself successor_pending, an
    /// already-classified successor observed earlier (out-of-order closure).
    /// </summary>
    private async Task ResolveChainAsync(OrchestratorDbContext db, WorkflowOutcomeRun row, WorkflowOutcomeVerdict verdict, DateTime now, CancellationToken ct)
    {
        // Backward: this run is the successor.
        var current = row;
        for (var depth = 0; depth < MaxChainDepth && current.PreviousRunId is { } previousRunId; depth++)
        {
            var hash = WorkflowOutcomeHashes.IdentityHash(row.Namespace, row.WorkflowId, previousRunId);
            var previous = db.WorkflowOutcomeRuns.Local.FirstOrDefault(r => r.IdentityHash == hash)
                ?? await db.WorkflowOutcomeRuns.SingleOrDefaultAsync(r => r.IdentityHash == hash, ct);
            if (previous is null || !SameIdentity(previous, row.Namespace, row.WorkflowId, previousRunId)) break;
            if (previous.EvidenceState == WorkflowOutcomeEvidenceStates.SuccessorPending)
                await ApplyPredecessorAsync(db, previous, row, verdict, now, ct);
            else if (!(previous.Classification == WorkflowOutcomeClassifications.ExcludedNonfailure
                       && previous.ReasonCode == WorkflowOutcomeReasonCodes.ContinuedAsNew))
                break;
            current = previous;
        }

        // Forward: this run waits on a successor that may already be classified.
        if (row.EvidenceState != WorkflowOutcomeEvidenceStates.SuccessorPending) return;
        var next = row;
        for (var depth = 0; depth < MaxChainDepth && next.NextRunId is { } nextRunId; depth++)
        {
            var hash = WorkflowOutcomeHashes.IdentityHash(row.Namespace, row.WorkflowId, nextRunId);
            var successor = await db.WorkflowOutcomeRuns.SingleOrDefaultAsync(r => r.IdentityHash == hash, ct);
            if (successor is null || successor.TerminalEventId is null || !SameIdentity(successor, row.Namespace, row.WorkflowId, nextRunId)) return;
            if (successor.Classification == WorkflowOutcomeClassifications.ExcludedNonfailure && successor.ReasonCode == WorkflowOutcomeReasonCodes.ContinuedAsNew)
            {
                next = successor;
                continue;
            }
            var successorVerdict = new WorkflowOutcomeVerdict(successor.Classification, successor.ReasonCode ?? "", successor.EvidenceState,
                successor.ClassifierId, [], [], successor.TaskOwner, null);
            await ApplyPredecessorAsync(db, row, successor, successorVerdict, now, ct);
            return;
        }
    }

    private async Task ApplyPredecessorAsync(OrchestratorDbContext db, WorkflowOutcomeRun predecessor, WorkflowOutcomeRun successor,
        WorkflowOutcomeVerdict successorVerdict, DateTime now, CancellationToken ct)
    {
        var change = WorkflowOutcomeClassifier.ResolvePredecessor(predecessor.Classification, predecessor.ReasonCode ?? "", successorVerdict);
        if (change is null) return;
        if (await db.WorkflowOutcomeObservations.AnyAsync(o => o.RunRowId == predecessor.Id && o.EventType == OperatorClassificationEvent, ct)) return;

        var eventHash = WorkflowOutcomeHashes.Sha256Hex(successor.IdentityHash + ":" + (successor.TerminalEventHash ?? ""));
        var successorEvent = successor.TerminalEventId ?? 0;
        var observed = db.WorkflowOutcomeObservations.Local.Any(o => o.RunRowId == predecessor.Id && o.EventType == SuccessorOutcomeEvent && o.EventHash == eventHash)
            || await db.WorkflowOutcomeObservations.AnyAsync(o => o.RunRowId == predecessor.Id && o.EventId == successorEvent
                && o.EventType == SuccessorOutcomeEvent && o.EventHash == eventHash, ct);

        var before = Material(predecessor);
        predecessor.Classification = change.Classification;
        predecessor.ReasonCode = change.ReasonCode;
        if (change.Classification != WorkflowOutcomeClassifications.ConfirmedFailure || change.ReasonCode != before.Item2)
        {
            var refs = predecessor.CauseRefsJson is null ? [] : JsonSerializer.Deserialize<List<string>>(predecessor.CauseRefsJson) ?? [];
            var successorRef = OutcomeRef(successor.Id);
            if (!refs.Contains(successorRef)) refs.Add(successorRef);
            predecessor.CauseRefsJson = JsonSerializer.Serialize(refs);
        }
        var changed = !Equals(before, Material(predecessor));
        if (changed)
        {
            predecessor.Version++;
            predecessor.StoredAt = now;
        }
        predecessor.EvidenceState = change.EvidenceState;
        predecessor.NextRunId ??= successor.RunId;
        if (change.ShareIncident && predecessor.FailureIncidentId is { } incident) successor.FailureIncidentId = incident;
        predecessor.UpdatedAt = now;
        if (!observed)
            db.WorkflowOutcomeObservations.Add(new WorkflowOutcomeObservation
            {
                RunRowId = predecessor.Id,
                EventId = successorEvent,
                EventType = SuccessorOutcomeEvent,
                EventHash = eventHash,
                Status = predecessor.Status,
                Classification = predecessor.Classification,
                ReasonCode = predecessor.ReasonCode,
                ObservedAt = now,
            });
        logger.LogInformation("workflow_outcome_chain_resolved outcome={OutcomeId} classification={Classification} evidence={EvidenceState} version={Version}",
            predecessor.Id, predecessor.Classification, predecessor.EvidenceState, predecessor.Version);
    }

    /// <summary>Encrypts failure/cause evidence. A keyring error fails closed: no ciphertext, no plaintext, evidence pending.</summary>
    private bool ProtectEvidence(WorkflowOutcomeRun row, WorkflowOutcomeRunFacts facts, WorkflowOutcomeVerdict verdict)
    {
        if (!EvidenceClassifications.Contains(verdict.Classification) || row.PayloadCiphertext is not null) return true;
        // An unclassified Completed run holds no failure or cause evidence, only its (success) result:
        // nothing is stored, and the history is re-read once the classifier policy changes.
        if (verdict.ReasonCode is WorkflowOutcomeReasonCodes.ClassifierMissing or WorkflowOutcomeReasonCodes.ClassifierUncoveredType) return true;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(EvidenceDocument(facts, verdict));
        try
        {
            row.PayloadCiphertext = protector.Protect(PayloadPurpose, bytes);
            row.PayloadKeyPurpose = PayloadPurpose;
            return true;
        }
        catch (Exception ex)
        {
            row.PayloadCiphertext = null;
            row.PayloadKeyPurpose = null;
            if (row.EvidenceState == WorkflowOutcomeEvidenceStates.Complete) row.EvidenceState = WorkflowOutcomeEvidenceStates.Pending;
            logger.LogError("workflow_outcome_keyring_unavailable outcome={OutcomeId} error={Error}", row.Id, ex.GetType().Name);
            return false;
        }
        finally
        {
            Array.Clear(bytes);
        }
    }

    private static object EvidenceDocument(WorkflowOutcomeRunFacts facts, WorkflowOutcomeVerdict verdict)
    {
        var t = facts.Terminal!;
        var referenced = verdict.CauseRefs.Concat(verdict.SourceTaskRefs).ToHashSet(StringComparer.Ordinal);
        var delegates = facts.Delegates
            .Where(d => referenced.Contains(WorkflowOutcomeClassifier.EventRef(d.ScheduledEventId)) || d.TaskId is not null && referenced.Contains("task:" + d.TaskId))
            .Select(d => new
            {
                d.ScheduledEventId, d.TaskId, d.Target, d.Attempt, d.Outcome, d.CloseEventType, d.ResultStatus, d.ResultText,
                d.FailureType, d.FailureMessage, resultSha256 = d.Result?.Sha256,
            });
        return new
        {
            schema = 1,
            run = new { facts.Namespace, facts.WorkflowId, facts.RunId, facts.WorkflowType },
            terminal = new
            {
                t.EventId, t.EventType, t.EventSha256, t.FailureType, t.FailureMessage, t.RetryState, t.NewExecutionRunId,
                attributes = t.AttributesJson,
                result = t.Result is null ? null : new { t.Result.Encoding, t.Result.Sha256, t.Result.Json },
            },
            definition = new { facts.DefinitionName, facts.DefinitionVersion, facts.DefinitionHash },
            delegates,
            facts.UnsupportedPayloadRefs,
        };
    }

    private DateTime? ExpiresAt(WorkflowOutcomeRun row, IReadOnlyList<WorkflowOutcomeObservation> observations, long? retentionSeconds)
    {
        // Only never-failed nonfailure metadata expires: after the run itself has left Temporal
        // retention plus one full scan, so a later sweep cannot re-discover it as new.
        var uncovered = row.Classification == WorkflowOutcomeClassifications.ClassificationPending
            && row.ReasonCode == WorkflowOutcomeReasonCodes.ClassifierUncoveredType;
        if (!(ExpiringClassifications.Contains(row.Classification) || uncovered) || row.Version != 1 || retentionSeconds is not > 0
            || observations.Any(o => o.EventType == OperatorClassificationEvent))
            return null;
        // Never in the past: a run Temporal has not yet deleted is kept for at least one more sweep.
        var retainedUntil = (row.CloseTime ?? Now).AddSeconds(retentionSeconds.Value);
        if (retainedUntil < Now) retainedUntil = Now;
        return retainedUntil.AddSeconds(options.Value.FullScanSeconds);
    }

    internal static string OutcomeRef(Guid id) => "outcome:" + id.ToString("D");

    /// <summary>Health key of the per-namespace, per-type uncovered gap (bounded: a long type name is hashed).</summary>
    public static string UncoveredTypeKey(string ns, string workflowType)
    {
        var key = UncoveredTypePrefix(ns) + workflowType;
        return key.Length <= 200 ? key : UncoveredTypePrefix(ns) + "sha256:" + WorkflowOutcomeHashes.Sha256Hex(workflowType);
    }

    public static string UncoveredTypePrefix(string ns) => WorkflowOutcomeClassifier.UncoveredTypeGap + ":" + ns + ":";

    /// <summary>Open per-type uncovered gaps of <paramref name="ns"/>, by health key.</summary>
    public async Task<IReadOnlyList<string>> OpenUncoveredTypeKeysAsync(string ns, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var prefix = UncoveredTypePrefix(ns);
        return await Db(scope).WorkflowOutcomeHealth.AsNoTracking()
            .Where(h => h.Component == WorkflowOutcomeHealthComponents.Coverage && h.State == "open" && h.OutageKey.StartsWith(prefix))
            .Select(h => h.OutageKey).ToListAsync(ct);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
