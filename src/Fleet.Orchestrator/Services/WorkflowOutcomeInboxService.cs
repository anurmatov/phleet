using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fleet.Orchestrator.Configuration;
using Fleet.Orchestrator.Data;
using Fleet.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Fleet.Orchestrator.Services;

// ─── Failure inbox, receipts, acknowledgements and record-only decisions (#476) ───────────────
// Only confirmed_failure runs ever enter a batch. Nothing here starts a workflow, sends a
// notification, runs a model turn or deletes an unresolved row: a decision is an audit record,
// and an acknowledgement is never an approval.

public static class WorkflowOutcomeBatchStates
{
    public const string Pending = "pending";
    public const string Delivered = "delivered";
    public const string PartiallyAcknowledged = "partially_acknowledged";
    public const string Acknowledged = "acknowledged";
    public const string Superseded = "superseded";
}

public static class WorkflowOutcomeItemStates
{
    public const string Pending = "pending";
    public const string Delivered = "delivered";
    public const string Acknowledged = "acknowledged";
    public const string Superseded = "superseded";
}

/// <summary>An HTTP status and a camelCase JSON body; the endpoints translate it verbatim.</summary>
public sealed record WorkflowOutcomeResult(int Status, object? Body)
{
    public static WorkflowOutcomeResult Ok(object body) => new(StatusCodes.Status200OK, body);
    public static WorkflowOutcomeResult Error(int status, string error) => new(status, new { error });
}

public sealed record WorkflowOutcomeStageTimes(
    DateTimeOffset? StoredAt, DateTimeOffset? EnqueuedAt, DateTimeOffset? DeliveredAt,
    DateTimeOffset? AcknowledgedAt, DateTimeOffset? DecisionMadeAt);

public sealed record WorkflowOutcomeLastDecision(
    string Operation, string Owner, string Reason, DateTimeOffset? DueAt, string Status, string DecidedVia, DateTimeOffset CreatedAt);

public sealed record WorkflowOutcomeRecoveryRef(Guid OperationId, string State);

/// <summary>The operator/dashboard view of one failure or cause-review run (never a successful run).</summary>
public sealed record WorkflowOutcomeView(
    Guid Id, int Version, string Namespace, string WorkflowId, string RunId, string WorkflowType, string Status,
    string Classification, string? ReasonCode, string? TerminalEventType, long? TerminalEventId,
    DateTimeOffset? StartTime, DateTimeOffset? CloseTime, string? Project, bool AccessNeeded,
    string? Coordinator, string? TaskOwner, string EvidenceState, Guid? FailureIncidentId,
    string? ParentWorkflowId, string? ParentRunId, string? PreviousRunId, string? NextRunId,
    string Stage, WorkflowOutcomeStageTimes Stages, string DeliveryState, bool TurnAmbiguous,
    WorkflowOutcomeLastDecision? LastDecision, WorkflowOutcomeRecoveryRef? Recovery, DateTimeOffset? DueAt,
    bool AttentionOverdue);

