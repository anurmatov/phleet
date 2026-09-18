using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Serialization;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Conversations.Contracts;
using Fleet.Protocol;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Fleet.Agent.Services;

/// <summary>
/// The agent half of the south seam: consume, claim, disposition, start, append, commit, ack
/// (#303).
/// </summary>
/// <remarks>
/// <para>
/// Everything that needs a retry, a bounded wait or an acknowledgement lives here rather than in
/// <see cref="ConversationSouthAdapter"/>, because the pump cancels an adapter call at five seconds
/// and swallows the exception (D10). This class owns every south HTTP call, all backoff, the
/// commit→ack handshake, and — through <see cref="ConversationOrdinalAllocator"/> — ordinal
/// allocation <b>at the send site</b> (D7a).
/// </para>
/// <para>
/// <b>The ack is the last thing that happens, never the first.</b> An ack-then-commit ordering
/// loses a turn's outcome permanently on a crash in the window and nothing detects it; a
/// commit-then-ack ordering can at worst redeliver, and a redelivery meets a <c>done</c> claim and
/// becomes a no-op (MUST NOT 5).
/// </para>
/// <para>
/// <b>Per conversation there is ONE in-order sender.</b> Every south call for a conversation —
/// disposition, turn start, appends, commit — is issued while holding that conversation's sender
/// gate, and its ordinal is taken inside the gate immediately before the call. Transmission order
/// and ordinal order therefore advance together by construction, which is what makes the store's
/// fence meaningful: an out-of-order rejection can only mean a real defect. Conversations are
/// independent and drain concurrently.
/// </para>
/// <para>
/// ⚠️ This class declares nothing and binds nothing on the broker. The queue, its
/// <c>x-queue-type: quorum</c> argument and its binding are owned by the publishing service, so a
/// submission published before the agent ever started is still there when it first attaches. A
/// consumer that re-declared with any argument mismatch would take a channel-level
/// <c>PRECONDITION_FAILED</c>; one that attaches before the service has ever declared takes
/// <c>NOT_FOUND</c>. Both are retried with backoff rather than treated as fatal.
/// </para>
/// </remarks>
public sealed class ConversationSouthConsumer : BackgroundService
{
    /// <summary>Attempts at a retried south call before the caller's failure path runs.</summary>
    private const int RetryAttempts = 5;

    /// <summary>
    /// Attempts at a terminal commit. Higher than the rest on purpose: a commit that never lands
    /// costs the client its answer, and the message stays unacked until it does.
    /// </summary>
    private const int CommitAttempts = 8;

    /// <summary>How long a graceful stop waits for in-flight terminals before giving up.</summary>
    public static readonly TimeSpan ShutdownDrainBudget = TimeSpan.FromSeconds(15);

    private readonly ConversationsOptions _options;
    private readonly ConversationSouthClient _client;
    private readonly ConversationSouthHandoff _handoff;
    private readonly ConversationOrdinalAllocator _allocator;
    private readonly ConversationIntake _intake;
    private readonly ConversationSouthCounters _counters;
    private readonly ILogger<ConversationSouthConsumer> _logger;

    private readonly ConcurrentDictionary<string, OwnedDelivery> _owned = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _attempts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConversationState> _conversations = new(StringComparer.Ordinal);

    private IConnection? _connection;
    private IChannel? _channel;
    private string? _consumerTag;

    public ConversationSouthConsumer(
        IOptions<ConversationsOptions> options,
        ConversationSouthClient client,
        ConversationSouthHandoff handoff,
        ConversationOrdinalAllocator allocator,
        ConversationIntake intake,
        ConversationSouthCounters counters,
        ILogger<ConversationSouthConsumer> logger)
    {
        _options = options.Value;
        _client = client;
        _handoff = handoff;
        _allocator = allocator;
        _intake = intake;
        _counters = counters;
        _logger = logger;
    }

    /// <summary>
    /// Who this process is, in a claim row and on a transferred lease.
    /// </summary>
    /// <remarks>
    /// The agent name alone would make two instances of one agent indistinguishable in the claim
    /// row — which is precisely the case a claim race is about. The per-process epoch is already a
    /// ULID minted at construction, so it separates them without inventing a second identity. It is
    /// an identity, never a credential.
    /// </remarks>
    public string Owner => $"{_options.AgentName}:{_allocator.Epoch}";

    /// <summary>
    /// Every attempt this process owns and has not committed — running and
    /// dispositioned-but-unstarted alike.
    /// </summary>
    /// <remarks>
    /// Heartbeating only the running attempt abandons a full queue at the lease bound and reports it
    /// to the client as an unknown outcome, which is the failure the contract's own
    /// <c>HeartbeatRequest.AttemptIds</c> documentation exists to prevent. Populated on a successful
    /// claim, removed on commit or terminal-on-arrival.
    /// </remarks>
    public IReadOnlyCollection<string> OwnedAttemptIds => _attempts.Keys.ToArray();

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = _client.InitialBackoff;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await AttachAsync(stoppingToken);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // Type only — a broker error carries a host and a virtual host, and the connection
                // string is a credential (MUST NOT 19). The agent's other paths keep running; a
                // broker that is down must never block host startup.
                _logger.LogWarning(
                    "south consumer could not attach to the inbound queue: {Error}. Retrying in {Delay}.",
                    e.GetType().Name, delay);

