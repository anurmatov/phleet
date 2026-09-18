using System.Net;
using Fleet.Comms.Contracts;
using Fleet.Conversations.Contracts;
using Fleet.Protocol;

namespace Fleet.Conversations.Tests;

/// <summary>
/// The south listener, over HTTP, against a real store (AC30 south half, AC31).
/// </summary>
/// <remarks>
/// <para>
/// Every other suite in this project calls <see cref="IConversationStore"/> directly. That proves
/// the transactions and says nothing about the surface the agent actually talks to: the bearer
/// filter, the fixed 401 body, the status mapping, and — the one that turned out to matter — whether
/// a request body written in the protocol's own wire vocabulary can be bound at all.
/// </para>
/// <para>
/// A route surface with zero requests against it is a test that never executes the thing it names.
/// </para>
/// </remarks>
[Collection("mysql")]
public sealed class SouthSurfaceTests(MySqlFixture fixture)
{
    private readonly MySqlConversationStore _store = fixture.CreateStore();

    /// <summary>
    /// The seven endpoints of this slice, with a body that BINDS.
    /// </summary>
    /// <remarks>
    /// Bodies are well-formed on purpose, so a refusal is unambiguously about the credential and
    /// not about model binding. The malformed case is asserted separately, and deliberately — see
    /// <see cref="An_unparseable_body_is_still_refused_before_it_is_parsed"/>.
    /// </remarks>
    public static TheoryData<string, object> SouthRequests() => new()
    {
        {
            "/deliveries:claim",
            new ClaimDeliveryRequest { MessageId = "m_absent", Owner = "agent-1" }
        },
        {
            "/submissions:disposition",
            new RecordDispositionRequest
            {
                MessageId = "m_absent", SubmissionId = "s_absent", AttemptId = "a_absent",
                Disposition = SubmissionDisposition.Ran, Epoch = "e", Ordinal = 1, Owner = "agent-1",
            }
        },
        {
            "/deliveries:complete",
            new CompleteDeliveryRequest
            {
                MessageId = "m_absent", SubmissionId = "s_absent", AttemptId = "a_absent",
                Disposition = SubmissionDisposition.Dropped, Epoch = "e", Ordinal = 1,
            }
        },
        {
            "/turns:start",
            new StartTurnRequest { AttemptId = "a_absent", TurnId = "t", Epoch = "e", Ordinal = 1 }
        },
        {
            "/turns:commit",
            new CommitTerminalRequest
            {
                AttemptId = "a_absent",
                TerminalEvent = new EventDescriptor
                {
                    Kind = ConversationEventKind.TurnFinal, EventId = Ulid.NewUlid(),
                },
                Epoch = "e", Ordinal = 1,
            }
        },
        {
            "/events:append",
            new AppendBatchRequest { ConversationId = "c_absent", Epoch = "e", Events = [] }
        },
        {
            "/leases:heartbeat",
            new HeartbeatRequest { AttemptIds = ["a_absent"], Owner = "agent-1" }
        },
    };

    // ── AC31: the credential ─────────────────────────────────────────────────

