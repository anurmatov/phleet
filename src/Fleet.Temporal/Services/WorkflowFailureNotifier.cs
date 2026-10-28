using System.Text;
using System.Text.Json;
using Fleet.Temporal.Configuration;
using Fleet.Temporal.Mcp;
using Fleet.Temporal.Workflows.Fleet;
using Microsoft.Extensions.Options;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Exceptions;

namespace Fleet.Temporal.Services;

/// <summary>A run that closed Failed or TimedOut, as listed by Temporal visibility.</summary>
public sealed record ClosedRun(
    string Namespace, string WorkflowId, string RunId, string WorkflowType, string Status, DateTime CloseTime)
{
    public string Identity => $"{Namespace}/{WorkflowId}/{RunId}";
}

/// <summary>Where failed runs are discovered. Production reads Temporal visibility.</summary>
public interface IWorkflowFailureSource
{
    Task<IReadOnlyList<ClosedRun>> ListFailedAsync(string @namespace, DateTime sinceUtc, CancellationToken ct);

    /// <summary>One of the fixed <see cref="WorkflowFailureClassifier"/> categories, never failure text.</summary>
    Task<string> CauseAsync(ClosedRun run, CancellationToken ct);
}

/// <summary>What became of one notification run.</summary>
public enum NotificationOutcome { Running, Delivered, Failed, Unknown }

/// <summary>How a notification reaches the coordinator. Production starts the existing NotifyCtoWorkflow.</summary>
public interface IFailureNotificationSender
{
    /// <summary>
    /// Starts one delivery attempt under <paramref name="notificationId"/>. Returns false when that id
    /// already exists (a start that was not recorded before a crash). Throws when the hand-over fails.
    /// </summary>
    Task<bool> StartAsync(string notificationId, string coordinator, string text, CancellationToken ct);

    /// <summary>Delivered only when the notification run completed, i.e. the coordinator answered.</summary>
    Task<NotificationOutcome> OutcomeAsync(string notificationId, CancellationToken ct);
}

/// <summary>
/// Notifies the configured coordinator when a workflow closes because of failure (#488).
///
/// It polls each configured namespace from outside the failed workflow, so the failed run needs no
/// final activity. Only Failed and TimedOut runs are listed; Completed (including a null result),
/// Canceled, Terminated and continue-as-new are never treated as failures. A deliberate UWE
/// <c>fail</c> step closes Failed with the <c>ExplicitFail</c> type and is labelled a business failure.
///
/// Every discovered failure is a record in a small durable state file: pending, in flight, delivered
/// or parked, with its attempt count. Each attempt starts the existing NotifyCtoWorkflow under
/// <c>workflow-failure-&lt;namespace&gt;-&lt;runId&gt;-&lt;attempt&gt;</c> with delivery-failure reporting
/// on, so the notification run closes Completed only when the coordinator answered and Failed when
/// delivery did not happen. A failed hand-over or a failed notification run counts as an attempt; the
/// record is retried on later polls and parked after <see cref="WorkflowFailureNotifierOptions.MaxDeliveryAttempts"/>.
/// The per-attempt ids also let a lost state file be rebuilt from Temporal for failures still inside
/// the lookback window.
///
/// The notice carries identity, type, status and a fixed cause category only: no failure message,
/// error-type text, inputs, results or logs. The coordinator reads details through its existing tools
/// and their access rules, and decides what to do. Nothing here restarts, resumes or reserves anything.
/// </summary>
public sealed class WorkflowFailureNotifier : BackgroundService
{
    internal const string NotificationIdPrefix = "workflow-failure-";
    internal const int MaxSendsPerCycle = 10;
    internal static readonly TimeSpan ParkedRetention = TimeSpan.FromDays(30);

    private readonly WorkflowFailureNotifierOptions _options;
    private readonly IReadOnlyList<string> _namespaces;
    private readonly CtoAgentConfigService _ctoConfig;
    private readonly IWorkflowFailureSource _source;
    private readonly IFailureNotificationSender _sender;
    private readonly WorkflowFailureStateFile _store;
    private readonly TimeProvider _time;
    private readonly ILogger<WorkflowFailureNotifier> _logger;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, FailureRecord> _records;
    private bool _warnedUnconfigured;

