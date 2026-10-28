using System.Text;
using Fleet.Temporal.Configuration;
using Fleet.Temporal.Mcp;
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

    /// <summary>A structural cause category for the run, never its failure text.</summary>
    Task<string> CauseAsync(ClosedRun run, CancellationToken ct);
}

/// <summary>How a notification reaches the coordinator. Production starts the existing NotifyCtoWorkflow.</summary>
public interface IFailureNotificationSender
{
    /// <summary>
    /// Starts the notification under <paramref name="notificationId"/>. Returns true when this call
    /// started it, false when that id was already used, which is the restart dedup. Throws when the
    /// delivery path is unavailable, so the failure stays pending.
    /// </summary>
    Task<bool> SendAsync(string notificationId, string coordinator, string text, CancellationToken ct);
}

/// <summary>
/// Notifies the configured coordinator when a workflow closes because of failure (#488).
///
/// It polls each configured namespace from outside the failed workflow, so the failed run needs no
/// final activity. Only Failed and TimedOut runs are listed; Completed (including a null result),
/// Canceled, Terminated and continue-as-new are never treated as failures. A deliberate UWE
/// <c>fail</c> step closes Failed with the <c>ExplicitFail</c> type and is labelled a business failure.
///
/// Delivery reuses the NotifyCtoWorkflow path that <c>notify_cto</c> already uses. Each failure gets
/// a deterministic notification workflow id, and Temporal's duplicate-id rejection is the persisted
/// "already notified" record, so a restart or a repeat poll does not notify twice. What it confirms is
/// that the notification was handed to that workflow; the agent-side delivery keeps that workflow's
/// existing semantics. A failure that cannot be handed over stays pending with its attempt count and
/// is retried on later polls, up to <see cref="WorkflowFailureNotifierOptions.MaxDeliveryAttempts"/>.
///
/// The notice carries identity, type, status and a cause category only: no failure message, inputs,
/// results or logs. The coordinator reads details through its existing tools and their access rules,
/// and decides what to do. Nothing here restarts, resumes or reserves anything.
/// </summary>
public sealed class WorkflowFailureNotifier : BackgroundService
{
    internal const string NotificationIdPrefix = "workflow-failure-";
    internal const int MaxSendsPerCycle = 10;