    /// <summary>
    /// No Authorization header: 401, on every one of the seven.
    /// </summary>
    /// <remarks>
    /// Asserted per endpoint rather than on one of them. The filter is on the group, so one
    /// endpoint passing is weak evidence for the rest — and the case this guards against is
    /// precisely an endpoint that was added outside the group.
    /// </remarks>
    [Theory]
    [MemberData(nameof(SouthRequests))]
    public async Task An_absent_credential_is_refused(string path, object body)
    {
        await using var host = await SouthTestHost.StartAsync(_store);

        var response = await host.PostAnonymouslyAsync(path, body);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(SouthRequests))]
    public async Task A_wrong_credential_is_refused(string path, object body)
    {
        await using var host = await SouthTestHost.StartAsync(_store);

        var response = await host.PostWithWrongTokenAsync(path, body);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Absent and wrong produce the SAME bytes, and those bytes are the fixed constant.
    /// </summary>
    /// <remarks>
    /// Compared as raw strings rather than as deserialized objects: a differing field order or a
    /// runtime-derived <c>message</c> is an oracle just as surely as a differing code would be, and
    /// neither shows up once the body has been parsed back into a record.
    /// </remarks>
    [Fact]
    public async Task The_two_refusals_are_byte_identical_and_carry_the_fixed_body()
    {
        await using var host = await SouthTestHost.StartAsync(_store);
        var body = new ClaimDeliveryRequest { MessageId = "m_absent", Owner = "agent-1" };

        var absent = await host.PostAnonymouslyAsync("/deliveries:claim", body);
        var wrong = await host.PostWithWrongTokenAsync("/deliveries:claim", body);

        var absentBytes = await absent.Content.ReadAsStringAsync();
        var wrongBytes = await wrong.Content.ReadAsStringAsync();

        Assert.Equal(absentBytes, wrongBytes);
        Assert.Equal(
            FleetProtocolJson.Serialize(ErrorResponse.For(ProtocolErrorCode.Unauthorized)),
            absentBytes);
    }

    /// <summary>
    /// A near-miss credential — the right token with one character removed — is refused.
    /// </summary>
    /// <remarks>
    /// A prefix comparison, or a length-only check, passes every other test in this class and fails
    /// this one.
    /// </remarks>
    [Fact]
    public async Task A_credential_that_is_a_prefix_of_the_real_one_is_refused()
    {
        await using var host = await SouthTestHost.StartAsync(_store);

        var request = new HttpRequestMessage(HttpMethod.Post, "/deliveries:claim")
        {
            Content = new StringContent(
                FleetProtocolJson.Serialize(
                    new ClaimDeliveryRequest { MessageId = "m_absent", Owner = "agent-1" }),
                System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation(
            "Authorization", $"Bearer {SouthTestHost.Token[..^1]}");

        var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// A body that cannot be parsed is STILL a 401 when the credential is absent — the credential
    /// is checked before the body is read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the test that distinguishes middleware from an endpoint filter, and it is not
    /// theoretical: as a filter it ran after argument binding, so an anonymous caller sending an
    /// unparseable body got the framework's empty-bodied 400 instead. That hands an unauthenticated
    /// caller a body-shape oracle — send a shape, watch 400 become 401 — and it breaks the
    /// indistinguishability the fixed body exists to provide.
    /// </para>
    /// <para>
    /// A path that matches no endpoint is included for the same reason: without the credential, the
    /// surface does not disclose which routes exist either.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("/submissions:disposition")]
    [InlineData("/deliveries:claim")]
    [InlineData("/no-such-endpoint")]
    public async Task An_unparseable_body_is_still_refused_before_it_is_parsed(string path)
    {
        await using var host = await SouthTestHost.StartAsync(_store);

        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent("{ this is not json", System.Text.Encoding.UTF8,
                "application/json"),
        };

        var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(
            FleetProtocolJson.Serialize(ErrorResponse.For(ProtocolErrorCode.Unauthorized)),
            await response.Content.ReadAsStringAsync());
    }

    // ── AC30: the surface actually carries the protocol ──────────────────────

    /// <summary>
    /// One command's whole south-side lifecycle, entirely over HTTP: claim, disposition, turn
    /// start, heartbeat, an append, and the terminal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The assertions are on what the store recorded, read back through the database, so this is a
    /// round-trip rather than an echo: a route that deserialized into the wrong field would return
    /// a plausible response and leave the wrong row.
    /// </para>
    /// <para>
    /// It is one test rather than six because the steps are not independent — a disposition needs a
    /// claim, a turn needs a disposition — and splitting them would either re-run the prefix six
    /// times or hide the ordering the surface exists to carry.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_command_completes_its_whole_south_lifecycle_over_http()
    {
        await using var host = await SouthTestHost.StartAsync(_store);

        var conversation = await OpenAsync();
        var accept = await AcceptAsync(conversation.ConversationId);
        var messageId = await MessageIdForAsync(accept.SubmissionId!);
        var epoch = Ulid.NewUlid();

        // claim
        var claimResponse = await host.PostAsync("/deliveries:claim",
            new ClaimDeliveryRequest { MessageId = messageId, Owner = "agent-1" });

        Assert.Equal(HttpStatusCode.OK, claimResponse.StatusCode);
        var claim = await SouthTestHost.ReadAsync<ClaimDeliveryResult>(claimResponse);

        Assert.Equal(ClaimOutcome.Claimed, claim.Outcome);
        Assert.Equal(accept.SubmissionId, claim.SubmissionId);
        Assert.Equal(conversation.ConversationId, claim.ConversationId);
        Assert.NotNull(claim.AttemptId);

        // disposition
        var dispositionResponse = await host.PostAsync("/submissions:disposition",
            new RecordDispositionRequest
            {
                MessageId = messageId,
                SubmissionId = claim.SubmissionId!,
                AttemptId = claim.AttemptId!,
                Disposition = SubmissionDisposition.Ran,
                Epoch = epoch,
                Ordinal = 1,
                Owner = "agent-1",
            });

        Assert.Equal(HttpStatusCode.OK, dispositionResponse.StatusCode);
        var disposition = await SouthTestHost.ReadAsync<DispositionResult>(dispositionResponse);

        Assert.False(disposition.Replayed);
        Assert.Equal(accept.AcceptedSeq, disposition.AcceptedSeq);

        // The disposition the store recorded is the one the wire carried. This is the assertion
        // that fails if the enum arrives as a default rather than as the value sent.
        Assert.Equal("ran", await fixture.ScalarRowAsync(
            $"SELECT disposition FROM delivery_claims WHERE claim_key = 'inbound:{messageId}'"));

        // turn
        var startResponse = await host.PostAsync("/turns:start",
            new StartTurnRequest
            {
                AttemptId = claim.AttemptId!, TurnId = "turn-1", Epoch = epoch, Ordinal = 10,
            });

        Assert.Equal(HttpStatusCode.OK, startResponse.StatusCode);
        var start = await SouthTestHost.ReadAsync<StartTurnResult>(startResponse);
        Assert.False(start.Replayed);

        // heartbeat
        var heartbeatResponse = await host.PostAsync("/leases:heartbeat",
            new HeartbeatRequest { AttemptIds = [claim.AttemptId!], Owner = "agent-1" });

        Assert.Equal(HttpStatusCode.OK, heartbeatResponse.StatusCode);
        var heartbeat = await SouthTestHost.ReadAsync<HeartbeatResult>(heartbeatResponse);

        Assert.Equal([claim.AttemptId], heartbeat.Renewed);
        Assert.Empty(heartbeat.NotOwned);

        // progress append
        var appendResponse = await host.PostAsync("/events:append",
            new AppendBatchRequest
            {
                ConversationId = conversation.ConversationId,
                Epoch = epoch,
                Events =
                [
                    new StagedEvent
                    {
                        Event = new EventDescriptor
                        {
                            Kind = ConversationEventKind.TurnProgress,
                            EventId = Ulid.NewUlid(),
                            PayloadJson = """{"activity":"typing"}""",
                        },
                        Ordinal = 11,
                        SubmissionId = claim.SubmissionId,
                        AttemptId = claim.AttemptId,
                        RetentionClass = EventRetentionClass.Ephemeral,
                    },
                ],
            });

        Assert.Equal(HttpStatusCode.OK, appendResponse.StatusCode);
        var appended = await SouthTestHost.ReadAsync<AppendBatchResult>(appendResponse);
        Assert.NotNull(Assert.Single(appended.Seqs));

        // terminal
        var commitResponse = await host.PostAsync("/turns:commit",
            new CommitTerminalRequest
            {
                AttemptId = claim.AttemptId!,
                TerminalEvent = new EventDescriptor
                {
                    Kind = ConversationEventKind.TurnFinal,
                    EventId = Ulid.NewUlid(),
                    PayloadJson = """{"text":"done"}""",
                },
                Epoch = epoch,
                Ordinal = 20,
            });

        Assert.Equal(HttpStatusCode.OK, commitResponse.StatusCode);
        var commit = await SouthTestHost.ReadAsync<CommitTerminalResult>(commitResponse);

        Assert.False(commit.Replayed);
        Assert.False(commit.RecoveredAnswer);
        Assert.True(commit.Seq > start.Seq);

        // And the attempt really is committed, read from the row rather than from the response.
        Assert.Equal("committed", await fixture.ScalarRowAsync(
            $"SELECT state FROM execution_attempts WHERE id = '{claim.AttemptId}'"));
    }

    /// <summary>
    /// A redelivered claim over HTTP reports the duplicate rather than starting a second turn.
    /// </summary>
    [Fact]
    public async Task A_redelivered_claim_is_reported_as_a_duplicate()
    {
        await using var host = await SouthTestHost.StartAsync(_store);

        var conversation = await OpenAsync();
        var accept = await AcceptAsync(conversation.ConversationId);
        var messageId = await MessageIdForAsync(accept.SubmissionId!);
        var epoch = Ulid.NewUlid();

        var claim = await SouthTestHost.ReadAsync<ClaimDeliveryResult>(
            await host.PostAsync("/deliveries:claim",
                new ClaimDeliveryRequest { MessageId = messageId, Owner = "agent-1" }));

        await host.PostAsync("/submissions:disposition", new RecordDispositionRequest
        {
            MessageId = messageId,
            SubmissionId = claim.SubmissionId!,
            AttemptId = claim.AttemptId!,
            Disposition = SubmissionDisposition.Ran,
            Epoch = epoch,
            Ordinal = 1,
            Owner = "agent-1",
        });

        var redelivery = await SouthTestHost.ReadAsync<ClaimDeliveryResult>(
            await host.PostAsync("/deliveries:claim",
                new ClaimDeliveryRequest { MessageId = messageId, Owner = "agent-1" }));

        Assert.Equal(ClaimOutcome.DuplicateDone, redelivery.Outcome);
        Assert.Null(redelivery.AttemptId);
    }

    // ── the status mappings ──────────────────────────────────────────────────

    /// <summary>
    /// <c>queue_full</c> and <c>dropped</c> are refused by <c>/submissions:disposition</c>, and
    /// <c>ran</c>, <c>injected</c> and <c>queued</c> by <c>/deliveries:complete</c> — 400, with the
    /// fixed <c>unsupported_kind</c> body.
    /// </summary>
    /// <remarks>
    /// Recording a disposition through both would append <c>submission.accepted</c> twice for one
    /// submission. The store refuses it; this asserts the refusal survives translation to HTTP as a
    /// client error rather than a 500.
    /// </remarks>
    [Theory]
    [InlineData(SubmissionDisposition.QueueFull)]
    [InlineData(SubmissionDisposition.Dropped)]
    public async Task A_terminal_disposition_is_refused_by_the_disposition_endpoint(
        SubmissionDisposition disposition)
    {
        await using var host = await SouthTestHost.StartAsync(_store);
        var claim = await ClaimedAsync(host);

        var response = await host.PostAsync("/submissions:disposition", new RecordDispositionRequest
        {
            MessageId = claim.MessageId,
            SubmissionId = claim.Result.SubmissionId!,
            AttemptId = claim.Result.AttemptId!,
            Disposition = disposition,
            Epoch = Ulid.NewUlid(),
            Ordinal = 1,
            Owner = "agent-1",
        });

        await AssertFixedErrorAsync(response, HttpStatusCode.BadRequest,
            ProtocolErrorCode.UnsupportedKind);
    }

    [Theory]
    [InlineData(SubmissionDisposition.Ran)]
    [InlineData(SubmissionDisposition.Injected)]
    [InlineData(SubmissionDisposition.Queued)]
    public async Task A_running_disposition_is_refused_by_the_completion_endpoint(
        SubmissionDisposition disposition)
    {
        await using var host = await SouthTestHost.StartAsync(_store);
        var claim = await ClaimedAsync(host);

        var response = await host.PostAsync("/deliveries:complete", new CompleteDeliveryRequest
        {
            MessageId = claim.MessageId,
            SubmissionId = claim.Result.SubmissionId!,
            AttemptId = claim.Result.AttemptId!,
            Disposition = disposition,
            Epoch = Ulid.NewUlid(),
            Ordinal = 1,
        });

        await AssertFixedErrorAsync(response, HttpStatusCode.BadRequest,
            ProtocolErrorCode.UnsupportedKind);
    }

    /// <summary>
    /// A second turn id on an attempt that is already running is a 409, not a silent acceptance.
    /// </summary>
    [Fact]
    public async Task A_different_turn_id_on_a_running_attempt_is_a_conflict()
    {
        await using var host = await SouthTestHost.StartAsync(_store);
        var claim = await ClaimedAsync(host);
        var epoch = Ulid.NewUlid();

        await host.PostAsync("/submissions:disposition", new RecordDispositionRequest
        {
            MessageId = claim.MessageId,
            SubmissionId = claim.Result.SubmissionId!,
            AttemptId = claim.Result.AttemptId!,
            Disposition = SubmissionDisposition.Ran,
            Epoch = epoch,
            Ordinal = 1,
            Owner = "agent-1",
        });

        await host.PostAsync("/turns:start", new StartTurnRequest
        {
            AttemptId = claim.Result.AttemptId!, TurnId = "turn-1", Epoch = epoch, Ordinal = 10,
        });

        var response = await host.PostAsync("/turns:start", new StartTurnRequest
        {
            AttemptId = claim.Result.AttemptId!, TurnId = "turn-OTHER", Epoch = epoch, Ordinal = 11,
        });

        await AssertFixedErrorAsync(response, HttpStatusCode.Conflict,
            ProtocolErrorCode.UnsupportedKind);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static async Task AssertFixedErrorAsync(
        HttpResponseMessage response, HttpStatusCode status, ProtocolErrorCode code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(
            FleetProtocolJson.Serialize(ErrorResponse.For(code)),
            await response.Content.ReadAsStringAsync());
    }

    private async Task<(string MessageId, ClaimDeliveryResult Result)> ClaimedAsync(SouthTestHost host)
    {
        var conversation = await OpenAsync();
        var accept = await AcceptAsync(conversation.ConversationId);
        var messageId = await MessageIdForAsync(accept.SubmissionId!);

        var result = await SouthTestHost.ReadAsync<ClaimDeliveryResult>(
            await host.PostAsync("/deliveries:claim",
                new ClaimDeliveryRequest { MessageId = messageId, Owner = "agent-1" }));

        return (messageId, result);
    }

    private async Task<OpenConversationResult> OpenAsync() =>
        await _store.OpenConversationAsync(new OpenConversationRequest
        {
            ChannelId = "client",
            ExternalRef = "conv-" + Ulid.NewUlid(),
            PrincipalId = "p_owner",
        });

    private async Task<AcceptSubmissionResult> AcceptAsync(string conversationId) =>
        await _store.AcceptSubmissionAsync(new AcceptSubmissionRequest
        {
            ConversationId = conversationId,
            ExternalSubmissionId = Guid.NewGuid().ToString("n"),
            PayloadFingerprint = PayloadFingerprint.Compute("hello", null, conversationId),
            CommandKind = ConversationEventKind.SubmissionCreate,
            CommandPayloadJson = """{"text":"hello"}""",
        });

    private async Task<string> MessageIdForAsync(string submissionId) =>
        await fixture.ScalarRowAsync(
            $"SELECT message_id FROM command_outbox WHERE submission_id = '{submissionId}'");
}
