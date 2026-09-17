using System.Net;
using System.Text.Json;
using Fleet.Comms.Auth;
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
    public async Task RegisterDeviceResponse_HasExactlyTheContractFields()
    {
        await using var host = await NorthTestHost.StartAsync();

        var response = await host.RegisterAsync(await host.IssueEnrollmentCodeAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertFieldSet(await response.Content.ReadAsStringAsync(),
            "protocol", "deviceId", "deviceSecret");
    }

    [Fact]
    public async Task TokenResponse_HasExactlyTheContractFields_AndNoRefreshToken()
    {
        await using var host = await NorthTestHost.StartAsync();
        var (deviceId, secret, _) = await host.EnrolledDeviceAsync();

        var body = await (await host.TokenAsync(deviceId, secret)).Content.ReadAsStringAsync();

        AssertFieldSet(body, "protocol", "accessToken", "expiresInSeconds");
    }

    [Fact]
    public async Task SessionResponse_HasExactlyTheContractFields()
    {
        await using var host = await NorthTestHost.StartAsync();
        var (_, _, token) = await host.EnrolledDeviceAsync();

        var body = await (await host.SessionAsync(token)).Content.ReadAsStringAsync();

        AssertFieldSet(body, "protocol", "principalId", "agentLabel", "limits");
        using var document = JsonDocument.Parse(body);
        AssertFieldSet(document.RootElement.GetProperty("limits").GetRawText(),
            "inboundTextBytes", "catchUpLimitDefault", "catchUpLimitMax",
            "identifierMaxLength", "outboundBufferEvents");
    }

    [Fact]
    public async Task RevokeResponse_HasExactlyTheContractFields()
    {
        await using var host = await NorthTestHost.StartAsync();
        var (deviceId, _, token) = await host.EnrolledDeviceAsync();

        var body = await (await host.RevokeAsync(deviceId, token)).Content.ReadAsStringAsync();

        AssertFieldSet(body, "protocol", "revoked");
    }

    [Fact]
    public async Task EveryErrorBody_IsProtocolCodeMessage_WithTheFixedMessage()
    {
        await using var host = await NorthTestHost.StartAsync();
        await host.RegisterAsync(await host.IssueEnrollmentCodeAsync());

        var conflict = await host.RegisterAsync(await host.IssueEnrollmentCodeAsync());
        var body = await conflict.Content.ReadAsStringAsync();

        AssertFieldSet(body, "protocol", "code", "message");
        using var document = JsonDocument.Parse(body);
        Assert.Equal(ProtocolVersion.Current, document.RootElement.GetProperty("protocol").GetString());

        // The wire value is the protocol's snake_case form, not the C# member name.
        Assert.Equal("device_limit", document.RootElement.GetProperty("code").GetString());
        Assert.Equal(ProtocolErrors.DeviceLimit, document.RootElement.GetProperty("message").GetString());
    }

    /// <summary>
    /// §5.2 status mapping for the codes this slice can produce. A code with the right body and the
    /// wrong status is still a contract break — the status line is what a client branches on.
    /// </summary>
    [Fact]
    public async Task StatusMappingMatchesTheContract()
    {
        await using var host = await NorthTestHost.StartAsync();

        // unsupported_protocol -> 400
        var wrongProtocol = await host.Client.PostAsync("/v1/auth/token",
            JsonContent("""{"protocol":"fleet.conversation.v2","deviceId":"d","deviceSecret":"s"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, wrongProtocol.StatusCode);
        Assert.Equal("unsupported_protocol", await CodeOf(wrongProtocol));

        // unsupported_kind -> 400 (malformed body)
        var malformed = await host.Client.PostAsync("/v1/auth/token", JsonContent("{not json"));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal("unsupported_kind", await CodeOf(malformed));

        // unsupported_kind -> 400 for bad request METADATA too, on BOTH auth POST routes.
        //
        // These used to be 500. `ReadFromJsonAsync` throws InvalidOperationException — not
        // JsonException — for an unsupported or absent media type, so the catch missed it and the
        // edge handler answered `internal`. §5.2 tells a client 500 means "the server is broken,
        // report this", which is the wrong instruction for a request the client got wrong, and it
        // hides a client bug behind an operator alert.
        foreach (var route in new[] { "/v1/auth/token", "/v1/auth/devices" })
        {
            var wrongContentType = await host.Client.PostAsync(route,
                new StringContent("""{"protocol":"fleet.conversation.v1"}""",
                    System.Text.Encoding.UTF8, "text/plain"));
            Assert.Equal(HttpStatusCode.BadRequest, wrongContentType.StatusCode);
            Assert.Equal("unsupported_kind", await CodeOf(wrongContentType));

            var noBody = await host.Client.PostAsync(route, new StringContent(""));
            Assert.Equal(HttpStatusCode.BadRequest, noBody.StatusCode);
            Assert.Equal("unsupported_kind", await CodeOf(noBody));

            var emptyJson = await host.Client.PostAsync(route, JsonContent(""));
            Assert.Equal(HttpStatusCode.BadRequest, emptyJson.StatusCode);
            Assert.Equal("unsupported_kind", await CodeOf(emptyJson));

            var jsonNull = await host.Client.PostAsync(route, JsonContent("null"));
            Assert.Equal(HttpStatusCode.BadRequest, jsonNull.StatusCode);
            Assert.Equal("unsupported_kind", await CodeOf(jsonNull));

            // `HasJsonContentType()` checks the media type and IGNORES the charset, so a bogus one
            // got past it and then failed inside the JSON reader with a non-JsonException —
            // reaching the client as `500 internal` over a parameter the client chose. The body
            // here is valid JSON; only the charset is wrong.
            foreach (var charset in new[]
                     {
                         "not-a-real-encoding", "iso-8859-1", "utf-32",
                         // Quoted too: a bogus value is no more acceptable for being quoted, and
                         // unquoting must not turn the rejection into an acceptance.
                         "\"not-a-real-encoding\"", "\"utf-32\"",
                     })
            {
                var badCharset = await host.Client.PostAsync(route,
                    Raw("""{"protocol":"fleet.conversation.v1"}""", $"application/json; charset={charset}"));
                Assert.Equal(HttpStatusCode.BadRequest, badCharset.StatusCode);
                Assert.Equal("unsupported_kind", await CodeOf(badCharset));
            }
        }

        // unauthorized -> 401
        var unauthorized = await host.SessionAsync(null);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Equal("unauthorized", await CodeOf(unauthorized));

        // device_limit -> 409
        await host.RegisterAsync(await host.IssueEnrollmentCodeAsync());
        var conflict = await host.RegisterAsync(await host.IssueEnrollmentCodeAsync());
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("device_limit", await CodeOf(conflict));
    }

    /// <summary>
    /// Every spelling of UTF-8 a legal client may send must reach credential validation.
    ///
    /// <para>RFC 9110 §5.6.6 lets a parameter value be a token <b>or</b> a quoted-string, so
    /// <c>charset="utf-8"</c> is exactly as legal as <c>charset=utf-8</c>. Comparing
    /// <c>MediaTypeHeaderValue.Charset</c> directly kept the quotes and rejected the quoted form —
    /// a validation meant to catch a client mistake inventing one instead, with no way for the
    /// client to discover which spelling the server wanted.</para>
    ///
    /// <para>`401` is the pass condition: it means the request got past the reader and was judged on
    /// its credentials, which is the whole point. A `400` here means it never got that far.</para>
    /// </summary>
    [Theory]
    [InlineData("/v1/auth/token", "application/json")]
    [InlineData("/v1/auth/token", "application/json; charset=utf-8")]
    [InlineData("/v1/auth/token", "application/json; charset=\"utf-8\"")]
    [InlineData("/v1/auth/token", "application/json; charset=UTF-8")]
    [InlineData("/v1/auth/token", "application/json; charset=\"UTF-8\"")]
    [InlineData("/v1/auth/token", "application/json;charset=\"utf-8\"")]
    [InlineData("/v1/auth/devices", "application/json")]
    [InlineData("/v1/auth/devices", "application/json; charset=utf-8")]
    [InlineData("/v1/auth/devices", "application/json; charset=\"utf-8\"")]
    [InlineData("/v1/auth/devices", "application/json; charset=UTF-8")]
    [InlineData("/v1/auth/devices", "application/json; charset=\"UTF-8\"")]
    [InlineData("/v1/auth/devices", "application/json;charset=\"utf-8\"")]
    public async Task EverySpellingOfUtf8ReachesCredentialValidation(string route, string contentType)
    {
        await using var host = await NorthTestHost.StartAsync();

        var response = await host.Client.PostAsync(route, Raw(
            """
            {"protocol":"fleet.conversation.v1","deviceId":"d","deviceSecret":"s",
             "enrollmentCode":"nosuchid.nosuchsecret"}
            """,
            contentType));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("unauthorized", await CodeOf(response));
    }

    /// <summary>
    /// `DeviceLimit` is the additive protocol amendment this slice lands (§17 A1). Append-only
    /// means it is LAST — inserting it would renumber every member after it, and the wire form of
    /// an enum member is its name, so a client keying on `device_limit` must keep working.
    /// </summary>
    [Fact]
    public void DeviceLimit_IsAppendedLastAndHasAFixedMessage()
    {
        var members = Enum.GetValues<ProtocolErrorCode>();

        Assert.Equal(ProtocolErrorCode.DeviceLimit, members[^1]);
        Assert.Equal(ProtocolErrors.DeviceLimit, ProtocolErrors.MessageFor(ProtocolErrorCode.DeviceLimit));
        Assert.Equal("\"device_limit\"",
            JsonSerializer.Serialize(ProtocolErrorCode.DeviceLimit, FleetProtocolJson.Options));
    }

    private static StringContent JsonContent(string raw) =>
        new(raw, System.Text.Encoding.UTF8, "application/json");

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
