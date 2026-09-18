using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Fleet.Conversations.Contracts;
using Fleet.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Fleet.Agent.Tests;

/// <summary>
/// <see cref="ConversationSouthConsumer"/> — the half that retries, allocates and acknowledges
/// (#303 D10).
/// </summary>
/// <remarks>
/// <para>
/// A recording <see cref="HttpMessageHandler"/> stands in for the south listener, so the ORDER and
/// the CONTENT of the calls are observable. The broker is not stood in for: the claim → ack loop
/// against a real queue belongs to the round-trip suite, which has one. What is asserted here is
/// everything that is decided before the socket — which endpoint an outcome takes, what an
/// unhandled envelope does, where the ordinal comes from, and that the ack never precedes the
/// commit.
/// </para>
/// <para>
/// <c>ConversationIntake</c> is passed as <c>null!</c> on the transmission paths. It is not a
/// convenience: the dispatch path is the one thing these tests must not exercise, because reaching
/// it would mean the transmission path had acquired a dependency on the runtime — and a
/// <c>NullReferenceException</c> is a louder way to learn that than a mocked call that silently
/// succeeded.
/// </para>
/// </remarks>
public sealed class ConversationSouthConsumerTests
{
    private const string ConversationId = "c_01HZZZ";
    private const string SubmissionId = "s_01HZZZ";
    private const string AttemptId = "a_01HZZZ";
    private const string MessageId = "m_01HZZZ";

    // ── the fake listener ────────────────────────────────────────────────────

    private abstract class RecordingHandler : HttpMessageHandler
    {
        /// <summary>Path and body of every call, in the order they were sent.</summary>
        public ConcurrentQueue<(string Path, string Body)> Calls { get; } = new();
    }

    private sealed class RecordingSouth : RecordingHandler
    {
        private readonly Func<string, HttpStatusCode>? _status;
        private readonly Action<string>? _onCall;

        public RecordingSouth(Func<string, HttpStatusCode>? status = null, Action<string>? onCall = null)
        {
            _status = status;
            _onCall = onCall;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);

            Calls.Enqueue((path, body));
            _onCall?.Invoke(path);

