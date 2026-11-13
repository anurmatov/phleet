using System.Runtime.ExceptionServices;
using Fleet.Temporal.Configuration;
using Fleet.Temporal.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fleet.Temporal.Services;

/// <summary>
/// Keeps delegations alive outside the activity execution that started them, so a bridge restart
/// no longer fails every delegation in flight.
///
/// <para>
/// With <see cref="DurableDelegationOptions.Enabled"/> on, <see cref="Activities.DelegateToAgentActivity"/>
/// hands the delegation to <see cref="StartDelegationAsync"/> and tells Temporal it will be
/// completed later. From then on the record in <see cref="PendingDelegationStore"/> is the whole
/// state. Each pass (<see cref="TickAsync"/>) does, per record, what the activity's wait loop did:
/// heartbeat (which is also how a workflow cancel is noticed and passed on to the agent), re-send
/// the directive every five minutes, and fail the activity when the agent's budget runs out. An
/// agent's reply arrives through <see cref="TryHandleReplyAsync"/>, which continues an incomplete
/// answer or completes the activity.
/// </para>
///
/// <para>
/// What it does not do: outlast a bridge outage longer than the <c>HeartbeatTimeout</c> the
/// workflow scheduled the delegation with. Temporal times such an activity out and the record is
/// dropped the next time it is heartbeated.
/// </para>
///
/// <para>Never logs an instruction or an answer; identifiers and counters only.</para>
/// </summary>
public sealed class DurableDelegationService : BackgroundService
{
    /// <summary>The same interval the activity re-sends a directive on.</summary>
    internal static readonly TimeSpan ResendInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long one publish may take inside a pass or a reply. A broker that has gone away must
    /// not hold up the heartbeats of every other delegation.
    /// </summary>
    internal static readonly TimeSpan PublishTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan MinimumSleep = TimeSpan.FromMilliseconds(200);

    private readonly DurableDelegationOptions _options;
    private readonly IAsyncActivityGateway _gateway;
    private readonly IDelegationRelay _relay;
    private readonly ILogger<DurableDelegationService> _logger;
    private readonly TimeProvider _time;
    private readonly PendingDelegationStore _store;

    // One delegation is acted on at a time: a reply and a pass can otherwise both decide what
    // happens to the same record. Held per record, never across a whole pass.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0);

    public DurableDelegationService(
        IOptions<DurableDelegationOptions> options,
        IAsyncActivityGateway gateway,
        IDelegationRelay relay,
        ILogger<DurableDelegationService> logger,
        TimeProvider? time = null)
    {
        _options = options.Value;
        _gateway = gateway;
        _relay = relay;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _store = new PendingDelegationStore(_options.Enabled ? _options.StateFile : null, logger);

        // Read here, not when the host starts the service: the relay listener takes this service
        // as a dependency, so the records are in memory before the first agent reply is consumed.
        if (_options.Enabled) _store.Load();
    }

    public bool Enabled => _options.Enabled;

    /// <summary>The interval between passes. Replaced in tests.</summary>
    internal TimeSpan TickInterval { get; set; } = TimeSpan.Zero;

    private TimeSpan EffectiveTick =>
        TickInterval > TimeSpan.Zero ? TickInterval : TimeSpan.FromSeconds(Math.Max(1, _options.TickSeconds));

