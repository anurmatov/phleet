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
/// state, and two things work from it that never wait for each other:
/// </para>
///
/// <list type="bullet">
/// <item>
/// <b>The pass</b> (<see cref="TickAsync"/>) talks to Temporal only: it heartbeats every pending
/// activity, fails one whose budget ran out, reports a cancellation, and delivers an answer
/// Temporal could not be told earlier. What it learns it writes to the record.
/// </item>
/// <item>
/// <b>The relay lane</b> sends what a record owes the outside: the directive (first time,
/// continuation, five-minute re-send), a cancel to the agent, a timeout notice to the escalation
/// target. It reads what is owed from the record, sends off the pass, and writes back what was
/// sent.
/// </item>
/// </list>
///
/// <para>
/// They are separate because a heartbeat has a deadline and a publish does not. When the relay
/// sends ran inside the pass, ten cancels of ten seconds each put the eleventh delegation's
/// heartbeat a hundred seconds out, past its ninety-second heartbeat timeout, and Temporal failed
/// a healthy activity. A slow or hung relay now delays relay effects and nothing else.
/// </para>
///
/// <para>
/// The lane keeps one order that the agent cannot keep for itself. The agent matches a cancel by
/// task id alone, and a later attempt of a step reuses its predecessor's task id, so a directive
/// is never published while a cancel for the same task id is still out or may still arrive
/// (<see cref="Fenced"/>).
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
    /// Relay calls that have been made and have not ended, at most. Each is a broker connection
    /// or an HTTP request, and a bridge that has just restarted can owe one per pending
    /// delegation. A call that is past its time still counts until it has really ended.
    /// </summary>
    internal const int MaxConcurrentRelaySends = 4;

    private static readonly TimeSpan MinimumSleep = TimeSpan.FromMilliseconds(200);

    private enum RelayEffect { None, Publish, Cancel, TimeoutNotice, PriorCancel }

    /// <summary>One relay call that has been made for one record and has not ended.</summary>
    private sealed class RelaySend
    {
        public required PendingDelegation Record { get; init; }
        public required RelayEffect Effect { get; init; }

        /// <summary>The record's task id: what directives and cancels for it are ordered by.</summary>
        public required string Root { get; init; }

        /// <summary>The record as it was when the call was decided on. The relay works from this.</summary>
        public required PendingDelegation Snapshot { get; init; }

        public PriorCancel? Prior { get; init; }

        /// <summary>Stops the call when its time is up, or when the service stops.</summary>
        public required CancellationTokenSource Cancellation { get; init; }

        public Task Completion { get; set; } = Task.CompletedTask;

        public bool IsCancel => Effect is RelayEffect.Cancel or RelayEffect.PriorCancel;
    }

    private readonly DurableDelegationOptions _options;
    private readonly IAsyncActivityGateway _gateway;
    private readonly IDelegationRelay _relay;
    private readonly ILogger<DurableDelegationService> _logger;
    private readonly TimeProvider _time;
    private readonly PendingDelegationStore _store;

    // Guards the store and the record fields. A reply, the pass and a finished relay send can all
    // want the same record. Held for in-memory work and for a call to Temporal; NEVER across a
    // relay send.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0);

    // The relay lane, guarded by _gate. _sends holds every call that has been made and has not
    // ended, keyed by record INSTANCE: one call per record at a time, and a record that was
    // replaced under its task id keeps its entry until its call has really ended. Its size is
    // what MaxConcurrentRelaySends bounds.
    private readonly Dictionary<PendingDelegation, RelaySend> _sends = new(ReferenceEqualityComparer.Instance);
    // Task ids for which a cancel ended without the bridge knowing whether it will still arrive,
    // and until when a directive for that task id is held.
    private readonly Dictionary<string, DateTimeOffset> _unsettledCancels = new(StringComparer.Ordinal);
    // Whether the last relay pass found no room, so it is said once.
    private bool _saturated;
    // Records whose activity is publishing their first directive right now, outside the lane.
    // They are calls that have not ended like any other, for ordering: a successor's directive
    // and a cancel carried for the predecessor both wait for them.
    private readonly HashSet<PendingDelegation> _startingPublishes = new(ReferenceEqualityComparer.Instance);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _pumpLock = new();
    // True while a pass is going round. The relay lane starts nothing new meanwhile: the pass is
    // the part with a deadline, and what it finds owed is sent when it has finished.
    private volatile bool _passInProgress;
    // Guarded by _gate: whether the last relay pass was held back, so it is said once.
    private bool _heldForConfiguration;
    private bool _pumpRunning;
    private bool _pumpRequested;
    private Task _pump = Task.CompletedTask;

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

    /// <summary>
    /// How long a directive may take to publish. A cancel or a timeout notice gets three times
    /// this, which is above the relay's own limits for them. When the time is up the call is told
    /// to stop. It is not abandoned: its place, its record's turn and the hold on its task id are
    /// given back when it has ended, because until then it may still deliver.
    /// </summary>
    internal TimeSpan RelaySendTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a directive for a task id is held after a cancel for that task id ended
    /// <see cref="CancelDelivery.Unknown"/>. The cancel goes bridge → orchestrator → agent over
    /// HTTP and the orchestrator gives the agent ten seconds, so a cancel the bridge stopped
    /// waiting for can be applied that much later, plus the orchestrator's own time. This is a
    /// bound taken from those limits, not a proof: nothing in the cancel says which execution it
    /// was for.
    /// </summary>
    internal TimeSpan CancelSettleTime { get; set; } = TimeSpan.FromSeconds(30);

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
            // Minted here, once. Every directive and every cancel for this record carries it, and
            // it is in the file before anything is sent.
            ExecutionId = Guid.NewGuid().ToString("N"),
            CurrentInstruction = start.Instruction,
            Repo = start.Repo,
            BudgetSeconds = (int)Math.Ceiling(start.Budget.TotalSeconds),
            RetryOnIncomplete = start.RetryOnIncomplete,
            MaxIncompleteRetries = start.MaxIncompleteRetries,
            CreatedAt = now,
            WaitStartedAt = now,
            LastPublishedAt = DateTimeOffset.MinValue,
        };

        bool held;
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

                // A cancel the predecessor still owes is not dropped with it and is not left to
                // arrive whenever it does: this record carries it and sends it first.
                record.PriorCancels.AddRange(prior.PriorCancels);
                if (prior.CancelRequested && !prior.CancelSent
                    && !record.PriorCancels.Any(p =>
                        p.Agent == prior.Agent && p.TaskId == prior.CurrentTaskId && p.ExecutionId == prior.ExecutionId))
                {
                    // With the predecessor's execution id, not this record's: it is the
                    // predecessor's work that is to stop.
                    record.PriorCancels.Add(new PriorCancel
                    {
                        Agent = prior.Agent, TaskId = prior.CurrentTaskId, ExecutionId = prior.ExecutionId,
                    });
                }

                // And a hold the predecessor was under is still a hold.
                record.PublishNotBefore = prior.PublishNotBefore;
            }

            if (_unsettledCancels.TryGetValue(start.TaskId, out var until) && until > record.PublishNotBefore)
                record.PublishNotBefore = until;

            // The directive goes out here and now unless a cancel for the same task id is owed,
            // still out, or may still arrive. Then the relay lane publishes it when that is over,
            // and the activity does not wait: the record is heartbeated from the next pass on.
            held = record.PriorCancels.Count > 0 || now < record.PublishNotBefore || Fenced(record, now);
            record.Publishing = !held;
            if (!held) _startingPublishes.Add(record);

            // Recorded before it is published, so a reply can never arrive for a delegation the
            // bridge does not know about.
            _store.Put(record);
        }
        finally
        {
            _gate.Release();
        }

        if (held)
        {
            _logger.LogWarning(
                "Delegation taskId={TaskId} is pending; its directive to {Agent} is held until a cancel for the same task id has finished (durable={Durable})",
                start.TaskId, start.Agent, _store.Durable);
            Wake();
            RequestRelayPass();
            return;
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
            _startingPublishes.Remove(record);
            // Whatever was waiting for this call to end.
            RequestRelayPass();
            // A reply can have settled it already; then there is nothing left to update.
            var stillOurs = _store.TryGet(start.TaskId, out var current) && ReferenceEquals(current, record);

            if (failure is not null)
            {
                if (stillOurs) _store.Remove(record.TaskId);
                _logger.LogError(failure.SourceException, "Failed to publish directive to agent {Agent}", start.Agent);
                failure.Throw();
            }

            if (stillOurs && string.Equals(record.CurrentTaskId, start.TaskId, StringComparison.Ordinal))
            {
                record.LastPublishedAt = _time.GetUtcNow();
                _store.Save();
            }
            else if (stillOurs)
            {
                // A continuation was recorded while the first directive was on its way. It has not
                // been published, and the relay lane could not send it until now.
                RequestRelayPass();
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

            if (record.HasResult || record.TimeoutMessage is not null || record.CancelRequested || record.TemporalSettled)
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
                return Continue(record, attempt + 1);

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

    /// <summary>
    /// Records the continuation and leaves the sending to the relay lane. The reply that caused it
    /// is settled as soon as the record is written: the continuation is owed from then on, and a
    /// broker that is away delays it without holding the listener or the pass.
    /// </summary>
    private DurableReplyOutcome Continue(PendingDelegation record, int attempt)
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
        record.PublishRetryAfter = default;
        _store.Save();

        RequestRelayPass();
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
                LogGone(record, "its answer arrived");
                _store.Remove(record.TaskId);
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

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Relay sends in progress are told to stop; what they had not sent is still in the
        // records and is sent after the next start.
        await _stopping.CancelAsync();
        await base.StopAsync(cancellationToken);
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
            if (record.HasResult || record.TimeoutMessage is not null || record.CancelRequested || record.TemporalSettled)
                continue;
            var untilDeadline = record.Deadline - now;
            if (untilDeadline < sleep) sleep = untilDeadline;
        }

        return sleep < MinimumSleep ? MinimumSleep : sleep;
    }

    // ── the pass: Temporal only ───────────────────────────────────────────────

    /// <summary>
    /// One pass over every pending delegation: what Temporal needs for each, and nothing else. It
    /// makes no relay send and waits for none, its own record's included, so the time a pass takes
    /// is bounded by Temporal alone (one bounded call per record, and a namespace that is away is
    /// asked once per pass, not once per delegation). Whatever the pass learns that the relay has
    /// to act on is written to the record, and the relay lane is told there is work.
    /// </summary>
    internal async Task TickAsync(CancellationToken ct)
    {
        _passInProgress = true;
        try
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
        }
        finally
        {
            _passInProgress = false;
            // What this pass found owed, re-sends that have come due, and anything a failed or
            // stalled send left owing.
            RequestRelayPass();
        }
    }

    /// <summary>
    /// What Temporal needs for one record. False when Temporal could not be reached, which the
    /// pass takes as true of the record's whole namespace.
    /// </summary>
    private async Task<bool> ProcessAsync(PendingDelegation record, CancellationToken ct)
    {
        // Temporal has been told everything; the record is waiting for the relay lane only.
        if (record.TemporalSettled)
        {
            RemoveIfFinished(record);
            return true;
        }

        // An answer Temporal could not be told about comes first: it is the delegation's outcome.
        if (record.HasResult)
            return await DeliverAsync(record, ct);

        if (record.TimeoutMessage is null && !record.CancelRequested)
        {
            var now = _time.GetUtcNow();
            if (now >= record.Deadline)
            {
                // Measured once, here, and kept: a second attempt to tell Temporal must not
                // report a longer wait than the agent was actually given. Writing it down is also
                // what makes the notice owed; the relay lane sends it.
                record.TimeoutMessage =
                    $"Agent {record.Agent} did not respond — elapsed {Format(now - record.WaitStartedAt)} " +
                    $"(budget {Format(record.Budget)}, taskId={record.CurrentTaskId}, workflowId={record.WorkflowId})";
                _store.Save();
                _logger.LogWarning("{Message}", record.TimeoutMessage);
            }
        }

        if (record.TimeoutMessage is not null)
            return Settle(record, await _gateway.FailAsync(record, new TimeoutException(record.TimeoutMessage), ct), "its timeout was reported");

        if (record.CancelRequested)
            return Settle(record, await _gateway.ReportCancellationAsync(record, ct), "its cancellation was reported");

        switch (await _gateway.HeartbeatAsync(record, ct))
        {
            case ActivityCallOutcome.CancelRequested:
                _logger.LogWarning(
                    "Activity cancelled for taskId={TaskId}, agent {Agent} will be told to stop", record.CurrentTaskId, record.Agent);
                record.CancelRequested = true;
                _store.Save();
                return Settle(record, await _gateway.ReportCancellationAsync(record, ct), "its cancellation was reported");

            case ActivityCallOutcome.Gone:
                // Timed out at the server, or its workflow closed. The agent may still be working
                // for nobody, which is what the activity's own cancellation path stopped.
                record.CancelRequested = true;
                return Settle(record, ActivityCallOutcome.Gone, "it was heartbeated");

            case ActivityCallOutcome.Unavailable:
                // Logged by the gateway. The record stays; if Temporal stays away for longer than
                // the activity's heartbeat timeout the next answer is Gone.
                return false;

            default:
                return true;
        }
    }

    /// <summary>
    /// Applies Temporal's answer to the call that ends the activity. Anything but "could not be
    /// reached" means Temporal needs nothing more: the record stops being heartbeated and goes as
    /// soon as the relay lane has sent what it still owes.
    /// </summary>
    private bool Settle(PendingDelegation record, ActivityCallOutcome outcome, string when)
    {
        if (outcome == ActivityCallOutcome.Unavailable) return false;

        if (outcome != ActivityCallOutcome.Ok) LogGone(record, when);

        record.TemporalSettled = true;
        _store.Save();
        RemoveIfFinished(record);
        return true;
    }

    private void RemoveIfFinished(PendingDelegation record)
    {
        if (record.Finished) _store.Remove(record.TaskId);
    }

    /// <summary>
    /// The one line for a record Temporal no longer has the activity of. Identifiers only.
    /// </summary>
    private void LogGone(PendingDelegation record, string when) =>
        _logger.LogWarning(
            "Dropped pending delegation taskId={TaskId} (workflow {WorkflowId}, activity {ActivityId}, agent {Agent}): Temporal no longer had the activity when {When}",
            record.TaskId, record.WorkflowId, record.ActivityId, record.Agent, when);

    // ── the relay lane ────────────────────────────────────────────────────────

    /// <summary>What the record owes the relay right now. At most one thing at a time.</summary>
    private RelayEffect Owed(PendingDelegation record, DateTimeOffset now)
    {
        if (record.Publishing) return RelayEffect.None;
        // Before anything of its own: these belong to executions this record replaced.
        if (record.PriorCancels.Count > 0) return RelayEffect.PriorCancel;
        if (record.CancelRequested) return record.CancelSent ? RelayEffect.None : RelayEffect.Cancel;
        if (record.TimeoutMessage is not null) return record.TimeoutReported ? RelayEffect.None : RelayEffect.TimeoutNotice;
        if (record.HasResult || record.TemporalSettled) return RelayEffect.None;

        return now - record.LastPublishedAt >= ResendInterval
               && now >= record.PublishRetryAfter
               && now >= record.PublishNotBefore
            ? RelayEffect.Publish
            : RelayEffect.None;
    }

    /// <summary>
    /// Whether a directive for this record's task id must wait.
    ///
    /// <para>
    /// The agent matches a cancel by task id and nothing else, and a task id is reused: a later
    /// attempt of the same step gets the id its predecessor had. So a cancel meant for one
    /// execution stops whatever is running under that id when it arrives. The only place that can
    /// be prevented without the agent's help is here, by order: a directive is not published
    /// while a cancel for the same task id is still out, or may still arrive.
    /// </para>
    ///
    /// <list type="bullet">
    /// <item>A cancel call for the task id has not ended. It does not matter which record it was
    /// made for, or how long ago; a call that is past its time has been told to stop and is
    /// waited for all the same, because a send that has left cannot be recalled.</item>
    /// <item>Any call made for an earlier record under the task id has not ended: a directive of
    /// the predecessor's arriving after this one would be taken for it.</item>
    /// <item>A cancel for the task id ended <see cref="CancelDelivery.Unknown"/> less than
    /// <see cref="CancelSettleTime"/> ago.</item>
    /// </list>
    /// </summary>
    private bool Fenced(PendingDelegation record, DateTimeOffset now)
    {
        foreach (var call in _sends.Values)
        {
            if (!string.Equals(call.Root, record.TaskId, StringComparison.Ordinal)) continue;
            if (call.IsCancel || !ReferenceEquals(call.Record, record)) return true;
        }

        if (PredecessorDirectiveIsOut(record)) return true;

        return _unsettledCancels.TryGetValue(record.TaskId, out var until) && now < until;
    }

    /// <summary>
    /// A directive published for an earlier record under this task id has not ended. A cancel
    /// carried for that record waits for it too: sent first, it would arrive ahead of the
    /// directive it is meant to stop.
    /// </summary>
    private bool PredecessorDirectiveIsOut(PendingDelegation record)
    {
        foreach (var starting in _startingPublishes)
        {
            if (!ReferenceEquals(starting, record) && string.Equals(starting.TaskId, record.TaskId, StringComparison.Ordinal))
                return true;
        }

        foreach (var call in _sends.Values)
        {
            if (call.Effect == RelayEffect.Publish && !ReferenceEquals(call.Record, record)
                && string.Equals(call.Root, record.TaskId, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Asks for a relay pass. One pump runs at a time; a request made while it runs makes it go
    /// round once more, so nothing asked for is missed and nothing piles up.
    /// </summary>
    private void RequestRelayPass()
    {
        if (!Enabled) return;

        lock (_pumpLock)
        {
            _pumpRequested = true;
            if (_pumpRunning) return;
            _pumpRunning = true;
            _pump = Task.Run(PumpRelayAsync);
        }
    }

    private async Task PumpRelayAsync()
    {
        while (true)
        {
            lock (_pumpLock)
            {
                if (!_pumpRequested || _stopping.IsCancellationRequested)
                {
                    _pumpRunning = false;
                    return;
                }

                _pumpRequested = false;
            }

            try
            {
                await RelayPassAsync(_stopping.Token);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                // The loop's own check ends it.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Durable delegation relay pass failed; the next pass tries again");
            }
        }
    }

    /// <summary>
    /// Makes a call for every record that owes one, as far as there is room.
    ///
    /// <para>
    /// Nothing waits in a queue. A call is either made now, in this method, or not made: what is
    /// not made stays owed in its record and is looked at afresh on the next relay pass, which
    /// every call asks for when it ends. So there is no moment between deciding to send something
    /// and sending it in which the record can change under a stale copy.
    /// </para>
    ///
    /// <para>
    /// <see cref="MaxConcurrentRelaySends"/> counts calls that have been made and have not ended.
    /// A call that is past its time is told to stop, and goes on counting until it has: its place
    /// is not handed to another call while it may still be using a connection. One call per record
    /// at a time, for the same reason, so a record's effects reach the relay in the order its
    /// state asked for them.
    /// </para>
    /// </summary>
    private async Task RelayPassAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // The pass asks for a relay pass when it ends, so nothing is missed by standing aside.
            if (_passInProgress) return;

            // Nothing is sent until the relay knows where to send. Everything owed stays owed in
            // the records, and every pass asks again.
            if (!_relay.Ready)
            {
                if (!_heldForConfiguration)
                    _logger.LogInformation(
                        "Durable delegation relay sends are held until the bridge has its configuration from the orchestrator");
                _heldForConfiguration = true;
                return;
            }

            if (_heldForConfiguration)
                _logger.LogInformation("Durable delegation relay sends resumed");
            _heldForConfiguration = false;

            var now = _time.GetUtcNow();
            foreach (var root in _unsettledCancels.Where(q => now >= q.Value).Select(q => q.Key).ToList())
                _unsettledCancels.Remove(root);

            foreach (var record in _store.All)
            {
                if (_sends.Count >= MaxConcurrentRelaySends)
                {
                    if (!_saturated)
                        _logger.LogWarning(
                            "Durable delegation relay: {Count} sends have not finished; nothing more is sent until one does. What is owed stays in the records",
                            _sends.Count);
                    _saturated = true;
                    return;
                }

                if (_sends.ContainsKey(record)) continue;
                if (PrepareCall(record, now) is not { } send) continue;

                _sends[record] = send;
                send.Completion = RunSendAsync(send);
            }

            _saturated = false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The call the record's state asks for now, with a copy of the record taken at this moment
    /// for the relay to work from, or null when nothing is owed or it has to wait. Under the gate.
    /// </summary>
    private RelaySend? PrepareCall(PendingDelegation record, DateTimeOffset now)
    {
        var effect = Owed(record, now);
        if (effect == RelayEffect.None) return null;
        if (effect == RelayEffect.Publish && Fenced(record, now)) return null;

        var snapshot = record.Clone();
        PriorCancel? prior = null;
        if (effect == RelayEffect.PriorCancel)
        {
            prior = record.PriorCancels[0];
            // The cancel a predecessor owed, addressed as the predecessor would have addressed it.
            snapshot.Agent = prior.Agent;
            snapshot.CurrentTaskId = prior.TaskId;
            snapshot.ExecutionId = prior.ExecutionId;

            // The predecessor's own call for it may still be out; one is enough.
            if (_sends.Values.Any(c => c.IsCancel && SameTarget(c.Snapshot, snapshot))) return null;
            if (PredecessorDirectiveIsOut(record)) return null;
        }

        return new RelaySend
        {
            Record = record,
            Effect = effect,
            Root = record.TaskId,
            Snapshot = snapshot,
            Prior = prior,
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token),
        };
    }

    private static bool SameTarget(PendingDelegation a, PendingDelegation b) =>
        string.Equals(a.Agent, b.Agent, StringComparison.Ordinal)
        && string.Equals(a.CurrentTaskId, b.CurrentTaskId, StringComparison.Ordinal)
        && string.Equals(a.ExecutionId, b.ExecutionId, StringComparison.Ordinal);

    private async Task RunSendAsync(RelaySend send)
    {
        // Off the caller, which holds the gate: a relay that blocks before its first await must
        // not block the pump, the pass or a reply.
        await Task.Yield();

        // Each call has its time. When it is up the call is told to stop; a relay that honours
        // that ends, and from then on can deliver nothing. Its place and its record's turn are
        // given back only when it HAS ended.
        var limit = send.Effect == RelayEffect.Publish ? RelaySendTimeout : RelaySendTimeout * 3;
        var token = send.Cancellation.Token;
        send.Cancellation.CancelAfter(limit);

        var sent = false;
        string? error = null;
        var delivery = CancelDelivery.Unknown;
        try
        {
            switch (send.Effect)
            {
                case RelayEffect.Publish:
                    await _relay.PublishDirectiveAsync(send.Snapshot, token);
                    break;
                case RelayEffect.TimeoutNotice:
                    await _relay.ReportTimeoutAsync(send.Snapshot, send.Snapshot.TimeoutMessage!, token);
                    break;
                default:
                    delivery = await _relay.SendCancelAsync(send.Snapshot, token);
                    break;
            }

            sent = true;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name;
        }

        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            if (_sends.TryGetValue(send.Record, out var current) && ReferenceEquals(current, send))
                _sends.Remove(send.Record);

            // A call cut short because the service is stopping proves nothing about what was
            // sent. What it was for stays owed, and the next start sends it.
            var cutShort = _stopping.IsCancellationRequested
                           && (send.IsCancel ? delivery != CancelDelivery.Settled : !(send.Effect == RelayEffect.Publish && sent));

            if (send.IsCancel) CancelEnded(send, delivery, keepOwed: cutShort);
            if (!cutShort) Apply(send, sent, error);
        }
        finally
        {
            _gate.Release();
            send.Cancellation.Dispose();
        }

        // What the record owes next, what was waiting for this call to end, and what was waiting
        // for room.
        RequestRelayPass();
    }

    /// <summary>
    /// A cancel call has ended. Unless the relay could say that nothing is still on its way, a
    /// directive for the same task id is held for <see cref="CancelSettleTime"/>, written into the
    /// record that is waiting so a restart keeps it. And whoever was carrying this cancel on a
    /// predecessor's behalf no longer has to send it: it has been sent.
    /// </summary>
    private void CancelEnded(RelaySend send, CancelDelivery delivery, bool keepOwed)
    {
        _store.TryGet(send.Root, out var current);
        var changed = false;

        if (delivery != CancelDelivery.Settled)
        {
            var until = _time.GetUtcNow() + CancelSettleTime;
            _unsettledCancels[send.Root] = until;
            if (current is { CancelRequested: false } && current.PublishNotBefore < until)
            {
                current.PublishNotBefore = until;
                changed = true;
            }

            _logger.LogWarning(
                "A cancel for taskId={TaskId} (agent {Agent}) ended without the bridge knowing whether it will still arrive; directives for that task id are held for {Seconds:0.#}s",
                send.Snapshot.CurrentTaskId, send.Snapshot.Agent, CancelSettleTime.TotalSeconds);
        }

        if (current is not null && !keepOwed)
        {
            var at = current.PriorCancels.FindIndex(p =>
                string.Equals(p.Agent, send.Snapshot.Agent, StringComparison.Ordinal)
                && string.Equals(p.TaskId, send.Snapshot.CurrentTaskId, StringComparison.Ordinal)
                && string.Equals(p.ExecutionId, send.Snapshot.ExecutionId, StringComparison.Ordinal));
            if (at >= 0)
            {
                current.PriorCancels.RemoveAt(at);
                changed = true;
            }
        }

        if (changed)
        {
            _store.Save();
            RemoveIfFinished(current!);
        }
    }

    /// <summary>
    /// Writes down what a finished call did for the record it was made for. It can finish late:
    /// after the record was completed by the agent's answer, or replaced by a later attempt under
    /// the same task id. So it changes only that record, only if it is still the one in the
    /// store, and only the thing it sent; a record is never removed twice and a successor is
    /// never touched.
    /// </summary>
    private void Apply(RelaySend send, bool sent, string? error)
    {
        var record = send.Record;
        if (!_store.TryGet(record.TaskId, out var current) || !ReferenceEquals(current, record)) return;

        switch (send.Effect)
        {
            case RelayEffect.PriorCancel:
                // CancelEnded has taken it off the list.
                return;

            case RelayEffect.Cancel:
            case RelayEffect.TimeoutNotice:
                // The relay reports its own failures and never throws; either way this was the
                // one attempt, as it was inside the activity.
                if (send.Effect == RelayEffect.Cancel) record.CancelSent = true;
                else record.TimeoutReported = true;
                _store.Save();
                RemoveIfFinished(record);
                return;
        }

        if (!sent)
        {
            // Not at once: the lane is asked again as soon as this returns.
            record.PublishRetryAfter = _time.GetUtcNow() + EffectiveTick;
            _logger.LogWarning(
                "Failed to publish directive to agent {Agent} for taskId={TaskId}: {Error}",
                record.Agent, send.Snapshot.CurrentTaskId, error);
            return;
        }

        // A continuation recorded while this was on its way is a different directive and is still
        // owed; only what was sent is marked as sent.
        if (!string.Equals(record.CurrentTaskId, send.Snapshot.CurrentTaskId, StringComparison.Ordinal)) return;

        record.LastPublishedAt = _time.GetUtcNow();
        _store.Save();
    }

    /// <summary>
    /// Completes when the relay lane has nothing in progress and nothing asked for. For tests,
    /// which otherwise could not tell "not sent" from "not sent yet".
    /// </summary>
    internal async Task RelayIdleAsync()
    {
        while (true)
        {
            Task pump;
            lock (_pumpLock) pump = _pump;
            await pump;

            Task[] running;
            await _gate.WaitAsync();
            try { running = [.. _sends.Values.Select(s => s.Completion)]; }
            finally { _gate.Release(); }

            if (running.Length > 0)
            {
                await Task.WhenAll(running);
                continue;
            }

            lock (_pumpLock)
            {
                if (!_pumpRunning && !_pumpRequested) return;
            }
        }
    }

    // ── read side ─────────────────────────────────────────────────────────────

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
                r.HasResult ? "answer-recorded" : r.TimeoutMessage is not null ? "timed-out" : r.CancelRequested ? "cancelling" : "waiting",
                r.ExecutionId))]);
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
    string State,
    string ExecutionId);
