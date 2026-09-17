using System.Net;
using Fleet.Comms.Auth;
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
    };

    [Theory]
    [MemberData(nameof(SouthRoutes))]
    public async Task NoSouthRouteIsReachableOnTheNorthListener(string method, string path)
    {
        await using var host = await NorthTestHost.StartAsync();

        var response = await host.Client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// The structural half, and the one that actually holds the line. Probing for 404s proves those
    /// nine paths are absent today; this proves the north app's route table is EXACTLY the four
    /// routes of this slice, so mapping a south endpoint onto it later fails here rather than
    /// shipping.
    /// </summary>
    [Fact]
    public async Task TheNorthRouteTableIsExactlyTheSliceOneRoutes()
    {
        await using var host = await NorthTestHost.StartAsync();

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

        Assert.Equal(NorthEndpoints.Routes.OrderBy(x => x, StringComparer.Ordinal).ToList(), registered);
    }

    // ── indistinguishable failures ───────────────────────────────────────────

    /// <summary>
    /// §3.6: enrollment failure, unknown device, bad secret, expired token and revoked token are
    /// indistinguishable to the caller. Asserted on the raw bytes, not on the status alone — a
    /// differing `message` or field order would be an oracle just as surely as a differing code.
    /// </summary>
    [Fact]
    public async Task TheFiveAuthFailures_ProduceByteIdenticalResponses()
    {
        await using var host = await NorthTestHost.StartAsync();
        var (deviceId, secret, token) = await host.EnrolledDeviceAsync();

        var bodies = new List<(string Case, string Body, HttpStatusCode Status)>();

        var unknownCode = await host.RegisterAsync(
            Credentials.Compose("nosuchid", Credentials.NewSecret()));
        bodies.Add(("unknown enrollment code", await unknownCode.Content.ReadAsStringAsync(), unknownCode.StatusCode));

        var unknownDevice = await host.TokenAsync("nosuchdevice", secret);
        bodies.Add(("unknown device", await unknownDevice.Content.ReadAsStringAsync(), unknownDevice.StatusCode));

        var badSecret = await host.TokenAsync(deviceId, Credentials.NewSecret());
        bodies.Add(("bad secret", await badSecret.Content.ReadAsStringAsync(), badSecret.StatusCode));

        await host.RevokeAsync(deviceId, token);
        var revokedToken = await host.SessionAsync(token);
        bodies.Add(("revoked token", await revokedToken.Content.ReadAsStringAsync(), revokedToken.StatusCode));

        // The expired case needs a token that is still valid when the clock moves, so it uses a
        // fresh device — the first one is revoked by now, which is also what makes a second
        // registration legal here. Rolling the clock BACKWARDS to reuse the first device, as this
        // test once did, is no longer possible and never should have been: expiry is irreversible,
        // and a test that depended on reversing it was asserting the bug.
        var replacement = await AuthLifecycleTests.Body<NorthTestHost.RegisterDeviceBody>(
            await host.RegisterAsync(await host.IssueEnrollmentCodeAsync()));
        var replacementToken = await AuthLifecycleTests.Body<NorthTestHost.TokenBody>(
            await host.TokenAsync(replacement.DeviceId, replacement.DeviceSecret));

        host.Time.Advance(AuthService.AccessTokenTtl);
        var expiredToken = await host.SessionAsync(replacementToken.AccessToken);
        bodies.Add(("expired token", await expiredToken.Content.ReadAsStringAsync(), expiredToken.StatusCode));

        Assert.All(bodies, b => Assert.Equal(HttpStatusCode.Unauthorized, b.Status));
        Assert.Single(bodies.Select(b => b.Body).Distinct(StringComparer.Ordinal));
    }

    // ── fail closed ──────────────────────────────────────────────────────────

    [Fact]
    public async Task AnAuthStoreOutage_Returns503AndNever2xx()
    {
        await using var host = await NorthTestHost.StartAsync();
        var (deviceId, secret, token) = await host.EnrolledDeviceAsync();

        host.Store.FailEveryOperation = true;

        foreach (var response in new[]
                 {
                     await host.RegisterAsync(Credentials.Compose("id", Credentials.NewSecret())),
                     await host.TokenAsync(deviceId, secret),
                     await host.SessionAsync(token),
                     await host.RevokeAsync(deviceId, token),
                 })
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            var body = await AuthLifecycleTests.Body<AuthLifecycleTests.ErrorBody>(response);
            Assert.Equal("internal", body.Code);
            Assert.Equal(ProtocolErrors.Internal, body.Message);
        }
    }

    /// <summary>
    /// §16: a code is consumed atomically or not at all. A partial consume leaving a code both
    /// spent and unusable locks the owner out with no recovery.
    /// </summary>
    [Fact]
    public async Task AFailedCommitLeavesNoHalfSpentEnrollmentState()
    {
        await using var host = await NorthTestHost.StartAsync();
        var code = await host.IssueEnrollmentCodeAsync();

        host.Store.FailOnCommit = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await host.RegisterAsync(code)).StatusCode);
        host.Store.FailOnCommit = false;

        // Nothing was written: no device, and the code is still unconsumed.
        Assert.Empty(host.Store.Devices);
        Assert.All(host.Store.Enrollments, e => Assert.Null(e.ConsumedAt));

        // And the owner is not locked out — the same code still registers.
        Assert.Equal(HttpStatusCode.OK, (await host.RegisterAsync(code)).StatusCode);
    }

    // ── session ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task SessionReturnsOnlyDerivedOwnerFieldsAndTheContractLimits()
    {
        await using var host = await NorthTestHost.StartAsync();
        var (_, _, token) = await host.EnrolledDeviceAsync();

        var body = await AuthLifecycleTests.Body<SessionBody>(await host.SessionAsync(token));

        Assert.Equal(ProtocolVersion.Current, body.Protocol);
        Assert.Equal("p_owner", body.PrincipalId);
        Assert.Equal("assistant", body.AgentLabel);
        Assert.Equal(ProtocolLimits.MaxInboundTextBytes, body.Limits.InboundTextBytes);
        Assert.Equal(CommsLimits.CatchUpLimitDefault, body.Limits.CatchUpLimitDefault);
        Assert.Equal(CommsLimits.CatchUpLimitMax, body.Limits.CatchUpLimitMax);
        Assert.Equal(CommsLimits.IdentifierMaxLength, body.Limits.IdentifierMaxLength);
        Assert.Equal(CommsLimits.OutboundBufferEvents, body.Limits.OutboundBufferEvents);
    }

    /// <summary>
    /// §1, §4: `principalId` is derived from the device record and never accepted from input. A
    /// request that supplies one must not be able to change the answer.
    /// </summary>
    [Fact]
    public async Task ASuppliedPrincipalIdCannotInfluenceTheSession()
    {
        await using var host = await NorthTestHost.StartAsync();
        var (_, _, token) = await host.EnrolledDeviceAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/session?principalId=p_attacker");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        request.Headers.TryAddWithoutValidation("X-Principal-Id", "p_attacker");

        var body = await AuthLifecycleTests.Body<SessionBody>(await host.Client.SendAsync(request));

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
        await using var host = await NorthTestHost.StartAsync();
        var (_, _, token) = await host.EnrolledDeviceAsync();

        var response = await host.Client.GetAsync($"/v1/session?access_token={token}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    internal sealed record SessionBody(
        string Protocol, string PrincipalId, string AgentLabel, SessionLimitsBody Limits);

    internal sealed record SessionLimitsBody(
        int InboundTextBytes, int CatchUpLimitDefault, int CatchUpLimitMax,
        int IdentifierMaxLength, int OutboundBufferEvents);
}