    /// <summary>
    /// Records the delegation and publishes its directive. Returns once both are done; the caller
    /// then tells Temporal the activity completes later. A directive that cannot be published
    /// fails here, with nothing left behind, exactly as the activity failed when it could not
    /// publish.
    /// </summary>
    public async Task StartDelegationAsync(DelegationStart start, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var record = new PendingDelegation
        {
            TaskId = start.TaskId,
            CurrentTaskId = start.TaskId,
            Namespace = start.Namespace,
            WorkflowId = start.WorkflowId,
            RunId = start.RunId,
            WorkflowType = start.WorkflowType,
            ActivityId = start.ActivityId,
            Attempt = start.Attempt,
            TaskToken = Convert.ToBase64String(start.TaskToken),
            Agent = start.Agent,
            CurrentInstruction = start.Instruction,
            Repo = start.Repo,
            BudgetSeconds = (int)Math.Ceiling(start.Budget.TotalSeconds),
            RetryOnIncomplete = start.RetryOnIncomplete,
            MaxIncompleteRetries = start.MaxIncompleteRetries,
            CreatedAt = now,
            WaitStartedAt = now,
            LastPublishedAt = DateTimeOffset.MinValue,
            // So a pass that runs while the directive is on its way does not send it as well.
            Publishing = true,
        };

        await _gate.WaitAsync(ct);
        try
        {
            if (_store.TryGet(start.TaskId, out var prior))
            {
                // Task ids are stable across Temporal retries, so a later attempt lands on its
                // predecessor's key. The newest attempt owns it, as in the in-memory registry.
                _logger.LogWarning(
                    "Delegation taskId={TaskId} replaced a pending record (attempt {PriorAttempt} → {Attempt})",
                    start.TaskId, prior.Attempt, start.Attempt);
            }

            // Recorded before it is published, so a reply can never arrive for a delegation the
            // bridge does not know about.
            _store.Put(record);
        }
        finally
        {
            _gate.Release();
        }

        // Outside the gate: a slow broker must not hold up the other delegations' heartbeats.
        ExceptionDispatchInfo? failure = null;
        try
        {
            await _relay.PublishDirectiveAsync(record, ct);
        }
        catch (Exception ex)
        {
            failure = ExceptionDispatchInfo.Capture(ex);
        }

        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            record.Publishing = false;
            // A reply can have settled it already; then there is nothing left to update.
            var stillOurs = _store.TryGet(start.TaskId, out var current) && ReferenceEquals(current, record);

            if (failure is not null)
            {
                if (stillOurs) _store.Remove(record.TaskId);
                _logger.LogError(failure.SourceException, "Failed to publish directive to agent {Agent}", start.Agent);
                failure.Throw();
            }

            if (stillOurs)
            {
                record.LastPublishedAt = _time.GetUtcNow();
                _store.Save();
            }
        }
        finally
        {
            _gate.Release();
        }