    private readonly WorkflowFailureNotifierOptions _options;
    private readonly IReadOnlyList<string> _namespaces;
    private readonly CtoAgentConfigService _ctoConfig;
    private readonly IWorkflowFailureSource _source;
    private readonly IFailureNotificationSender _sender;
    private readonly TimeProvider _time;
    private readonly ILogger<WorkflowFailureNotifier> _logger;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, PendingFailure> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _handled = new(StringComparer.Ordinal);
    private int _sent;
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
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Workflow failure notifier is disabled (WorkflowFailureNotifier:Enabled=false)");
            return;
        }

        _logger.LogInformation("Workflow failure notifier polling {Namespaces} every {Seconds}s",
            string.Join(", ", _namespaces), _options.PollIntervalSeconds);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogError(ex, "Workflow failure notifier cycle failed; retrying next poll");
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, _options.PollIntervalSeconds)), ct)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    /// <summary>One poll: discover new failures, then hand pending ones to the coordinator.</summary>
    public async Task RunCycleAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var since = now.AddHours(-Math.Max(1, _options.LookbackHours));

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
                    // Our own notifications are never notified about.
                    if (run.WorkflowId.StartsWith(NotificationIdPrefix, StringComparison.Ordinal)) continue;
                    if (_handled.ContainsKey(run.Identity) || _pending.ContainsKey(run.Identity)) continue;
                    _pending[run.Identity] = new PendingFailure(run);
                }
            }
        }

        lock (_gate)
        {
            foreach (var old in _handled.Where(h => h.Value < since).Select(h => h.Key).ToList())
                _handled.Remove(old);
        }

        var coordinator = ResolveCoordinator();
        if (coordinator is null)
        {
            if (!_warnedUnconfigured)
            {
                _logger.LogWarning(
                    "Workflow failure notifier has no coordinator (WorkflowFailureNotifier:CoordinatorAgent and " +
                    "FLEET_CTO_AGENT are empty); failures are held, not sent");
                _warnedUnconfigured = true;
            }
            return;
        }
        _warnedUnconfigured = false;

        List<PendingFailure> batch;
        lock (_gate)
        {
            batch = _pending.Values
                .Where(p => !p.Parked)
                .OrderBy(p => p.Run.CloseTime)
                .Take(MaxSendsPerCycle)
                .ToList();
        }

        foreach (var pending in batch)
        {
            ct.ThrowIfCancellationRequested();
            await TrySendAsync(pending, coordinator, ct);
        }
    }

    private async Task TrySendAsync(PendingFailure pending, string coordinator, CancellationToken ct)
    {
        var run = pending.Run;
        if (pending.Cause is null)
        {
            try
            {
                pending.Cause = await _source.CauseAsync(run, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // The notice is still worth sending; the coordinator can look the cause up.
                _logger.LogDebug("Cause lookup failed for {Identity}: {Error}", run.Identity, ex.GetType().Name);
                pending.Cause = "unknown";
            }
        }

        try
        {
            var startedNow = await _sender.SendAsync(
                NotificationId(run), coordinator, ComposeNotice(run, pending.Cause), ct);
            lock (_gate)
            {
                _pending.Remove(run.Identity);
                _handled[run.Identity] = run.CloseTime;
                if (startedNow) _sent++;
            }
            if (startedNow)
                _logger.LogInformation("Workflow failure {Identity} ({Cause}) handed to coordinator {Coordinator}",
                    run.Identity, pending.Cause, coordinator);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            lock (_gate)
            {
                pending.Attempts++;
                pending.LastError = ex.GetType().Name;
                pending.Parked = pending.Attempts >= Math.Max(1, _options.MaxDeliveryAttempts);
            }
            if (pending.Parked)
                _logger.LogError("Workflow failure {Identity} not delivered after {Attempts} attempts ({Error}); parked",
                    run.Identity, pending.Attempts, pending.LastError);
            else
                _logger.LogWarning("Workflow failure {Identity} pending, attempt {Attempts} failed ({Error})",
                    run.Identity, pending.Attempts, pending.LastError);
        }
    }

    private string? ResolveCoordinator()
    {
        var configured = _options.CoordinatorAgent;
        if (string.IsNullOrWhiteSpace(configured)) configured = _ctoConfig.GetCtoAgent();
        return string.IsNullOrWhiteSpace(configured) ? null : configured.Trim();
    }

    /// <summary>Deterministic per failed run; the duplicate-id rejection on it is the dedup record.</summary>
    internal static string NotificationId(ClosedRun run) => $"{NotificationIdPrefix}{run.Namespace}-{run.RunId}";

    internal static string ComposeNotice(ClosedRun run, string cause)
    {
        var business = cause.StartsWith(WorkflowFailureClassifier.ExplicitFailCategory, StringComparison.Ordinal)
            ? " (explicit business failure)" : "";
        var sb = new StringBuilder();
        sb.AppendLine("[workflow-failure] A workflow closed because of failure.");
        sb.AppendLine($"namespace: {run.Namespace}");
        sb.AppendLine($"workflowId: {run.WorkflowId}");
        sb.AppendLine($"runId: {run.RunId}");
        sb.AppendLine($"type: {run.WorkflowType}");
        sb.AppendLine($"status: {run.Status}");
        sb.AppendLine($"cause: {cause}{business}");
        sb.AppendLine($"closed: {run.CloseTime:yyyy-MM-ddTHH:mm:ssZ}");
        sb.AppendLine($"reference: temporal://{run.Namespace}/{Uri.EscapeDataString(run.WorkflowId)}/{run.RunId}");
        sb.AppendLine();
        sb.Append("The failure message, inputs and results are not copied here. Inspect the run with your " +
                  "existing Temporal tools, under their usual access rules, and decide whether to restart, resume " +
                  "or leave it. Nothing was restarted automatically.");
        return sb.ToString();
    }

    /// <summary>Pending/error visibility for the status endpoint. Identity and counts only.</summary>
    public WorkflowFailureNotifierStatus Snapshot()
    {
        lock (_gate)
        {
            return new WorkflowFailureNotifierStatus(
                Enabled: _options.Enabled,
                CoordinatorConfigured: ResolveCoordinator() is not null,
                Sent: _sent,
                Pending: _pending.Values.Where(p => !p.Parked).Select(p => p.ToView()).ToList(),
                Parked: _pending.Values.Where(p => p.Parked).Select(p => p.ToView()).ToList());
        }
    }

    private sealed class PendingFailure(ClosedRun run)
    {
        public ClosedRun Run { get; } = run;
        public string? Cause { get; set; }
        public int Attempts { get; set; }
        public string? LastError { get; set; }
        public bool Parked { get; set; }

        public PendingFailureView ToView() =>
            new(Run.Namespace, Run.WorkflowId, Run.RunId, Run.Status, Attempts, LastError);
    }
}