    public WorkflowFailureNotifier(
        IOptions<WorkflowFailureNotifierOptions> options,
        IOptions<TemporalBridgeOptions> bridgeOptions,
        CtoAgentConfigService ctoConfig,
        IWorkflowFailureSource source,
        IFailureNotificationSender sender,
        ILogger<WorkflowFailureNotifier> logger,
        TimeProvider? time = null)
    {
        _options = options.Value;
        _namespaces = bridgeOptions.Value.Namespaces
            .Where(ns => !string.IsNullOrWhiteSpace(ns))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _ctoConfig = ctoConfig;
        _source = source;
        _sender = sender;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _store = new WorkflowFailureStateFile(_options.StateFile, logger);
        _records = _options.Enabled ? _store.Load() : new Dictionary<string, FailureRecord>(StringComparer.Ordinal);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Workflow failure notifier is disabled (WorkflowFailureNotifier:Enabled=false)");
            return;
        }

        _logger.LogInformation("Workflow failure notifier polling {Namespaces} every {Seconds}s, state {State}",
            string.Join(", ", _namespaces), _options.PollIntervalSeconds,
            _store.Durable ? "durable" : "in memory only");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogError("Workflow failure notifier cycle failed ({Error}); retrying next poll", ex.GetType().Name);
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, _options.PollIntervalSeconds)), ct)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    /// <summary>One poll: discover, settle in-flight attempts, start pending ones, prune, persist.</summary>
    public async Task RunCycleAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        await DiscoverAsync(now.AddHours(-Math.Max(1, _options.LookbackHours)), now, ct);
        await SettleInFlightAsync(now, ct);

        var coordinator = ResolveCoordinator();
        if (coordinator is null)
        {
            if (!_warnedUnconfigured)
                _logger.LogWarning(
                    "Workflow failure notifier has no coordinator (WorkflowFailureNotifier:CoordinatorAgent and " +
                    "FLEET_CTO_AGENT are empty); failures are held, not sent");
            _warnedUnconfigured = true;
        }
        else
        {
            _warnedUnconfigured = false;
            await StartPendingAsync(coordinator, now, ct);
        }

        Prune(now);
        Persist();
    }

    private async Task DiscoverAsync(DateTime since, DateTime now, CancellationToken ct)
    {
        foreach (var ns in _namespaces)
        {
            IReadOnlyList<ClosedRun> runs;
            try
            {
                runs = await _source.ListFailedAsync(ns, since, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning("Workflow failure notifier could not list namespace {Namespace}: {Error}",
                    ns, ex.GetType().Name);
                continue;
            }

            lock (_gate)
            {
                foreach (var run in runs)
                {
                    // Our own notification runs are never notified about.
                    if (run.WorkflowId.StartsWith(NotificationIdPrefix, StringComparison.Ordinal)) continue;
                    if (_records.ContainsKey(run.Identity)) continue;
                    _records[run.Identity] = FailureRecord.From(run, now);
                }
            }
        }
    }

    private async Task SettleInFlightAsync(DateTime now, CancellationToken ct)
    {
        List<FailureRecord> inFlight;
        lock (_gate) inFlight = _records.Values.Where(r => r.State == FailureState.InFlight).ToList();

        foreach (var record in inFlight)
        {
            NotificationOutcome outcome;
            try
            {
                outcome = await _sender.OutcomeAsync(record.NotificationId!, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogDebug("Outcome lookup for {Identity} failed: {Error}", record.Identity, ex.GetType().Name);
                continue;
            }

            lock (_gate)
            {
                switch (outcome)
                {
                    case NotificationOutcome.Delivered:
                        record.State = FailureState.Delivered;
                        record.UpdatedAt = now;
                        _logger.LogInformation("Workflow failure {Identity} delivered ({Notification})",
                            record.Identity, record.NotificationId);
                        break;
                    case NotificationOutcome.Failed:
                        FailAttempt(record, WorkflowFailureClassifier.DeliveryFailed, now);
                        break;
                }
            }
        }
    }

    private async Task StartPendingAsync(string coordinator, DateTime now, CancellationToken ct)
    {
        List<FailureRecord> batch;
        lock (_gate)
        {
            batch = _records.Values
                .Where(r => r.State == FailureState.Pending)
                .OrderBy(r => r.CloseTime)
                .Take(MaxSendsPerCycle)
                .ToList();
        }

        foreach (var record in batch)
        {
            ct.ThrowIfCancellationRequested();
            if (record.Cause is null)
            {
                try
                {
                    record.Cause = WorkflowFailureClassifier.Known(await _source.CauseAsync(record.Run, ct));
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // The notice is still worth sending; the coordinator can look the cause up.
                    _logger.LogDebug("Cause lookup failed for {Identity}: {Error}", record.Identity, ex.GetType().Name);
                    record.Cause = WorkflowFailureClassifier.Unknown;
                }
            }

            var id = NotificationId(record.Run, record.Attempts + 1);
            try
            {
                var startedNow = await _sender.StartAsync(id, coordinator, ComposeNotice(record.Run, record.Cause), ct);
                lock (_gate)
                {
                    record.State = FailureState.InFlight;
                    record.NotificationId = id;
                    record.UpdatedAt = now;
                }
                if (startedNow)
                    _logger.LogInformation("Workflow failure {Identity} ({Cause}) handed to {Coordinator} as {Notification}",
                        record.Identity, record.Cause, coordinator, id);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogDebug("Hand-over of {Identity} failed: {Error}", record.Identity, ex.GetType().Name);
                lock (_gate) FailAttempt(record, WorkflowFailureClassifier.HandoverFailed, now);
            }
        }
    }

    private void FailAttempt(FailureRecord record, string error, DateTime now)
    {
        record.Attempts++;
        record.LastError = error;
        record.NotificationId = null;
        record.UpdatedAt = now;
        record.State = record.Attempts >= Math.Max(1, _options.MaxDeliveryAttempts) ? FailureState.Parked : FailureState.Pending;
        if (record.State == FailureState.Parked)
            _logger.LogError("Workflow failure {Identity} not delivered after {Attempts} attempts ({Error}); parked",
                record.Identity, record.Attempts, error);
        else
            _logger.LogWarning("Workflow failure {Identity} still pending after attempt {Attempts} ({Error})",
                record.Identity, record.Attempts, error);
    }

    private void Prune(DateTime now)
    {
        var lookback = now.AddHours(-Math.Max(1, _options.LookbackHours));
        lock (_gate)
        {
            // A delivered record is kept while discovery could still list its run; a parked one for 30 days.
            foreach (var key in _records.Values
                         .Where(r => (r.State == FailureState.Delivered && r.CloseTime < lookback)
                                  || (r.State == FailureState.Parked && r.UpdatedAt < now - ParkedRetention))
                         .Select(r => r.Identity).ToList())
                _records.Remove(key);
        }
    }

    private void Persist()
    {
        List<FailureRecord> snapshot;
        lock (_gate) snapshot = _records.Values.ToList();
        _store.Save(snapshot);
    }

    private string? ResolveCoordinator()
    {
        var configured = _options.CoordinatorAgent;
        if (string.IsNullOrWhiteSpace(configured)) configured = _ctoConfig.GetCtoAgent();
        return string.IsNullOrWhiteSpace(configured) ? null : configured.Trim();
    }

    /// <summary>Deterministic per failed run and attempt; a lost state file is rebuilt from these.</summary>
    internal static string NotificationId(ClosedRun run, int attempt) =>
        $"{NotificationIdPrefix}{run.Namespace}-{run.RunId}-{attempt}";

    internal static string ComposeNotice(ClosedRun run, string cause)
    {
        var business = cause == WorkflowFailureClassifier.ExplicitFail ? " (explicit business failure)" : "";
        var sb = new StringBuilder();
        sb.AppendLine("[workflow-failure] A workflow closed because of failure.");
        sb.AppendLine($"namespace: {run.Namespace}");
        sb.AppendLine($"workflowId: {run.WorkflowId}");
        sb.AppendLine($"runId: {run.RunId}");
        sb.AppendLine($"type: {run.WorkflowType}");
        sb.AppendLine($"status: {run.Status}");
        sb.AppendLine($"cause: {WorkflowFailureClassifier.Known(cause)}{business}");
        sb.AppendLine($"closed: {run.CloseTime:yyyy-MM-ddTHH:mm:ssZ}");
        sb.AppendLine($"reference: temporal://{run.Namespace}/{Uri.EscapeDataString(run.WorkflowId)}/{run.RunId}");
        sb.AppendLine();
        sb.Append("The failure message, inputs and results are not copied here. Inspect the run with your " +
                  "existing Temporal tools, under their usual access rules, and decide whether to restart, resume " +
                  "or leave it. Nothing was restarted automatically. A short acknowledgement is enough.");
        return sb.ToString();
    }

    /// <summary>Pending/in-flight/parked visibility for the status endpoint. Identity and counts only.</summary>
    public WorkflowFailureNotifierStatus Snapshot()
    {
        lock (_gate)
        {
            List<FailureRecordView> In(string state) =>
                _records.Values.Where(r => r.State == state).OrderBy(r => r.CloseTime).Select(r => r.ToView()).ToList();
            return new WorkflowFailureNotifierStatus(
                Enabled: _options.Enabled,
                CoordinatorConfigured: ResolveCoordinator() is not null,
                Durable: _store.Durable,
                Delivered: _records.Values.Count(r => r.State == FailureState.Delivered),
                Pending: In(FailureState.Pending),
                InFlight: In(FailureState.InFlight),
                Parked: In(FailureState.Parked));
        }
    }
}

public static class FailureState
{
    public const string Pending = "pending";
    public const string InFlight = "in_flight";
    public const string Delivered = "delivered";
    public const string Parked = "parked";

    public static bool IsKnown(string? s) => s is Pending or InFlight or Delivered or Parked;
}

/// <summary>The persisted per-failure record: identity, state and attempt count, no content.</summary>
public sealed class FailureRecord
{
    public string Namespace { get; set; } = "";
    public string WorkflowId { get; set; } = "";
    public string RunId { get; set; } = "";
    public string WorkflowType { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTime CloseTime { get; set; }
    public string State { get; set; } = FailureState.Pending;
    public int Attempts { get; set; }
    public string? Cause { get; set; }
    public string? LastError { get; set; }
    public string? NotificationId { get; set; }
    public DateTime UpdatedAt { get; set; }

    public string Identity => $"{Namespace}/{WorkflowId}/{RunId}";
    public ClosedRun Run => new(Namespace, WorkflowId, RunId, WorkflowType, Status, CloseTime);

    public static FailureRecord From(ClosedRun run, DateTime now) => new()
    {
        Namespace = run.Namespace, WorkflowId = run.WorkflowId, RunId = run.RunId,
        WorkflowType = run.WorkflowType, Status = run.Status, CloseTime = run.CloseTime, UpdatedAt = now,
    };

    public FailureRecordView ToView() => new(Namespace, WorkflowId, RunId, Status, State, Attempts, LastError);
}

public sealed record FailureRecordView(
    string Namespace, string WorkflowId, string RunId, string Status, string State, int Attempts, string? LastError);

public sealed record WorkflowFailureNotifierStatus(
    bool Enabled, bool CoordinatorConfigured, bool Durable, int Delivered,
    IReadOnlyList<FailureRecordView> Pending, IReadOnlyList<FailureRecordView> InFlight,
    IReadOnlyList<FailureRecordView> Parked);

/// <summary>
/// The durable record of failures, written atomically (temp file, then rename) after every poll.
/// An empty path, or a directory that cannot be written, keeps state in memory and says so.
/// </summary>
public sealed class WorkflowFailureStateFile(string? path, ILogger logger)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private bool _writable = !string.IsNullOrWhiteSpace(path);

    public bool Durable => _writable;

    public Dictionary<string, FailureRecord> Load()
    {
        var records = new Dictionary<string, FailureRecord>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return records;
        try
        {
            foreach (var r in JsonSerializer.Deserialize<List<FailureRecord>>(File.ReadAllText(path), Json) ?? [])
            {
                if (string.IsNullOrEmpty(r.Namespace) || string.IsNullOrEmpty(r.RunId) || !FailureState.IsKnown(r.State)) continue;
                // Only fixed categories ever leave this process, even if the file was edited.
                r.Cause = r.Cause is null ? null : WorkflowFailureClassifier.Known(r.Cause);
                r.LastError = r.LastError is null ? null : WorkflowFailureClassifier.Known(r.LastError);
                if (r.State == FailureState.InFlight && string.IsNullOrEmpty(r.NotificationId)) r.State = FailureState.Pending;
                records[r.Identity] = r;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unreadable: start empty. Per-attempt notification ids still rebuild failures in the lookback.
            logger.LogError("Workflow failure state {Path} unreadable ({Error}); starting empty", path, ex.GetType().Name);
        }
        return records;
    }

    public void Save(IReadOnlyCollection<FailureRecord> records)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(records.OrderBy(r => r.Identity, StringComparer.Ordinal).ToList(), Json));
            File.Move(temp, path, overwrite: true);
            if (!_writable) logger.LogInformation("Workflow failure state {Path} is writable again", path);
            _writable = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (_writable)
                logger.LogError("Workflow failure state {Path} not writable ({Error}); state is in memory only", path, ex.GetType().Name);
            _writable = false;
        }
    }
}