        _logger.LogInformation(
            "Directive published to {Agent}, delegation taskId={TaskId} is pending (durable={Durable})",
            start.Agent, start.TaskId, _store.Durable);
        Wake();
    }

    /// <summary>
    /// Offers an agent reply to the pending delegations. <see cref="DurableReplyOutcome.NotPending"/>
    /// means it belongs to none of them and the caller treats it as it always did.
    /// </summary>
    public async Task<DurableReplyOutcome> TryHandleReplyAsync(string taskId, AgentTaskResult result, CancellationToken ct)
    {
        if (!Enabled) return DurableReplyOutcome.NotPending;

        await _gate.WaitAsync(ct);
        try
        {
            if (!_store.TryGet(taskId, out var record) && !_store.TryGet(RootOf(taskId), out record))
                return DurableReplyOutcome.NotPending;

            if (!string.Equals(taskId, record.CurrentTaskId, StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "Reply for taskId={TaskId} ignored — the delegation has moved on to {CurrentTaskId}",
                    taskId, record.CurrentTaskId);
                return DurableReplyOutcome.Ignored;
            }

            if (record.HasResult || record.TimeoutMessage is not null || record.CancelSent)
            {
                _logger.LogInformation("Repeat reply for taskId={TaskId} ignored — the delegation is already settled", taskId);
                return DurableReplyOutcome.Ignored;
            }

            var attempt = record.IncompleteAttempt;
            var incomplete = record.RetryOnIncomplete && result.IsIncomplete;

            if (attempt > 0)
                record.AccumulatedText += "\n" + result.Text;
            else if (incomplete)
                record.AccumulatedText = result.Text;

            if (incomplete && attempt < record.MaxIncompleteRetries)
                return await ContinueAsync(record, attempt + 1, ct);

            if (incomplete)
            {
                _logger.LogWarning(
                    "Agent {Agent} still incomplete after {Max} retries, returning accumulated text. taskId={TaskId}",
                    record.Agent, record.MaxIncompleteRetries, record.TaskId);
            }

            // The first answer is returned as it came; once anything has been accumulated the text
            // is the whole of it. This is the activity's own rule.
            record.ResultStatus = result.Status;
            record.ResultText = attempt > 0 || incomplete ? record.AccumulatedText : result.Text;
            _store.Save();

            return await DeliverAsync(record, ct) ? DurableReplyOutcome.Completed : DurableReplyOutcome.Stored;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<DurableReplyOutcome> ContinueAsync(PendingDelegation record, int attempt, CancellationToken ct)
    {
        _logger.LogWarning(
            "Agent {Agent} returned incomplete response (attempt {Attempt}/{Max}), retrying with continuation prompt. taskId={TaskId}",
            record.Agent, attempt, record.MaxIncompleteRetries, record.TaskId);

        record.IncompleteAttempt = attempt;
        record.CurrentTaskId = $"{record.TaskId}/incomplete-retry-{attempt}";
        record.CurrentInstruction =
            $"continue where you left off — your previous response was truncated due to context limits.\n\n" +
            $"Previous incomplete response:\n{record.AccumulatedText}\n\n" +
            $"Resume and complete the task.";
        record.WaitStartedAt = _time.GetUtcNow();
        record.LastPublishedAt = DateTimeOffset.MinValue;
        _store.Save();

        if (!await TryPublishAsync(record, ct))
        {
            // The continuation is recorded and a later pass sends it. The activity used to fail
            // here, which threw away an answer that was already partly in hand.
            _logger.LogWarning(
                "Continuation directive for agent {Agent} (attempt {Attempt}, taskId={TaskId}) is recorded but not yet published",
                record.Agent, attempt, record.TaskId);
        }

        return DurableReplyOutcome.Continued;
    }

    /// <summary>
    /// Completes the activity with the recorded answer. False while Temporal cannot be told; the
    /// answer stays in the record.
    /// </summary>
    private async Task<bool> DeliverAsync(PendingDelegation record, CancellationToken ct)
    {
        var outcome = await _gateway.CompleteAsync(
            record, new AgentTaskResult(record.ResultText ?? "", record.ResultStatus!), ct);

        switch (outcome)
        {
            case ActivityCallOutcome.Ok:
                _store.Remove(record.TaskId);
                _logger.LogInformation(
                    "Completed delegation taskId={TaskId} with status={Status}", record.TaskId, record.ResultStatus);
                return true;

            case ActivityCallOutcome.Unavailable:
                _logger.LogWarning(
                    "Answer for delegation taskId={TaskId} is recorded but Temporal could not be told — a later pass delivers it",
                    record.TaskId);
                return false;

            default:
                Drop(record, "its answer arrived");
                return true;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled) return;

        _logger.LogInformation(
            "Durable delegation is on: state {Path}, {Count} delegation(s) resumed, pass every {Tick}s",
            _options.StateFile, _store.Count, EffectiveTick.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Durable delegation pass failed; the next one runs as scheduled");
            }

            try
            {
                // Woken early when a delegation starts, so its budget is counted in the next sleep.
                await _wake.WaitAsync(await NextSleepAsync(stoppingToken), stoppingToken);
                while (_wake.Wait(0)) { }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// The next pass is due after the tick, or sooner when a budget runs out before then, so a
    /// timeout is reported when it happens and not up to a tick late.
    /// </summary>
    private async Task<TimeSpan> NextSleepAsync(CancellationToken ct)
    {
        var sleep = EffectiveTick;
        var records = await SnapshotRecordsAsync(ct);
        var now = _time.GetUtcNow();
        foreach (var record in records)
        {
            if (record.HasResult || record.TimeoutMessage is not null) continue;
            var untilDeadline = record.Deadline - now;
            if (untilDeadline < sleep) sleep = untilDeadline;
        }

        return sleep < MinimumSleep ? MinimumSleep : sleep;
    }

    /// <summary>
    /// One pass over every pending delegation, in two rounds. Everything Temporal needs comes
    /// first (answers it has not been told, budgets, heartbeats), then the directives that are due
    /// to be sent again. They are separate so that a broker that is away costs a pass one bounded
    /// publish attempt and no heartbeat, and a Temporal namespace that is away is asked once per
    /// pass, not once per delegation.
    /// </summary>
    internal async Task TickAsync(CancellationToken ct)
    {
        var taskIds = await TaskIdsAsync(ct);
        var away = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var taskId in taskIds)
        {
            ct.ThrowIfCancellationRequested();
            await _gate.WaitAsync(ct);
            try
            {
                if (_store.TryGet(taskId, out var record) && !away.Contains(record.Namespace)
                    && !await ProcessAsync(record, ct))
                {
                    away.Add(record.Namespace);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One delegation's trouble must not stop the others being heartbeated.
                _logger.LogError(ex, "Durable delegation pass failed for taskId={TaskId}", taskId);
            }
            finally
            {
                _gate.Release();
            }
        }

        foreach (var taskId in taskIds)
        {
            ct.ThrowIfCancellationRequested();
            await _gate.WaitAsync(ct);
            try
            {
                if (!_store.TryGet(taskId, out var record) || !DueForPublish(record)) continue;

                _logger.LogInformation(
                    record.LastPublishedAt == DateTimeOffset.MinValue
                        ? "Sending directive to {Agent} (taskId={TaskId}) — it had not been published yet"
                        : "Re-sending directive to {Agent} (taskId={TaskId}) — agent may have restarted",
                    record.Agent, record.CurrentTaskId);

                // The broker is the same for all of them: one failure ends the round.
                if (!await TryPublishAsync(record, ct)) break;
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private bool DueForPublish(PendingDelegation record) =>
        !record.Publishing && !record.HasResult && record.TimeoutMessage is null && !record.CancelSent
        && _time.GetUtcNow() - record.LastPublishedAt >= ResendInterval;

    /// <summary>Publishes the record's current directive within <see cref="PublishTimeout"/>.</summary>
    private async Task<bool> TryPublishAsync(PendingDelegation record, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(PublishTimeout);
        try
        {
            await _relay.PublishDirectiveAsync(record, timeout.Token);
            record.LastPublishedAt = _time.GetUtcNow();
            _store.Save();
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Failed to publish directive to agent {Agent} for taskId={TaskId}: {Error}",
                record.Agent, record.CurrentTaskId, ex.GetType().Name);
            return false;
        }
    }

    /// <summary>
    /// What Temporal needs for one record. False when Temporal could not be reached, which the
    /// pass takes as true of the record's whole namespace.
    /// </summary>
    private async Task<bool> ProcessAsync(PendingDelegation record, CancellationToken ct)
    {
        // An answer Temporal could not be told about comes first: it is the delegation's outcome.
        if (record.HasResult)
            return await DeliverAsync(record, ct);

        var now = _time.GetUtcNow();
        if (record.TimeoutMessage is not null || now >= record.Deadline)
            return await TimeOutAsync(record, now, ct);

        switch (await _gateway.HeartbeatAsync(record, ct))
        {
            case ActivityCallOutcome.CancelRequested:
                return await CancelAsync(record, ct);

            case ActivityCallOutcome.Gone:
                // Timed out at the server, or its workflow closed. The agent may still be working
                // for nobody, which is what the activity's own cancellation path stopped.
                if (!record.CancelSent) await _relay.SendCancelAsync(record);
                Drop(record, "it was heartbeated");
                return true;

            case ActivityCallOutcome.Unavailable:
                // Logged by the gateway. The record stays; if Temporal stays away for longer than
                // the activity's heartbeat timeout the next answer is Gone.
                return false;

            default:
                return true;
        }
    }

    private async Task<bool> TimeOutAsync(PendingDelegation record, DateTimeOffset now, CancellationToken ct)
    {
        if (record.TimeoutMessage is null)
        {
            // Measured once, here, and kept: a second attempt to tell Temporal must not report a
            // longer wait than the agent was actually given.
            record.TimeoutMessage =
                $"Agent {record.Agent} did not respond — elapsed {Format(now - record.WaitStartedAt)} " +
                $"(budget {Format(record.Budget)}, taskId={record.CurrentTaskId}, workflowId={record.WorkflowId})";
            _store.Save();
            _logger.LogWarning("{Message}", record.TimeoutMessage);
            await _relay.ReportTimeoutAsync(record, record.TimeoutMessage);
        }

        switch (await _gateway.FailAsync(record, new TimeoutException(record.TimeoutMessage), ct))
        {
            case ActivityCallOutcome.Unavailable:
                return false; // a later pass tells Temporal; the escalation target has been told already
            case ActivityCallOutcome.Ok:
                _store.Remove(record.TaskId);
                return true;
            default:
                Drop(record, "its timeout was reported");
                return true;
        }
    }

    private async Task<bool> CancelAsync(PendingDelegation record, CancellationToken ct)
    {
        if (!record.CancelSent)
        {
            _logger.LogWarning(
                "Activity cancelled for taskId={TaskId}, sending /cancel to agent {Agent}", record.CurrentTaskId, record.Agent);
            await _relay.SendCancelAsync(record);
            record.CancelSent = true;
            _store.Save();
        }

        // Unavailable keeps the record: the next heartbeat says "cancel requested" again and only
        // this report is repeated.
        if (await _gateway.ReportCancellationAsync(record, ct) == ActivityCallOutcome.Unavailable)
            return false;

        _store.Remove(record.TaskId);
        return true;
    }

    /// <summary>
    /// The one place a record is abandoned: Temporal no longer has its activity attempt. One line,
    /// identifiers only.
    /// </summary>
    private void Drop(PendingDelegation record, string when)
    {
        _store.Remove(record.TaskId);
        _logger.LogWarning(
            "Dropped pending delegation taskId={TaskId} (workflow {WorkflowId}, activity {ActivityId}, agent {Agent}): Temporal no longer had the activity when {When}",
            record.TaskId, record.WorkflowId, record.ActivityId, record.Agent, when);
    }

    /// <summary>
    /// Identity, counters and times of every pending delegation: no instruction, no answer, no
    /// token.
    /// </summary>
    public async Task<DurableDelegationSnapshot> SnapshotAsync(CancellationToken ct = default)
    {
        var records = await SnapshotRecordsAsync(ct);
        return new DurableDelegationSnapshot(
            Enabled,
            Enabled && _store.Durable,
            [.. records.Select(r => new PendingDelegationView(
                r.TaskId, r.CurrentTaskId, r.Agent, r.Namespace, r.WorkflowId, r.RunId, r.ActivityId,
                r.IncompleteAttempt, r.CreatedAt, r.WaitStartedAt, r.Deadline,
                r.LastPublishedAt == DateTimeOffset.MinValue ? null : r.LastPublishedAt,
                r.HasResult ? "answer-recorded" : r.TimeoutMessage is not null ? "timed-out" : r.CancelSent ? "cancelling" : "waiting"))]);
    }

    private async Task<List<PendingDelegation>> SnapshotRecordsAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return [.. _store.All.Select(r => r.Clone())]; }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyList<string>> TaskIdsAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return _store.TaskIds; }
        finally { _gate.Release(); }
    }

    private void Wake() => _wake.Release();

    /// <summary><c>x/incomplete-retry-N</c> → <c>x</c>; anything else is returned as it is.</summary>
    internal static string RootOf(string taskId)
    {
        const string marker = "/incomplete-retry-";
        var at = taskId.LastIndexOf(marker, StringComparison.Ordinal);
        if (at <= 0) return taskId;
        var suffix = taskId.AsSpan(at + marker.Length);
        return suffix.Length > 0 && int.TryParse(suffix, out _) ? taskId[..at] : taskId;
    }

    private static string Format(TimeSpan span) =>
        span.TotalMinutes >= 1
            ? $"{span.TotalMinutes:0.0}m"
            : $"{span.TotalSeconds:0.0}s";
}

public sealed record DurableDelegationSnapshot(bool Enabled, bool Durable, IReadOnlyList<PendingDelegationView> Pending);

public sealed record PendingDelegationView(
    string TaskId,
    string CurrentTaskId,
    string Agent,
    string Namespace,
    string WorkflowId,
    string RunId,
    string ActivityId,
    int IncompleteAttempt,
    DateTimeOffset CreatedAt,
    DateTimeOffset WaitStartedAt,
    DateTimeOffset Deadline,
    DateTimeOffset? LastPublishedAt,
    string State);
