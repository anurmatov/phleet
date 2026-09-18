using Fleet.Conversations.Contracts;
using Fleet.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Conversations.Tests;

/// <summary>
/// The outbox's own semantics — claiming, confirm ordering and backoff — against a real MySQL.
/// </summary>
/// <remarks>
/// The transport is a test double, and deliberately so: what is under test is whether a row can be
/// marked published without a confirm, whether an unconfirmed row is retried, and whether order
/// holds — none of which is a property of the broker. The broker adapter is a thin thing with no
/// bookkeeping of its own.
/// </remarks>
[Collection("mysql")]
public sealed class OutboxPublisherTests(MySqlFixture fixture)
{
    /// <summary>Records what it was asked to publish and confirms exactly what it is told to.</summary>
    private sealed class FakeTransport : IOutboxTransport
    {
        public string Exchange => "test.exchange";

        public List<OutboxMessage> Published { get; } = [];
        public Func<IReadOnlyList<OutboxMessage>, IReadOnlySet<ulong>> ConfirmSelector { get; set; } =
            batch => batch.Select(m => m.Id).ToHashSet();
        public Exception? Throw { get; set; }

        public Task<IReadOnlySet<ulong>> PublishAsync(IReadOnlyList<OutboxMessage> batch, CancellationToken ct)
        {
            Published.AddRange(batch);
            if (Throw is { } error) throw error;
            return Task.FromResult(ConfirmSelector(batch));
        }
    }

    private async Task<(string Conversation, string Submission)> AcceptOneAsync(string text = "hello")
    {
        var store = fixture.CreateStore();

        var conversation = await store.OpenConversationAsync(new OpenConversationRequest
        {
            ChannelId = "client", ExternalRef = "conv-" + Ulid.NewUlid(), PrincipalId = "p_owner",
        });

        var accept = await store.AcceptSubmissionAsync(new AcceptSubmissionRequest
        {
            ConversationId = conversation.ConversationId,
            ExternalSubmissionId = Guid.NewGuid().ToString("n"),
            PayloadFingerprint = PayloadFingerprint.Compute(text, null, conversation.ConversationId),
            CommandKind = ConversationEventKind.SubmissionCreate,
            CommandPayloadJson = $$"""{"text":"{{text}}"}""",
        });

        return (conversation.ConversationId, accept.SubmissionId!);
    }

    private OutboxPublisher CommandPublisher(IOutboxTransport transport) =>
        new(fixture.ConnectionString, OutboxPublisher.CommandTable, transport,
            fixture.Options, NullLogger.Instance);

    private OutboxPublisher EventPublisher(IOutboxTransport transport) =>
        new(fixture.ConnectionString, OutboxPublisher.EventTable, transport,
            fixture.Options, NullLogger.Instance);

    [Fact]
    public async Task A_confirmed_row_is_marked_published_with_a_timestamp()
    {
        var (_, submissionId) = await AcceptOneAsync();
        var transport = new FakeTransport();

        var confirmed = await CommandPublisher(transport).DrainOnceAsync("example-agent");

        Assert.Equal(1, confirmed);
        Assert.Single(transport.Published);
        Assert.Equal("example-agent", transport.Published[0].RoutingKey);

        Assert.Equal("published|1", await fixture.ScalarRowAsync($"""
            SELECT state, published_at IS NOT NULL
              FROM command_outbox WHERE submission_id = '{submissionId}'
            """));
    }

    /// <summary>
    /// A row the broker did not confirm stays pending and is republished. Marking it published
    /// before the confirm loses the message AND removes the only record that would have retried it.
    /// </summary>
    [Fact]
    public async Task An_unconfirmed_row_is_never_marked_published_and_is_retried()
    {
        var (_, submissionId) = await AcceptOneAsync();

        var transport = new FakeTransport
        {
            // Published, but the confirm never came back.
            ConfirmSelector = _ => new HashSet<ulong>(),
        };

        var confirmed = await CommandPublisher(transport).DrainOnceAsync("example-agent");

        Assert.Equal(0, confirmed);
        Assert.Single(transport.Published);

        Assert.Equal("pending|NULL|1", await fixture.ScalarRowAsync($"""
            SELECT state, published_at, attempts
              FROM command_outbox WHERE submission_id = '{submissionId}'
            """));

        // It comes back once the backoff elapses, and the consumer deduplicates on the dedupe id.
        await fixture.ExecuteAsync($"""
            UPDATE command_outbox SET next_attempt_at = UTC_TIMESTAMP(6)
             WHERE submission_id = '{submissionId}'
            """);

        var retry = new FakeTransport();
        Assert.Equal(1, await CommandPublisher(retry).DrainOnceAsync("example-agent"));
        Assert.Equal(transport.Published[0].DedupeId, retry.Published[0].DedupeId);
    }

    /// <summary>An unreachable broker leaves everything pending and loses nothing.</summary>
    [Fact]
    public async Task An_unreachable_broker_leaves_the_row_pending()
    {
        var (_, submissionId) = await AcceptOneAsync();

        var transport = new FakeTransport { Throw = new IOException("broker unreachable") };
        var confirmed = await CommandPublisher(transport).DrainOnceAsync("example-agent");

        Assert.Equal(0, confirmed);
        Assert.Equal("pending|NULL", await fixture.ScalarRowAsync($"""
            SELECT state, published_at FROM command_outbox WHERE submission_id = '{submissionId}'
            """));
    }

