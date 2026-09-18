using System.Runtime.CompilerServices;
using Fleet.Conversations.Contracts;
using Fleet.Protocol;

namespace Fleet.Comms.Tests;

/// <summary>
/// An in-process <see cref="IConversationStore"/> for the route tests.
/// </summary>
/// <remarks>
/// <para>
/// A substitute, deliberately, and only for the questions these tests ask: what the route table is,
/// which status a given request produces, what the body looks like, and whether a disabled install
/// touches the store at all. None of those is a question about MySQL.
/// </para>
/// <para>
/// The transactional behaviour — admission, idempotency, claims, seq allocation — is exercised
/// against a real database in <c>Fleet.Conversations.Tests</c>, which fails rather than skips
/// without one. This class must never be used to make a claim about that layer.
/// </para>
/// </remarks>
internal sealed class FakeConversationStore : IConversationStore
{
    /// <summary>Principal the fake conversation belongs to. Anything else is not-found.</summary>
    public string OwnerPrincipalId { get; set; } = "p_owner";

    public string ConversationId { get; set; } = "01JCONVERSATION00000000001";

    public ulong NextSeq { get; set; } = 41;

    public ulong RetainedFloorSeq { get; set; } = 1;

    /// <summary>What the next accept returns. Set per test.</summary>
    public AcceptOutcome NextOutcome { get; set; } = AcceptOutcome.Accepted;

    /// <summary>Commands this store was asked to dispatch, in order.</summary>
    public List<AcceptSubmissionRequest> Accepted { get; } = [];

    public List<AckCursorRequest> Cursors { get; } = [];

    /// <summary>Events catch-up returns. Empty unless a test sets them.</summary>
    public List<StoredEvent> Events { get; } = [];

    public ConversationReplayGapPayload? Gap { get; set; }

    public Task<OpenConversationResult> OpenConversationAsync(
        OpenConversationRequest request, CancellationToken ct = default) =>
        Task.FromResult(new OpenConversationResult
        {
            ConversationId = ConversationId,
            NextSeq = NextSeq,
            RetainedFloorSeq = RetainedFloorSeq,
        });

    public Task<AcceptSubmissionResult> AcceptSubmissionAsync(
        AcceptSubmissionRequest request, CancellationToken ct = default)
    {
        Accepted.Add(request);

        return Task.FromResult(new AcceptSubmissionResult
        {
            Outcome = NextOutcome,
            SubmissionId = "01JSUBMISSION0000000000001",
            ExternalSubmissionId = request.ExternalSubmissionId,
            AcceptedSeq = NextOutcome is AcceptOutcome.Accepted or AcceptOutcome.Replay
                ? NextSeq
                : null,
        });
    }

    public Task<ReadConversationResult> ReadAsync(
        ReadConversationRequest request, CancellationToken ct = default)
    {
        // The existence oracle the real store closes, closed here too: a conversation belonging to
        // someone else is reported exactly as one that does not exist.
        if (!string.Equals(request.PrincipalId, OwnerPrincipalId, StringComparison.Ordinal))
            throw new Fleet.Conversations.ConversationNotFoundException();

        if (request.AfterSeq >= NextSeq)
            throw new Fleet.Conversations.InvalidCursorException("cursor is at or beyond nextSeq");

        return Task.FromResult(new ReadConversationResult
        {
            Gap = Gap,
            Events = Events,
            NextAfterSeq = Events.Count > 0 ? Events[^1].Seq : request.AfterSeq,
            HasMore = false,
        });
    }

    public Task AckCursorAsync(AckCursorRequest request, CancellationToken ct = default)
    {
        Cursors.Add(request);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<StoredEvent> TailAsync(
        string conversationId, ulong afterSeq, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var stored in Events.Where(e => e.Seq > afterSeq))
            yield return stored;

        // Then wait, like the real tail does, rather than completing and closing the socket.
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }

    // ── south surface: not reachable from the north listener, and never called here ──

    public Task<ClaimDeliveryResult> ClaimDeliveryAsync(
        ClaimDeliveryRequest request, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<DispositionResult> RecordDispositionAsync(
        RecordDispositionRequest request, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<DispositionResult> CompleteDeliveryAsync(
        CompleteDeliveryRequest request, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<StartTurnResult> StartTurnAsync(
        StartTurnRequest request, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<AppendBatchResult> AppendBatchAsync(
        AppendBatchRequest request, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<CommitTerminalResult> CommitTerminalAsync(
        CommitTerminalRequest request, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<HeartbeatResult> HeartbeatAsync(
        HeartbeatRequest request, CancellationToken ct = default) => throw new NotSupportedException();

    public Task MarkExternalEffectAsync(string attemptId, CancellationToken ct = default) =>
        throw new NotSupportedException();

    /// <summary>A stored event with the fields these tests care about and defaults for the rest.</summary>
    public static StoredEvent Stored(ulong seq, string kind = ConversationEventKind.TurnStarted) =>
        new()
        {
            Seq = seq,
            EventId = $"01JEVENT{seq:D18}",
            Kind = kind,
            EmittedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            PayloadJson = null,
            IsTerminal = ConversationEventKind.Terminal.Contains(kind),
            RetentionClass = EventRetentionClass.Durable,
            SubmissionId = "01JSUBMISSION0000000000001",
        };
}