                try { await Task.Delay(delay, stoppingToken); }
                catch (OperationCanceledException) { return; }

                delay = _client.Backoff(delay);
            }
        }
    }

    private async Task AttachAsync(CancellationToken ct)
    {
        var factory = new ConnectionFactory
        {
            Uri = new Uri(_options.BrokerConnectionString),
            ClientProvidedName = $"{_options.AgentName}-conversations",
            AutomaticRecoveryEnabled = true,
        };

        _connection = await factory.CreateConnectionAsync(ct);
        _channel = await _connection.CreateChannelAsync(cancellationToken: ct);

        // Prefetch must be greater than one. A delivery is not acked until its terminal commits, so
        // a prefetch of one would stop a second submission ever being delivered while the first turn
        // ran — and `injected` and `queued`, the two dispositions this seam exists to report, could
        // then never occur.
        await _channel.BasicQosAsync(0, _options.Prefetch, global: false, cancellationToken: ct);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += async (_, ea) =>
        {
            try
            {
                await HandleDeliveryAsync(ea, ct);
            }
            catch (Exception e)
            {
                // A handler that threw has not acked, so the delivery is redelivered after the
                // channel closes or the claim expires. Never let it take the consumer down.
                _logger.LogError(e, "south delivery handler failed");
            }
        };

        _consumerTag = await _channel.BasicConsumeAsync(
            QueueName, autoAck: false, consumer: consumer, cancellationToken: ct);

        _logger.LogInformation("South conversation consumer attached to {Queue}", QueueName);
    }

    /// <summary>The per-agent inbound queue. Declared and bound by the publishing service.</summary>
    private string QueueName => $"fleet.conversations.inbound.{_options.AgentName}";

    // ── one delivery ─────────────────────────────────────────────────────────

    internal async Task HandleDeliveryAsync(BasicDeliverEventArgs ea, CancellationToken ct)
    {
        var deliveryTag = ea.DeliveryTag;
        var messageId = ea.BasicProperties.MessageId;

        if (string.IsNullOrWhiteSpace(messageId))
        {
            // The claim key IS the message id. Without one there is nothing to claim and nothing
            // that could make a redelivery idempotent, so requeuing is an infinite loop. Terminate.
            _counters.Disposition("no_message_id");
            _logger.LogError("south delivery carried no message id; acknowledged without a claim");
            await AckAsync(deliveryTag);
            return;
        }

        ClaimDeliveryResult claim;
        try
        {
            claim = await _client.ClaimAsync(
                new ClaimDeliveryRequest { MessageId = messageId, Owner = Owner }, ct);
        }
        catch (SouthCallException e)
        {
            // Status only, never the credential or the header. A misconfigured token must not spin
            // a hot loop, so every failed claim backs off before the requeue.
            _logger.LogWarning(
                "south claim failed with status {Status}; requeuing after backoff", (int)e.Status);
            await BackoffThenNackAsync(deliveryTag, ct);
            return;
        }
        catch (Exception e)
        {
            _logger.LogWarning("south claim failed: {Error}; requeuing after backoff", e.GetType().Name);
            await BackoffThenNackAsync(deliveryTag, ct);
            return;
        }

        _counters.Claim(claim.Outcome.ToString());

        switch (claim.Outcome)
        {
            case ClaimOutcome.DuplicateDone:
                // Already processed. Within its retention this row is the only thing standing
                // between a redelivery and a duplicate turn (AC2).
                await AckAsync(deliveryTag);
                return;

            case ClaimOutcome.HeldElsewhere:
                // A live claim elsewhere — a normal outcome of a race, not an error, so it is logged
                // at Debug and requeued AFTER a backoff. The store returns HeldElsewhere without
                // comparing owner, so a redelivery to the holder itself lands here too; an immediate
                // requeue would then spin against the store for the claim-hold duration.
                _logger.LogDebug("south delivery is claimed elsewhere; requeuing after backoff");
                await BackoffThenNackAsync(deliveryTag, ct);
                return;
        }

        var submissionId = claim.SubmissionId;
        var attemptId = claim.AttemptId;
        var conversationId = claim.ConversationId;

        if (submissionId is null || attemptId is null || conversationId is null)
        {
            _logger.LogError("south claim reported Claimed without identifiers; requeuing after backoff");
            await BackoffThenNackAsync(deliveryTag, ct);
            return;
        }

        var envelope = ParseEnvelope(ea.Body.Span);
        var state = Conversation(conversationId);

        // Claimed: this process now owns the attempt, so the heartbeat must cover it from here —
        // including a submission that is about to be queued and has no turn yet.
        _attempts[attemptId] = 0;
        var owned = new OwnedDelivery(messageId, submissionId, attemptId, conversationId, deliveryTag);
        _owned[submissionId] = owned;

        if (envelope is null || !IsDispatchable(envelope.Kind))
        {
            await TerminateOnArrivalAsync(state, owned, LabelFor(envelope?.Kind), ct);
            return;
        }

        await DispatchAsync(state, owned, envelope, ct);
    }

    /// <summary>
    /// The two kinds this slice runs. Everything else is terminated on arrival (D11, D12).
    /// </summary>
    private static bool IsDispatchable(string? kind) =>
        kind is ConversationEventKind.SubmissionCreate or ConversationEventKind.SubmissionSteer;

    private static string LabelFor(string? kind) => kind switch
    {
        ConversationEventKind.SubmissionCancel => ConversationSouthCounters.DispositionCancel,
        _ => ConversationSouthCounters.DispositionUnknownKind,
    };

    /// <summary>
    /// Claim, <c>/deliveries:complete{dropped}</c>, ack — for a cancel, an unrecognised kind, or a
    /// submission intake refused (D11, D12).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Silence is not an option and neither is a requeue. An unclaimed or undispositioned envelope
    /// leaves a held claim and a live lease, which the reconciler eventually turns into
    /// <c>turn.outcome_unknown { attempt_abandoned }</c> against a client that is still waiting; a
    /// requeue of something this consumer cannot handle is an infinite broker loop (MUST NOT 11, 12).
    /// </para>
    /// <para>
    /// ⚠️ Known limitation for a cancel: the north route already returned <c>200</c>, and the client
    /// receives <c>submission.accepted { dropped }</c> and no <c>control.ack</c>. Cancel
    /// <i>execution</i> is deferred to its own issue, together with the open store-contract question
    /// of what closes a cancel submission's attempt given that <c>control.ack</c> is not one of the
    /// four terminal kinds.
    /// </para>
    /// </remarks>
    private async Task TerminateOnArrivalAsync(
        ConversationState state, OwnedDelivery owned, string label, CancellationToken ct)
    {
        // Identifiers only. The kind string is safe; the payload is conversation text.
        _logger.LogInformation(
            "south delivery terminated on arrival ({Label}): submissionId={SubmissionId}", label, owned.SubmissionId);

        var completed = await CompleteDeliveryAsync(state, owned, SubmissionDisposition.Dropped, senderHeld: false, ct);
        if (!completed) return;

        _counters.Disposition(label);
        Forget(owned);
        await AckAsync(owned.DeliveryTag);
    }

    private async Task DispatchAsync(
        ConversationState state, OwnedDelivery owned, CommandEnvelope envelope, CancellationToken ct)
    {
        // The sender gate is taken BEFORE dispatch and held until the disposition is recorded, so
        // this conversation's drain loop cannot transmit a `turn.started` for a submission the store
        // has not been told about yet. It is the same gate every south call for the conversation
        // takes, which is what keeps ordinal order and transmission order identical.
        await state.Sender.WaitAsync(ct);

        // Released EXACTLY ONCE, in the finally. An earlier revision released inline before each
        // early return and again in a catch, which double-releases a binary semaphore the moment an
        // exception follows an inline release — and a semaphore silently counting to two is a second
        // concurrent sender, which is the one thing this gate exists to prevent.
        var outcome = TaskDispatchOutcome.Dropped;
        var dispatched = false;
        var terminateReason = (string?)null;

        try
        {
            var open = _intake.OpenAuthorized(
                ConversationSouthAdapter.ClientChannel, owned.ConversationId, envelope.PrincipalId ?? "");

            if (!open.Success)
            {
                terminateReason = ConversationSouthCounters.DispositionIntakeRejected;
            }
            else
            {
                if (state.TryBindRuntimeKey(open.RuntimeKey))
                {
                    // Nothing evicts this (D9a), so growth has to be observable rather than inferred.
                    _counters.ConversationRegistered();
                    state.StartDrain(this, ct);
                }

                var dispatch = await _intake.SubmitAsync(
                    open.RuntimeKey,
                    envelope.Payload?.Text ?? string.Empty,
                    attachments: null,
                    replyToEventId: envelope.Payload?.ReplyToEventId,
                    submissionId: owned.SubmissionId);

                if (dispatch is null)
                {
                    // Intake refused it — an oversize body, or an attachment this phase does not
                    // carry. The runtime published its own protocol.rejected; the delivery still has
                    // to be dispositioned or it leaks a claim (MUST NOT 11).
                    terminateReason = ConversationSouthCounters.DispositionIntakeRejected;
                }
                else
                {
                    outcome = dispatch.Value;
                    dispatched = true;
                }
            }

            if (dispatched && TerminatesOnArrival(outcome))
            {
                // Terminal on arrival: no turn start, no commit, and submission.accepted is appended
                // exactly once — by /deliveries:complete rather than /submissions:disposition (D3).
                var disposition = outcome is TaskDispatchOutcome.QueueFull
                    ? SubmissionDisposition.QueueFull
                    : SubmissionDisposition.Dropped;

                if (await CompleteDeliveryAsync(state, owned, disposition, senderHeld: true, ct))
                {
                    _counters.Disposition(disposition.ToString());
                    Forget(owned);
                    await AckAsync(owned.DeliveryTag);
                }

                return;
            }

            if (dispatched)
            {
                if (!await RecordDispositionAsync(state, owned, Map(outcome), ct)) return;

                _counters.Disposition(outcome.ToString());
                state.NoteDispositioned(owned.SubmissionId, outcome);

                // Ran, Injected and Queued all leave the message UNACKED. Ran and Queued are acked
                // when their own terminal commits; Injected is acked when the host turn's commit
                // closes it through MergedSubmissionIds (D6a).
            }
        }
        finally
        {
            state.Sender.Release();
        }

        if (terminateReason is not null)
            await TerminateOnArrivalAsync(state, owned, terminateReason, ct);
    }

    /// <summary>
    /// Which of the two disposition endpoints an outcome takes (D3).
    /// </summary>
    /// <remarks>
    /// The store refuses the wrong endpoint for each set, in both directions, because recording a
    /// submission through both would append <c>submission.accepted</c> twice for one submission.
    /// </remarks>
    internal static bool TerminatesOnArrival(TaskDispatchOutcome outcome) =>
        outcome is TaskDispatchOutcome.QueueFull or TaskDispatchOutcome.Dropped;

    /// <summary>The 1:1 mapping between the runtime's dispatch outcome and the wire enum.</summary>
    internal static SubmissionDisposition Map(TaskDispatchOutcome outcome) => outcome switch
    {
        TaskDispatchOutcome.Ran => SubmissionDisposition.Ran,
        TaskDispatchOutcome.Injected => SubmissionDisposition.Injected,
        TaskDispatchOutcome.Queued => SubmissionDisposition.Queued,
        TaskDispatchOutcome.QueueFull => SubmissionDisposition.QueueFull,
        _ => SubmissionDisposition.Dropped,
    };

    // ── the per-conversation in-order sender ─────────────────────────────────

    /// <summary>
    /// Drains one conversation's hand-off queue, strictly FIFO, one item at a time.
    /// </summary>
    /// <remarks>
    /// The sender gate is taken per item and the ordinal is allocated inside it, so whatever order
    /// the gate grants is the order both the calls and their ordinals take. A terminal that the bus
    /// delivered ahead of earlier progress therefore carries the LOWER ordinal, commits, and the
    /// trailing progress falls into the store's documented after-terminal drop with a
    /// <c>null</c> seq — rather than tripping the fence with a reordering of our own making.
    /// </remarks>
    internal async Task DrainConversationAsync(ConversationState state, CancellationToken ct)
    {
        var reader = _handoff.Reader(state.ConversationId);

        try
        {
            while (await reader.WaitToReadAsync(ct))
            {
                while (reader.TryRead(out var item))
                {
                    await state.Sender.WaitAsync(ct);
                    try
                    {
                        await TransmitAsync(state, item, ct);
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(
                            "south transmission failed: action={Action} kind={Kind} conversationId={ConversationId} error={Error}",
                            item.Action, item.Event.Kind, state.ConversationId, e.GetType().Name);
                    }
                    finally
                    {
                        state.Sender.Release();
                        _handoff.Release(state.ConversationId);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping. Anything still queued is not acked, so its lease expires and the reconciler
            // records the honest outcome rather than a silent success.
        }
    }

    private Task TransmitAsync(ConversationState state, SouthOutboundItem item, CancellationToken ct) =>
        item.Action switch
        {
            SouthOutboundAction.StartTurn => StartTurnAsync(state, item, ct),
            SouthOutboundAction.Commit => CommitAsync(state, item, ct),
            _ => AppendAsync(state, item, ct),
        };

    private async Task StartTurnAsync(ConversationState state, SouthOutboundItem item, CancellationToken ct)
    {
        if (item.SubmissionId is null || item.TurnId is null) return;

        // Already started — either this submission's terminal overtook its `turn.started` and started
        // it (see CommitAsync), or the same start arrived twice. Sending a second /turns:start is not
        // harmless: the attempt is no longer `pending`, so the store takes its lost-attempt branch
        // and ABANDONS the merged children of a turn that is running or has already answered.
        //
        // Checked BEFORE the ownership lookup on purpose. A committed delivery is forgotten, so the
        // lookup below would also swallow the late start — but only as a side effect of the ack
        // having already happened, which leaves the window between commit and ack uncovered and says
        // nothing in the counters about a duplicate that did arrive.
        if (state.TurnStarted(item.SubmissionId))
        {
            _counters.TurnStartSuppressed();
            return;
        }

        if (!_owned.TryGetValue(item.SubmissionId, out var owned)) return;

        // Submissions queued for this conversation BEFORE the turn began are coalesced into it by
        // the task manager and never get a turn of their own, so /turns:start moves their attempts
        // pending → merged and the host commit closes them (D6a).
        var merged = state.TakeQueuedExcept(item.SubmissionId);

        try
        {
            var result = await _client.ExecuteWithRetryAsync(
                token => _client.StartTurnAsync(new StartTurnRequest
                {
                    AttemptId = owned.AttemptId,
                    TurnId = item.TurnId,
                    Epoch = _allocator.Epoch,
                    Ordinal = _allocator.Next(state.ConversationId),
                    MergedSubmissionIds = merged.Count == 0 ? null : merged,
                }, token),
                "turns:start", RetryAttempts, ct);

            _counters.TurnStarted();
            state.BeginTurn(item.TurnId, owned.AttemptId, item.SubmissionId, merged);

            if (result.Replayed)
                _logger.LogDebug("south turn start replayed: attemptId={AttemptId}", owned.AttemptId);
        }
        catch (SouthCallException e) when (e.Status == System.Net.HttpStatusCode.Conflict)
        {
            // A different turn id on an already-running attempt — the resume path reaches here.
            // Log it and do NOT start a second turn for one attempt.
            _counters.TurnStartConflict();
            _logger.LogWarning(
                "south turn start refused as a conflict: attemptId={AttemptId} turnId={TurnId}",
                owned.AttemptId, item.TurnId);

            // The attempt IS running, just under another turn id, so a later duplicate start must
            // not fire either.
            state.NoteStarted(item.SubmissionId);

            // The merged set still belongs to the running turn, or it would never be closed.
            state.RestoreMerged(merged);
        }
    }

    private async Task AppendAsync(ConversationState state, SouthOutboundItem item, CancellationToken ct)
    {
        var owned = item.SubmissionId is null ? null : Lookup(item.SubmissionId);

        // The stop is scoped to the ATTEMPT, never the conversation (MUST NOT 10). A conversation-
        // wide stop turns one rejected write into permanent silence for that client.
        if (owned is not null && state.AppendsStopped(owned.AttemptId)) return;

        try
        {
            var result = await _client.ExecuteWithRetryAsync(
                token => _client.AppendAsync(new AppendBatchRequest
                {
                    ConversationId = state.ConversationId,
                    Epoch = _allocator.Epoch,
                    Events =
                    [
                        new StagedEvent
                        {
                            Event = item.Event,
                            Ordinal = _allocator.Next(state.ConversationId),
                            SubmissionId = owned?.SubmissionId,
                            AttemptId = owned?.AttemptId,
                            RetentionClass = item.RetentionClass,
                        },
                    ],
                }, token),
                "events:append", RetryAttempts, ct);

            var dropped = result.Seqs.Count(seq => seq is null);

            // A null seq means the event was dropped after a terminal. Expected, not an error — the
            // client already has its terminal — but counted, because "expected" and "invisible" are
            // different things.
            if (dropped > 0) _counters.AppendDroppedAfterTerminal(dropped);
            _counters.Appended(result.Seqs.Count - dropped);
        }
        catch (Exception e)
        {
            // The listener does not map the store's fence exceptions to a distinguishable status, so
            // an exhausted retry is treated as the worst case it could be. With send-site allocation
            // and in-order transmission, a fence trip can only mean a second appender or a rewound
            // counter — hence Error, and a counter, rather than a warning. Ordinals are never
            // renumbered to force the write through (MUST NOT 8).
            _counters.FenceRejection();

            if (owned is not null) state.StopAppends(owned.AttemptId);

            _logger.LogError(
                "south append failed and appending is stopped for this attempt: kind={Kind} conversationId={ConversationId} error={Error}",
                item.Event.Kind, state.ConversationId, e.GetType().Name);
        }
    }

    private async Task CommitAsync(ConversationState state, SouthOutboundItem item, CancellationToken ct)
    {
        if (item.SubmissionId is null) return;
        if (!_owned.TryGetValue(item.SubmissionId, out var owned)) return;

        // ⚠️ The terminal can legitimately arrive BEFORE its own turn.started.
        //
        // `ConversationEventPump` drains the per-conversation terminal outboxes first, every
        // iteration, ahead of the shared progress channel — deliberately, and `turn.started` travels
        // on the progress side. The store requires the start first: /turns:commit only moves an
        // attempt out of `running`, so a commit on a `pending` attempt appends the terminal, leaves
        // the attempt open for the reconciler to abandon, and the turn.started that follows is
        // dropped with a null seq because the submission is already terminal. The conversation then
        // reads `submission.accepted, turn.final` with no turn.started, and an answered turn is
        // reported to the client as turn.outcome_unknown { attempt_abandoned } when the lease runs
        // out. Observed in CI on a run where the terminal won; AC1 and AC2 both saw it.
        //
        // The terminal carries the same identity.turnId as the start it overtook, so the start can
        // be made here from the terminal itself rather than waiting for an event that is behind us.
        if (!state.TurnStarted(item.SubmissionId) && item.TurnId is not null)
        {
            try
            {
                await StartTurnAsync(state, item, ct);
                _counters.TurnStartedFromTerminal();
            }
            catch (Exception e)
            {
                // Same rule as a failed commit: leave the message unacked rather than record a
                // terminal against an attempt the store never saw start.
                _logger.LogError(
                    "south turn start ahead of its terminal failed; the message stays unacked: "
                    + "attemptId={AttemptId} error={Error}",
                    owned.AttemptId, e.GetType().Name);
                return;
            }
        }

        var merged = state.EndTurn(item.SubmissionId);

        try
        {
            var result = await _client.ExecuteWithRetryAsync(
                token => _client.CommitTerminalAsync(new CommitTerminalRequest
                {
                    AttemptId = owned.AttemptId,
                    TerminalEvent = item.Event,
                    MergedSubmissionIds = merged.Count == 0 ? null : merged,
                    Epoch = _allocator.Epoch,
                    Ordinal = _allocator.Next(state.ConversationId),
                }, token),
                "turns:commit", CommitAttempts, ct);

            _counters.Commit();

            if (result.RecoveredAnswer)
            {
                // The reconciler won the race and the answer was preserved as
                // turn.recovered_answer. That is a SUCCESSFUL commit; retrying it would be wrong.
                _counters.RecoveredAnswer();
                _logger.LogInformation(
                    "south terminal arrived after abandonment and was preserved: attemptId={AttemptId}",
                    owned.AttemptId);
            }
        }
        catch (Exception e)
        {
            // Leave the message UNACKED. A redelivery meets a done claim and is a no-op, and the
            // lease expires into an honest outcome_unknown — both are better than acking a turn
            // whose outcome was never recorded.
            _logger.LogError(
                "south terminal commit failed after retries; the message stays unacked: attemptId={AttemptId} error={Error}",
                owned.AttemptId, e.GetType().Name);
            return;
        }

        // Only now. The commit is durable, so the redelivery this ack prevents can no longer cost
        // anything (MUST NOT 5).
        Forget(owned);
        await AckAsync(owned.DeliveryTag);

        // The children this turn answered are closed by the same transaction, so their deliveries
        // are done too.
        foreach (var child in merged)
        {
            if (!_owned.TryGetValue(child, out var childDelivery)) continue;
            Forget(childDelivery);
            await AckAsync(childDelivery.DeliveryTag);
        }
    }

    // ── broker acknowledgement ───────────────────────────────────────────────

    /// <summary>
    /// Test seam for the ONE property that cannot be observed without a broker: that the ack happens
    /// after the commit and never before it.
    /// </summary>
    /// <remarks>
    /// The alternative — inferring the ordering from which identifiers are still tracked — proves
    /// that <c>Forget</c> ran, not that the ack did, and the mutation this guards against is exactly
    /// a reordering of those two lines. Never set outside tests.
    /// </remarks>
    internal Func<ulong, Task>? AckHook { get; set; }

    /// <summary>Record a claimed delivery without a broker. Test seam.</summary>
    internal void TrackForTesting(OwnedDelivery owned)
    {
        _attempts[owned.AttemptId] = 0;
        _owned[owned.SubmissionId] = owned;
    }

    /// <summary>True while this process still owns the submission's delivery. Test seam.</summary>
    internal bool OwnsForTesting(string submissionId) => _owned.ContainsKey(submissionId);

    private async Task AckAsync(ulong deliveryTag)
    {
        if (AckHook is { } hook)
        {
            await hook(deliveryTag);
            return;
        }

        var channel = _channel;
        if (channel is null || !channel.IsOpen) return;

        try
        {
            await channel.BasicAckAsync(deliveryTag, multiple: false);
        }
        catch (Exception e)
        {
            // The connection went away. The delivery is redelivered and meets a done claim.
            _logger.LogWarning("south ack failed: {Error}", e.GetType().Name);
        }
    }

    private async Task BackoffThenNackAsync(ulong deliveryTag, CancellationToken ct)
    {
        try { await Task.Delay(_client.InitialBackoff, ct); }
        catch (OperationCanceledException) { return; }

        var channel = _channel;
        if (channel is null || !channel.IsOpen) return;

        try
        {
            await channel.BasicNackAsync(deliveryTag, multiple: false, requeue: true);
            _counters.Nack();
        }
        catch (Exception e)
        {
            _logger.LogWarning("south nack failed: {Error}", e.GetType().Name);
        }
    }

    // ── south writes that need the sender gate already held ──────────────────

    private async Task<bool> RecordDispositionAsync(
        ConversationState state, OwnedDelivery owned, SubmissionDisposition disposition, CancellationToken ct)
    {
        try
        {
            // Idempotent on the claim: a retry that finds it already done writes nothing and
            // returns the recorded result, so no event is appended twice.
            await _client.ExecuteWithRetryAsync(
                token => _client.RecordDispositionAsync(new RecordDispositionRequest
                {
                    MessageId = owned.MessageId,
                    SubmissionId = owned.SubmissionId,
                    AttemptId = owned.AttemptId,
                    Disposition = disposition,
                    Epoch = _allocator.Epoch,
                    Ordinal = _allocator.Next(state.ConversationId),
                    Owner = Owner,
                }, token),
                "submissions:disposition", RetryAttempts, ct);

            return true;
        }
        catch (Exception e)
        {
            _logger.LogError(
                "south disposition failed after retries; the message stays unacked: submissionId={SubmissionId} error={Error}",
                owned.SubmissionId, e.GetType().Name);
            return false;
        }
    }

    /// <param name="senderHeld">
    /// Whether the caller already holds the conversation's sender gate. Passed explicitly rather
    /// than probed from the semaphore's count: a count of zero says <i>someone</i> holds it, not that
    /// <i>this</i> caller does, and a wrong answer either deadlocks or opens a second sender.
    /// </param>
    private async Task<bool> CompleteDeliveryAsync(
        ConversationState state, OwnedDelivery owned, SubmissionDisposition disposition,
        bool senderHeld, CancellationToken ct)
    {
        if (!senderHeld) await state.Sender.WaitAsync(ct);

        try
        {
            await _client.ExecuteWithRetryAsync(
                token => _client.CompleteDeliveryAsync(new CompleteDeliveryRequest
                {
                    MessageId = owned.MessageId,
                    SubmissionId = owned.SubmissionId,
                    AttemptId = owned.AttemptId,
                    Disposition = disposition,
                    Epoch = _allocator.Epoch,
                    Ordinal = _allocator.Next(state.ConversationId),
                }, token),
                "deliveries:complete", RetryAttempts, ct);

            return true;
        }
        catch (Exception e)
        {
            // Do not ack until it succeeds. An unacked delivery is redelivered; an acked one that
            // was never dispositioned leaks a claim.
            _logger.LogError(
                "south delivery completion failed after retries; the message stays unacked: submissionId={SubmissionId} error={Error}",
                owned.SubmissionId, e.GetType().Name);
            return false;
        }
        finally
        {
            if (!senderHeld) state.Sender.Release();
        }
    }

    // ── bookkeeping ──────────────────────────────────────────────────────────

    private OwnedDelivery? Lookup(string submissionId) =>
        _owned.TryGetValue(submissionId, out var owned) ? owned : null;

    private void Forget(OwnedDelivery owned)
    {
        _owned.TryRemove(owned.SubmissionId, out _);
        _attempts.TryRemove(owned.AttemptId, out _);
    }

    internal ConversationState Conversation(string conversationId) =>
        _conversations.GetOrAdd(conversationId, static id => new ConversationState(id));

    // ── shutdown ─────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Stop CONSUMING first, so nothing new is claimed while in-flight turns finish. Then wait a
        // bounded budget for terminals already on their way. Anything still owned after that is left
        // unacked on purpose: its lease expires and the reconciler records
        // turn.outcome_unknown { attempt_abandoned }, which is the honest outcome for work whose
        // result was never committed.
        var channel = _channel;

        if (channel is { IsOpen: true } && _consumerTag is not null)
        {
            try { await channel.BasicCancelAsync(_consumerTag, noWait: false, cancellationToken); }
            catch (Exception e) { _logger.LogDebug("south consumer cancel failed: {Error}", e.GetType().Name); }
        }

        var deadline = DateTimeOffset.UtcNow + ShutdownDrainBudget;
        while (!_owned.IsEmpty && DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
            await Task.Delay(TimeSpan.FromMilliseconds(100), CancellationToken.None);

        if (!_owned.IsEmpty)
        {
            _logger.LogWarning(
                "south consumer stopped with {Count} uncommitted delivery(ies); their leases will expire",
                _owned.Count);
        }

        _handoff.CompleteAll();

        await base.StopAsync(cancellationToken);

        if (channel is not null) await channel.DisposeAsync();
        if (_connection is not null) await _connection.DisposeAsync();
    }

    // ── envelope ─────────────────────────────────────────────────────────────

    private CommandEnvelope? ParseEnvelope(ReadOnlySpan<byte> body)
    {
        try
        {
            return FleetProtocolJson.Deserialize<CommandEnvelope>(Encoding.UTF8.GetString(body));
        }
        catch (Exception e)
        {
            // The body can contain conversation text, so the exception type is all that is logged.
            _logger.LogWarning("south command envelope could not be read: {Error}", e.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// The command envelope the service publishes.
    /// </summary>
    /// <remarks>
    /// It deliberately carries no <c>attemptId</c>: publishing one before anyone owned it would hand
    /// a redelivery an identifier another process is mid-way through using. Identifiers come from
    /// the claim (D2).
    /// </remarks>
    internal sealed record CommandEnvelope
    {
        [JsonPropertyName("kind")] public string? Kind { get; init; }
        [JsonPropertyName("principalId")] public string? PrincipalId { get; init; }
        [JsonPropertyName("channelId")] public string? ChannelId { get; init; }
        [JsonPropertyName("conversationId")] public string? ConversationId { get; init; }
        [JsonPropertyName("submissionId")] public string? SubmissionId { get; init; }
        [JsonPropertyName("payload")] public CommandPayload? Payload { get; init; }
    }

    internal sealed record CommandPayload
    {
        [JsonPropertyName("text")] public string? Text { get; init; }
        [JsonPropertyName("replyToEventId")] public string? ReplyToEventId { get; init; }
    }

    /// <summary>One claimed delivery this process owns until its terminal is committed.</summary>
    internal sealed record OwnedDelivery(
        string MessageId, string SubmissionId, string AttemptId, string ConversationId, ulong DeliveryTag);
}

/// <summary>
/// Per-conversation sender state: the in-order gate, the merged-submission bookkeeping, and the
/// attempt-scoped append stop.
/// </summary>
internal sealed class ConversationState(string conversationId)
{
    public string ConversationId { get; } = conversationId;

    /// <summary>The conversation's single in-order sender (D10).</summary>
    public SemaphoreSlim Sender { get; } = new(1, 1);

    private readonly object _lock = new();
    private readonly List<string> _queued = [];
    private readonly HashSet<string> _stoppedAttempts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _startedSubmissions = new(StringComparer.Ordinal);
    private RunningTurn? _turn;
    private long _runtimeKey;
    private Task? _drain;

    /// <summary>True when this call was the one that bound the runtime key.</summary>
    public bool TryBindRuntimeKey(long runtimeKey)
    {
        lock (_lock)
        {
            if (_runtimeKey != 0) return false;
            _runtimeKey = runtimeKey;
            return true;
        }
    }

    /// <summary>Start this conversation's drain loop exactly once.</summary>
    public void StartDrain(ConversationSouthConsumer consumer, CancellationToken ct)
    {
        lock (_lock)
        {
            _drain ??= Task.Run(() => consumer.DrainConversationAsync(this, ct), CancellationToken.None);
        }
    }

    /// <summary>Record what dispatch did, so the submission can be closed by the right commit.</summary>
    public void NoteDispositioned(string submissionId, TaskDispatchOutcome outcome)
    {
        lock (_lock)
        {
            if (outcome is TaskDispatchOutcome.Injected && _turn is not null)
            {
                // Injected means it went INTO the running turn, which is the turn that will close it.
                _turn.Merged.Add(submissionId);
                return;
            }

            if (outcome is TaskDispatchOutcome.Injected or TaskDispatchOutcome.Queued)
            {
                // Queued — and an injection whose turn.started has not been transmitted yet — are
                // carried on /turns:start instead.
                _queued.Add(submissionId);
            }
        }
    }

    /// <summary>Everything queued before this turn began, minus the turn's own submission.</summary>
    public IReadOnlyList<string> TakeQueuedExcept(string primarySubmissionId)
    {
        lock (_lock)
        {
            var taken = _queued.Where(id => !string.Equals(id, primarySubmissionId, StringComparison.Ordinal)).ToList();
            _queued.Clear();
            return taken;
        }
    }

    /// <summary>Put a merged set back when the turn start was refused.</summary>
    public void RestoreMerged(IReadOnlyList<string> merged)
    {
        lock (_lock) _queued.AddRange(merged);
    }

    public void BeginTurn(string turnId, string attemptId, string submissionId, IReadOnlyList<string> merged)
    {
        lock (_lock)
        {
            _turn = new RunningTurn(turnId, attemptId, submissionId, [.. merged]);
            _startedSubmissions.Add(submissionId);
        }
    }

    /// <summary>
    /// True once <c>/turns:start</c> has been transmitted for this submission by this process.
    /// </summary>
    /// <remarks>
    /// Never cleared by <see cref="EndTurn"/>: the question a late <c>turn.started</c> asks is "has
    /// this submission's turn ever been started", not "is a turn running now", and those differ for
    /// exactly the ordering this exists to survive.
    /// </remarks>
    public bool TurnStarted(string submissionId)
    {
        lock (_lock) return _startedSubmissions.Contains(submissionId);
    }

    /// <summary>Record a submission's turn as started without owning a running turn for it.</summary>
    public void NoteStarted(string submissionId)
    {
        lock (_lock) _startedSubmissions.Add(submissionId);
    }

    /// <summary>The submissions this turn answered besides its own. Clears the running turn.</summary>
    public IReadOnlyList<string> EndTurn(string submissionId)
    {
        lock (_lock)
        {
            if (_turn is null || !string.Equals(_turn.SubmissionId, submissionId, StringComparison.Ordinal))
                return [];

            var merged = _turn.Merged.ToList();
            _turn = null;
            return merged;
        }
    }

    public void StopAppends(string attemptId)
    {
        lock (_lock) _stoppedAttempts.Add(attemptId);
    }

    public bool AppendsStopped(string attemptId)
    {
        lock (_lock) return _stoppedAttempts.Contains(attemptId);
    }

    private sealed record RunningTurn(string TurnId, string AttemptId, string SubmissionId, HashSet<string> Merged);
}
