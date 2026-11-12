using System.Net;
using System.Text.Json;
using Fleet.Comms.Contracts;
using Fleet.Protocol;

namespace Fleet.Comms.Tests;

/// <summary>
/// Pins the public wire shapes of docs/first-party-api.md §5.1 and the status mapping of §5.2.
///
/// <para>Field NAMES and the field SET are the contract — a client parses them. Asserting a
/// deserialized object would pass while the wire form drifted, so these assert the JSON.</para>
/// </summary>
public class NorthSerializationTests
{
    [Fact]
    public async Task SessionResponse_HasExactlyTheContractFields()
    {
        await using var host = await NorthTestHost.StartAsync(conversations: new FakeConversationStore());
        var (_, _, token) = await host.EnrolledDeviceAsync();

        var body = await (await host.SessionAsync(token)).Content.ReadAsStringAsync();

        // No agent label since #530: a person chats with many agents, named per conversation.
        AssertFieldSet(body, "protocol", "principalId", "limits");
        using var document = JsonDocument.Parse(body);
        AssertFieldSet(document.RootElement.GetProperty("limits").GetRawText(),
            "inboundTextBytes", "catchUpLimitDefault", "catchUpLimitMax",
            "identifierMaxLength", "outboundBufferEvents");
    }

    [Fact]
    public async Task OpenConversationResponse_HasExactlyTheContractFields()
    {
        await using var host = await NorthTestHost.StartAsync(conversations: new FakeConversationStore());
        var (_, _, token) = await host.EnrolledDeviceAsync();

        var response = await host.Client.SendAsync(Authorized(OpenBody(), "application/json", token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertFieldSet(await response.Content.ReadAsStringAsync(),
            "protocol", "conversationId", "agent", "nextSeq", "retainedFloorSeq");
    }

    [Fact]
    public async Task ConversationListResponse_HasExactlyTheContractFields()
    {
        await using var host = await NorthTestHost.StartAsync(conversations: new FakeConversationStore());
        var (_, _, token) = await host.EnrolledDeviceAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/conversations");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        var body = await (await host.Client.SendAsync(request)).Content.ReadAsStringAsync();

        AssertFieldSet(body, "protocol", "conversations");
        using var document = JsonDocument.Parse(body);
        AssertFieldSet(document.RootElement.GetProperty("conversations")[0].GetRawText(),
            "conversationId", "agent", "externalRef", "nextSeq", "retainedFloorSeq", "lastActivityAt");
    }

    [Fact]
    public async Task EveryErrorBody_IsProtocolCodeMessage_WithTheFixedMessage()
    {
        await using var host = await NorthTestHost.StartAsync(conversations: new FakeConversationStore());
        var (_, _, token) = await host.EnrolledDeviceAsync();

        var conflict = await host.Client.SendAsync(
            Authorized(OpenBody(agent: "not-a-chat-agent"), "application/json", token));
        var body = await conflict.Content.ReadAsStringAsync();

        AssertFieldSet(body, "protocol", "code", "message");
        using var document = JsonDocument.Parse(body);
        Assert.Equal(ProtocolVersion.Current, document.RootElement.GetProperty("protocol").GetString());

        // The wire value is the protocol's snake_case form, not the C# member name.
        Assert.Equal("agent_unavailable", document.RootElement.GetProperty("code").GetString());
        Assert.Equal(ProtocolErrors.AgentUnavailable, document.RootElement.GetProperty("message").GetString());
    }

    /// <summary>
    /// §5.2 status mapping for the codes the open route can produce. A code with the right body and
    /// the wrong status is still a contract break — the status line is what a client branches on.
    /// </summary>
    [Fact]
    public async Task StatusMappingMatchesTheContract()
    {
        await using var host = await NorthTestHost.StartAsync(conversations: new FakeConversationStore());
        var (_, _, token) = await host.EnrolledDeviceAsync();

        // unsupported_protocol -> 400
        var wrongProtocol = await host.Client.SendAsync(Authorized(
            """{"protocol":"fleet.conversation.v2","externalRef":"main","agent":"example-agent"}""",
            "application/json", token));
        Assert.Equal(HttpStatusCode.BadRequest, wrongProtocol.StatusCode);
        Assert.Equal("unsupported_protocol", await CodeOf(wrongProtocol));

        // unsupported_kind -> 400 (malformed body)
        var malformed = await host.Client.SendAsync(Authorized("{not json", "application/json", token));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal("unsupported_kind", await CodeOf(malformed));

        // unsupported_kind -> 400 for bad request METADATA too.
        //
        // These used to be 500. `ReadFromJsonAsync` throws InvalidOperationException — not
        // JsonException — for an unsupported or absent media type, so the catch missed it and the
        // edge handler answered `internal`. §5.2 tells a client 500 means "the server is broken,
        // report this", which is the wrong instruction for a request the client got wrong.
        foreach (var (body, contentType) in new[]
                 {
                     (OpenBody(), "text/plain"),
                     ("", "application/json"),
                     ("null", "application/json"),
                 })
        {
            var refused = await host.Client.SendAsync(Authorized(body, contentType, token));
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal("unsupported_kind", await CodeOf(refused));
        }

        // `HasJsonContentType()` checks the media type and IGNORES the charset, so a bogus one got
        // past it and then failed inside the JSON reader with a non-JsonException — reaching the
        // client as `500 internal` over a parameter the client chose. The body is valid JSON; only
        // the charset is wrong.
        foreach (var charset in new[]
                 {
                     "not-a-real-encoding", "iso-8859-1", "utf-32",
                     // Quoted too: a bogus value is no more acceptable for being quoted, and
                     // unquoting must not turn the rejection into an acceptance.
                     "\"not-a-real-encoding\"", "\"utf-32\"",
                 })
        {
            var badCharset = await host.Client.SendAsync(
                Authorized(OpenBody(), $"application/json; charset={charset}", token));
            Assert.Equal(HttpStatusCode.BadRequest, badCharset.StatusCode);
            Assert.Equal("unsupported_kind", await CodeOf(badCharset));
        }

        // unauthorized -> 401
        var unauthorized = await host.SessionAsync(null);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Equal("unauthorized", await CodeOf(unauthorized));

        // agent_unavailable -> 409
        var unavailable = await host.Client.SendAsync(
            Authorized(OpenBody(agent: "not-a-chat-agent"), "application/json", token));
        Assert.Equal(HttpStatusCode.Conflict, unavailable.StatusCode);
        Assert.Equal("agent_unavailable", await CodeOf(unavailable));
    }

    /// <summary>
    /// Every spelling of UTF-8 a legal client may send must be read.
    ///
    /// <para>RFC 9110 §5.6.6 lets a parameter value be a token <b>or</b> a quoted-string, so
    /// <c>charset="utf-8"</c> is exactly as legal as <c>charset=utf-8</c>. Comparing
    /// <c>MediaTypeHeaderValue.Charset</c> directly kept the quotes and rejected the quoted form —
    /// a validation meant to catch a client mistake inventing one instead, with no way for the
    /// client to discover which spelling the server wanted.</para>
    ///
    /// <para>`200` is the pass condition: the body was read and the conversation opened. A `400`
    /// here means it never got that far.</para>
    /// </summary>
    [Theory]
    [InlineData("application/json")]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("application/json; charset=\"utf-8\"")]
    [InlineData("application/json; charset=UTF-8")]
    [InlineData("application/json; charset=\"UTF-8\"")]
    [InlineData("application/json;charset=\"utf-8\"")]
    public async Task EverySpellingOfUtf8IsRead(string contentType)
    {
        await using var host = await NorthTestHost.StartAsync(conversations: new FakeConversationStore());
        var (_, _, token) = await host.EnrolledDeviceAsync();

        var response = await host.Client.SendAsync(Authorized(OpenBody(), contentType, token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Error codes are APPEND-ONLY: existing members keep their position and their wire name, and a
    /// new one may only be added at the end.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This used to assert that <c>DeviceLimit</c> was <i>last</i>. That was a proxy for the real
    /// rule and it aged badly the moment a later slice appended anything — the conversation slice
    /// added <c>IdempotencyConflict</c> and <c>InvalidCursor</c>, which is a perfectly legal append,
    /// and the test failed for it.
    /// </para>
    /// <para>
    /// Pinning the whole ordered list instead catches what actually matters — an INSERTION or a
    /// REORDER, either of which renumbers every member after it — while a legitimate append stays a
    /// deliberate, visible one-line edit here rather than a silent one.
    /// </para>
    /// </remarks>
    [Fact]
    public void ProtocolErrorCodes_AreAppendOnly()
    {
        Assert.Equal(
            [
                ProtocolErrorCode.Unauthorized,
                ProtocolErrorCode.UnsupportedProtocol,
                ProtocolErrorCode.UnsupportedKind,
                ProtocolErrorCode.UnsupportedRole,
                ProtocolErrorCode.UnsupportedAttachments,
                ProtocolErrorCode.PayloadTooLarge,
                ProtocolErrorCode.ConversationNotFound,
                ProtocolErrorCode.RateLimited,
                ProtocolErrorCode.RuntimeBusy,
                ProtocolErrorCode.ExecutorError,
                ProtocolErrorCode.Canceled,
                ProtocolErrorCode.Internal,
                ProtocolErrorCode.DeviceLimit,
                ProtocolErrorCode.IdempotencyConflict,
                ProtocolErrorCode.InvalidCursor,

                // #308, appended at the end so every deployed client's mapping of the earlier
                // values is unchanged.
                ProtocolErrorCode.AttachmentNotFound,
                ProtocolErrorCode.AttachmentLimit,
                ProtocolErrorCode.UnsupportedMediaType,
                ProtocolErrorCode.AttachmentGone,

                // #530.
                ProtocolErrorCode.AgentUnavailable,
            ],
            Enum.GetValues<ProtocolErrorCode>());
    }

    /// <summary>
    /// The additive amendment the auth slice landed (§17 A1) keeps its fixed message and wire form.
    /// </summary>
    [Fact]
    public void DeviceLimit_HasAFixedMessageAndWireForm()
    {
        Assert.Equal(ProtocolErrors.DeviceLimit, ProtocolErrors.MessageFor(ProtocolErrorCode.DeviceLimit));
        Assert.Equal("\"device_limit\"",
            JsonSerializer.Serialize(ProtocolErrorCode.DeviceLimit, FleetProtocolJson.Options));
    }

    /// <summary>
    /// The codes the conversation and attachment slices append, each with a fixed message and the
    /// snake_case wire form a deployed client keys on.
    /// </summary>
    [Theory]
    [InlineData(ProtocolErrorCode.IdempotencyConflict, "\"idempotency_conflict\"")]
    [InlineData(ProtocolErrorCode.InvalidCursor, "\"invalid_cursor\"")]
    [InlineData(ProtocolErrorCode.AttachmentNotFound, "\"attachment_not_found\"")]
    [InlineData(ProtocolErrorCode.AttachmentLimit, "\"attachment_limit\"")]
    [InlineData(ProtocolErrorCode.UnsupportedMediaType, "\"unsupported_media_type\"")]
    [InlineData(ProtocolErrorCode.AttachmentGone, "\"attachment_gone\"")]
    [InlineData(ProtocolErrorCode.AgentUnavailable, "\"agent_unavailable\"")]
    public void ConversationErrorCodes_HaveFixedMessagesAndWireForms(ProtocolErrorCode code, string wire)
    {
        Assert.Equal(wire, JsonSerializer.Serialize(code, FleetProtocolJson.Options));

        var message = ProtocolErrors.MessageFor(code);
        Assert.NotEqual(ProtocolErrors.Internal, message);
        Assert.False(string.IsNullOrWhiteSpace(message));
    }

    private static string OpenBody(string agent = NorthTestHost.ChatAgent) =>
        $$"""{"protocol":"{{ProtocolVersion.Current}}","externalRef":"main-thread","agent":"{{agent}}"}""";

    private static HttpRequestMessage Authorized(string body, string contentType, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/conversations") { Content = Raw(body, contentType) };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        return request;
    }

    /// <summary>
    /// A body with the Content-Type set verbatim. <see cref="StringContent"/> appends its own
    /// charset, which would overwrite the one under test.
    /// </summary>
    private static ByteArrayContent Raw(string body, string contentType)
    {
        var content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(body));
        content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        return content;
    }

    private static async Task<string?> CodeOf(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("code").GetString();
    }

    private static void AssertFieldSet(string json, params string[] expected)
    {
        using var document = JsonDocument.Parse(json);
        var actual = document.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(expected.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            actual.OrderBy(x => x, StringComparer.Ordinal).ToList());
    }
}