/// <summary>Shared helpers for the workflow-outcome API services.</summary>
public static class WorkflowOutcomeApi
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The views an operator list can ask for. None of them includes a successful run.</summary>
    public static readonly IReadOnlyList<string> ListStates = ["failures", "unresolved_cause", "all_attention"];

    /// <summary>UTC now, truncated to the microsecond MySQL <c>datetime(6)</c> stores.</summary>
    public static DateTime Now(TimeProvider clock)
    {
        var ticks = clock.GetUtcNow().UtcTicks;
        return new DateTime(ticks - ticks % 10, DateTimeKind.Utc);
    }

    public static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    public static DateTimeOffset? Utc(DateTime? value) => value is { } v ? Utc(v) : null;

    public static bool SameName(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    public static bool IsHex64(string? value) =>
        value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static bool FixedEquals(string? a, string? b) =>
        a is not null && b is not null
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    public static string NewNonce() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// The strings of a JSON array column. Elements may be strings or objects carrying a
    /// <c>ref</c>, <c>taskId</c> or <c>id</c> string; anything unreadable yields nothing.
    /// </summary>
    public static IReadOnlyList<string> Strings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            var values = new List<string>();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } s) values.Add(s);
                else if (e.ValueKind == JsonValueKind.Object)
                    foreach (var name in new[] { "ref", "taskId", "id" })
                        if (e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && p.GetString() is { Length: > 0 } v)
                        {
                            values.Add(v);
                            break;
                        }
            }
            return values;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The recorded source delegate task ids, without a <c>task:</c> prefix; null when no ledger was recorded.</summary>
    public static IReadOnlyList<string>? TaskIds(WorkflowOutcomeRun run) =>
        run.SourceTaskRefsJson is null
            ? null
            : Strings(run.SourceTaskRefsJson).Select(s => s.StartsWith("task:", StringComparison.Ordinal) ? s[5..] : s)
                .Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Authorized evidence references for a run: outcome, terminal event, cause, task and artifact refs.</summary>
    public static IReadOnlyList<string> EvidenceRefs(WorkflowOutcomeRun run)
    {
        var refs = new List<string> { "outcome:" + run.Id.ToString("D") };
        if (run.TerminalEventId is { } eventId) refs.Add("event:" + eventId.ToString(CultureInfo.InvariantCulture));
        refs.AddRange(Strings(run.CauseRefsJson));
        refs.AddRange((TaskIds(run) ?? []).Select(t => "task:" + t));
        refs.AddRange(Strings(run.ArtifactRefsJson).Select(a => a.Contains(':') ? a : "artifact:" + a));
        return refs.Where(WorkflowOutcomeLimits.IsEvidenceRef).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Locks the run row for the rest of the transaction (<c>FOR UPDATE</c> on MySQL).</summary>
    public static async Task<WorkflowOutcomeRun?> LockRunAsync(OrchestratorDbContext db, Guid id, CancellationToken ct)
    {
        if (db.Database.IsMySql())
        {
            var locked = await db.WorkflowOutcomeRuns
                .FromSqlInterpolated($"SELECT * FROM workflow_outcome_runs WHERE Id = {id} FOR UPDATE")
                .AsNoTracking()
                .ToListAsync(ct);
            return locked.FirstOrDefault();
        }
        return await db.WorkflowOutcomeRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
    }

    /// <summary>
    /// Runs <paramref name="work"/> in one transaction inside the execution strategy (the production
    /// context retries transient failures, which forbids a bare user transaction). The work returns
    /// whether to commit; every write it makes is conditional, so a strategy retry is safe.
    /// </summary>
    public static Task<T> InTransactionAsync<T>(OrchestratorDbContext db, Func<Task<(T Result, bool Commit)>> work, CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var (result, commit) = await work();
            if (commit) await tx.CommitAsync(CancellationToken.None);
            else await tx.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            return result;
        });
    }

    /// <summary>One deduplicated health incident per component+outage; never a workflow or notification.</summary>
    public static async Task UpsertHealthAsync(
        OrchestratorDbContext db, string component, string outageKey, bool open, string reason, long count,
        DateTime now, CancellationToken ct, string? @namespace = null)
    {
        var row = await db.WorkflowOutcomeHealth.FirstOrDefaultAsync(h => h.Component == component && h.OutageKey == outageKey, ct);
        if (row is null)
        {
            if (!open) return;
            db.WorkflowOutcomeHealth.Add(new WorkflowOutcomeHealth
            {
                Component = component, OutageKey = outageKey, State = "open", Reason = reason, Namespace = @namespace,
                KnownRunCount = count, FirstSeenAt = now, LastSeenAt = now,
            });
        }
        else if (open)
        {
            if (row.State != "open")
            {
                row.State = "open";
                row.FirstSeenAt = now;
                row.ResolvedAt = null;
            }
            row.LastSeenAt = now;
            row.Reason = reason;
            row.KnownRunCount = count;
        }
        else if (row.State == "open")
        {
            row.State = "resolved";
            row.ResolvedAt = now;
            row.LastSeenAt = now;
        }
        else return;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (EpicGrantService.IsDuplicateKey(ex))
        {
            // Another writer created the row first; the next pass updates it.
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    public static string ReceiptHash(WorkflowOutcomeBatch batch, DateTime deliveredAt) =>
        WorkflowOutcomeHashes.Sha256Hex(string.Join('|', "receipt", batch.Id.ToString("D"),
            batch.CoordinatorGeneration.ToString(CultureInfo.InvariantCulture), batch.Nonce, batch.ItemVersionsHash,
            deliveredAt.ToString("O", CultureInfo.InvariantCulture)));

    public static string EncodeCursor(params string[] parts) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join('\u001f', parts))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string[]? DecodeCursor(string? cursor, int parts)
    {
        if (string.IsNullOrEmpty(cursor) || cursor.Length > 1024) return null;
        try
        {
            var b64 = cursor.Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            var split = Encoding.UTF8.GetString(Convert.FromBase64String(b64)).Split('\u001f');
            return split.Length == parts ? split : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static bool IsAuthorityRef(string? value) =>
        WorkflowOutcomeLimits.IsEvidenceRef(value)
        && (value!.StartsWith("decision:", StringComparison.Ordinal)
            || Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host.Length > 0);

    public static bool IsTerminal(string status) =>
        status is not ("Running" or "Unspecified" or "");
}

public sealed class WorkflowOutcomeInboxService(
    OrchestratorDbContext db,
    WorkflowOutcomeAuthorization auth,
    IOptions<WorkflowOutcomeOptions> options,
    IWorkflowOutcomePayloadProtector protector,
    TimeProvider clock,
    ILogger<WorkflowOutcomeInboxService> logger)
{
    public const string LateResultPurpose = "late-result";
    private const int DefaultInboxLimit = 10;
    private const int DefaultListLimit = 50;

    private readonly WorkflowOutcomeOptions _options = options.Value;

    private static readonly string[] ReviewableClassifications =
        [WorkflowOutcomeClassifications.CauseUnresolved, WorkflowOutcomeClassifications.InsufficientEvidence, WorkflowOutcomeClassifications.ClassificationPending];

    // ── Coordinator inbox ─────────────────────────────────────────────────────

    /// <summary>Already-enqueued batches of the current generation. Never mutates, never asserts delivery.</summary>
    public async Task<WorkflowOutcomeResult> GetInboxAsync(WorkflowOutcomePrincipal principal, long? generation, string? cursor, int? limit, CancellationToken ct)
    {
        if (!principal.IsCoordinator) return WorkflowOutcomeResult.Error(403, "wrong_scope");
        if (generation != auth.Generation)
            return new(409, new { error = "stale_generation", currentGeneration = auth.Generation });
        var take = limit ?? DefaultInboxLimit;
        if (take is < 1 or > WorkflowOutcomeLimits.MaxPageLimit) return WorkflowOutcomeResult.Error(400, "limit_out_of_range");

        var gen = auth.Generation;
        var query = db.WorkflowOutcomeBatches.AsNoTracking().Where(b =>
            b.Coordinator == principal.Name && b.CoordinatorGeneration == gen
            && (b.State == WorkflowOutcomeBatchStates.Pending || b.State == WorkflowOutcomeBatchStates.Delivered
                || b.State == WorkflowOutcomeBatchStates.PartiallyAcknowledged));
        if (cursor is not null)
        {
            if (WorkflowOutcomeApi.DecodeCursor(cursor, 3) is not ["inbox", var ticksText, var nonce]
                || !long.TryParse(ticksText, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks))
                return WorkflowOutcomeResult.Error(400, "cursor_invalid");
            var at = new DateTime(ticks, DateTimeKind.Utc);
            query = query.Where(b => b.CreatedAt > at || (b.CreatedAt == at && string.Compare(b.Nonce, nonce) > 0));
        }

        var page = await query.OrderBy(b => b.CreatedAt).ThenBy(b => b.Nonce).Take(take + 1).ToListAsync(ct);
        string? next = null;
        if (page.Count > take)
        {
            page.RemoveAt(take);
            var last = page[^1];
            next = WorkflowOutcomeApi.EncodeCursor("inbox", last.CreatedAt.Ticks.ToString(CultureInfo.InvariantCulture), last.Nonce);
        }

        var batchIds = page.Select(b => b.Id).ToList();
        var items = await db.WorkflowOutcomeBatchItems.AsNoTracking().Where(i => batchIds.Contains(i.BatchId)).OrderBy(i => i.Id).ToListAsync(ct);
        var runIds = items.Select(i => i.OutcomeId).Distinct().ToList();
        var runs = await db.WorkflowOutcomeRuns.AsNoTracking().Where(r => runIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        var projects = (await db.AgentProjectAccess.AsNoTracking().Where(a => a.AgentName == principal.Name).Select(a => a.Project).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var batches = page.Select(b => new WorkflowOutcomeInboxBatch(
            b.Id, b.CoordinatorGeneration, b.Nonce, b.Coordinator, WorkflowOutcomeApi.Utc(b.CreatedAt), b.ItemVersionsHash,
            items.Where(i => i.BatchId == b.Id && runs.ContainsKey(i.OutcomeId))
                .Select(i => InboxItem(i, runs[i.OutcomeId], projects)).ToList())).ToList();
        return WorkflowOutcomeResult.Ok(new WorkflowOutcomeInboxPage(batches, next));
    }

    /// <summary>
    /// Minimal facts only. A run whose project the coordinator has no <c>agent_project_access</c>
    /// for (or whose project is unknown) is opaque: id, version, classification and access-needed.
    /// </summary>
    private static WorkflowOutcomeInboxItem InboxItem(WorkflowOutcomeBatchItem item, WorkflowOutcomeRun run, HashSet<string> projects)
    {
        var visible = !string.IsNullOrWhiteSpace(run.ProjectName) && projects.Contains(run.ProjectName);
        if (!visible)
            return new WorkflowOutcomeInboxItem(item.OutcomeId, item.OutcomeVersion, "", "", "", "", run.Classification, run.ReasonCode,
                null, null, null, null, true, null, run.FailureIncidentId, ["outcome:" + item.OutcomeId.ToString("D")]);
        return new WorkflowOutcomeInboxItem(item.OutcomeId, item.OutcomeVersion, run.Namespace, run.WorkflowId, run.RunId, run.WorkflowType,
            run.Classification, run.ReasonCode, run.TerminalEventType, run.TerminalEventId, WorkflowOutcomeApi.Utc(run.CloseTime),
            run.ProjectName, false, run.TaskOwner, run.FailureIncidentId,
            WorkflowOutcomeApi.EvidenceRefs(run).Take(WorkflowOutcomeLimits.MaxEvidenceRefs).ToList());
    }

    /// <summary>Records the fsynced delivery once. A duplicate with the same nonce/hash returns the same receipt.</summary>
    public async Task<WorkflowOutcomeResult> DeliveredAsync(WorkflowOutcomePrincipal principal, Guid batchId, WorkflowOutcomeDeliveredRequest? request, CancellationToken ct)
    {
        if (!principal.IsCoordinator) return WorkflowOutcomeResult.Error(403, "wrong_scope");
        if (request is null || string.IsNullOrEmpty(request.Nonce) || !WorkflowOutcomeApi.IsHex64(request.ItemVersionsHash))
            return WorkflowOutcomeResult.Error(400, "malformed_request");

        var batch = await db.WorkflowOutcomeBatches.AsNoTracking().FirstOrDefaultAsync(b => b.Id == batchId, ct);
        if (batch is null) return WorkflowOutcomeResult.Error(404, "batch_not_found");
        if (!WorkflowOutcomeApi.SameName(batch.Coordinator, principal.Name)) return WorkflowOutcomeResult.Error(403, "foreign_batch");
        if (Superseded(batch)) return SupersededResult();
        if (request.Generation != batch.CoordinatorGeneration) return WorkflowOutcomeResult.Error(409, "stale_generation");
        if (!WorkflowOutcomeApi.FixedEquals(request.Nonce, batch.Nonce)) return WorkflowOutcomeResult.Error(409, "nonce_mismatch");
        if (request.ItemVersionsHash != batch.ItemVersionsHash) return WorkflowOutcomeResult.Error(409, "item_versions_mismatch");

        if (batch.DeliveredAt is null)
        {
            var now = WorkflowOutcomeApi.Now(clock);
            var hash = WorkflowOutcomeApi.ReceiptHash(batch, now);
            await WorkflowOutcomeApi.InTransactionAsync(db, async () =>
            {
                var updated = await db.WorkflowOutcomeBatches
                    .Where(b => b.Id == batchId && b.DeliveredAt == null && b.State == WorkflowOutcomeBatchStates.Pending)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(b => b.DeliveredAt, now)
                        .SetProperty(b => b.LeasedAt, now)
                        .SetProperty(b => b.ReceiptHash, hash)
                        .SetProperty(b => b.State, WorkflowOutcomeBatchStates.Delivered), ct);
                if (updated == 1)
                {
                    await db.WorkflowOutcomeBatchItems.Where(i => i.BatchId == batchId && i.DeliveredAt == null)
                        .ExecuteUpdateAsync(s => s.SetProperty(i => i.DeliveredAt, now), ct);
                    await db.WorkflowOutcomeBatchItems.Where(i => i.BatchId == batchId && i.State == WorkflowOutcomeItemStates.Pending)
                        .ExecuteUpdateAsync(s => s.SetProperty(i => i.State, WorkflowOutcomeItemStates.Delivered), ct);
                }
                return (updated, true);
            }, ct);
            logger.LogInformation("workflow_outcome_delivered batch={BatchId} generation={Generation}", batchId, batch.CoordinatorGeneration);
        }

        var receipt = await ReceiptAsync(batchId, ct);
        return receipt is null ? WorkflowOutcomeResult.Error(409, "superseded_generation") : WorkflowOutcomeResult.Ok(receipt);
    }

    /// <summary>Authoritative receipt readback for an exact batch+nonce. Never mutates or invents a receipt.</summary>
    public async Task<WorkflowOutcomeResult> GetReceiptAsync(WorkflowOutcomePrincipal principal, Guid batchId, string? nonce, CancellationToken ct)
    {
        if (!principal.IsCoordinator && !principal.IsOperator) return WorkflowOutcomeResult.Error(403, "wrong_scope");
        var batch = await db.WorkflowOutcomeBatches.AsNoTracking().FirstOrDefaultAsync(b => b.Id == batchId, ct);
        if (batch is null) return WorkflowOutcomeResult.Error(404, "batch_not_found");
        if (principal.IsCoordinator && !WorkflowOutcomeApi.SameName(batch.Coordinator, principal.Name))
            return WorkflowOutcomeResult.Error(403, "foreign_batch");
        if (!WorkflowOutcomeApi.FixedEquals(nonce, batch.Nonce)) return WorkflowOutcomeResult.Error(404, "receipt_not_found");
        if (Superseded(batch)) return SupersededResult();
        if (batch.DeliveredAt is null) return WorkflowOutcomeResult.Error(404, "receipt_not_found");
        var receipt = await ReceiptAsync(batchId, ct);
        return receipt is null ? WorkflowOutcomeResult.Error(404, "receipt_not_found") : WorkflowOutcomeResult.Ok(receipt);
    }

    /// <summary>
    /// Explicit acknowledgement of delivered members at their exact versions. All-or-nothing per
    /// request; a subset is allowed, a wildcard or an unseen member never is.
    /// </summary>
    public async Task<WorkflowOutcomeResult> AckAsync(WorkflowOutcomePrincipal principal, Guid batchId, WorkflowOutcomeAckRequest? request, CancellationToken ct)
    {
        if (!principal.IsCoordinator && !principal.IsOperator) return WorkflowOutcomeResult.Error(403, "wrong_scope");
        if (request?.Items is not { Count: > 0 } requested || requested.Count > WorkflowOutcomeLimits.MaxBatchItems
            || requested.Any(i => i is null || i.Id == Guid.Empty || i.Version < 1)
            || requested.Select(i => i.Id).Distinct().Count() != requested.Count
            || string.IsNullOrEmpty(request.Nonce))
            return WorkflowOutcomeResult.Error(400, "items_required");

        var batch = await db.WorkflowOutcomeBatches.AsNoTracking().FirstOrDefaultAsync(b => b.Id == batchId, ct);
        if (batch is null) return WorkflowOutcomeResult.Error(404, "batch_not_found");
        if (principal.IsCoordinator && !WorkflowOutcomeApi.SameName(batch.Coordinator, principal.Name))
            return WorkflowOutcomeResult.Error(403, "foreign_batch");
        if (Superseded(batch)) return SupersededResult();
        if (request.Generation != batch.CoordinatorGeneration) return WorkflowOutcomeResult.Error(409, "stale_generation");
        if (!WorkflowOutcomeApi.FixedEquals(request.Nonce, batch.Nonce)) return WorkflowOutcomeResult.Error(409, "nonce_mismatch");
        if (batch.DeliveredAt is null) return WorkflowOutcomeResult.Error(409, "not_delivered");
        // A coordinator ack comes only from the admitted internal turn of this batch.
        if (principal.IsCoordinator && request.TaskId != WorkflowOutcomeTurnIdentity.TaskId(batchId))
            return WorkflowOutcomeResult.Error(422, "task_mismatch");

        var members = await db.WorkflowOutcomeBatchItems.AsNoTracking().Where(i => i.BatchId == batchId).ToListAsync(ct);
        var ids = requested.Select(r => r.Id).ToList();
        var versions = await db.WorkflowOutcomeRuns.AsNoTracking().Where(r => ids.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Version, ct);
        foreach (var r in requested)
        {
            var member = members.FirstOrDefault(m => m.OutcomeId == r.Id);
            if (member is null) return WorkflowOutcomeResult.Error(409, "not_member");
            if (member.OutcomeVersion != r.Version || versions.GetValueOrDefault(r.Id) != r.Version)
                return WorkflowOutcomeResult.Error(409, "stale_version");
            if (member.DeliveredAt is null) return WorkflowOutcomeResult.Error(409, "not_delivered");
            if (member.State == WorkflowOutcomeItemStates.Superseded) return WorkflowOutcomeResult.Error(409, "superseded_item");
        }

        var now = WorkflowOutcomeApi.Now(clock);
        var by = principal.IsCoordinator ? principal.Name : WorkflowOutcomePrincipal.OperatorKind;
        var source = principal.IsCoordinator ? WorkflowOutcomePrincipal.CoordinatorKind : WorkflowOutcomePrincipal.OperatorKind;
        var memberIds = requested.Select(r => members.First(m => m.OutcomeId == r.Id).Id).ToList();
        var applied = await WorkflowOutcomeApi.InTransactionAsync(db, async () =>
        {
            // Re-checked under the write: a concurrent supersede wins and the ack is refused.
            var acked = await db.WorkflowOutcomeBatchItems
                .Where(i => memberIds.Contains(i.Id) && i.AckedAt == null
                    && (i.State == WorkflowOutcomeItemStates.Delivered || i.State == WorkflowOutcomeItemStates.Pending))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(i => i.AckedAt, now)
                    .SetProperty(i => i.AckedBy, by)
                    .SetProperty(i => i.AckSource, source)
                    .SetProperty(i => i.State, WorkflowOutcomeItemStates.Acknowledged), ct);
            var allAcked = await db.WorkflowOutcomeBatchItems.Where(i => memberIds.Contains(i.Id))
                .AllAsync(i => i.State == WorkflowOutcomeItemStates.Acknowledged, ct);
            if (!allAcked) return (false, false);
            var open = await db.WorkflowOutcomeBatchItems.AnyAsync(i => i.BatchId == batchId
                && i.State != WorkflowOutcomeItemStates.Acknowledged && i.State != WorkflowOutcomeItemStates.Superseded, ct);
            var batchRow = db.WorkflowOutcomeBatches.Where(b => b.Id == batchId
                && b.State != WorkflowOutcomeBatchStates.Superseded && b.State != WorkflowOutcomeBatchStates.Acknowledged);
            if (open)
                await batchRow.ExecuteUpdateAsync(s => s.SetProperty(b => b.State, WorkflowOutcomeBatchStates.PartiallyAcknowledged), ct);
            else
                await batchRow.ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.State, WorkflowOutcomeBatchStates.Acknowledged)
                    .SetProperty(b => b.AckedAt, now), ct);
            return (true, true);
        }, ct);
        if (!applied) return WorkflowOutcomeResult.Error(409, "superseded_item");

        logger.LogInformation("workflow_outcome_acked batch={BatchId} items={Count} source={Source}", batchId, requested.Count, source);
        var receipt = await ReceiptAsync(batchId, ct);
        return receipt is null ? WorkflowOutcomeResult.Error(409, "superseded_generation") : WorkflowOutcomeResult.Ok(receipt);
    }

    private bool Superseded(WorkflowOutcomeBatch batch) =>
        batch.CoordinatorGeneration != auth.Generation
        || batch.State == WorkflowOutcomeBatchStates.Superseded
        || auth.Coordinator is null
        || !WorkflowOutcomeApi.SameName(batch.Coordinator, auth.Coordinator);

    private WorkflowOutcomeResult SupersededResult() =>
        new(409, new { error = "superseded_generation", currentGeneration = auth.Generation });

    private async Task<WorkflowOutcomeReceipt?> ReceiptAsync(Guid batchId, CancellationToken ct)
    {
        var batch = await db.WorkflowOutcomeBatches.AsNoTracking().FirstOrDefaultAsync(b => b.Id == batchId, ct);
        if (batch is null || batch.DeliveredAt is null) return null;
        var items = await db.WorkflowOutcomeBatchItems.AsNoTracking().Where(i => i.BatchId == batchId).OrderBy(i => i.Id).ToListAsync(ct);
        return new WorkflowOutcomeReceipt(
            batch.Id, batch.CoordinatorGeneration, batch.Nonce, batch.ItemVersionsHash,
            items.Select(i => new WorkflowOutcomeItemRef(i.OutcomeId, i.OutcomeVersion)).ToList(),
            items.Where(i => i.DeliveredAt is not null).Select(i => new WorkflowOutcomeItemRef(i.OutcomeId, i.OutcomeVersion)).ToList(),
            items.Where(i => i.AckedAt is not null).Select(i => new WorkflowOutcomeItemRef(i.OutcomeId, i.OutcomeVersion)).ToList(),
            batch.ReceiptHash, WorkflowOutcomeApi.Utc(batch.DeliveredAt), WorkflowOutcomeApi.Utc(batch.AckedAt));
    }

    // ── Record-only decisions ─────────────────────────────────────────────────

    /// <summary>
    /// Records one of the nine decisions for an acknowledged exact version. Nothing is executed;
    /// a refused decision is audited with <c>status=rejected</c>. Only finish/close change the
    /// failure item, and only by setting <c>ResolvedAt</c> with disposition evidence.
    /// </summary>
    public async Task<WorkflowOutcomeResult> DecideAsync(WorkflowOutcomePrincipal principal, Guid outcomeId, WorkflowOutcomeDecisionRequest? request, CancellationToken ct)
    {
        if (!principal.IsCoordinator && !principal.IsOperator) return WorkflowOutcomeResult.Error(403, "wrong_scope");
        if (request is null) return WorkflowOutcomeResult.Error(400, "malformed_request");

        var run = await db.WorkflowOutcomeRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == outcomeId, ct);
        if (run is null) return WorkflowOutcomeResult.Error(404, "outcome_not_found");
        if (principal.IsCoordinator && !WorkflowOutcomeApi.SameName(run.Coordinator, principal.Name))
            return WorkflowOutcomeResult.Error(403, "foreign_outcome");

        var now = WorkflowOutcomeApi.Now(clock);
        // Shape and generation first: a malformed, oversized or other-generation resend is never
        // matched against a recorded decision.
        var refusal = ValidateDecisionShape(request);
        if (refusal is not null) return await RejectDecisionAsync(principal, run, request, refusal.Value.Status, refusal.Value.Reason, now, ct);

        // A lost reply is resent with the identical body: answer with the recorded decision rather
        // than a second audit row (and rather than "already_resolved" after a finish/close).
        if (await AcceptedDuplicateAsync(principal, run, request, ct) is { } recorded)
            return new(201, new { decisionId = recorded, status = "accepted", duplicate = true });

        refusal = await ValidateDecisionStateAsync(run, request, now, ct);
        if (refusal is not null) return await RejectDecisionAsync(principal, run, request, refusal.Value.Status, refusal.Value.Reason, now, ct);

        var decisionId = Guid.NewGuid();
        var resolves = request.Operation is WorkflowOutcomeOperations.Finish or WorkflowOutcomeOperations.Close;
        var outcome = await WorkflowOutcomeApi.InTransactionAsync<(int Status, string? Reason)>(db, async () =>
        {
            var locked = await WorkflowOutcomeApi.LockRunAsync(db, outcomeId, ct);
            if (locked is null || locked.Version != request.ExpectedVersion) return ((409, "stale_version"), false);
            if (locked.ResolvedAt is not null) return ((409, "already_resolved"), false);
            db.WorkflowOutcomeDecisions.Add(DecisionRow(decisionId, principal, run, request, "accepted", null, now));
            await db.SaveChangesAsync(ct);
            if (resolves)
            {
                var closed = await db.WorkflowOutcomeRuns
                    .Where(r => r.Id == outcomeId && r.Version == request.ExpectedVersion && r.ResolvedAt == null)
                    .ExecuteUpdateAsync(u => u.SetProperty(r => r.ResolvedAt, now), ct);
                if (closed != 1) return ((409, "stale_version"), false);
            }
            return ((201, null), true);
        }, ct);
        if (outcome.Status != 201)
            return await RejectDecisionAsync(principal, run, request, outcome.Status, outcome.Reason!, now, ct);

        logger.LogInformation("workflow_outcome_decision outcome={OutcomeId} version={Version} operation={Operation} via={Via}",
            outcomeId, request.ExpectedVersion, request.Operation, principal.Kind);
        var noop = request.Operation == WorkflowOutcomeOperations.Cancel && WorkflowOutcomeApi.IsTerminal(run.Status);
        return new(201, noop
            ? new { decisionId, status = "accepted", effect = "closed_target_noop" }
            : (object)new { decisionId, status = "accepted" });
    }

    /// <summary>Request-only checks that need no database: enum, bounds, ref syntax and generation.</summary>
    private (int Status, string Reason)? ValidateDecisionShape(WorkflowOutcomeDecisionRequest request)
    {
        if (!WorkflowOutcomeOperations.IsKnown(request.Operation)) return (422, "operation_unknown");
        if (string.IsNullOrWhiteSpace(request.Owner) || request.Owner.Length > WorkflowOutcomeLimits.MaxOwnerChars) return (422, "owner_invalid");
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > WorkflowOutcomeLimits.MaxReasonChars) return (422, "reason_invalid");
        if (request.EvidenceRefs is not { Count: > 0 } refs || refs.Count > WorkflowOutcomeLimits.MaxEvidenceRefs
            || !refs.All(WorkflowOutcomeLimits.IsEvidenceRef))
            return (422, "evidence_invalid");
        if (request.TaskRef is not null && !WorkflowOutcomeLimits.IsEvidenceRef(request.TaskRef)) return (422, "task_ref_invalid");
        if (request.ArtifactRef is not null && !WorkflowOutcomeLimits.IsEvidenceRef(request.ArtifactRef)) return (422, "artifact_ref_invalid");
        if (WorkflowOutcomeOperations.RequiresDueAt(request.Operation) && request.DueAt is null) return (422, "due_at_required");
        if (request.Generation is { } g && g != auth.Generation) return (409, "generation_mismatch");
        return null;
    }

    /// <summary>
    /// The recorded accepted decision this request repeats exactly, or null. Only called after
    /// <see cref="ValidateDecisionShape"/>, so every compared field is complete and in bounds.
    /// </summary>
    private async Task<Guid?> AcceptedDuplicateAsync(
        WorkflowOutcomePrincipal principal, WorkflowOutcomeRun run, WorkflowOutcomeDecisionRequest request, CancellationToken ct)
    {
        var decidedBy = principal.IsCoordinator ? principal.Name : WorkflowOutcomePrincipal.OperatorKind;
        var via = principal.IsCoordinator ? WorkflowOutcomePrincipal.CoordinatorKind : WorkflowOutcomePrincipal.OperatorKind;
        var refsJson = JsonSerializer.Serialize(request.EvidenceRefs);
        var dueAt = request.DueAt?.UtcDateTime;
        var candidates = await db.WorkflowOutcomeDecisions.AsNoTracking()
            .Where(d => d.OutcomeId == run.Id && d.Status == "accepted" && d.ExpectedVersion == request.ExpectedVersion
                && d.Operation == request.Operation && d.DecidedVia == via && d.DecidedBy == decidedBy)
            .ToListAsync(ct);
        return candidates.FirstOrDefault(d => d.Owner == request.Owner && d.Reason == request.Reason
            && d.EvidenceRefsJson == refsJson && d.DueAt == dueAt && d.TaskRef == request.TaskRef
            && d.ArtifactRef == request.ArtifactRef && d.BatchId == request.BatchId)?.Id;
    }

    private async Task<(int Status, string Reason)?> ValidateDecisionStateAsync(WorkflowOutcomeRun run, WorkflowOutcomeDecisionRequest request, DateTime now, CancellationToken ct)
    {
        var refs = request.EvidenceRefs;
        if (WorkflowOutcomeOperations.RequiresDueAt(request.Operation) && request.DueAt!.Value.UtcDateTime <= now)
            return (422, "due_at_required");
        if (run.Classification != WorkflowOutcomeClassifications.ConfirmedFailure) return (422, "not_confirmed_failure");
        if (run.ResolvedAt is not null) return (409, "already_resolved");
        if (run.Version != request.ExpectedVersion) return (409, "stale_version");

        // The exact version must have been explicitly acknowledged in the current generation.
        var gen = auth.Generation;
        var acked = db.WorkflowOutcomeBatchItems.Where(i => i.OutcomeId == run.Id && i.OutcomeVersion == request.ExpectedVersion
            && i.DeliveryGeneration == gen && i.AckedAt != null);
        if (request.BatchId is { } batchId) acked = acked.Where(i => i.BatchId == batchId);
        if (!await acked.AnyAsync(ct)) return (422, "not_acknowledged");

        // Every ref must point at this outcome (or at an external artifact/URL): evidence from
        // another outcome can never justify this decision.
        var scoped = request.TaskRef is null ? refs : [.. refs, request.TaskRef];
        if (request.ArtifactRef is not null) scoped = [.. scoped, request.ArtifactRef];
        foreach (var r in scoped)
            if (!await InScopeAsync(run, r, ct)) return (422, "evidence_out_of_scope");

        switch (request.Operation)
        {
            case WorkflowOutcomeOperations.Resume when WorkflowOutcomeApi.IsTerminal(run.Status):
                return (422, "closed_run_cannot_resume");
            case WorkflowOutcomeOperations.Finish when request.ArtifactRef is null:
                // recovered_with_existing_artifact: the accepted artifact itself is the proof.
                return (422, "disposition_evidence_missing");
            case WorkflowOutcomeOperations.Close when !refs.Any(r => !r.StartsWith("outcome:", StringComparison.Ordinal) && !r.StartsWith("run:", StringComparison.Ordinal)):
                // accepted_failure / deliberate closure needs evidence beyond the item's own id.
                return (422, "disposition_evidence_missing");
            case WorkflowOutcomeOperations.AcceptLate:
            {
                var lateIds = refs.Where(r => r.StartsWith(LateRefPrefix, StringComparison.Ordinal))
                    .Select(r => Guid.TryParseExact(r[LateRefPrefix.Length..], "D", out var id) ? id : Guid.Empty)
                    .Where(id => id != Guid.Empty).ToList();
                if (lateIds.Count == 0 || !await db.WorkflowOutcomeLateResults.AnyAsync(l => lateIds.Contains(l.Id) && l.OutcomeId == run.Id && !l.Quarantined, ct))
                    return (422, "late_result_unverified");
                break;
            }
            case WorkflowOutcomeOperations.Reassign:
            {
                var owner = request.Owner.Trim();
                if (!await db.Agents.AnyAsync(a => a.Name == owner, ct)) return (422, "reassign_target_unknown");
                break;
            }
        }
        return null;
    }

    private const string LateRefPrefix = "late-result:";

    /// <summary>True when <paramref name="evidenceRef"/> names this outcome's own records, or an external artifact/URL.</summary>
    private async Task<bool> InScopeAsync(WorkflowOutcomeRun run, string evidenceRef, CancellationToken ct)
    {
        var colon = evidenceRef.IndexOf(':');
        var kind = evidenceRef[..(colon + 1)];
        var value = evidenceRef[(colon + 1)..];
        switch (kind)
        {
            case "artifact:":
            case "https:":
                return true;
            case "outcome:":
                return value == run.Id.ToString("D");
            case "run:":
                return value == run.Namespace + "/" + run.WorkflowId + "/" + run.RunId;
            case "event:":
                return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var eventId) && eventId > 0;
            case "task:":
                return (WorkflowOutcomeApi.TaskIds(run) ?? []).Contains(value, StringComparer.Ordinal);
        }
        if (!Guid.TryParseExact(value, "D", out var id)) return false;
        return kind switch
        {
            LateRefPrefix => await db.WorkflowOutcomeLateResults.AnyAsync(l => l.Id == id && l.OutcomeId == run.Id, ct),
            "decision:" => await db.WorkflowOutcomeDecisions.AnyAsync(d => d.Id == id && d.OutcomeId == run.Id, ct),
            "operation:" => await db.WorkflowRecoveryOperations.AnyAsync(o => o.Id == id && o.OutcomeId == run.Id, ct),
            _ => false,
        };
    }

    private async Task<WorkflowOutcomeResult> RejectDecisionAsync(
        WorkflowOutcomePrincipal principal, WorkflowOutcomeRun run, WorkflowOutcomeDecisionRequest request, int status, string reason, DateTime now, CancellationToken ct)
    {
        var decisionId = Guid.NewGuid();
        try
        {
            db.ChangeTracker.Clear();
            db.WorkflowOutcomeDecisions.Add(DecisionRow(decisionId, principal, run, request, "rejected", reason, now));
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
        logger.LogInformation("workflow_outcome_decision_rejected outcome={OutcomeId} reason={Reason} via={Via}", run.Id, reason, principal.Kind);
        return status == 409
            ? new(409, new { error = reason, decisionId, status = "rejected", currentVersion = run.Version })
            : new(status, new { error = reason, decisionId, status = "rejected" });
    }

    private static WorkflowOutcomeDecision DecisionRow(
        Guid id, WorkflowOutcomePrincipal principal, WorkflowOutcomeRun run, WorkflowOutcomeDecisionRequest request,
        string status, string? rejectReason, DateTime now) => new()
    {
        Id = id,
        OutcomeId = run.Id,
        ExpectedVersion = request.ExpectedVersion,
        Operation = Bound(request.Operation, 32),
        DecidedBy = Bound(principal.IsCoordinator ? principal.Name : WorkflowOutcomePrincipal.OperatorKind, 128),
        DecidedVia = principal.IsCoordinator ? WorkflowOutcomePrincipal.CoordinatorKind : WorkflowOutcomePrincipal.OperatorKind,
        Owner = Bound(request.Owner, WorkflowOutcomeLimits.MaxOwnerChars),
        Reason = Bound(request.Reason, WorkflowOutcomeLimits.MaxReasonChars),
        EvidenceRefsJson = JsonSerializer.Serialize((request.EvidenceRefs ?? []).Take(WorkflowOutcomeLimits.MaxEvidenceRefs)
            .Select(r => Bound(r, WorkflowOutcomeLimits.MaxEvidenceRefChars))),
        TaskRef = request.TaskRef is null ? null : Bound(request.TaskRef, 500),
        ArtifactRef = request.ArtifactRef is null ? null : Bound(request.ArtifactRef, 500),
        DueAt = request.DueAt?.UtcDateTime,
        BatchId = request.BatchId,
        Status = status,
        RejectReason = rejectReason,
        CreatedAt = now,
    };

    private static string Bound(string? value, int max) => value is null ? "" : value.Length <= max ? value : value[..max];

    // ── Operator classification review ────────────────────────────────────────

    /// <summary>
    /// Promotes a cause-review run to confirmed_failure or intentional_nonfailure with exact
    /// terminal-event and causal evidence. Increments the version and appends an immutable
    /// <c>operator_classification</c> observation. Never mutates Temporal.
    /// </summary>
    public async Task<WorkflowOutcomeResult> ClassifyAsync(WorkflowOutcomePrincipal principal, Guid outcomeId, WorkflowOutcomeClassificationRequest? request, CancellationToken ct)
    {
        if (!principal.IsOperator) return WorkflowOutcomeResult.Error(403, "wrong_scope");
        if (request is null) return WorkflowOutcomeResult.Error(400, "malformed_request");
        var run = await db.WorkflowOutcomeRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == outcomeId, ct);
        if (run is null || run.Classification == WorkflowOutcomeClassifications.ExcludedNonfailure)
            return WorkflowOutcomeResult.Error(404, "outcome_not_found");
        if (!ReviewableClassifications.Contains(run.Classification)) return WorkflowOutcomeResult.Error(422, "not_reviewable");
        if (run.Version != request.ExpectedVersion)
            return new(409, new { error = "stale_version", currentVersion = run.Version });

        var reasonOk = request.Classification switch
        {
            WorkflowOutcomeClassifications.ConfirmedFailure => request.ReasonCode switch
            {
                WorkflowOutcomeReasonCodes.FailureCausedCancel => IsStatus(run, "Canceled"),
                WorkflowOutcomeReasonCodes.FailureCausedTerminate => IsStatus(run, "Terminated"),
                _ => WorkflowOutcomeReasonCodes.IsBusiness(request.ReasonCode) && IsStatus(run, "Completed"),
            },
            WorkflowOutcomeClassifications.IntentionalNonfailure => request.ReasonCode == WorkflowOutcomeReasonCodes.DeliberateDisposition,
            _ => (bool?)null,
        };
        if (reasonOk is null) return WorkflowOutcomeResult.Error(422, "classification_invalid");
        if (reasonOk == false) return WorkflowOutcomeResult.Error(422, "reason_invalid");
        if (run.TerminalEventId is not { } terminalId || request.TerminalEventRef != "event:" + terminalId.ToString(CultureInfo.InvariantCulture))
            return WorkflowOutcomeResult.Error(422, "terminal_event_mismatch");
        var causal = request.CausalEvidenceRefs ?? [];
        if (causal.Count > WorkflowOutcomeLimits.MaxEvidenceRefs || !causal.All(WorkflowOutcomeLimits.IsEvidenceRef)
            || !causal.Any(r => r != request.TerminalEventRef))
            return WorkflowOutcomeResult.Error(422, "causal_evidence_missing");
        if (!WorkflowOutcomeApi.IsAuthorityRef(request.AuthorityRef)) return WorkflowOutcomeResult.Error(422, "authority_missing");

        var now = WorkflowOutcomeApi.Now(clock);
        var refs = WorkflowOutcomeApi.Strings(run.CauseRefsJson).Concat(causal).Append(request.AuthorityRef)
            .Distinct(StringComparer.Ordinal).ToList();
        var eventHash = WorkflowOutcomeHashes.Sha256Hex(string.Join('\n',
            new[] { "operator_classification", run.Id.ToString("D"), request.Classification, request.ReasonCode, request.TerminalEventRef, request.AuthorityRef }
                .Concat(causal.Order(StringComparer.Ordinal))));
        var applied = await WorkflowOutcomeApi.InTransactionAsync(db, async () =>
        {
            var updated = await db.WorkflowOutcomeRuns
                .Where(r => r.Id == outcomeId && r.Version == request.ExpectedVersion && r.Classification == run.Classification)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Classification, request.Classification)
                    .SetProperty(r => r.ReasonCode, request.ReasonCode)
                    .SetProperty(r => r.Version, r => r.Version + 1)
                    .SetProperty(r => r.CauseRefsJson, JsonSerializer.Serialize(refs, WorkflowOutcomeApi.Json))
                    .SetProperty(r => r.ClassificationPendingSince, (DateTime?)null)
                    .SetProperty(r => r.UpdatedAt, now), ct);
            if (updated != 1) return (false, false);
            db.WorkflowOutcomeObservations.Add(new WorkflowOutcomeObservation
            {
                RunRowId = outcomeId,
                EventId = terminalId,
                EventType = "operator_classification",
                EventHash = eventHash,
                Status = run.Status,
                Classification = request.Classification,
                ReasonCode = request.ReasonCode,
                ObservedAt = now,
            });
            await db.SaveChangesAsync(ct);
            return (true, true);
        }, ct);
        if (!applied) return new(409, new { error = "stale_version" });

        logger.LogInformation("workflow_outcome_classified outcome={OutcomeId} classification={Classification} reason={Reason}",
            outcomeId, request.Classification, request.ReasonCode);
        var fresh = await db.WorkflowOutcomeRuns.AsNoTracking().FirstAsync(r => r.Id == outcomeId, ct);
        return WorkflowOutcomeResult.Ok((await ViewsAsync([fresh], ct))[0]);
    }

    private static bool IsStatus(WorkflowOutcomeRun run, string status) =>
        run.Status == status || run.TerminalEventType == "WorkflowExecution" + status;

    // ── Operator views ────────────────────────────────────────────────────────

    public async Task<WorkflowOutcomeResult> ListAsync(string? @namespace, string? state, string? cursor, int? limit, CancellationToken ct)
    {
        state = string.IsNullOrEmpty(state) ? "failures" : state;
        if (!WorkflowOutcomeApi.ListStates.Contains(state)) return WorkflowOutcomeResult.Error(400, "state_invalid");
        var take = limit ?? DefaultListLimit;
        if (take is < 1 or > WorkflowOutcomeLimits.MaxPageLimit) return WorkflowOutcomeResult.Error(400, "limit_out_of_range");
        var ns = string.IsNullOrEmpty(@namespace) ? null : @namespace;

        var query = Filter(db.WorkflowOutcomeRuns.AsNoTracking(), state, ns);
        if (cursor is not null)
        {
            // The cursor echoes the filters it was issued for; a page never mixes two views.
            if (WorkflowOutcomeApi.DecodeCursor(cursor, 5) is not ["list", var cState, var cNs, var ticksText, var hash]
                || cState != state || cNs != (ns ?? "")
                || !long.TryParse(ticksText, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks))
                return WorkflowOutcomeResult.Error(400, "cursor_invalid");
            var at = new DateTime(ticks, DateTimeKind.Utc);
            query = query.Where(r => r.StoredAt < at || (r.StoredAt == at && string.Compare(r.IdentityHash, hash) < 0));
        }

        var page = await query.OrderByDescending(r => r.StoredAt).ThenByDescending(r => r.IdentityHash).Take(take + 1).ToListAsync(ct);
        string? next = null;
        if (page.Count > take)
        {
            page.RemoveAt(take);
            var last = page[^1];
            next = WorkflowOutcomeApi.EncodeCursor("list", state, ns ?? "", last.StoredAt.Ticks.ToString(CultureInfo.InvariantCulture), last.IdentityHash);
        }

        var runs = db.WorkflowOutcomeRuns.AsNoTracking().Where(r => ns == null || r.Namespace == ns);
        var counts = new
        {
            failures = await runs.CountAsync(r => r.Classification == WorkflowOutcomeClassifications.ConfirmedFailure && r.ResolvedAt == null, ct),
            unresolvedCauses = await runs.CountAsync(r => r.ResolvedAt == null
                && (r.Classification == WorkflowOutcomeClassifications.CauseUnresolved || r.Classification == WorkflowOutcomeClassifications.ClassificationPending), ct),
            coverageGaps = await db.WorkflowOutcomeHealth.CountAsync(h => h.State == "open" && h.GapStart != null && (ns == null || h.Namespace == ns), ct),
        };
        var namespaceErrors = (await db.WorkflowOutcomeHealth.AsNoTracking()
                .Where(h => h.State == "open" && h.Namespace != null && (ns == null || h.Namespace == ns))
                .OrderBy(h => h.Namespace).ThenBy(h => h.Id).ToListAsync(ct))
            .Select(h => new { @namespace = h.Namespace, error = h.Reason }).ToList();

        return WorkflowOutcomeResult.Ok(new { items = await ViewsAsync(page, ct), nextCursor = next, namespaceErrors, counts });
    }

    private static IQueryable<WorkflowOutcomeRun> Filter(IQueryable<WorkflowOutcomeRun> q, string state, string? ns)
    {
        q = state switch
        {
            "unresolved_cause" => q.Where(r => r.Classification == WorkflowOutcomeClassifications.CauseUnresolved
                || r.Classification == WorkflowOutcomeClassifications.ClassificationPending),
            "all_attention" => q.Where(r => r.ResolvedAt == null && (r.Classification == WorkflowOutcomeClassifications.ConfirmedFailure
                || r.Classification == WorkflowOutcomeClassifications.CauseUnresolved
                || r.Classification == WorkflowOutcomeClassifications.ClassificationPending)),
            _ => q.Where(r => r.Classification == WorkflowOutcomeClassifications.ConfirmedFailure),
        };
        return ns is null ? q : q.Where(r => r.Namespace == ns);
    }

    /// <summary>One run with its decision audit, batch memberships and observations. Raw payload only on request.</summary>
    public async Task<WorkflowOutcomeResult> DetailAsync(Guid outcomeId, bool payload, CancellationToken ct)
    {
        var run = await db.WorkflowOutcomeRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == outcomeId, ct);
        if (run is null || run.Classification == WorkflowOutcomeClassifications.ExcludedNonfailure)
            return WorkflowOutcomeResult.Error(404, "outcome_not_found");

        string? plaintext = null;
        if (payload && run.PayloadCiphertext is { } ciphertext)
        {
            try
            {
                plaintext = Encoding.UTF8.GetString(protector.Unprotect(run.PayloadKeyPurpose ?? "run", ciphertext));
            }
            catch (Exception ex) when (ex is CryptographicException or InvalidOperationException)
            {
                // Fail closed: the ciphertext is retained and nothing about it is logged.
                logger.LogWarning("workflow_outcome_payload_unavailable outcome={OutcomeId}", outcomeId);
                return WorkflowOutcomeResult.Error(503, "payload_unavailable");
            }
        }

        var view = (await ViewsAsync([run], ct))[0];
        var decisions = await db.WorkflowOutcomeDecisions.AsNoTracking().Where(d => d.OutcomeId == outcomeId)
            .OrderBy(d => d.CreatedAt).ToListAsync(ct);
        var memberships = await db.WorkflowOutcomeBatchItems.AsNoTracking().Where(i => i.OutcomeId == outcomeId).ToListAsync(ct);
        var batchIds = memberships.Select(m => m.BatchId).Distinct().ToList();
        var batches = await db.WorkflowOutcomeBatches.AsNoTracking().Where(b => batchIds.Contains(b.Id)).OrderBy(b => b.CreatedAt).ToListAsync(ct);
        var observations = await db.WorkflowOutcomeObservations.AsNoTracking().Where(o => o.RunRowId == outcomeId)
            .OrderBy(o => o.Id).ToListAsync(ct);

        var node = JsonSerializer.SerializeToNode(view, WorkflowOutcomeApi.Json)!.AsObject();
        node["decisions"] = JsonSerializer.SerializeToNode(decisions.Select(d => new
        {
            decisionId = d.Id, version = d.ExpectedVersion, operation = d.Operation, owner = d.Owner, reason = d.Reason,
            evidenceRefs = WorkflowOutcomeApi.Strings(d.EvidenceRefsJson), dueAt = WorkflowOutcomeApi.Utc(d.DueAt), status = d.Status,
            rejectReason = d.RejectReason, decidedBy = d.DecidedBy, decidedVia = d.DecidedVia, batchId = d.BatchId,
            taskRef = d.TaskRef, artifactRef = d.ArtifactRef, createdAt = WorkflowOutcomeApi.Utc(d.CreatedAt),
        }), WorkflowOutcomeApi.Json);
        node["batches"] = JsonSerializer.SerializeToNode(batches.Select(b =>
        {
            var m = memberships.First(x => x.BatchId == b.Id);
            return new
            {
                batchId = b.Id, generation = b.CoordinatorGeneration, nonce = b.Nonce, state = b.State, version = m.OutcomeVersion, itemState = m.State,
                deliveredAt = WorkflowOutcomeApi.Utc(m.DeliveredAt), ackedAt = WorkflowOutcomeApi.Utc(m.AckedAt), ackSource = m.AckSource,
            };
        }), WorkflowOutcomeApi.Json);
        node["observations"] = JsonSerializer.SerializeToNode(observations.Select(o => new
        {
            eventId = o.EventId, eventType = o.EventType, classification = o.Classification, reasonCode = o.ReasonCode,
            contradicting = o.Contradicting, observedAt = WorkflowOutcomeApi.Utc(o.ObservedAt),
        }), WorkflowOutcomeApi.Json);
        node["evidenceRefs"] = JsonSerializer.SerializeToNode(WorkflowOutcomeApi.EvidenceRefs(run), WorkflowOutcomeApi.Json);
        if (payload) node["payload"] = plaintext;
        return WorkflowOutcomeResult.Ok(node);
    }

    /// <summary>Builds the views for a page of runs with a fixed number of queries.</summary>
    internal async Task<List<WorkflowOutcomeView>> ViewsAsync(IReadOnlyList<WorkflowOutcomeRun> runs, CancellationToken ct)
    {
        if (runs.Count == 0) return [];
        var ids = runs.Select(r => r.Id).ToList();
        var gen = auth.Generation;
        var items = await db.WorkflowOutcomeBatchItems.AsNoTracking().Where(i => ids.Contains(i.OutcomeId) && i.DeliveryGeneration == gen).ToListAsync(ct);
        var batchIds = items.Select(i => i.BatchId).Distinct().ToList();
        var batches = await db.WorkflowOutcomeBatches.AsNoTracking().Where(b => batchIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, ct);
        var decisions = await db.WorkflowOutcomeDecisions.AsNoTracking().Where(d => ids.Contains(d.OutcomeId)).ToListAsync(ct);
        var operations = await db.WorkflowRecoveryOperations.AsNoTracking().Where(o => ids.Contains(o.OutcomeId)).ToListAsync(ct);
        var now = WorkflowOutcomeApi.Now(clock);
        var attention = TimeSpan.FromMinutes(_options.UnackedAttentionMinutes);

        return runs.Select(r =>
        {
            var item = items.FirstOrDefault(i => i.OutcomeId == r.Id && i.OutcomeVersion == r.Version);
            var batch = item is null ? null : batches.GetValueOrDefault(item.BatchId);
            var current = decisions.Where(d => d.OutcomeId == r.Id && d.ExpectedVersion == r.Version && d.Status == "accepted").MaxBy(d => d.CreatedAt);
            var last = decisions.Where(d => d.OutcomeId == r.Id).MaxBy(d => d.CreatedAt);
            var operation = operations.Where(o => o.OutcomeId == r.Id).MaxBy(o => o.ReservedAt);
            var failure = r.Classification == WorkflowOutcomeClassifications.ConfirmedFailure;
            var acked = item?.AckedAt is not null;
            var overdue = failure && r.ResolvedAt is null && current is null && !acked && WorkflowOutcomeApi.Utc(r.StoredAt) <= now - attention;
            var reviewOverdue = r.ResolvedAt is null && current is { DueAt: { } due } && WorkflowOutcomeApi.Utc(due) <= now;
            var deliveryState = item is null ? "none"
                : acked ? "acknowledged"
                : item.State == WorkflowOutcomeItemStates.Superseded ? "unknown"
                : item.DeliveredAt is not null ? "delivered"
                : "pending";
            var assigned = r.Coordinator is not null && WorkflowOutcomeApi.SameName(r.Coordinator, auth.Coordinator);
            var stage =
                r.ResolvedAt is not null || current is not null ? (reviewOverdue ? WorkflowOutcomeStages.Escalated : WorkflowOutcomeStages.DecisionMade)
                : overdue ? WorkflowOutcomeStages.Escalated
                : acked ? WorkflowOutcomeStages.Acknowledged
                : item?.DeliveredAt is not null ? WorkflowOutcomeStages.Delivered
                : item is not null ? WorkflowOutcomeStages.Enqueued
                : !failure || !assigned ? WorkflowOutcomeStages.Unresolved
                : r.StoredAt == default ? WorkflowOutcomeStages.Detected
                : WorkflowOutcomeStages.Stored;

            return new WorkflowOutcomeView(
                r.Id, r.Version, r.Namespace, r.WorkflowId, r.RunId, r.WorkflowType, r.Status, r.Classification, r.ReasonCode,
                r.TerminalEventType, r.TerminalEventId, WorkflowOutcomeApi.Utc(r.StartTime), WorkflowOutcomeApi.Utc(r.CloseTime),
                r.ProjectName, false, r.Coordinator, r.TaskOwner, r.EvidenceState, r.FailureIncidentId, r.ParentWorkflowId,
                r.ParentRunId, r.PreviousRunId, r.NextRunId, stage,
                new WorkflowOutcomeStageTimes(
                    r.StoredAt == default ? null : WorkflowOutcomeApi.Utc(r.StoredAt), WorkflowOutcomeApi.Utc(batch?.CreatedAt),
                    WorkflowOutcomeApi.Utc(item?.DeliveredAt), WorkflowOutcomeApi.Utc(item?.AckedAt),
                    WorkflowOutcomeApi.Utc(r.ResolvedAt ?? current?.CreatedAt)),
                deliveryState,
                deliveryState == "delivered" && overdue,
                last is null ? null : new WorkflowOutcomeLastDecision(last.Operation, last.Owner, last.Reason, WorkflowOutcomeApi.Utc(last.DueAt),
                    last.Status, last.DecidedVia, WorkflowOutcomeApi.Utc(last.CreatedAt)),
                operation is null ? null : new WorkflowOutcomeRecoveryRef(operation.Id, operation.State),
                WorkflowOutcomeApi.Utc(current?.DueAt),
                overdue);
        }).ToList();
    }

    /// <summary>Per-namespace coverage: retention, enrollment, scan freshness, known pending and gaps.</summary>
    public async Task<WorkflowOutcomeResult> CoverageAsync(CancellationToken ct)
    {
        var checkpoints = await db.WorkflowOutcomeCheckpoints.AsNoTracking().ToListAsync(ct);
        var open = await db.WorkflowOutcomeHealth.AsNoTracking().Where(h => h.State == "open").ToListAsync(ct);
        var namespaces = _options.NamespaceList()
            .Concat(checkpoints.Select(c => c.Namespace).Where(n => n.Length > 0))
            .Distinct(StringComparer.Ordinal).ToList();
        var pending = (await db.WorkflowOutcomeRuns.AsNoTracking()
                .Where(r => r.ResolvedAt == null && (r.Classification == WorkflowOutcomeClassifications.ClassificationPending
                    || r.EvidenceState == WorkflowOutcomeEvidenceStates.Pending))
                .GroupBy(r => r.Namespace).Select(g => new { Namespace = g.Key, Count = g.Count() }).ToListAsync(ct))
            .ToDictionary(x => x.Namespace, x => x.Count, StringComparer.Ordinal);
        var now = WorkflowOutcomeApi.Now(clock);
        var stale = TimeSpan.FromSeconds(3L * _options.RecentScanSeconds);

        var views = namespaces.Select(ns =>
        {
            var mine = checkpoints.Where(c => c.Namespace == ns && c.WindowKind != "lease").ToList();
            var gaps = open.Where(h => h.Namespace == ns && h.GapStart is not null)
                .Select(h => new { start = WorkflowOutcomeApi.Utc(h.GapStart), end = WorkflowOutcomeApi.Utc(h.GapEnd), reason = h.Reason, unknownRunCount = h.UnknownRunCount })
                .ToList();
            var enrolledAt = mine.Max(c => c.EnrolledAt);
            var lastRecent = mine.Where(c => c.WindowKind == "recent").Max(c => c.PassCompletedAt);
            var lastFull = mine.Where(c => c.WindowKind == "full").Max(c => c.PassCompletedAt);
            var degraded = open.Any(h => h.Namespace == ns) || mine.Any(c => c.LastError is not null)
                || lastRecent is null || WorkflowOutcomeApi.Utc(lastRecent.Value) < now - stale;
            var state = enrolledAt is null && lastFull is null ? "pending" : degraded ? "degraded" : "healthy";
            return new
            {
                @namespace = ns, state, retentionSeconds = mine.Max(c => c.RetentionSeconds), enrolledAt = WorkflowOutcomeApi.Utc(enrolledAt),
                lastRecentPassAt = WorkflowOutcomeApi.Utc(lastRecent), lastFullPassAt = WorkflowOutcomeApi.Utc(lastFull),
                knownPending = pending.GetValueOrDefault(ns), gaps,
            };
        }).ToList();

        return WorkflowOutcomeResult.Ok(new { enabled = true, degraded = open.Count > 0 || views.Any(v => v.state != "healthy"), namespaces = views });
    }

    public async Task<WorkflowOutcomeResult> HealthAsync(CancellationToken ct)
    {
        var incidents = (await db.WorkflowOutcomeHealth.AsNoTracking().Where(h => h.State == "open")
                .OrderBy(h => h.FirstSeenAt).ThenBy(h => h.Id).ToListAsync(ct))
            .Select(h => new
            {
                component = h.Component, outageKey = h.OutageKey, state = h.State, reason = h.Reason, @namespace = h.Namespace,
                firstSeenAt = WorkflowOutcomeApi.Utc(h.FirstSeenAt), lastSeenAt = WorkflowOutcomeApi.Utc(h.LastSeenAt),
            }).ToList();
        return WorkflowOutcomeResult.Ok(new { state = incidents.Count == 0 ? "healthy" : "degraded", incidents });
    }

    /// <summary>Anonymous: a single boolean, no namespace, task or owner data.</summary>
    public async Task<WorkflowOutcomeResult> PublicHealthAsync(CancellationToken ct) =>
        WorkflowOutcomeResult.Ok(new { healthy = !await db.WorkflowOutcomeHealth.AnyAsync(h => h.State == "open", ct) });

    // ── Late results (bridge) ─────────────────────────────────────────────────

    /// <summary>
    /// Stores one orphaned reply, encrypted, idempotently on namespace+taskId+resultSha256 and
    /// committed before any 2xx. A reply that maps to no unique recorded task is quarantined; an
    /// oversize one keeps metadata only. Never accepted, never overwrites the current attempt.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex ContinuationSuffix =
        new("/incomplete-retry-[0-9]+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>The workflow id that scheduled <paramref name="taskId"/>, or null when it has no step segment.</summary>
    internal static string? TaskOwnerWorkflowId(string taskId)
    {
        var baseId = ContinuationSuffix.Replace(taskId, "");
        var slash = baseId.LastIndexOf('/');
        return slash > 0 ? baseId[..slash] : null;
    }

    public async Task<WorkflowOutcomeResult> StoreLateResultAsync(WorkflowOutcomePrincipal principal, WorkflowOutcomeLateResultRequest? request, CancellationToken ct)
    {
        if (!principal.IsBridge) return WorkflowOutcomeResult.Error(403, "wrong_scope");
        if (request is null
            || string.IsNullOrWhiteSpace(request.TaskId) || request.TaskId.Length > 300
            || string.IsNullOrWhiteSpace(request.MessageId) || request.MessageId.Length > 200
            || string.IsNullOrWhiteSpace(request.Status) || request.Status.Length > 32
            || request.Namespace is { Length: > 64 }
            || request.Text is null || request.Attempt < 0
            || !WorkflowOutcomeApi.IsHex64(request.ResultSha256))
            return WorkflowOutcomeResult.Error(400, "malformed_request");

        var ns = request.Namespace?.Trim() ?? "";
        var size = Encoding.UTF8.GetByteCount(request.Text);
        var tooLarge = size > WorkflowOutcomeLimits.MaxLateResultBytes;
        // The hash is the dedup key, so it must be the hash of these exact bytes.
        if (!tooLarge && WorkflowOutcomeHashes.Sha256Hex(request.Text) != request.ResultSha256)
            return WorkflowOutcomeResult.Error(400, "result_hash_mismatch");

        var existing = await db.WorkflowOutcomeLateResults.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Namespace == ns && l.TaskId == request.TaskId && l.ResultHash == request.ResultSha256, ct);
        if (existing is not null) return Stored(existing, created: false, tooLarge);

        Guid? outcomeId = null;
        string? quarantine = tooLarge ? "too_large" : null;
        if (quarantine is null)
        {
            var taskId = request.TaskId;
            // A delegate task id is "<workflowId>/<step>" (a continuation appends
            // "/incomplete-retry-N"), so only runs of that exact workflow id can own it. Every one of
            // them is checked exactly; nothing is truncated before the exact match.
            var owner = TaskOwnerWorkflowId(taskId);
            var candidates = owner is null ? [] : (await db.WorkflowOutcomeRuns.AsNoTracking()
                    .Where(r => r.WorkflowId == owner && r.SourceTaskRefsJson != null && (ns == "" || r.Namespace == ns))
                    .ToListAsync(ct))
                .Where(r => (WorkflowOutcomeApi.TaskIds(r) ?? []).Contains(taskId, StringComparer.Ordinal)).ToList();
            // Never assume a default namespace or guess the current run.
            if (candidates.Count == 1) outcomeId = candidates[0].Id;
            else quarantine = candidates.Count == 0 ? "correlation_unknown" : "correlation_ambiguous";
        }

        byte[]? ciphertext = null;
        if (!tooLarge)
        {
            try
            {
                ciphertext = protector.Protect(LateResultPurpose, Encoding.UTF8.GetBytes(request.Text));
            }
            catch (Exception ex) when (ex is CryptographicException or InvalidOperationException or IOException)
            {
                logger.LogWarning("workflow_outcome_late_result_keyring_unavailable task_hash={TaskHash}", WorkflowOutcomeHashes.Sha256Hex(request.TaskId)[..12]);
                return WorkflowOutcomeResult.Error(503, "keyring_unavailable");
            }
        }

        var now = WorkflowOutcomeApi.Now(clock);
        var row = new WorkflowOutcomeLateResult
        {
            Id = Guid.NewGuid(),
            Namespace = ns,
            TaskId = request.TaskId,
            ResultHash = request.ResultSha256,
            MessageId = request.MessageId,
            Status = quarantine is null ? "stored" : "quarantined",
            ResultStatus = request.Status,
            Ciphertext = ciphertext,
            SizeBytes = size,
            SourceCorrelation = outcomeId is { } o ? "outcome:" + o.ToString("D") : null,
            OutcomeId = outcomeId,
            Quarantined = quarantine is not null,
            QuarantineReason = quarantine,
            Attempt = request.Attempt,
            ReceivedAt = request.ReceivedAt == default ? now : request.ReceivedAt.UtcDateTime,
            StoredAt = now,
        };
        db.WorkflowOutcomeLateResults.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (EpicGrantService.IsDuplicateKey(ex))
        {
            db.ChangeTracker.Clear();
            var raced = await db.WorkflowOutcomeLateResults.AsNoTracking()
                .FirstAsync(l => l.Namespace == ns && l.TaskId == request.TaskId && l.ResultHash == request.ResultSha256, ct);
            return Stored(raced, created: false, tooLarge);
        }
        finally
        {
            db.ChangeTracker.Clear();
        }

        if (quarantine is not null)
            await WorkflowOutcomeApi.UpsertHealthAsync(db, "late_results", "quarantined", true,
                "late results quarantined (latest reason: " + quarantine + ")",
                await db.WorkflowOutcomeLateResults.CountAsync(l => l.Quarantined && l.Status == "quarantined", ct), now, ct);
        logger.LogInformation("workflow_outcome_late_result id={Id} status={Status} size={Size}", row.Id, row.Status, size);
        return Stored(row, created: true, tooLarge);
    }

    private static WorkflowOutcomeResult Stored(WorkflowOutcomeLateResult row, bool created, bool tooLarge) =>
        new(tooLarge ? StatusCodes.Status413PayloadTooLarge : created ? StatusCodes.Status201Created : StatusCodes.Status200OK,
            new { id = row.Id, status = row.Status, quarantineReason = row.QuarantineReason, outcomeId = row.OutcomeId });
}

