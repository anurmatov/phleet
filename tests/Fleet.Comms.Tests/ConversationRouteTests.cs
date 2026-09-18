using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Fleet.Comms.Contracts;
using Fleet.Comms.Routes;
using Fleet.Conversations.Contracts;
using Fleet.Protocol;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Fleet.Comms.Tests;

/// <summary>
/// The six conversation routes: their statuses, their bodies, and the two things that are never
/// clamped (AC1–AC9).
/// </summary>
/// <remarks>
/// Driven against the real route table through <see cref="NorthTestHost"/>, with the store
/// substituted — these are questions about the boundary, not about MySQL, and the transactional
/// layer is exercised against a real database in <c>Fleet.Conversations.Tests</c>.
/// </remarks>
public class ConversationRouteTests
{
    private static readonly FakeConversationStore Prototype = new();

    // ── AC1: the surface exists and answers ──────────────────────────────────

    [Fact]
    public async Task Opening_a_conversation_returns_the_identifiers_a_client_needs()
    {
        var store = new FakeConversationStore();
        await using var host = await NorthTestHost.StartAsync(conversations: store);
        var token = await AuthorizeAsync(host);

        var response = await host.Client.SendAsync(
            Post("/v1/conversations", token, new { protocol = ProtocolVersion.Current, externalRef = "main-thread" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ReadAsync<OpenConversationResponse>(response);
        Assert.Equal(store.ConversationId, body.ConversationId);
        Assert.Equal(store.NextSeq, body.NextSeq);
        Assert.Equal(store.RetainedFloorSeq, body.RetainedFloorSeq);
    }

    /// <summary>
    /// The route table is EXACTLY the ten routes §5 publishes when the feature is on.
    /// </summary>
    /// <remarks>
    /// Probing for 200s proves these ten answer; this proves nothing else was mapped. The auth
    /// slice's four-route assertion was a slice-1 statement, and D-D says the surface grows to ten
    /// deliberately and visibly — this is where "visibly" is enforced.
    /// </remarks>
    [Fact]
    public async Task The_enabled_north_route_table_is_exactly_the_ten_published_routes()
    {
        await using var host = await NorthTestHost.StartAsync(conversations: new FakeConversationStore());

        var registered = host.Services.GetServices<EndpointDataSource>()
            .SelectMany(s => s.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(e =>
            {
                var methods = e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [];
                return $"{string.Join(",", methods)} /{e.RoutePattern.RawText?.TrimStart('/')}";
            })
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        var expected = NorthEndpoints.Routes.Concat(ConversationEndpoints.Routes)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(expected, registered);
    }

    // ── AC2: the limit is never clamped ──────────────────────────────────────

    [Fact]
    public async Task A_limit_above_the_maximum_is_refused_and_the_maximum_itself_succeeds()
    {
        var store = new FakeConversationStore();
        await using var host = await NorthTestHost.StartAsync(conversations: store);
        var token = await AuthorizeAsync(host);

        var over = await host.Client.SendAsync(
            Get($"/v1/conversations/{store.ConversationId}/events?afterSeq=0&limit=1001", token));

        Assert.Equal(HttpStatusCode.BadRequest, over.StatusCode);
        await AssertFixedBodyAsync(over, ProtocolErrorCode.UnsupportedKind);

        var atMax = await host.Client.SendAsync(
            Get($"/v1/conversations/{store.ConversationId}/events?afterSeq=0&limit=1000", token));

        Assert.Equal(HttpStatusCode.OK, atMax.StatusCode);
    }

    // ── AC3 and AC4: the cursor is never clamped, and is rejected before comparison ──

    [Fact]
    public async Task A_cursor_at_or_beyond_next_seq_is_refused()
    {
        var store = new FakeConversationStore();
        await using var host = await NorthTestHost.StartAsync(conversations: store);
        var token = await AuthorizeAsync(host);

        var response = await host.Client.SendAsync(
            Get($"/v1/conversations/{store.ConversationId}/events?afterSeq={store.NextSeq}", token));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertFixedBodyAsync(response, ProtocolErrorCode.InvalidCursor);
    }

    /// <summary>
    /// A negative or non-integral cursor is refused BEFORE anything compares it against an unsigned
    /// column.
    /// </summary>
    /// <remarks>
    /// `-1` bound to an unsigned type is a very large number, and a very large number compared
    /// against `next_seq` is an empty page rather than an error — which is the specific way an
    /// over-range cursor turns into silent truncation. Parsing it as unsigned from the raw string is
    /// what makes it a refusal.
    /// </remarks>
    [Theory]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("99999999999999999999999999")]
    [InlineData("not-a-number")]
    public async Task A_malformed_cursor_is_refused_rather_than_coerced(string cursor)
    {
        var store = new FakeConversationStore();
        await using var host = await NorthTestHost.StartAsync(conversations: store);
        var token = await AuthorizeAsync(host);

        var response = await host.Client.SendAsync(
            Get($"/v1/conversations/{store.ConversationId}/events?afterSeq={cursor}", token));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertFixedBodyAsync(response, ProtocolErrorCode.InvalidCursor);
    }

    // ── AC5 and AC6 ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Another_principals_conversation_is_not_found()
    {
        var store = new FakeConversationStore { OwnerPrincipalId = "p_somebody_else" };
        await using var host = await NorthTestHost.StartAsync(conversations: store);
        var token = await AuthorizeAsync(host);

        var theirs = await host.Client.SendAsync(
            Get($"/v1/conversations/{store.ConversationId}/events?afterSeq=0", token));

        Assert.Equal(HttpStatusCode.NotFound, theirs.StatusCode);
        await AssertFixedBodyAsync(theirs, ProtocolErrorCode.ConversationNotFound);
    }

    [Fact]
    public async Task The_same_catch_up_request_twice_returns_byte_identical_bodies()
    {
        var store = new FakeConversationStore();
        store.Events.Add(FakeConversationStore.Stored(20));
        store.Events.Add(FakeConversationStore.Stored(21, ConversationEventKind.TurnFinal));

        await using var host = await NorthTestHost.StartAsync(conversations: store);
        var token = await AuthorizeAsync(host);

        var first = await host.Client.SendAsync(
            Get($"/v1/conversations/{store.ConversationId}/events?afterSeq=0", token));
        var second = await host.Client.SendAsync(
            Get($"/v1/conversations/{store.ConversationId}/events?afterSeq=0", token));

        Assert.Equal(
            await first.Content.ReadAsStringAsync(),
            await second.Content.ReadAsStringAsync());
    }

    // ── AC7, AC7a, AC8 ───────────────────────────────────────────────────────

    [Fact]
    public async Task A_submission_carrying_attachments_is_refused_and_creates_nothing()
    {
        var store = new FakeConversationStore();
        await using var host = await NorthTestHost.StartAsync(conversations: store);
        var token = await AuthorizeAsync(host);

        var response = await host.Client.SendAsync(Post(
            $"/v1/conversations/{store.ConversationId}/submissions", token,
            new
            {
                protocol = ProtocolVersion.Current,
                type = "create",
                submissionId = "s_1",
                text = "hello",
                attachments = new[] { new { kind = "image" } },
            }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertFixedBodyAsync(response, ProtocolErrorCode.UnsupportedAttachments);

        // "creates nothing" asserted on the store, not inferred from the status.
        Assert.Empty(store.Accepted);
    }

    [Theory]
    [InlineData("create", ConversationEventKind.SubmissionCreate)]
    [InlineData("steer", ConversationEventKind.SubmissionSteer)]
    public async Task The_submission_type_selects_the_command_kind(string type, string expectedKind)
    {
        var store = new FakeConversationStore();
        await using var host = await NorthTestHost.StartAsync(conversations: store);
        var token = await AuthorizeAsync(host);

        var response = await host.Client.SendAsync(Post(
            $"/v1/conversations/{store.ConversationId}/submissions", token,
            new { protocol = ProtocolVersion.Current, type, submissionId = "s_1", text = "hello" }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(expectedKind, Assert.Single(store.Accepted).CommandKind);

        var body = await ReadAsync<SubmitAcceptedResponse>(response);
        Assert.Equal(store.NextSeq, body.AcceptedSeq);
    }

    [Fact]
    public async Task Text_above_the_inbound_bound_is_refused_and_creates_nothing()
    {
        var store = new FakeConversationStore();
        await using var host = await NorthTestHost.StartAsync(conversations: store);
        var token = await AuthorizeAsync(host);

        var response = await host.Client.SendAsync(Post(
            $"/v1/conversations/{store.ConversationId}/submissions", token,
            new
            {
                protocol = ProtocolVersion.Current,
                type = "create",
                submissionId = "s_1",
                text = new string('x', ProtocolLimits.MaxInboundTextBytes + 1),
            }));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        await AssertFixedBodyAsync(response, ProtocolErrorCode.PayloadTooLarge);
        Assert.Empty(store.Accepted);
    }

    /// <summary>
    /// The bound is BYTES, so text that is short in characters and long in bytes is still refused.
    /// </summary>
    /// <remarks>
    /// A character count admits roughly four times the payload for text outside Basic Latin — which
    /// is most of the world's text, and exactly the input a limit like this exists to bound.
    /// </remarks>
    [Fact]
    public async Task The_inbound_bound_counts_bytes_rather_than_characters()
    {
        var store = new FakeConversationStore();
        await using var host = await NorthTestHost.StartAsync(conversations: store);
        var token = await AuthorizeAsync(host);

        // Three bytes each in UTF-8, so a third of the character budget is over the byte budget.
        var text = new string('世', (ProtocolLimits.MaxInboundTextBytes / 3) + 1);
        Assert.True(text.Length < ProtocolLimits.MaxInboundTextBytes);

        var response = await host.Client.SendAsync(Post(
            $"/v1/conversations/{store.ConversationId}/submissions", token,
            new { protocol = ProtocolVersion.Current, type = "create", submissionId = "s_1", text }));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task A_same_key_retry_mid_disposition_is_accepted_pending_with_no_accepted_seq()
    {
        var store = new FakeConversationStore { NextOutcome = AcceptOutcome.ReplayPending };
        await using var host = await NorthTestHost.StartAsync(conversations: store);
        var token = await AuthorizeAsync(host);

        var response = await host.Client.SendAsync(Post(
            $"/v1/conversations/{store.ConversationId}/submissions", token,
            new
            {
                protocol = ProtocolVersion.Current, type = "create", submissionId = "s_1",
                idempotencyKey = "k-1", text = "hello",
            }));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // No acceptedSeq — it is not yet known, and a null on the wire would be a number a client
        // could try to catch up from.
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("acceptedSeq", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_same_key_with_a_different_payload_is_a_conflict()
    {
        var store = new FakeConversationStore { NextOutcome = AcceptOutcome.Conflict };
        await using var host = await NorthTestHost.StartAsync(conversations: store);
        var token = await AuthorizeAsync(host);

        var response = await host.Client.SendAsync(Post(
            $"/v1/conversations/{store.ConversationId}/submissions", token,
            new
            {
                protocol = ProtocolVersion.Current, type = "create", submissionId = "s_1",
                idempotencyKey = "k-1", text = "different",
            }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertFixedBodyAsync(response, ProtocolErrorCode.IdempotencyConflict);
    }

    // ── AC7b: cancel ─────────────────────────────────────────────────────────

    /// <summary>
    /// Cancel answers `200 { protocol, accepted: true }` and carries NO `hadRunningTask`.
    /// </summary>
    /// <remarks>
    /// Only the agent runtime knows whether a turn was running, and cancel is dispatched
    /// asynchronously through the command outbox — so a value here would be invented, and it would
    /// contradict the `control.ack` event that follows. Asserted on the raw bytes, because a field
    /// whose value happened to be right would still be a field a client could come to depend on.
    /// </remarks>
    [Fact]
    public async Task Cancel_is_accepted_and_carries_no_had_running_task()
    {
        var store = new FakeConversationStore();
        await using var host = await NorthTestHost.StartAsync(conversations: store);
        var token = await AuthorizeAsync(host);

        var response = await host.Client.SendAsync(Post(
            $"/v1/conversations/{store.ConversationId}:cancel", token,
            new { protocol = ProtocolVersion.Current, scope = "current" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var raw = await response.Content.ReadAsStringAsync();
        Assert.Equal(
            FleetProtocolJson.Serialize(new CancelResponse()), raw);
        Assert.DoesNotContain("hadRunningTask", raw, StringComparison.OrdinalIgnoreCase);

        // And it really dispatched a cancel command rather than answering locally.
        Assert.Equal(ConversationEventKind.SubmissionCancel,
            Assert.Single(store.Accepted).CommandKind);
    }

    // ── cursor ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_cursor_advance_is_no_content_and_reaches_the_store()
    {
        var store = new FakeConversationStore();
        await using var host = await NorthTestHost.StartAsync(conversations: store);
        var token = await AuthorizeAsync(host);

        var response = await host.Client.SendAsync(Post(
            $"/v1/conversations/{store.ConversationId}/cursor", token,
            new
            {
                protocol = ProtocolVersion.Current, clientInstanceId = "inst_a1",
                deliveredSeq = 12, readSeq = 12,
            }));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var written = Assert.Single(store.Cursors);
        Assert.Equal("inst_a1", written.ClientInstanceId);
        Assert.Equal(12UL, written.DeliveredSeq);
    }

    /// <summary>A read cursor ahead of the delivered one is incoherent and is refused.</summary>
    [Fact]
    public async Task A_read_cursor_ahead_of_the_delivered_one_is_refused_and_writes_nothing()
    {
        var store = new FakeConversationStore();
        await using var host = await NorthTestHost.StartAsync(conversations: store);
        var token = await AuthorizeAsync(host);

        var response = await host.Client.SendAsync(Post(
            $"/v1/conversations/{store.ConversationId}/cursor", token,
            new
            {
                protocol = ProtocolVersion.Current, clientInstanceId = "inst_a1",
                deliveredSeq = 10, readSeq = 11,
            }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertFixedBodyAsync(response, ProtocolErrorCode.InvalidCursor);
        Assert.Empty(store.Cursors);
    }

    // ── authentication ───────────────────────────────────────────────────────

    /// <summary>Every conversation route refuses an absent credential.</summary>
    public static TheoryData<string, string> AuthenticatedRoutes() => new()
    {
        { "POST", "/v1/conversations" },
        { "GET", "/v1/conversations/c_1/events" },
        { "POST", "/v1/conversations/c_1/submissions" },
        { "POST", "/v1/conversations/c_1:cancel" },
        { "POST", "/v1/conversations/c_1/cursor" },
    };

    [Theory]
    [MemberData(nameof(AuthenticatedRoutes))]
    public async Task Every_conversation_route_refuses_an_absent_credential(string method, string path)
    {
        await using var host = await NorthTestHost.StartAsync(conversations: new FakeConversationStore());

        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
        {
            request.Content = new StringContent(
                $$"""{"protocol":"{{ProtocolVersion.Current}}"}""", Encoding.UTF8, "application/json");
        }

        var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertFixedBodyAsync(response, ProtocolErrorCode.Unauthorized);
    }

    /// <summary>
    /// A stream upgrade carrying the token in the query string is refused.
    /// </summary>
    /// <remarks>
    /// A token in a query string is a token in every intermediary's access log. There is no code
    /// path that reads one from there, and this is the assertion that keeps it that way.
    /// </remarks>
    [Fact]
    public async Task A_stream_upgrade_with_the_token_in_the_query_string_is_refused()
    {
        var store = new FakeConversationStore();
        await using var host = await NorthTestHost.StartAsync(conversations: store);
        var token = await AuthorizeAsync(host);

        var response = await host.Client.SendAsync(new HttpRequestMessage(
            HttpMethod.Get,
            $"/v1/conversations/{store.ConversationId}/stream?clientInstanceId=inst_a1&access_token={token}"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static async Task<string> AuthorizeAsync(NorthTestHost host)
    {
        var (_, _, token) = await host.EnrolledDeviceAsync();
        return token;
    }

    private static HttpRequestMessage Post(string path, string token, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(
                FleetProtocolJson.Serialize(body), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static HttpRequestMessage Get(string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        return FleetProtocolJson.Deserialize<T>(json)
            ?? throw new InvalidOperationException($"could not read a {typeof(T).Name} from: {json}");
    }

    private static async Task AssertFixedBodyAsync(
        HttpResponseMessage response, ProtocolErrorCode code) =>
        Assert.Equal(
            FleetProtocolJson.Serialize(ErrorResponse.For(code)),
            await response.Content.ReadAsStringAsync());
}
