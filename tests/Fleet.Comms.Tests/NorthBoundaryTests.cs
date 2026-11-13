using System.Net;
using Fleet.Comms.Configuration;
using Fleet.Comms.Routes;
using Fleet.Protocol;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Fleet.Comms.Tests;

/// <summary>
/// The properties that make this a boundary rather than a route table: what is reachable, what the
/// bodies look like, what is indistinguishable, and what fails closed.
/// </summary>
public class NorthBoundaryTests
{
    /// <summary>
    /// The south endpoint list from docs/first-party-api.md §1, committed as data.
    ///
    /// <para>None is reachable from the north listener. <c>/events:append</c> is why this is a list
    /// rather than a principle: on a device-reachable listener it lets a client write arbitrary
    /// events — including forged terminals — into the durable log.</para>
    /// </summary>
    public static TheoryData<string, string> SouthRoutes() => new()
    {
        { "POST", "/conversations" },
        { "POST", "/submissions" },
        { "POST", "/events:append" },
        { "POST", "/turns:start" },
        { "POST", "/leases:heartbeat" },
        { "POST", "/turns:commit" },
        { "POST", "/cursors" },
        { "GET", "/conversations/c_1/events" },
        { "GET", "/conversations/c_1/tail" },

        // The three the durable slice added. The list predated them, so for one slice the south
        // surface grew and the boundary probe did not — including `/submissions:disposition` and
        // `/deliveries:complete`, either of which on a device-reachable listener lets a client
        // resolve someone else's submission and append `submission.accepted` for it.
        { "POST", "/deliveries:claim" },
        { "POST", "/submissions:disposition" },
        { "POST", "/deliveries:complete" },
    };

    [Theory]
    [MemberData(nameof(SouthRoutes))]
    public async Task NoSouthRouteIsReachableOnTheNorthListener(string method, string path)
    {
        // With conversations ON, which is when the north listener has routes at all.
        await using var host = await NorthTestHost.StartAsync(conversations: new FakeConversationStore());

        var response = await host.Client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>The journal routes (#375) are never mapped on the north listener.</summary>
    [Theory]
    [InlineData("POST", "/journal/v1/messages")]
    [InlineData("GET", "/journal/v1/status")]
    public async Task NoJournalRouteIsReachableOnTheNorthListener(string method, string path)
    {
        // With conversations ON, which is when the north listener has routes at all.
        await using var host = await NorthTestHost.StartAsync(conversations: new FakeConversationStore());

        var response = await host.Client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// With conversations off, the north listener maps nothing (#530): no sign-in route, because
    /// sign-in is the orchestrator's, and no conversation route.
    /// </summary>
    [Fact]
    public async Task WithConversationsOff_TheNorthRouteTableIsEmpty()
    {
        await using var host = await NorthTestHost.StartAsync();

        var registered = host.Services.GetServices<EndpointDataSource>()
            .SelectMany(s => s.Endpoints)
            .OfType<RouteEndpoint>()
            .ToList();

        Assert.Empty(registered);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/v1/session")).StatusCode);
    }

    // ── indistinguishable failures ───────────────────────────────────────────

    /// <summary>
    /// Absent, unknown, revoked, a cookie nobody issued, and a bearer in the wrong scheme: one
    /// status and one body, byte for byte, so a caller learns nothing from which it got.
    /// </summary>
    [Fact]
    public async Task EveryRefusedCredential_ProducesByteIdenticalResponses()
    {
        await using var host = await NorthTestHost.StartAsync(conversations: new FakeConversationStore());
        var (_, _, revoked) = await host.EnrolledDeviceAsync();
        host.SignIn.Revoke(revoked);

        var requests = new List<HttpRequestMessage>
        {
            new(HttpMethod.Get, "/v1/session"),
            WithHeader("Authorization", "Bearer not-a-token"),
            WithHeader("Authorization", $"Bearer {revoked}"),
            WithHeader("Authorization", $"Basic {revoked}"),
            WithHeader("Cookie", "fleet_session=never-issued"),
        };

        var bodies = new List<string>();
        foreach (var request in requests)
        {
            var response = await host.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            bodies.Add(await response.Content.ReadAsStringAsync());
        }

        Assert.Single(bodies.Distinct(StringComparer.Ordinal));

        static HttpRequestMessage WithHeader(string name, string value)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/v1/session");
            request.Headers.TryAddWithoutValidation(name, value);
            return request;
        }
    }

    // ── session ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task SessionReturnsOnlyDerivedOwnerFieldsAndTheContractLimits()
    {
        await using var host = await NorthTestHost.StartAsync(conversations: new FakeConversationStore());
        var (_, _, token) = await host.EnrolledDeviceAsync();

        var body = await Body<SessionBody>(await host.SessionAsync(token));

        Assert.Equal(ProtocolVersion.Current, body.Protocol);
        Assert.Equal("p_owner", body.PrincipalId);
        Assert.Equal(ProtocolLimits.MaxInboundTextBytes, body.Limits.InboundTextBytes);
        Assert.Equal(CommsLimits.CatchUpLimitDefault, body.Limits.CatchUpLimitDefault);
        Assert.Equal(CommsLimits.CatchUpLimitMax, body.Limits.CatchUpLimitMax);
        Assert.Equal(CommsLimits.IdentifierMaxLength, body.Limits.IdentifierMaxLength);
        Assert.Equal(CommsLimits.OutboundBufferEvents, body.Limits.OutboundBufferEvents);
    }

    /// <summary>
    /// §1, §4: `principalId` comes from the credential and is never accepted from input. A request
    /// that supplies one must not be able to change the answer.
    /// </summary>
    [Fact]
    public async Task ASuppliedPrincipalIdCannotInfluenceTheSession()
    {
        await using var host = await NorthTestHost.StartAsync(conversations: new FakeConversationStore());
        var (_, _, token) = await host.EnrolledDeviceAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/session?principalId=p_attacker");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        request.Headers.TryAddWithoutValidation("X-Principal-Id", "p_attacker");

        var body = await Body<SessionBody>(await host.Client.SendAsync(request));

        Assert.Equal("p_owner", body.PrincipalId);
    }

    /// <summary>
    /// §3.7 / MUST NOT 2: the token is read from the Authorization header and from nowhere else.
    /// There is no code path that accepts one from a query string, so a client that put it there
    /// is simply unauthenticated.
    /// </summary>
    [Fact]
    public async Task ATokenInTheQueryStringDoesNotAuthenticate()
    {
        await using var host = await NorthTestHost.StartAsync(conversations: new FakeConversationStore());
        var (_, _, token) = await host.EnrolledDeviceAsync();

        var response = await host.Client.GetAsync($"/v1/session?access_token={token}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<T> Body<T>(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return System.Text.Json.JsonSerializer.Deserialize<T>(
            await response.Content.ReadAsStringAsync(), FleetProtocolJson.Options)!;
    }

    internal sealed record SessionBody(string Protocol, string PrincipalId, SessionLimitsBody Limits);

    internal sealed record SessionLimitsBody(
        int InboundTextBytes, int CatchUpLimitDefault, int CatchUpLimitMax,
        int IdentifierMaxLength, int OutboundBufferEvents);
}