/// <summary>
/// Turns a close failure into one of a fixed set of categories. Caller-supplied text, including an
/// application error type, never becomes part of a category.
/// </summary>
public static class WorkflowFailureClassifier
{
    public const string ExplicitFail = "explicit_fail";            // UWE fail step (#424): business failure
    public const string RefusedSignal = "refused_signal";          // UWE approver-only signal refusal
    public const string ApplicationError = "application_error";
    public const string ActivityFailure = "activity_failure";
    public const string ActivityTimeout = "activity_timeout";
    public const string ChildWorkflowFailure = "child_workflow_failure";
    public const string WorkflowTimeout = "workflow_timeout";
    public const string Canceled = "canceled";
    public const string Terminated = "terminated";
    public const string Unknown = "unknown";
    public const string HandoverFailed = "handover_failed";        // the notification run could not be started
    public const string DeliveryFailed = "delivery_failed";        // the notification run closed without delivering

    private static readonly HashSet<string> All =
    [
        ExplicitFail, RefusedSignal, ApplicationError, ActivityFailure, ActivityTimeout, ChildWorkflowFailure,
        WorkflowTimeout, Canceled, Terminated, Unknown, HandoverFailed, DeliveryFailed,
    ];

    public static string Category(Exception? cause) => cause switch
    {
        ApplicationFailureException { ErrorType: "ExplicitFail" } => ExplicitFail,
        ApplicationFailureException { ErrorType: "ReservedSignalName" } => RefusedSignal,
        ApplicationFailureException => ApplicationError,
        ActivityFailureException { InnerException: TimeoutFailureException } => ActivityTimeout,
        ActivityFailureException => ActivityFailure,
        ChildWorkflowFailureException => ChildWorkflowFailure,
        TimeoutFailureException => WorkflowTimeout,
        CanceledFailureException => Canceled,
        TerminatedFailureException => Terminated,
        _ => Unknown,
    };