    /// <summary>
    /// A partially confirmed batch marks exactly the confirmed rows and retries the rest.
    /// </summary>
    [Fact]
    public async Task A_partially_confirmed_batch_splits_correctly()
    {
        // Scoped to the conversations THIS test creates. The class shares one database, so a global
        // count would read other tests' rows — which is how a test passes or fails depending on what
        // ran before it.
        var mine = new List<string>();
        for (var i = 0; i < 4; i++) mine.Add((await AcceptOneAsync($"text-{i}")).Conversation);

        var scope = string.Join(", ", mine.Select(id => $"'{id}'"));

        var transport = new FakeTransport
        {
            // Confirm the first two of the batch only.
            ConfirmSelector = batch => batch.Take(2).Select(m => m.Id).ToHashSet(),
        };

        var confirmed = await CommandPublisher(transport).DrainOnceAsync("example-agent");

        Assert.Equal(2, confirmed);
        Assert.Equal("2|2", await fixture.ScalarRowAsync($"""
            SELECT (SELECT COUNT(*) FROM command_outbox
                     WHERE state = 'published' AND conversation_id IN ({scope})),
                   (SELECT COUNT(*) FROM command_outbox
                     WHERE state = 'pending' AND conversation_id IN ({scope}))
            """));
    }

    /// <summary>
    /// Every appended event gets an outbox row in the same transaction, and they publish in seq
    /// order within a conversation.
    /// </summary>
    [Fact]
    public async Task Events_are_published_in_sequence_order_within_a_conversation()
    {
        var store = fixture.CreateStore();

        var conversation = await store.OpenConversationAsync(new OpenConversationRequest
        {
            ChannelId = "client", ExternalRef = "conv-" + Ulid.NewUlid(), PrincipalId = "p_owner",
        });

        var epoch = Ulid.NewUlid();

        await store.AppendBatchAsync(new AppendBatchRequest
        {
            ConversationId = conversation.ConversationId,
            Epoch = epoch,
            Events =
            [
                Staged("first", 1), Staged("second", 2), Staged("third", 3),
            ],
        });

        var transport = new FakeTransport();
        var confirmed = await EventPublisher(transport).DrainOnceAsync(routingKey: "unused");

        // Scoped to THIS conversation. The drain takes whatever is pending in the database, and the
        // class shares one with every other class in this collection — so a global count is a
        // function of which test ran first, not of what this test is about. That was the third
        // instance of the same mistake in this file; the batch bound (100) is far above what any
        // one class leaves behind, so the three are present whatever else is.
        var mine = transport.Published
            .Where(m => m.RoutingKey == conversation.ConversationId)
            .ToArray();

        Assert.Equal(3, mine.Length);
        Assert.True(confirmed >= mine.Length);

        // Routed per conversation, and in id order, which is seq order for a single writer.
        Assert.Equal(
            mine.Select(m => m.Id).Order().ToArray(),
            mine.Select(m => m.Id).ToArray());

        static StagedEvent Staged(string text, ulong ordinal) => new()
        {
            Event = new EventDescriptor
            {
                Kind = ConversationEventKind.TurnNotice,
                EventId = Ulid.NewUlid(),
                PayloadJson = $$"""{"text":"{{text}}"}""",
            },
            Ordinal = ordinal,
            RetentionClass = EventRetentionClass.Ephemeral,
        };
    }

    /// <summary>
    /// Concurrent publishers do not hand the same row to two of them.
    /// </summary>
    /// <remarks>
    /// This is what <c>FOR UPDATE SKIP LOCKED</c> buys, and it is why the outbox needs no
    /// distributed lock.
    /// </remarks>
    [Fact]
    public async Task Two_publishers_never_claim_the_same_row()
    {
        for (var i = 0; i < 8; i++) await AcceptOneAsync($"row-{i}");

        var a = new FakeTransport();
        var b = new FakeTransport();

        await Task.WhenAll(
            CommandPublisher(a).DrainOnceAsync("example-agent"),
            CommandPublisher(b).DrainOnceAsync("example-agent"));

        var claimed = a.Published.Select(m => m.Id).Concat(b.Published.Select(m => m.Id)).ToList();

        Assert.Equal(claimed.Count, claimed.Distinct().Count());
    }

    /// <summary>
    /// The backlog measure counts pending rows and falls to zero once they publish.
    /// </summary>
    /// <remarks>
    /// Asserted as a DELTA rather than an absolute, because this class shares one database and an
    /// absolute count would depend on which tests ran first.
    /// </remarks>
    [Fact]
    public async Task The_backlog_measure_reports_depth()
    {
        // Drain anything a previous test left pending, so the delta below is this test's own.
        await CommandPublisher(new FakeTransport()).DrainOnceAsync("example-agent");

        var (before, _) = await CommandPublisher(new FakeTransport()).MeasureBacklogAsync();

        for (var i = 0; i < 3; i++) await AcceptOneAsync($"backlog-{i}");

        var (queued, _) = await CommandPublisher(new FakeTransport()).MeasureBacklogAsync();
        Assert.Equal(before + 3, queued);

        await CommandPublisher(new FakeTransport()).DrainOnceAsync("example-agent");

        var (drained, _) = await CommandPublisher(new FakeTransport()).MeasureBacklogAsync();
        Assert.Equal(0, drained);
    }
}
