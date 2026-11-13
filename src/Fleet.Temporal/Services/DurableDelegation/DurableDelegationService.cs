using System.Diagnostics;
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
    /// Relay sends in progress at once. Each is a broker connection or an HTTP request, and a
    /// bridge that has just restarted can owe one per pending delegation.
    /// </summary>
    internal const int MaxConcurrentRelaySends = 4;

    private static readonly TimeSpan MinimumSleep = TimeSpan.FromMilliseconds(200);

    [Flags]
    private enum RelayEffect { None = 0, Publish = 1, Cancel = 2, TimeoutNotice = 4 }

    /// <summary>One relay send that has been started for one record.</summary>
    private sealed class RelaySend
    {
        public required PendingDelegation Record { get; init; }
        public required RelayEffect Effect { get; init; }
        public required string TaskIdAtStart { get; init; }
        /// <summary>Set when the send gets its place and is made; zero while it waits for one.</summary>
        public long StartedAt;


        public Task Completion { get; set; } = Task.CompletedTask;

        /// <summary>The service stopped waiting for it. Whatever it does when it ends is ignored.</summary>
        public bool GivenUp { get; set; }
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

    // The relay lane. _sends is keyed by record INSTANCE, so a delegation that replaced another
    // under the same task id has a lane of its own, and is guarded by _gate.
    private readonly Dictionary<PendingDelegation, RelaySend> _sends = new(ReferenceEqualityComparer.Instance);
    // Calls that were given up and have not ended yet, by kind. No second call of a kind is made
    // for a record while one is outstanding, so a relay that never returns costs at most one
    // outstanding call per kind per record.
    private readonly Dictionary<PendingDelegation, RelayEffect> _abandoned = new(ReferenceEqualityComparer.Instance);
    private readonly SemaphoreSlim _relaySlots = new(MaxConcurrentRelaySends);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _pumpLock = new();
    // True while a pass is going round. The relay lane starts nothing new meanwhile: the pass is
    // the part with a deadline, and what it finds owed is sent when it has finished.
    private volatile bool _passInProgress;
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
    /// How long one relay send holds a place among <see cref="MaxConcurrentRelaySends"/>. A send
    /// that has not ended by then is told to stop and its place is given to the next one; a
    /// broker that has gone away must not hold up the sends that could still get through.
    /// </summary>
    internal TimeSpan RelaySendTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a send may stay unfinished before the lane stops waiting for it, so that whatever
    /// the record owes next can go out.
    /// </summary>
    private TimeSpan RelayGiveUpAfter => RelaySendTimeout * 3;

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
            // So the relay lane does not send the directive while the activity is sending it.
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
        if (record.CancelRequested) return record.CancelSent ? RelayEffect.None : RelayEffect.Cancel;
        if (record.TimeoutMessage is not null) return record.TimeoutReported ? RelayEffect.None : RelayEffect.TimeoutNotice;
        if (record.HasResult || record.TemporalSettled) return RelayEffect.None;

        return now - record.LastPublishedAt >= ResendInterval && now >= record.PublishRetryAfter
            ? RelayEffect.Publish
            : RelayEffect.None;
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
    /// Starts a send for every record that owes one and has none in progress.
    ///
    /// <para>
    /// One send per record at a time, so a record's effects go out in the order its state asks
    /// for them. <see cref="MaxConcurrentRelaySends"/> at once across records. A send that has not
    /// ended after <see cref="RelaySendTimeout"/> gives its place up, so the lane keeps moving
    /// past a send that hangs, and after <see cref="RelayGiveUpAfter"/> it is given up altogether
    /// so the same record's next effect can go out.
    /// </para>
    /// </summary>
    private async Task RelayPassAsync(CancellationToken ct)
    {
        var due = new List<(RelaySend Send, PendingDelegation Snapshot)>();

        await _gate.WaitAsync(ct);
        try
        {
            // The pass asks for a relay pass when it ends, so nothing is missed by standing aside.
            if (_passInProgress) return;

            var now = _time.GetUtcNow();
            foreach (var record in _store.All)
            {
                if (_sends.TryGetValue(record, out var running))
                {
                    // Waiting for a place is not hanging: only a send that was made can be given up.
                    var startedAt = Volatile.Read(ref running.StartedAt);
                    if (startedAt == 0 || Stopwatch.GetElapsedTime(startedAt) < RelayGiveUpAfter) continue;
                    GiveUp(running, now);
                }

                var effect = Owed(record, now);
                if (effect == RelayEffect.None) continue;
                if ((_abandoned.GetValueOrDefault(record) & effect) != 0) continue;

                var send = new RelaySend { Record = record, Effect = effect, TaskIdAtStart = record.CurrentTaskId };
                _sends[record] = send;
                // The send works from a copy: the pass and a reply go on changing the record.
                due.Add((send, record.Clone()));
            }
        }
        finally
        {
            _gate.Release();
        }

        foreach (var (send, snapshot) in due)
        {
            try
            {
                await _relaySlots.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                await ForgetAsync(due.Select(d => d.Send).Where(s => s.Completion == Task.CompletedTask));
                throw;
            }

            Volatile.Write(ref send.StartedAt, Stopwatch.GetTimestamp());
            send.Completion = RunSendAsync(send, snapshot);
        }
    }

    /// <summary>Sends that were chosen but never started because the service is stopping.</summary>
    private async Task ForgetAsync(IEnumerable<RelaySend> sends)
    {
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            foreach (var send in sends)
                if (_sends.TryGetValue(send.Record, out var current) && ReferenceEquals(current, send))
                    _sends.Remove(send.Record);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Stops waiting for a send that has been in progress for too long. Whatever it does when it
    /// finally ends is ignored.
    ///
    /// <para>
    /// A cancel or a timeout notice is marked as sent: both were single best-effort attempts
    /// inside the activity, and the record must not wait for one for ever. A directive is simply
    /// owed again, once the outstanding call has ended and the usual wait has passed.
    /// </para>
    /// </summary>
    private void GiveUp(RelaySend send, DateTimeOffset now)
    {
        send.GivenUp = true;
        _sends.Remove(send.Record);
        _abandoned[send.Record] = _abandoned.GetValueOrDefault(send.Record) | send.Effect;
        _logger.LogWarning(
            "Gave up on a {Effect} for taskId={TaskId} (agent {Agent}) that has not finished after {Seconds:0.#}s",
            send.Effect, send.TaskIdAtStart, send.Record.Agent, RelayGiveUpAfter.TotalSeconds);

        if (send.Effect == RelayEffect.Publish) send.Record.PublishRetryAfter = now + EffectiveTick;
        else MarkSent(send.Record, send.Effect);
    }

    private async Task RunSendAsync(RelaySend send, PendingDelegation snapshot)
    {
        // Off the caller: a relay that blocks before its first await must not block the pump.
        await Task.Yield();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        timeout.CancelAfter(RelaySendTimeout);

        Task sending;
        try
        {
            sending = send.Effect switch
            {
                RelayEffect.Publish => _relay.PublishDirectiveAsync(snapshot, timeout.Token),
                RelayEffect.Cancel => _relay.SendCancelAsync(snapshot),
                _ => _relay.ReportTimeoutAsync(snapshot, snapshot.TimeoutMessage!),
            };
        }
        catch (Exception ex)
        {
            sending = Task.FromException(ex);
        }

        // The place is given back when the send ends or when its time is up, whichever is first.
        var released = 0;
        void Release()
        {
            if (Interlocked.Exchange(ref released, 1) == 0) _relaySlots.Release();
        }

        using var timer = new Timer(_ => Release(), null, RelaySendTimeout, Timeout.InfiniteTimeSpan);

        var sent = false;
        string? error = null;
        try
        {
            await sending;
            sent = true;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name;
        }
        finally
        {
            Release();
        }

        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            if (send.GivenUp)
            {
                // It ended at last: a call of this kind may be made for the record again.
                var left = _abandoned.GetValueOrDefault(send.Record) & ~send.Effect;
                if (left == RelayEffect.None) _abandoned.Remove(send.Record);
                else _abandoned[send.Record] = left;
            }
            else
            {
                if (_sends.TryGetValue(send.Record, out var current) && ReferenceEquals(current, send))
                    _sends.Remove(send.Record);

                Apply(send, sent, error);
            }
        }
        finally
        {
            _gate.Release();
        }

        // What the record owes next, if anything, and the sends that were waiting for a place.
        RequestRelayPass();
    }

    /// <summary>
    /// Writes down what a finished send did. It can finish late: after the record was completed
    /// by the agent's answer, replaced by a later attempt under the same task id, or given up on.
    /// So it changes only the record it was started for, only if that record is still the one in
    /// the store, and only the thing it sent; a record is never removed twice and a successor is
    /// never touched.
    /// </summary>
    private void Apply(RelaySend send, bool sent, string? error)
    {
        var record = send.Record;
        if (!_store.TryGet(record.TaskId, out var current) || !ReferenceEquals(current, record)) return;

        if (send.Effect != RelayEffect.Publish)
        {
            // The relay reports its own failures and never throws; either way this was the one
            // attempt, as it was inside the activity.
            MarkSent(record, send.Effect);
            return;
        }

        if (!sent)
        {
            // Not at once: the lane is asked again as soon as this returns.
            record.PublishRetryAfter = _time.GetUtcNow() + EffectiveTick;
            _logger.LogWarning(
                "Failed to publish directive to agent {Agent} for taskId={TaskId}: {Error}",
                record.Agent, send.TaskIdAtStart, error);
            return;
        }

        // A continuation recorded while this was on its way is a different directive and is still
        // owed; only what was sent is marked as sent.
        if (!string.Equals(record.CurrentTaskId, send.TaskIdAtStart, StringComparison.Ordinal)) return;

        record.LastPublishedAt = _time.GetUtcNow();
        _store.Save();
    }

    private void MarkSent(PendingDelegation record, RelayEffect effect)
    {
        if (!_store.TryGet(record.TaskId, out var current) || !ReferenceEquals(current, record)) return;

        if (effect == RelayEffect.Cancel) record.CancelSent = true;
        else record.TimeoutReported = true;

        _store.Save();
        RemoveIfFinished(record);
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
                r.HasResult ? "answer-recorded" : r.TimeoutMessage is not null ? "timed-out" : r.CancelRequested ? "cancelling" : "waiting"))]);
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