/// <summary>
/// Cuts failure batches from stored confirmed failures. Runs only when the feature is enabled.
/// The predicate is exactly confirmed_failure (business_* reasons included), not quarantined,
/// unresolved, not successor_pending, owned by the configured coordinator and lacking a member
/// for <c>(outcomeId, version, generation)</c>. Nothing else ever enters a batch.
/// </summary>
public sealed class WorkflowOutcomeBatcher(
    IServiceScopeFactory scopes,
    WorkflowOutcomeAuthorization auth,
    IOptions<WorkflowOutcomeOptions> options,
    TimeProvider clock,
    ILogger<WorkflowOutcomeBatcher> logger) : BackgroundService
{
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(2);
    private const int MaxCandidatesPerPass = 400;

    private readonly WorkflowOutcomeOptions _options = options.Value;

    /// <summary>
    /// Oldest-candidate age that triggers a flush: the coalescing window less a margin for the
    /// tick and the write, so every failure is enqueued within <c>BatchSeconds</c> (≤30 s) of storage.
    /// </summary>
    public TimeSpan FlushAge => TimeSpan.FromSeconds(Math.Max(0, _options.BatchSeconds - 3));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!auth.Enabled) return;
        using var timer = new PeriodicTimer(Tick, clock);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Type only: provider messages can quote row values.
                logger.LogWarning("workflow_outcome_batcher_failed type={Type}", ex.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One pass: supersede stale memberships, cut due batches, refresh attention health. Returns batches created.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        if (!auth.Enabled) return 0;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
        if (db is null) return 0;
        var now = WorkflowOutcomeApi.Now(clock);
        var coordinator = auth.Coordinator;

        await SupersedeAsync(db, coordinator, now, ct);
        var created = coordinator is null ? 0 : await EnqueueAsync(db, coordinator, now, ct);
        await AttentionAsync(db, coordinator, now, ct);
        return created;
    }

    private async Task SupersedeAsync(OrchestratorDbContext db, string? coordinator, DateTime now, CancellationToken ct)
    {
        var gen = auth.Generation;
        const string pending = WorkflowOutcomeItemStates.Pending, delivered = WorkflowOutcomeItemStates.Delivered;

        // Owner change: every open membership of another generation needs a re-ack in the new one.
        await db.WorkflowOutcomeBatchItems.Where(i => i.DeliveryGeneration != gen && (i.State == pending || i.State == delivered))
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.State, WorkflowOutcomeItemStates.Superseded), ct);
        // Material version change: the old exact version can no longer be acknowledged.
        await db.WorkflowOutcomeBatchItems.Where(i => (i.State == pending || i.State == delivered)
                && db.WorkflowOutcomeRuns.Any(r => r.Id == i.OutcomeId && r.Version != i.OutcomeVersion))
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.State, WorkflowOutcomeItemStates.Superseded), ct);
        await db.WorkflowOutcomeBatches.Where(b => b.CoordinatorGeneration != gen
                && b.State != WorkflowOutcomeBatchStates.Superseded && b.State != WorkflowOutcomeBatchStates.Acknowledged)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.State, WorkflowOutcomeBatchStates.Superseded), ct);

        // An open batch with no open member left: acknowledged if any member was, else superseded.
        var openBatches = db.WorkflowOutcomeBatches.Where(b => b.State == WorkflowOutcomeBatchStates.Pending
            || b.State == WorkflowOutcomeBatchStates.Delivered || b.State == WorkflowOutcomeBatchStates.PartiallyAcknowledged);
        var drained = openBatches.Where(b =>
            !db.WorkflowOutcomeBatchItems.Any(i => i.BatchId == b.Id && (i.State == pending || i.State == delivered)));
        await drained.Where(b => db.WorkflowOutcomeBatchItems.Any(i => i.BatchId == b.Id && i.State == WorkflowOutcomeItemStates.Acknowledged))
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.State, WorkflowOutcomeBatchStates.Acknowledged)
                .SetProperty(b => b.AckedAt, b => b.AckedAt ?? now), ct);
        await drained.ExecuteUpdateAsync(s => s.SetProperty(b => b.State, WorkflowOutcomeBatchStates.Superseded), ct);

        // The coordinator changed but the generation did not: its old batches cannot be re-enqueued.
        var foreign = coordinator is not null && await openBatches.AnyAsync(b => b.CoordinatorGeneration == gen
            && b.Coordinator != coordinator, ct);
        await WorkflowOutcomeApi.UpsertHealthAsync(db, "inbox", "generation_not_incremented", foreign,
            "open batches belong to another coordinator in the current generation; increment CoordinatorGeneration", 0, now, ct);
    }

    private async Task<int> EnqueueAsync(OrchestratorDbContext db, string coordinator, DateTime now, CancellationToken ct)
    {
        var gen = auth.Generation;
        var candidates = await db.WorkflowOutcomeRuns.AsNoTracking()
            .Where(r => r.Classification == WorkflowOutcomeClassifications.ConfirmedFailure
                && !r.Quarantined
                && r.ResolvedAt == null
                && r.EvidenceState != WorkflowOutcomeEvidenceStates.SuccessorPending
                && r.Coordinator == coordinator
                && !db.WorkflowOutcomeBatchItems.Any(i => i.OutcomeId == r.Id && i.OutcomeVersion == r.Version && i.DeliveryGeneration == gen))
            .OrderBy(r => r.StoredAt).ThenBy(r => r.IdentityHash)
            .Select(r => new Candidate(r.Id, r.Version, r.StoredAt, r.FailureIncidentId))
            .Take(MaxCandidatesPerPass)
            .ToListAsync(ct);
        if (candidates.Count == 0) return 0;

        var size = _options.BatchSize;
        if (candidates.Count < size && now - DateTime.SpecifyKind(candidates[0].StoredAt, DateTimeKind.Utc) < FlushAge) return 0;

        var created = 0;
        foreach (var members in Pack(candidates, size))
        {
            var refs = members.Select(m => new WorkflowOutcomeItemRef(m.Id, m.Version)).ToList();
            db.WorkflowOutcomeBatches.Add(new WorkflowOutcomeBatch
            {
                Id = Guid.NewGuid(),
                Coordinator = coordinator,
                CoordinatorGeneration = gen,
                State = WorkflowOutcomeBatchStates.Pending,
                Nonce = WorkflowOutcomeApi.NewNonce(),
                ItemVersionsHash = WorkflowOutcomeHashes.ItemVersionsHash(refs),
                CreatedAt = now,
                Items = members.Select(m => new WorkflowOutcomeBatchItem
                {
                    OutcomeId = m.Id, OutcomeVersion = m.Version, DeliveryGeneration = gen, State = WorkflowOutcomeItemStates.Pending,
                }).ToList(),
            });
            try
            {
                await db.SaveChangesAsync(ct);
                created++;
            }
            catch (Exception ex) when (EpicGrantService.IsDuplicateKey(ex))
            {
                // A concurrent pass enqueued a member first; the next pass re-reads.
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }
        if (created > 0) logger.LogInformation("workflow_outcome_enqueued batches={Batches} members={Members}", created, candidates.Count);
        return created;
    }

    /// <summary>Groups by failure incident (oldest first), keeps a group together where it fits, splits only oversize groups.</summary>
    internal static List<List<Candidate>> Pack(IReadOnlyList<Candidate> candidates, int size)
    {
        var batches = new List<List<Candidate>>();
        var current = new List<Candidate>();
        foreach (var group in candidates.GroupBy(c => c.FailureIncidentId ?? c.Id).OrderBy(g => g.Min(c => c.StoredAt)))
        {
            var members = group.ToList();
            if (current.Count > 0 && current.Count + members.Count > size)
            {
                batches.Add(current);
                current = [];
            }
            foreach (var m in members)
            {
                if (current.Count == size)
                {
                    batches.Add(current);
                    current = [];
                }
                current.Add(m);
            }
        }
        if (current.Count > 0) batches.Add(current);
        return batches;
    }

    /// <summary>The unacked-attention incident: one deduplicated row, never a notification or workflow.</summary>
    private async Task AttentionAsync(OrchestratorDbContext db, string? coordinator, DateTime now, CancellationToken ct)
    {
        var gen = auth.Generation;
        var threshold = now - TimeSpan.FromMinutes(_options.UnackedAttentionMinutes);
        var count = await db.WorkflowOutcomeRuns.CountAsync(r => r.Classification == WorkflowOutcomeClassifications.ConfirmedFailure
            && r.ResolvedAt == null && !r.Quarantined && r.EvidenceState != WorkflowOutcomeEvidenceStates.SuccessorPending
            && r.StoredAt <= threshold
            && !db.WorkflowOutcomeBatchItems.Any(i => i.OutcomeId == r.Id && i.OutcomeVersion == r.Version && i.DeliveryGeneration == gen && i.AckedAt != null), ct);
        await WorkflowOutcomeApi.UpsertHealthAsync(db, "inbox", "unacked_attention", count > 0,
            string.Create(CultureInfo.InvariantCulture, $"{count} confirmed failure(s) unacknowledged for more than {_options.UnackedAttentionMinutes} minutes"),
            count, now, ct);
        await WorkflowOutcomeApi.UpsertHealthAsync(db, "inbox", "coordinator_unconfigured", coordinator is null,
            "FLEET_CTO_AGENT is not configured; failures stay unassigned", 0, now, ct);
    }

    internal sealed record Candidate(Guid Id, int Version, DateTime StoredAt, Guid? FailureIncidentId);
}

/// <summary>The protector registered while the feature is off: every use fails closed.</summary>
public sealed class UnavailableWorkflowOutcomePayloadProtector : IWorkflowOutcomePayloadProtector
{
    public byte[] Protect(string purpose, ReadOnlySpan<byte> plaintext) =>
        throw new CryptographicException("workflow-outcome keyring is not configured");

    public byte[] Unprotect(string purpose, byte[] ciphertext) =>
        throw new CryptographicException("workflow-outcome keyring is not configured");
}
