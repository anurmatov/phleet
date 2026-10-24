using System.Collections.Concurrent;
using System.Globalization;
using Fleet.Orchestrator.Configuration;
using Fleet.Shared;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Fleet.Orchestrator.Services;

public static class WorkflowOutcomeObserverRegistration
{
    /// <summary>
    /// Registers the independent workflow-failure observer (#476): history reader, policy loader,
    /// store and the reconciler hosted service. Options binding, the data-protection keyring and
    /// <see cref="IWorkflowOutcomePayloadProtector"/> are registered by Program.cs. With the feature
    /// disabled the reconciler resolves nothing else and only logs once.
    /// </summary>
    public static IServiceCollection AddWorkflowOutcomeObserver(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IWorkflowOutcomeRpc, TemporalWorkflowOutcomeRpc>();
        services.TryAddSingleton<IWorkflowOutcomeHistoryReader, WorkflowOutcomeHistoryReader>();
        services.TryAddSingleton<WorkflowOutcomePolicyLoader>();
        services.TryAddSingleton<WorkflowOutcomeStore>();
        services.TryAddSingleton<WorkflowOutcomeReconciler>();
        services.AddHostedService(sp => sp.GetRequiredService<WorkflowOutcomeReconciler>());
        return services;
    }
}

/// <summary>
/// Independent failure observer (#476). Runs in the orchestrator, outside every Temporal worker;
/// it never starts, signals or cancels a workflow. Per configured namespace it records enrollment,
/// re-lists the recent (24 h) UTC daily StartTime partitions every RecentScanSeconds, sweeps every
/// retained run (StartTime below a fixed scan start, no lower bound) every FullScanSeconds,
/// Describes known nonterminal runs every PollSeconds and, once a run is closed, reads its whole
/// exact-RunId history, classifies it and stores the result. Only confirmed failures ever become
/// enqueue-eligible; everything else is bounded discovery bookkeeping.
/// </summary>
public sealed class WorkflowOutcomeReconciler(
    IServiceProvider services,
    IOptions<WorkflowOutcomeOptions> options,
    IConfiguration configuration,
    TimeProvider clock,
    ILogger<WorkflowOutcomeReconciler> logger) : BackgroundService
{
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);

    /// <summary>A full sweep slower than this is capacity_degraded (no coverage claim on the stated budget).</summary>
    public static readonly TimeSpan FullSweepBudget = TimeSpan.FromSeconds(300);

    private const int RecentPagesPerCycle = 50;
    private const int FullPagesPerCycle = 50;
    private const int KnownRunsPerCycle = 1000;
    private const int HistoryRunsPerCycle = 200;
    private const int CleanupBatch = 500;
    private const int MaxParentDepth = 4;

    private readonly WorkflowOutcomeOptions _options = options.Value;
    private readonly string _owner = Environment.MachineName + "/" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "/" + Guid.NewGuid().ToString("N")[..8];
    private readonly Dictionary<string, NamespaceSchedule> _schedules = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, WorkflowOutcomeHistoryProgress> _partial = new(StringComparer.Ordinal);
    private WorkflowOutcomeStore? _store;
    private IWorkflowOutcomeHistoryReader? _reader;
    private WorkflowOutcomePolicies? _policies;
    private WorkflowOutcomeLease? _lease;
    private bool _leaseWarned;

    private sealed class NamespaceSchedule
    {
        public DateTime NextRecentAt;
        public DateTime NextFullAt;
        public DateTime NextPollAt;
        public DateTime RetryAt;
        public int Failures;
        public bool Degraded;
        public bool Enrolled;
        public long? RetentionSeconds;
        public bool KeyringDegraded;
    }

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>The lease currently held (tests and diagnostics).</summary>
    public WorkflowOutcomeLease? Lease => _lease;

    /// <summary>
    /// Every startup fault when the feature is enabled (empty when valid or disabled): option
    /// faults plus a policy file/project fault. Program.cs can call this before app.Run().
    /// </summary>
    public static async Task<IReadOnlyList<string>> ValidateStartupAsync(IServiceProvider services, CancellationToken ct)
    {
        var opts = services.GetRequiredService<IOptions<WorkflowOutcomeOptions>>().Value;
        if (!opts.Enabled) return [];
        var faults = opts.Validate().ToList();
        if (faults.Count > 0) return faults;
        try
        {
            await services.GetRequiredService<WorkflowOutcomePolicyLoader>().LoadAsync(ct);
        }
        catch (WorkflowOutcomePolicyException ex)
        {
            faults.Add(ex.Message);
        }
        return faults;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("WorkflowOutcomes observer disabled");
            return;
        }
        if (!await InitializeAsync(stoppingToken)) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError("workflow_outcome_cycle_failed error={Error}", ex.GetType().Name);
            }
            await Task.Delay(Tick, clock, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        // Shutdown: committed checkpoints/rows are already durable; nothing is marked done or cleared.
    }

    /// <summary>
    /// Validates options and loads policies. Invalid options make the feature unavailable (false);
    /// an invalid policy file or project is a startup failure (throws).
    /// </summary>
    public async Task<bool> InitializeAsync(CancellationToken ct)
    {
        var faults = _options.Validate();
        if (faults.Count > 0)
        {
            logger.LogError("workflow_outcome_observer_misconfigured faults={Faults}", string.Join("; ", faults));
            return false;
        }
        try
        {
            _policies = await services.GetRequiredService<WorkflowOutcomePolicyLoader>().LoadAsync(ct);
        }
        catch (WorkflowOutcomePolicyException ex)
        {
            logger.LogCritical("workflow_outcome_policy_invalid fault={Fault}", ex.Message);
            throw;
        }
        _store = services.GetRequiredService<WorkflowOutcomeStore>();
        _reader = services.GetRequiredService<IWorkflowOutcomeHistoryReader>();

        var coordinator = Coordinator;
        if (coordinator is null)
            logger.LogWarning("workflow_outcome_coordinator_unconfigured: failures are retained, none can be enqueued");
        else if (coordinator != _policies.Ownership.DefaultCoordinator)
            logger.LogWarning("workflow_outcome_default_coordinator_differs: unmatched failures stay unassigned until reviewed");
        logger.LogInformation("WorkflowOutcomes observer starting namespaces={Count} poll={Poll}s recent={Recent}s full={Full}s",
            _options.NamespaceList().Count, _options.PollSeconds, _options.RecentScanSeconds, _options.FullScanSeconds);
        return true;
    }

    private string? Coordinator => configuration["FLEET_CTO_AGENT"] is { Length: > 0 } c ? c.Trim() : null;

    /// <summary>One scheduling slice over every namespace. Public so tests drive it with a manual clock.</summary>
    public async Task RunCycleAsync(CancellationToken ct)
    {
        if (_store is null || _reader is null || _policies is null)
            throw new InvalidOperationException("InitializeAsync has not succeeded");

        try
        {
            _lease = await _store.AcquireLeaseAsync(_owner, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // DB down: no claims, no cursor movement; previously committed rows survive.
            _lease = null;
            logger.LogError("workflow_outcome_store_unavailable stage=lease error={Error}", ex.GetType().Name);
            return;
        }
        if (_lease is null)
        {
            if (!_leaseWarned) logger.LogWarning("workflow_outcome_lease_held_elsewhere: observer idle");
            _leaseWarned = true;
            return;
        }
        _leaseWarned = false;

        try
        {
            foreach (var ns in _options.NamespaceList())
                await NamespaceCycleAsync(ns, ct);
            var removed = await _store.CleanupExpiredAsync(_lease, CleanupBatch, ct);
            if (removed > 0) logger.LogInformation("workflow_outcome_metadata_expired count={Count}", removed);
        }
        catch (WorkflowOutcomeLeaseLostException)
        {
            _lease = null;
            logger.LogWarning("workflow_outcome_lease_lost: new work stopped");
        }
        catch (Exception ex) when (ex is not WorkflowOutcomeReadException && (ex is not OperationCanceledException || !ct.IsCancellationRequested))
        {
            logger.LogError("workflow_outcome_store_unavailable stage=cycle error={Error}", ex.GetType().Name);
        }
    }

    private NamespaceSchedule Schedule(string ns)
    {
        if (!_schedules.TryGetValue(ns, out var schedule))
            _schedules[ns] = schedule = new NamespaceSchedule();
        return schedule;
    }

    private async Task NamespaceCycleAsync(string ns, CancellationToken ct)
    {
        var schedule = Schedule(ns);
        var now = Now;
        if (now < schedule.RetryAt) return;
        try
        {
            if (!schedule.Enrolled) await EnrollAsync(ns, schedule, ct);

            if (now >= schedule.NextRecentAt)
            {
                if (await RecentWindowsAsync(ns, ct)) schedule.NextRecentAt = now.AddSeconds(_options.RecentScanSeconds);
            }
            await FullSweepAsync(ns, schedule, ct);
            if (now >= schedule.NextPollAt)
            {
                await PollKnownAsync(ns, ct);
                schedule.NextPollAt = now.AddSeconds(_options.PollSeconds);
            }
            await ClassifyClosedAsync(ns, schedule, ct);
            await CauseReviewAsync(ns, ct);

            if (schedule.Degraded)
            {
                await _store!.ResolveAsync(_lease!, WorkflowOutcomeHealthComponents.Temporal, "namespace_unavailable:" + ns, ct);
                logger.LogInformation("workflow_outcome_namespace_recovered namespace={Namespace}", ns);
            }
            schedule.Degraded = false;
            schedule.Failures = 0;
        }
        catch (WorkflowOutcomeReadException ex)
        {
            // Never "empty = none": the namespace stays registered and is retried with backoff.
            schedule.Failures++;
            var delay = Math.Min(60, 5 * (1 << Math.Min(schedule.Failures - 1, 4)));
            schedule.RetryAt = Now.AddSeconds(delay);
            schedule.Degraded = true;
            logger.LogWarning("workflow_outcome_scan_pending namespace={Namespace} failure={Kind} reason={Reason} retry_s={Delay}",
                ns, ex.Kind, ex.Reason, delay);
            await _store!.RaiseAsync(_lease!, WorkflowOutcomeHealthComponents.Temporal, "namespace_unavailable:" + ns,
                "scan_pending: " + ex.Kind + " " + ex.Reason, ns, ct: ct);
        }
    }

    private async Task EnrollAsync(string ns, NamespaceSchedule schedule, CancellationToken ct)
    {
        var info = await _reader!.DescribeNamespaceAsync(ns, ct);
        await _store!.UpsertEnrollmentAsync(_lease!, ns, info, Coordinator, _policies!.Fingerprint, ct);
        schedule.RetentionSeconds = info.RetentionSeconds;
        schedule.Enrolled = true;
        if (info.RetentionSeconds < 2 * _options.FullScanSeconds)
            logger.LogWarning("workflow_outcome_retention_margin_low namespace={Namespace} retention_s={Retention}", ns, info.RetentionSeconds);
        logger.LogInformation("workflow_outcome_namespace_enrolled namespace={Namespace} retention_s={Retention}", ns, info.RetentionSeconds);
    }

    // ─── Visibility windows ─────────────────────────────────────────────────

    /// <summary>The UTC daily half-open StartTime partitions that intersect the last 24 hours.</summary>
    public static IReadOnlyList<(DateTime Start, DateTime End)> RecentPartitions(DateTime now)
    {
        var first = now.AddHours(-24).Date;
        var list = new List<(DateTime, DateTime)>();
        for (var day = first; day <= now.Date; day = day.AddDays(1))
            list.Add((DateTime.SpecifyKind(day, DateTimeKind.Utc), DateTime.SpecifyKind(day.AddDays(1), DateTimeKind.Utc)));
        return list;
    }

    public static string RecentQuery(DateTime start, DateTime end) =>
        "StartTime >= \"" + Iso(start) + "\" AND StartTime < \"" + Iso(end) + "\"";

    /// <summary>Full sweep: every status and type started before the fixed scan start, with no lower bound.</summary>
    public static string FullQuery(DateTime scanStart) => "StartTime < \"" + Iso(scanStart) + "\"";

    private static string Iso(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    /// <returns>True when every recent partition completed a pass.</returns>
    private async Task<bool> RecentWindowsAsync(string ns, CancellationToken ct)
    {
        var complete = true;
        foreach (var (start, end) in RecentPartitions(Now))
        {
            var window = new WorkflowOutcomeWindow(ns, WorkflowOutcomeWindowKinds.Recent, WorkflowOutcomeStore.RecentKey(ns, start), start, end, RecentQuery(start, end));
            var checkpoint = await _store!.GetCheckpointAsync(window.Key, ct);
            var resume = checkpoint is { Complete: false, PageToken: not null } && checkpoint.QueryHash == window.QueryHash;
            if (!resume) await _store.BeginPassAsync(_lease!, window, ct);
            complete &= await ScanWindowAsync(window, resume ? Convert.FromBase64String(checkpoint!.PageToken!) : null, RecentPagesPerCycle, ct);
        }
        return complete;
    }

    private async Task FullSweepAsync(string ns, NamespaceSchedule schedule, CancellationToken ct)
    {
        var now = Now;
        var key = WorkflowOutcomeStore.FullKey(ns);
        var checkpoint = await _store!.GetCheckpointAsync(key, ct);
        WorkflowOutcomeWindow window;
        byte[]? token = null;
        if (checkpoint is { Complete: false, WindowEnd: { } scanStart, PassStartedAt: not null })
        {
            // Resume the pass in progress with its own fixed upper bound.
            window = new WorkflowOutcomeWindow(ns, WorkflowOutcomeWindowKinds.Full, key, null, scanStart, FullQuery(scanStart));
            if (checkpoint.QueryHash == window.QueryHash && checkpoint.PageToken is { } stored) token = Convert.FromBase64String(stored);
            if (now - checkpoint.PassStartedAt.Value > FullSweepBudget)
                await _store.RaiseAsync(_lease!, WorkflowOutcomeHealthComponents.Capacity, "capacity_degraded:" + ns,
                    "full sweep exceeded 300 s; coverage budget must be adjusted", ns, ct: ct);
        }
        else
        {
            if (now < schedule.NextFullAt) return;
            if (checkpoint?.PassStartedAt is { } lastStart && checkpoint.Complete && now < lastStart.AddSeconds(_options.FullScanSeconds))
            {
                schedule.NextFullAt = lastStart.AddSeconds(_options.FullScanSeconds);
                return;
            }
            await RetentionGapCheckAsync(ns, schedule, checkpoint, ct);
            // Whole seconds: the stored bound (datetime(6)) must rebuild the identical query on resume.
            var passStart = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
            window = new WorkflowOutcomeWindow(ns, WorkflowOutcomeWindowKinds.Full, key, null, passStart, FullQuery(passStart));
            await _store.BeginPassAsync(_lease!, window, ct);
        }

        if (await ScanWindowAsync(window, token, FullPagesPerCycle, ct))
        {
            var done = await _store.GetCheckpointAsync(key, ct);
            var duration = (done?.PassCompletedAt ?? now) - (done?.PassStartedAt ?? now);
            schedule.NextFullAt = (done?.PassStartedAt ?? now).AddSeconds(_options.FullScanSeconds);
            if (duration > FullSweepBudget)
                await _store.RaiseAsync(_lease!, WorkflowOutcomeHealthComponents.Capacity, "capacity_degraded:" + ns,
                    "full sweep exceeded 300 s; coverage budget must be adjusted", ns, ct: ct);
            else
                await _store.ResolveAsync(_lease!, WorkflowOutcomeHealthComponents.Capacity, "capacity_degraded:" + ns, ct);
            logger.LogInformation("workflow_outcome_full_pass namespace={Namespace} seen={Seen} duration_ms={Duration}",
                ns, done?.SeenCount ?? 0, (long)duration.TotalMilliseconds);
        }
    }

    /// <summary>
    /// Before a new full pass: if the previous pass started longer than retention ago, runs that
    /// both started and expired in between were never observable — record a durable unknown-count gap.
    /// </summary>
    private async Task RetentionGapCheckAsync(string ns, NamespaceSchedule schedule, Data.WorkflowOutcomeCheckpoint? lastFull, CancellationToken ct)
    {
        if (schedule.RetentionSeconds is not { } retention) return;
        var state = await _store!.GetNamespaceStateAsync(ns, ct);
        var since = lastFull?.PassStartedAt ?? state.EnrolledAt;
        if (since is null) return;
        var horizon = Now.AddSeconds(-retention);
        if (horizon <= since.Value) return;
        logger.LogWarning("workflow_outcome_retention_gap namespace={Namespace} from={From:O} to={To:O}", ns, since, horizon);
        await _store.RaiseAsync(_lease!, WorkflowOutcomeHealthComponents.Coverage, "retention_overrun:" + ns + ":" + Iso(since.Value),
            "observer outage longer than namespace retention: runs in this interval may be unknown", ns,
            gapStart: since, gapEnd: horizon, unknownRuns: true, ct: ct);
    }

    /// <summary>
    /// Lists pages from <paramref name="token"/>, committing each page's runs and next token
    /// atomically. An error leaves the last committed token; an invalid token restarts the same
    /// window once (identity upserts dedup). Returns true when the pass completed.
    /// </summary>
    private async Task<bool> ScanWindowAsync(WorkflowOutcomeWindow window, byte[]? token, int maxPages, CancellationToken ct)
    {
        var restarted = false;
        for (var page = 0; page < maxPages; page++)
        {
            WorkflowOutcomeListPage result;
            try
            {
                result = await _reader!.ListPageAsync(window.Namespace, window.Query, token, ct);
            }
            catch (WorkflowOutcomeReadException ex) when (ex.Kind == WorkflowOutcomeReadFailure.InvalidArgument && token is not null && !restarted)
            {
                logger.LogWarning("workflow_outcome_invalid_page_token namespace={Namespace} window={Window}", window.Namespace, window.Key);
                await _store!.RecordWindowErrorAsync(_lease!, window, "invalid_token", restart: true, ct);
                token = null;
                restarted = true;
                continue;
            }
            catch (WorkflowOutcomeReadException ex)
            {
                await _store!.RecordWindowErrorAsync(_lease!, window, ex.Kind + ":" + ex.Reason,
                    restart: ex.Kind == WorkflowOutcomeReadFailure.RepeatedToken, ct);
                throw;
            }
            await _store!.CommitPageAsync(_lease!, window, result.Runs, result.NextPageToken, ct);
            if (result.NextPageToken is null) return true;
            token = result.NextPageToken;
        }
        return false;
    }

    // ─── Known runs ─────────────────────────────────────────────────────────

    private async Task PollKnownAsync(string ns, CancellationToken ct)
    {
        // Every known nonterminal run, in keyset pages: none is starved behind the first batch.
        Guid? after = null;
        while (true)
        {
            var known = await _store!.ListNonterminalAsync(ns, after, KnownRunsPerCycle, ct);
            if (known.Count == 0) return;
            after = known[^1].Id;
            await DescribeKnownAsync(ns, known, ct);
            if (known.Count < KnownRunsPerCycle) return;
        }
    }

    private async Task DescribeKnownAsync(string ns, IReadOnlyList<WorkflowOutcomeKnownRun> known, CancellationToken ct)
    {
        await Parallel.ForEachAsync(known, ReadOptions(ct), async (run, token) =>
        {
            var described = await _reader!.DescribeAsync(ns, run.WorkflowId, run.RunId, token);
            if (described is null)
            {
                await _store!.MarkHistoryMissingAsync(_lease!, run.Id, "describe_not_found", token);
                return;
            }
            if (WorkflowOutcomeStore.IsNonterminal(described.Status)) return;
            await _store!.MarkClosedAsync(_lease!, run.Id, described.Status, described.CloseTime, token);
            if (described.CloseTime is { } closed)
                logger.LogInformation("workflow_outcome_closure_detected namespace={Namespace} status={Status} detection_lag_ms={Lag}",
                    ns, described.Status, (long)(Now - closed).TotalMilliseconds);
        });
    }

    private async Task ClassifyClosedAsync(string ns, NamespaceSchedule schedule, CancellationToken ct)
    {
        var awaiting = await _store!.ListAwaitingHistoryAsync(ns, _policies!.Classifiers.Hash, HistoryRunsPerCycle, ct);
        var keyringFailed = 0;
        var stored = 0;
        await Parallel.ForEachAsync(awaiting, ReadOptions(ct), async (run, token) =>
        {
            var key = WorkflowOutcomeHashes.IdentityHash(ns, run.WorkflowId, run.RunId);
            _partial.TryGetValue(key, out var progress);
            try
            {
                progress = await _reader!.ReadHistorySliceAsync(ns, run.WorkflowId, run.RunId, progress, token);
            }
            catch (WorkflowOutcomeReadException ex) when (ex.Kind == WorkflowOutcomeReadFailure.NotFound)
            {
                _partial.TryRemove(key, out _);
                await _store.MarkHistoryMissingAsync(_lease!, run.Id, ex.Reason, token);
                return;
            }
            catch (WorkflowOutcomeReadException ex) when (ex.Kind is WorkflowOutcomeReadFailure.Unsupported)
            {
                _partial.TryRemove(key, out _);
                await _store.MarkHistoryUnverifiedAsync(_lease!, run.Id, ex.Reason, token);
                return;
            }
            catch (WorkflowOutcomeReadException ex) when (ex.Kind is WorkflowOutcomeReadFailure.RepeatedToken or WorkflowOutcomeReadFailure.InvalidArgument)
            {
                // Start this history over next slice; nothing partial is ever classified.
                _partial.TryRemove(key, out _);
                await _store.RaiseAsync(_lease!, WorkflowOutcomeHealthComponents.History, "history_read:" + ns,
                    "history page token rejected or repeated; retrying", ns, ct: token);
                return;
            }
            catch (WorkflowOutcomeReadException ex)
            {
                // One unreadable run (unavailable, timed out) must not fail the namespace cycle:
                // keep its resumable progress, record the read gap and classify the others.
                logger.LogWarning("workflow_outcome_history_unavailable namespace={Namespace} outcome={OutcomeId} kind={Kind}", ns, run.Id, ex.Kind);
                await _store.RaiseAsync(_lease!, WorkflowOutcomeHealthComponents.History, "history_read:" + ns,
                    "a run history could not be read; retrying next cycle", ns, ct: token);
                return;
            }

            if (!progress.Complete)
            {
                _partial[key] = progress; // resumable: the next slice continues from the token
                return;
            }
            _partial.TryRemove(key, out _);

            var facts = progress.Facts();
            if (!facts.HistoryComplete)
            {
                await _store.MarkHistoryUnverifiedAsync(_lease!, run.Id, "event_id_gap", token);
                return;
            }
            if (facts.Terminal is null) return; // visibility ran ahead of history; Describe again later

            var verdict = WorkflowOutcomeClassifier.Classify(facts, _policies.Classifiers);
            WorkflowOutcomeParentOwnership? parent;
            try
            {
                parent = await ResolveParentAsync(facts, 0, token);
            }
            catch (WorkflowOutcomeReadException ex)
            {
                // The parent is unreadable right now: leave this run unclassified for the next cycle.
                logger.LogWarning("workflow_outcome_parent_unavailable namespace={Namespace} outcome={OutcomeId} kind={Kind}", ns, run.Id, ex.Kind);
                await _store.RaiseAsync(_lease!, WorkflowOutcomeHealthComponents.History, "history_read:" + ns,
                    "a parent run history could not be read; retrying next cycle", ns, ct: token);
                return;
            }
            var ownership = WorkflowOutcomeOwnership.Resolve(facts, _policies.Ownership, parent);
            var result = await _store.StoreOutcomeAsync(_lease!, facts, verdict, ownership, schedule.RetentionSeconds, token);
            if (result.KeyringFailed) Interlocked.Increment(ref keyringFailed);
            else if (result.Classification is not null) Interlocked.Increment(ref stored);

            if (facts.Terminal.NewExecutionRunId is { } next)
                await _store.EnsureTrackedAsync(_lease!, ns, facts.WorkflowId, next, facts.WorkflowType, facts.RunId, token);

            var closeTime = facts.Terminal.EventTime;
            var eligible = result.Classification == WorkflowOutcomeClassifications.ConfirmedFailure
                && result.EvidenceState != WorkflowOutcomeEvidenceStates.SuccessorPending && ownership.Coordinator == Coordinator;
            logger.LogInformation(
                "workflow_outcome_stored namespace={Namespace} outcome={OutcomeId} classification={Classification} reason={Reason} version={Version} result={Result} detection_lag_ms={Lag} store_ms={StoreMs} enqueue_ready={Ready}",
                ns, result.RunRowId, result.Classification, verdict.ReasonCode, result.Version, result.Outcome,
                closeTime is null || result.DetectedAt is null ? -1 : (long)(result.DetectedAt.Value - closeTime.Value).TotalMilliseconds,
                result.DetectedAt is null || result.StoredAt is null ? -1 : (long)(result.StoredAt.Value - result.DetectedAt.Value).TotalMilliseconds,
                eligible);
        });

        if (keyringFailed > 0)
        {
            schedule.KeyringDegraded = true;
            await _store.RaiseAsync(_lease!, WorkflowOutcomeHealthComponents.Keyring, "payload_protect",
                "evidence encryption failed; records kept with evidence pending, no plaintext", ns, ct: ct);
        }
        else if (schedule.KeyringDegraded && stored > 0)
        {
            schedule.KeyringDegraded = false;
            await _store.ResolveAsync(_lease!, WorkflowOutcomeHealthComponents.Keyring, "payload_protect", ct);
        }
    }

    /// <summary>
    /// A parent's ownership derived from its own exact Started event (or the stored derivation under
    /// the current policy). Null when the run has no parent or the parent cannot be verified.
    /// </summary>
    private async Task<WorkflowOutcomeParentOwnership?> ResolveParentAsync(WorkflowOutcomeRunFacts facts, int depth, CancellationToken ct)
    {
        if (facts.ParentWorkflowId is not { } parentId || facts.ParentRunId is not { } parentRun) return null;
        var parentNs = facts.ParentNamespace ?? facts.Namespace;
        if (!_options.NamespaceList().Contains(parentNs, StringComparer.Ordinal)) return null;
        var known = await _store!.GetOwnershipAsync(parentNs, parentId, parentRun, _policies!.Ownership.Hash, ct);
        if (known is not null || depth >= MaxParentDepth) return known;

        WorkflowOutcomeRunFacts parentFacts;
        try
        {
            parentFacts = await _reader!.ReadStartedAsync(parentNs, parentId, parentRun, ct);
        }
        catch (WorkflowOutcomeReadException ex) when (ex.Kind != WorkflowOutcomeReadFailure.Unavailable)
        {
            return null; // parent gone or unreadable: unverified, never guessed
        }
        if (parentFacts.WorkflowType.Length == 0) return null;
        var grandparent = await ResolveParentAsync(parentFacts, depth + 1, ct);
        var parentOwnership = WorkflowOutcomeOwnership.Resolve(parentFacts, _policies.Ownership, grandparent);
        await _store.StoreOwnershipAsync(_lease!, parentFacts, parentOwnership, ct);
        return new WorkflowOutcomeParentOwnership(parentNs, parentId, parentRun, parentOwnership.ProjectName, parentOwnership.Coordinator, Verified: true);
    }

    private async Task CauseReviewAsync(string ns, CancellationToken ct)
    {
        var (count, refs) = await _store!.StalePendingAsync(ns, Now.AddSeconds(-_options.CauseUnresolvedAlertSeconds), ct);
        await _store.SetAggregateAsync(_lease!, WorkflowOutcomeHealthComponents.CauseReview, "cause_unresolved:" + ns,
            "unresolved causes or pending classifications awaiting operator review", ns, count, refs, ct);
        // Classifier gaps close once no run is pending on them (e.g. after a reviewed policy pin and
        // restart); missing-history and retention gaps are facts and stay open.
        if (await _store.ClassifierGapCountAsync(ns, ct) == 0)
            foreach (var gap in ClassifierGapKeys)
                await _store.ResolveAsync(_lease!, WorkflowOutcomeHealthComponents.Coverage, gap + ":" + ns, ct);
    }

    private static readonly string[] ClassifierGapKeys = ["classifier_missing", "classifier_conflict", "evidence_unreadable"];

    private ParallelOptions ReadOptions(CancellationToken ct) =>
        new() { MaxDegreeOfParallelism = Math.Max(1, _options.MaxConcurrentReads), CancellationToken = ct };
}