public sealed record PendingFailureView(
    string Namespace, string WorkflowId, string RunId, string Status, int Attempts, string? LastError);

public sealed record WorkflowFailureNotifierStatus(
    bool Enabled, bool CoordinatorConfigured, int Sent,
    IReadOnlyList<PendingFailureView> Pending, IReadOnlyList<PendingFailureView> Parked);

/// <summary>Turns a close failure into a short category made of type names, never message text.</summary>
public static class WorkflowFailureClassifier
{
    /// <summary>The UWE <c>fail</c> step's error type (#424): a deliberate business failure.</summary>
    public const string ExplicitFailCategory = "explicit_fail";

    public static string Category(Exception? cause) => cause switch
    {
        ApplicationFailureException { ErrorType: "ExplicitFail" } => ExplicitFailCategory,
        ApplicationFailureException a => "application_error:" + Safe(a.ErrorType),
        ActivityFailureException a => "activity_failure:" + Safe(a.ActivityType) + Inner(a.InnerException),
        ChildWorkflowFailureException c => "child_workflow_failure:" + Safe(c.WorkflowType) + Inner(c.InnerException),
        TimeoutFailureException t => "timeout:" + t.TimeoutType,
        CanceledFailureException => "canceled",
        TerminatedFailureException => "terminated",
        null => "unknown",
        _ => Safe(cause.GetType().Name),
    };

    private static string Inner(Exception? inner) => inner switch
    {
        null => "",
        ApplicationFailureException a => "/" + Safe(a.ErrorType),
        TimeoutFailureException t => "/timeout:" + t.TimeoutType,
        _ => "/" + Safe(inner.GetType().Name),
    };

    /// <summary>Type names only: letters, digits and . _ - :, at most 80 characters.</summary>
    internal static string Safe(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unspecified";
        var kept = new string(value.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ':').ToArray());
        if (kept.Length == 0) return "unspecified";
        return kept.Length > 80 ? kept[..80] : kept;
    }
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
        if (run.Status == "TimedOut") return "timeout:workflow_execution";

        var client = await clientFactory.GetClientAsync(run.Namespace);
        try
        {
            // Reads only the close event; the result is never deserialised into the notice.
            await client.GetWorkflowHandle(run.WorkflowId, runId: run.RunId)
                .GetResultAsync<object?>(followRuns: false, rpcOptions: new RpcOptions { CancellationToken = ct });
            return "unknown";
        }
        catch (WorkflowFailedException ex)
        {
            return WorkflowFailureClassifier.Category(ex.InnerException);
        }
    }
}

/// <summary>Starts the existing NotifyCtoWorkflow, the same delivery path <c>notify_cto</c> uses.</summary>
public sealed class TemporalFailureNotificationSender(
    ITemporalClientFactory clientFactory,
    IOptions<WorkflowFailureNotifierOptions> options) : IFailureNotificationSender
{
    public async Task<bool> SendAsync(string notificationId, string coordinator, string text, CancellationToken ct)
    {
        var opts = options.Value;
        var client = await clientFactory.GetClientAsync(opts.NotificationNamespace);
        try
        {
            await client.StartWorkflowAsync(
                TemporalWorkflowDispatcher.NotifyCtoWorkflowType,
                [new { TargetAgent = coordinator, TaskDescription = text }],
                new WorkflowOptions(id: notificationId, taskQueue: opts.NotificationTaskQueue)
                {
                    // A closed or running notification with this id means it was already sent.
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
}