    /// <summary>The value itself when it is one of the fixed categories, otherwise <see cref="Unknown"/>.</summary>
    public static string Known(string? value) => value is not null && All.Contains(value) ? value : Unknown;
}

/// <summary>Lists Failed and TimedOut runs through the bridge's per-namespace Temporal clients.</summary>
public sealed class TemporalWorkflowFailureSource(ITemporalClientFactory clientFactory) : IWorkflowFailureSource
{
    public async Task<IReadOnlyList<ClosedRun>> ListFailedAsync(string @namespace, DateTime sinceUtc, CancellationToken ct)
    {
        var client = await clientFactory.GetClientAsync(@namespace);
        var query = "(ExecutionStatus = 'Failed' OR ExecutionStatus = 'TimedOut') AND " +
                    $"CloseTime >= '{sinceUtc:yyyy-MM-ddTHH:mm:ssZ}'";
        var runs = new List<ClosedRun>();
        await foreach (var wf in client.ListWorkflowsAsync(query, new WorkflowListOptions { Rpc = new RpcOptions { CancellationToken = ct } }))
        {
            var status = wf.Status switch
            {
                WorkflowExecutionStatus.Failed => "Failed",
                WorkflowExecutionStatus.TimedOut => "TimedOut",
                _ => null,
            };
            if (status is null || wf.RunId is null) continue;
            runs.Add(new ClosedRun(@namespace, wf.Id, wf.RunId, wf.WorkflowType, status, wf.CloseTime ?? sinceUtc));
        }
        return runs;
    }