            var status = _status?.Invoke(path) ?? HttpStatusCode.OK;
            if (status != HttpStatusCode.OK)
                return new HttpResponseMessage(status) { Content = new StringContent("{}") };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ResponseFor(path), Encoding.UTF8, "application/json"),
            };
        }

        private static string ResponseFor(string path) => path switch
        {
            "/deliveries:claim" => FleetProtocolJson.Serialize(new ClaimDeliveryResult
            {
                Outcome = ClaimOutcome.Claimed,
                SubmissionId = SubmissionId,
                AttemptId = AttemptId,
                ConversationId = ConversationId,
            }),
            "/submissions:disposition" or "/deliveries:complete" =>
                FleetProtocolJson.Serialize(new DispositionResult { AcceptedSeq = 1, Replayed = false }),
            "/turns:start" => FleetProtocolJson.Serialize(new StartTurnResult { Seq = 2, Replayed = false }),
            "/turns:commit" => FleetProtocolJson.Serialize(
                new CommitTerminalResult { Seq = 9, Replayed = false, RecoveredAnswer = false }),
            "/events:append" => FleetProtocolJson.Serialize(new AppendBatchResult { Seqs = [3UL] }),
            "/leases:heartbeat" => FleetProtocolJson.Serialize(
                new HeartbeatResult { Renewed = [AttemptId], NotOwned = [] }),
            _ => "{}",
        };
    }

    private static ConversationsOptions SouthOptions() => new()
    {
        SouthBaseUrl = "http://south.invalid",
        SouthBearerToken = "token",
        AgentName = "example-agent",
        BrokerConnectionString = "amqp://broker.invalid",
        RetryBaseDelay = TimeSpan.FromMilliseconds(1),
        RetryMaxDelay = TimeSpan.FromMilliseconds(4),
    };

    private static (ConversationSouthConsumer Consumer, ConversationSouthHandoff Handoff,
        ConversationSouthCounters Counters, ConversationOrdinalAllocator Allocator)
        Build(RecordingHandler south, ConversationIntake? intake = null)
    {
        var options = Microsoft.Extensions.Options.Options.Create(SouthOptions());
        var client = new ConversationSouthClient(
            new HttpClient(south), options, NullLogger<ConversationSouthClient>.Instance);

        var handoff = new ConversationSouthHandoff();
        var counters = new ConversationSouthCounters();
        var allocator = new ConversationOrdinalAllocator();

        var consumer = new ConversationSouthConsumer(
            options, client, handoff, allocator, intake!, counters,
            NullLogger<ConversationSouthConsumer>.Instance);

        return (consumer, handoff, counters, allocator);
    }

    private static BasicDeliverEventArgs Delivery(string? kind, string? messageId = MessageId)
    {
        var properties = new BasicProperties { MessageId = messageId };

        var body = kind is null
            ? "not json at all"u8.ToArray()
            : Encoding.UTF8.GetBytes(
                $$$"""
                   {"kind":"{{{kind}}}","principalId":"p_1","channelId":"client",
                    "conversationId":"{{{ConversationId}}}","submissionId":"{{{SubmissionId}}}",
                    "payload":{"text":"hello","replyToEventId":null}}
                   """);

        return new BasicDeliverEventArgs(
            consumerTag: "ct", deliveryTag: 1, redelivered: false,
            exchange: "fleet.conversations", routingKey: "example-agent",
            properties: properties, body: body);
    }

    private static SouthOutboundItem Item(
        SouthOutboundAction action, string kind, string? turnId = null) => new()
    {
        ConversationId = ConversationId,
        Action = action,
        Event = new EventDescriptor { Kind = kind, EventId = Ulid.NewUlid(), PayloadJson = "{}" },
        RetentionClass = ConversationSouthAdapter.RetentionFor(kind),
        SubmissionId = SubmissionId,
        TurnId = turnId,
    };

    /// <summary>Run the conversation's drain loop until <paramref name="expected"/> calls land.</summary>
    private static async Task DrainAsync(
        ConversationSouthConsumer consumer, RecordingHandler south, int expected)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var state = consumer.Conversation(ConversationId);
        var loop = Task.Run(() => consumer.DrainConversationAsync(state, cts.Token), CancellationToken.None);

        while (south.Calls.Count < expected && !cts.IsCancellationRequested)
            await Task.Delay(5, CancellationToken.None);

        await cts.CancelAsync();
        await loop;
    }

    // ── D3: the two disposition endpoints ────────────────────────────────────

    /// <summary>
    /// <c>ran</c>, <c>injected</c> and <c>queued</c> go to <c>/submissions:disposition</c>;
    /// <c>queue_full</c> and <c>dropped</c> go to <c>/deliveries:complete</c>.
    /// </summary>
    /// <remarks>
    /// The store refuses the wrong endpoint in BOTH directions, because recording a submission
    /// through both would append <c>submission.accepted</c> twice for one submission.
    /// </remarks>
    [Theory]
    [InlineData(TaskDispatchOutcome.Ran, false)]
    [InlineData(TaskDispatchOutcome.Injected, false)]
    [InlineData(TaskDispatchOutcome.Queued, false)]
    [InlineData(TaskDispatchOutcome.QueueFull, true)]
    [InlineData(TaskDispatchOutcome.Dropped, true)]
    public void DispatchOutcomesSplitAcrossTheTwoEndpoints(TaskDispatchOutcome outcome, bool terminal) =>
        Assert.Equal(terminal, ConversationSouthConsumer.TerminatesOnArrival(outcome));

    /// <summary>The five values map 1:1 onto the wire enum — no invented sixth meaning.</summary>
    [Theory]
    [InlineData(TaskDispatchOutcome.Ran, SubmissionDisposition.Ran)]
    [InlineData(TaskDispatchOutcome.Injected, SubmissionDisposition.Injected)]
    [InlineData(TaskDispatchOutcome.Queued, SubmissionDisposition.Queued)]
    [InlineData(TaskDispatchOutcome.QueueFull, SubmissionDisposition.QueueFull)]
    [InlineData(TaskDispatchOutcome.Dropped, SubmissionDisposition.Dropped)]
    public void TheDispositionMappingIsOneToOne(TaskDispatchOutcome outcome, SubmissionDisposition expected) =>
        Assert.Equal(expected, ConversationSouthConsumer.Map(outcome));

    // ── D2: claim outcomes ───────────────────────────────────────────────────

    /// <summary>
    /// <c>DuplicateDone</c> acks and starts no turn.
    /// </summary>
    /// <remarks>
    /// Within its retention the <c>done</c> claim is the only thing standing between a redelivery
    /// and a duplicate turn, so acting on it again is the failure this outcome exists to prevent.
    /// </remarks>
    [Fact]
    public async Task ADuplicateClaimIsAckedAndStartsNoTurn()
    {
        var duplicate = new RecordingSouthWithClaim(ClaimOutcome.DuplicateDone);
        var (consumer, _, counters, _) = Build(duplicate);

        var acked = new List<ulong>();
        consumer.AckHook = tag => { acked.Add(tag); return Task.CompletedTask; };

        await consumer.HandleDeliveryAsync(
            Delivery(ConversationEventKind.SubmissionCreate), CancellationToken.None);

        Assert.Equal([1UL], acked);
        Assert.Equal(1, counters.ClaimCount(nameof(ClaimOutcome.DuplicateDone)));
        Assert.DoesNotContain(duplicate.Calls, c => c.Path != "/deliveries:claim");
        Assert.Empty(consumer.OwnedAttemptIds);
    }

    /// <summary>
    /// <c>HeldElsewhere</c> is a normal race outcome: no ack, no turn, no error — and a requeue that
    /// happens only after a backoff.
    /// </summary>
    /// <remarks>
    /// The store returns <c>HeldElsewhere</c> on any live claim WITHOUT comparing owner, so a
    /// redelivery to the holder itself lands here too. An immediate requeue would then spin
    /// claim → HeldElsewhere → nack → claim against the store for the whole claim-hold duration.
    /// </remarks>
    [Fact]
    public async Task ALostClaimRaceIsNotAckedAndStartsNoTurn()
    {
        var south = new RecordingSouthWithClaim(ClaimOutcome.HeldElsewhere);
        var (consumer, _, counters, _) = Build(south);

        var acked = new List<ulong>();
        consumer.AckHook = tag => { acked.Add(tag); return Task.CompletedTask; };

        await consumer.HandleDeliveryAsync(
            Delivery(ConversationEventKind.SubmissionCreate), CancellationToken.None);

        Assert.Empty(acked);
        Assert.Equal(1, counters.ClaimCount(nameof(ClaimOutcome.HeldElsewhere)));
        Assert.DoesNotContain(south.Calls, c => c.Path != "/deliveries:claim");
        Assert.Empty(consumer.OwnedAttemptIds);
    }

    // ── D11 / D12: cancel and unknown kinds ──────────────────────────────────

    /// <summary>
    /// A <c>submission.cancel</c> is claimed, terminated on arrival with <c>dropped</c>, and acked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It cannot simply be ignored. An unclaimed or undispositioned cancel leaves a held claim and a
    /// live lease, and the reconciler eventually turns that into
    /// <c>turn.outcome_unknown { attempt_abandoned }</c> against a client that is still waiting —
    /// verbatim the symptom this whole issue exists to end.
    /// </para>
    /// <para>
    /// It is also not requeued: nothing about a redelivery would make this consumer able to execute
    /// it, so a requeue is an infinite broker loop (MUST NOT 12).
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ACancelIsClaimedTerminatedAndAcked()
    {
        var south = new RecordingSouth();
        var (consumer, _, counters, _) = Build(south);

        var acked = new List<ulong>();
        consumer.AckHook = tag => { acked.Add(tag); return Task.CompletedTask; };

        await consumer.HandleDeliveryAsync(
            Delivery(ConversationEventKind.SubmissionCancel), CancellationToken.None);

        var paths = south.Calls.Select(c => c.Path).ToList();
        Assert.Equal(["/deliveries:claim", "/deliveries:complete"], paths);
        Assert.Equal([1UL], acked);
        Assert.Equal(1, counters.DispositionCount(ConversationSouthCounters.DispositionCancel));

        // No turn was started and nothing is left owned, so no lease can expire against it.
        Assert.DoesNotContain(paths, p => p == "/turns:start");
        Assert.Empty(consumer.OwnedAttemptIds);

        var complete = south.Calls.Single(c => c.Path == "/deliveries:complete").Body;
        Assert.Contains("\"dropped\"", complete, StringComparison.Ordinal);
    }

    /// <summary>An unrecognised kind takes the identical path, under its own counter label.</summary>
    [Fact]
    public async Task AnUnrecognisedKindIsTerminatedRatherThanRequeued()
    {
        var south = new RecordingSouth();
        var (consumer, _, counters, _) = Build(south);

        var acked = new List<ulong>();
        consumer.AckHook = tag => { acked.Add(tag); return Task.CompletedTask; };

        await consumer.HandleDeliveryAsync(Delivery("submission.telepathy"), CancellationToken.None);

        Assert.Equal(
            ["/deliveries:claim", "/deliveries:complete"],
            south.Calls.Select(c => c.Path).ToList());
        Assert.Equal([1UL], acked);
        Assert.Equal(1, counters.DispositionCount(ConversationSouthCounters.DispositionUnknownKind));
        Assert.Empty(consumer.OwnedAttemptIds);
    }

    /// <summary>An unreadable envelope is terminated too — it is an envelope whose kind is unknown.</summary>
    [Fact]
    public async Task AnUnreadableEnvelopeIsTerminatedRatherThanLeaked()
    {
        var south = new RecordingSouth();
        var (consumer, _, counters, _) = Build(south);
        consumer.AckHook = _ => Task.CompletedTask;

        await consumer.HandleDeliveryAsync(Delivery(kind: null), CancellationToken.None);

        Assert.Contains(south.Calls, c => c.Path == "/deliveries:complete");
        Assert.Equal(1, counters.DispositionCount(ConversationSouthCounters.DispositionUnknownKind));
    }

    /// <summary>
    /// A delivery with no message id is acked, not requeued.
    /// </summary>
    /// <remarks>
    /// The claim key IS the message id. Without one there is nothing to claim and nothing that could
    /// make a redelivery idempotent, so a requeue would loop forever against a message that can
    /// never be handled.
    /// </remarks>
    [Fact]
    public async Task ADeliveryWithNoMessageIdIsAckedWithoutAClaim()
    {
        var south = new RecordingSouth();
        var (consumer, _, _, _) = Build(south);

        var acked = new List<ulong>();
        consumer.AckHook = tag => { acked.Add(tag); return Task.CompletedTask; };

        await consumer.HandleDeliveryAsync(
            Delivery(ConversationEventKind.SubmissionCreate, messageId: null), CancellationToken.None);

        Assert.Equal([1UL], acked);
        Assert.Empty(south.Calls);
    }

    // ── D7a: send-site allocation and in-order transmission ──────────────────

    /// <summary>
    /// Ordinals are taken in TRANSMISSION order, so a terminal that the bus handed over ahead of
    /// earlier progress still carries the higher number when it is sent last.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The property under test is that ordinal order and wire order are the same object. Allocation
    /// at translation time would let the two disagree whenever the bus reordered anything — and the
    /// bus reorders by design, draining terminal outboxes before the shared progress channel.
    /// </para>
    /// <para>
    /// The mutation this fails on is moving <c>_allocator.Next(...)</c> out of the send site.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task OrdinalsAreAllocatedAtTheSendSiteInTransmissionOrder()
    {
        var south = new RecordingSouth();
        var (consumer, handoff, _, _) = Build(south);

        consumer.TrackForTesting(
            new ConversationSouthConsumer.OwnedDelivery(MessageId, SubmissionId, AttemptId, ConversationId, 1));
        consumer.AckHook = _ => Task.CompletedTask;

        Assert.True(handoff.TryEnqueue(Item(SouthOutboundAction.StartTurn, ConversationEventKind.TurnStarted, "t_1")));
        Assert.True(handoff.TryEnqueue(Item(SouthOutboundAction.Append, ConversationEventKind.TurnProgress)));
        Assert.True(handoff.TryEnqueue(Item(SouthOutboundAction.Append, ConversationEventKind.TurnProgress)));
        Assert.True(handoff.TryEnqueue(Item(SouthOutboundAction.Commit, ConversationEventKind.TurnFinal)));

        await DrainAsync(consumer, south, expected: 4);

        var calls = south.Calls.ToList();
        Assert.Equal(
            ["/turns:start", "/events:append", "/events:append", "/turns:commit"],
            calls.Select(c => c.Path).ToList());

        // 1, 2, 3, 4 — strictly increasing, in the order the calls went out.
        var ordinals = calls.Select(c => OrdinalOf(c.Body)).ToList();
        Assert.Equal([1UL, 2UL, 3UL, 4UL], ordinals);
    }

    /// <summary>
    /// Every south call for a conversation carries the SAME epoch.
    /// </summary>
    /// <remarks>
    /// A per-call epoch would read to the store as a restart on every write, and the store resets
    /// <c>last_ordinal</c> on a higher epoch — a fence that resets on every write is not a fence.
    /// </remarks>
    [Fact]
    public async Task EverySouthCallCarriesTheProcessEpoch()
    {
        var south = new RecordingSouth();
        var (consumer, handoff, _, allocator) = Build(south);

        consumer.TrackForTesting(
            new ConversationSouthConsumer.OwnedDelivery(MessageId, SubmissionId, AttemptId, ConversationId, 1));
        consumer.AckHook = _ => Task.CompletedTask;

        handoff.TryEnqueue(Item(SouthOutboundAction.Append, ConversationEventKind.TurnProgress));
        handoff.TryEnqueue(Item(SouthOutboundAction.Commit, ConversationEventKind.TurnFinal));

        await DrainAsync(consumer, south, expected: 2);

        Assert.All(south.Calls, call =>
            Assert.Contains($"\"{allocator.Epoch}\"", call.Body, StringComparison.Ordinal));
    }

    // ── MUST NOT 5: commit before ack ────────────────────────────────────────

    /// <summary>
    /// The ack happens AFTER the commit, and only after it succeeds.
    /// </summary>
    /// <remarks>
    /// An ack-then-commit ordering loses a turn's outcome permanently on a crash in the window, and
    /// nothing detects it. Asserted on the ack hook rather than on bookkeeping, because the mutation
    /// this guards against is a reordering of exactly those two statements.
    /// </remarks>
    [Fact]
    public async Task TheTerminalIsCommittedBeforeTheMessageIsAcked()
    {
        // BOTH sides of the ordering are recorded into ONE list. An earlier revision recorded only
        // the ack and asserted `["ack"]`, which is true whatever the commit did — hoisting the ack
        // above the commit left that test green. A relative-order assertion has to observe both
        // events or it is asserting nothing about order at all.
        var order = new List<string>();
        var south = new RecordingSouth(onCall: path =>
        {
            if (path == "/turns:commit") order.Add("commit");
        });

        var (consumer, handoff, counters, _) = Build(south);

        consumer.TrackForTesting(
            new ConversationSouthConsumer.OwnedDelivery(MessageId, SubmissionId, AttemptId, ConversationId, 1));

        consumer.AckHook = _ => { order.Add("ack"); return Task.CompletedTask; };

        handoff.TryEnqueue(Item(SouthOutboundAction.Commit, ConversationEventKind.TurnFinal));

        await DrainAsync(consumer, south, expected: 1);
        await WaitForAsync(() => order.Contains("ack"));

        Assert.Equal("/turns:commit", south.Calls.Single().Path);
        Assert.Equal(["commit", "ack"], order);
        Assert.Equal(1, counters.CommitCount);
        Assert.False(consumer.OwnsForTesting(SubmissionId));
    }

    /// <summary>
    /// A commit that fails after its retries leaves the message UNACKED.
    /// </summary>
    /// <remarks>
    /// A redelivery meets a <c>done</c> claim and is a no-op, and the lease expires into an honest
    /// <c>outcome_unknown</c>. Both are better than acking a turn whose outcome was never recorded —
    /// which is silent, permanent loss.
    /// </remarks>
    [Fact]
    public async Task AFailedCommitDoesNotAck()
    {
        var south = new RecordingSouth(path =>
            path == "/turns:commit" ? HttpStatusCode.InternalServerError : HttpStatusCode.OK);

        var (consumer, handoff, counters, _) = Build(south);

        consumer.TrackForTesting(
            new ConversationSouthConsumer.OwnedDelivery(MessageId, SubmissionId, AttemptId, ConversationId, 1));

        var acked = new List<ulong>();
        consumer.AckHook = tag => { acked.Add(tag); return Task.CompletedTask; };

        handoff.TryEnqueue(Item(SouthOutboundAction.Commit, ConversationEventKind.TurnFinal));

        // Retried, then given up on — every attempt is a recorded call.
        await DrainAsync(consumer, south, expected: 8);

        Assert.Empty(acked);
        Assert.Equal(0, counters.CommitCount);

        // Still owned, so the heartbeat keeps renewing it and the lease is the thing that decides.
        Assert.True(consumer.OwnsForTesting(SubmissionId));
    }

    // ── D6: a recovered answer is a success ──────────────────────────────────

    /// <summary>
    /// <c>RecoveredAnswer</c> means the reconciler won the race and the answer was preserved as
    /// <c>turn.recovered_answer</c>. It is a successful commit — logged and counted, never retried.
    /// </summary>
    [Fact]
    public async Task ARecoveredAnswerCountsAsACommitAndIsNotRetried()
    {
        var south = new RecoveredAnswerSouth();
        var (consumer, handoff, counters, _) = Build(south);

        consumer.TrackForTesting(
            new ConversationSouthConsumer.OwnedDelivery(MessageId, SubmissionId, AttemptId, ConversationId, 1));
        consumer.AckHook = _ => Task.CompletedTask;

        handoff.TryEnqueue(Item(SouthOutboundAction.Commit, ConversationEventKind.TurnFinal));

        await DrainAsync(consumer, south, expected: 1);

        Assert.Single(south.Calls);
        Assert.Equal(1, counters.CommitCount);
        Assert.Equal(1, counters.RecoveredAnswerCount);
    }

    // ── the failure table: a null seq is expected, not an error ──────────────

    /// <summary>
    /// An append that returns a <c>null</c> seq was dropped after a terminal. Counted under its own
    /// label rather than treated as a failure — the client already has its terminal.
    /// </summary>
    [Fact]
    public async Task AnAppendDroppedAfterATerminalIsCountedRatherThanRetried()
    {
        var south = new NullSeqSouth();
        var (consumer, handoff, counters, _) = Build(south);

        consumer.TrackForTesting(
            new ConversationSouthConsumer.OwnedDelivery(MessageId, SubmissionId, AttemptId, ConversationId, 1));
        consumer.AckHook = _ => Task.CompletedTask;

        handoff.TryEnqueue(Item(SouthOutboundAction.Append, ConversationEventKind.TurnProgress));

        await DrainAsync(consumer, south, expected: 1);

        Assert.Single(south.Calls);
        Assert.Equal(1, counters.AppendDroppedAfterTerminalCount);
        Assert.Equal(0, counters.FenceRejectionCount);
    }

    /// <summary>
    /// A rejected append stops appending for THAT ATTEMPT, and never for the conversation.
    /// </summary>
    /// <remarks>
    /// A conversation-wide stop turns one rejected write into permanent silence for that client, for
    /// the rest of the process lifetime — after the first turn that had anything still queued when
    /// it finished, which is the common case rather than the edge case (MUST NOT 10).
    /// </remarks>
    [Fact]
    public async Task ARejectedAppendStopsTheAttemptAndNotTheConversation()
    {
        var south = new RecordingSouth(path =>
            path == "/events:append" ? HttpStatusCode.InternalServerError : HttpStatusCode.OK);

        var (consumer, handoff, counters, _) = Build(south);

        consumer.TrackForTesting(
            new ConversationSouthConsumer.OwnedDelivery(MessageId, SubmissionId, AttemptId, ConversationId, 1));
        consumer.AckHook = _ => Task.CompletedTask;

        handoff.TryEnqueue(Item(SouthOutboundAction.Append, ConversationEventKind.TurnProgress));
        await DrainAsync(consumer, south, expected: 5);

        Assert.Equal(1, counters.FenceRejectionCount);

        var state = consumer.Conversation(ConversationId);
        Assert.True(state.AppendsStopped(AttemptId));

        // A DIFFERENT attempt in the same conversation is untouched.
        Assert.False(state.AppendsStopped("a_other"));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static ulong OrdinalOf(string body)
    {
        using var document = System.Text.Json.JsonDocument.Parse(body);
        var root = document.RootElement;

        if (root.TryGetProperty("ordinal", out var ordinal))
            return ordinal.GetUInt64();

        // An append batch carries the ordinal per staged event.
        return root.GetProperty("events")[0].GetProperty("ordinal").GetUInt64();
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(5);
    }

    /// <summary>A listener whose claim always reports a fixed outcome.</summary>
    private sealed class RecordingSouthWithClaim(ClaimOutcome outcome) : RecordingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Calls.Enqueue((path, await request.Content!.ReadAsStringAsync(cancellationToken)));

            // DuplicateDone and HeldElsewhere return NO identifiers at all — that is the contract,
            // and a consumer that read them anyway would act on someone else's attempt.
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    FleetProtocolJson.Serialize(new ClaimDeliveryResult { Outcome = outcome }),
                    Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class RecoveredAnswerSouth : RecordingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Enqueue((request.RequestUri!.AbsolutePath,
                await request.Content!.ReadAsStringAsync(cancellationToken)));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    FleetProtocolJson.Serialize(new CommitTerminalResult
                    {
                        Seq = 9, Replayed = false, RecoveredAnswer = true,
                    }), Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class NullSeqSouth : RecordingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Enqueue((request.RequestUri!.AbsolutePath,
                await request.Content!.ReadAsStringAsync(cancellationToken)));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    FleetProtocolJson.Serialize(new AppendBatchResult { Seqs = [null] }),
                    Encoding.UTF8, "application/json"),
            };
        }
    }
}