    public async Task<string> CauseAsync(ClosedRun run, CancellationToken ct)
    {
        if (run.Status == "TimedOut") return WorkflowFailureClassifier.WorkflowTimeout;

        var client = await clientFactory.GetClientAsync(run.Namespace);
        try
        {
            // Reads only the close event; the result is never deserialised into the notice.
            await client.GetWorkflowHandle(run.WorkflowId, runId: run.RunId)
                .GetResultAsync<object?>(followRuns: false, rpcOptions: new RpcOptions { CancellationToken = ct });
            return WorkflowFailureClassifier.Unknown;
        }
        catch (WorkflowFailedException ex)
        {
            return WorkflowFailureClassifier.Category(ex.InnerException);
        }
    }
}

/// <summary>
/// Starts the existing NotifyCtoWorkflow, the same delivery path <c>notify_cto</c> uses, with
/// delivery-failure reporting on so the run's own status is the delivery outcome.
/// </summary>
public sealed class TemporalFailureNotificationSender(
    ITemporalClientFactory clientFactory,
    IOptions<WorkflowFailureNotifierOptions> options) : IFailureNotificationSender
{
    public async Task<bool> StartAsync(string notificationId, string coordinator, string text, CancellationToken ct)
    {
        var opts = options.Value;
        var client = await clientFactory.GetClientAsync(opts.NotificationNamespace);
        try
        {
            await client.StartWorkflowAsync(
                TemporalWorkflowDispatcher.NotifyCtoWorkflowType,
                [new NotifyCtoWorkflowInput(coordinator, text, ReportDeliveryFailure: true)],
                new WorkflowOptions(id: notificationId, taskQueue: opts.NotificationTaskQueue)
                {
                    // Each attempt has its own id; an existing one means this attempt already started.
                    IdReusePolicy = WorkflowIdReusePolicy.RejectDuplicate,
                    Rpc = new RpcOptions { CancellationToken = ct },
                });
            return true;
        }
        catch (WorkflowAlreadyStartedException)
        {
            return false;
        }
    }

    public async Task<NotificationOutcome> OutcomeAsync(string notificationId, CancellationToken ct)
    {
        var client = await clientFactory.GetClientAsync(options.Value.NotificationNamespace);
        try
        {
            var description = await client.GetWorkflowHandle(notificationId)
                .DescribeAsync(new WorkflowDescribeOptions { Rpc = new RpcOptions { CancellationToken = ct } });
            return description.Status switch
            {
                WorkflowExecutionStatus.Running => NotificationOutcome.Running,
                WorkflowExecutionStatus.Completed => NotificationOutcome.Delivered,
                WorkflowExecutionStatus.Failed or WorkflowExecutionStatus.TimedOut
                    or WorkflowExecutionStatus.Canceled or WorkflowExecutionStatus.Terminated => NotificationOutcome.Failed,
                _ => NotificationOutcome.Unknown,
            };
        }
        catch (RpcException ex) when (ex.Code == RpcException.StatusCode.NotFound)
        {
            // The attempt is gone (retention): it cannot be confirmed, so it counts as not delivered.
            return NotificationOutcome.Failed;
        }
    }
}
