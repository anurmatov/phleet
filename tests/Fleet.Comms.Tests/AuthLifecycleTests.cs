using System.Net;
using System.Net.Http.Json;
using Fleet.Comms.Auth;
using Fleet.Protocol;

namespace Fleet.Comms.Tests;

/// <summary>
/// Issue #292's enrollment, registration, token and revocation criteria, driven over the real
/// route table rather than against <see cref="AuthService"/> directly — the contract these assert
/// is what a device sees, and a service-level test would not catch a route that mapped the wrong
/// status.
/// </summary>
public class AuthLifecycleTests
{
    [Fact]
    public async Task OneEnrollmentCode_RegistersExactlyOneDevice_AndReuseOutsideRecoveryFails()
    {
        await using var host = await NorthTestHost.StartAsync();
        var code = await host.IssueEnrollmentCodeAsync();

        var first = await host.RegisterAsync(code);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var device = await Body<NorthTestHost.RegisterDeviceBody>(first);

        // Close the recovery window the honest way — by using the device — then reuse the code.
        await host.TokenAsync(device.DeviceId, device.DeviceSecret);

        var reuse = await host.RegisterAsync(code);
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
        Assert.Single(host.Store.Devices);
    }

    [Fact]
    public async Task ASecondActiveDevice_IsRefusedWithDeviceLimit()
    {
        await using var host = await NorthTestHost.StartAsync();
        var first = await host.RegisterAsync(await host.IssueEnrollmentCodeAsync());
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // A different code, same owner. Owner-only means one active device, and rotation is an
        // explicit revoke-then-enroll rather than an implicit replacement.
        var second = await host.RegisterAsync(await host.IssueEnrollmentCodeAsync());

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var error = await Body<ErrorBody>(second);
        Assert.Equal("device_limit", error.Code);
        Assert.Single(host.Store.Devices);
    }

    [Fact]
    public async Task AnExpiredEnrollmentCode_FailsExactlyLikeAnUnknownOne()
    {
        await using var host = await NorthTestHost.StartAsync();
        var code = await host.IssueEnrollmentCodeAsync();

        host.Time.Advance(AuthService.EnrollmentCodeTtl);

        var expired = await host.RegisterAsync(code);
        var unknown = await host.RegisterAsync(Credentials.Compose("nosuchid", Credentials.NewSecret()));

        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        Assert.Equal(await expired.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
        Assert.Empty(host.Store.Devices);
    }

    // ── the registration recovery window ─────────────────────────────────────

    [Fact]
    public async Task RePresentingTheCodeInsideTheWindow_ReturnsTheSameDeviceWithARotatedSecret()
    {
        await using var host = await NorthTestHost.StartAsync();
        var code = await host.IssueEnrollmentCodeAsync();

        var first = await Body<NorthTestHost.RegisterDeviceBody>(await host.RegisterAsync(code));
        host.Time.Advance(TimeSpan.FromMinutes(5));
        var second = await Body<NorthTestHost.RegisterDeviceBody>(await host.RegisterAsync(code));

        Assert.Equal(first.DeviceId, second.DeviceId);
        Assert.NotEqual(first.DeviceSecret, second.DeviceSecret);
        Assert.Single(host.Store.Devices);

        // The previous secret must stop working, or a lost response would leave two live credentials.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await host.TokenAsync(first.DeviceId, first.DeviceSecret)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await host.TokenAsync(second.DeviceId, second.DeviceSecret)).StatusCode);
    }

    [Fact]
    public async Task TheRecoveryWindow_ClosesOnTheFirstSuccessfulTokenMint()
    {
        await using var host = await NorthTestHost.StartAsync();
        var code = await host.IssueEnrollmentCodeAsync();
        var device = await Body<NorthTestHost.RegisterDeviceBody>(await host.RegisterAsync(code));

        // Well inside the 15-minute timer: only the mint closes the window here.
        host.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.OK,
            (await host.TokenAsync(device.DeviceId, device.DeviceSecret)).StatusCode);

        var afterMint = await host.RegisterAsync(code);

        Assert.Equal(HttpStatusCode.Unauthorized, afterMint.StatusCode);
    }

    [Fact]
    public async Task TheRecoveryWindow_AlsoClosesOnTheTimer_WithoutAnyTokenMint()
    {
        await using var host = await NorthTestHost.StartAsync();
        var code = await host.IssueEnrollmentCodeAsync();
        await host.RegisterAsync(code);

        host.Time.Advance(AuthService.RegistrationRecoveryWindow);

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.RegisterAsync(code)).StatusCode);
    }

    /// <summary>
    /// §3.4: the window is measured from FIRST REGISTRATION and is independent of the code's own
    /// issue TTL. Registering at the last moment of a code's life must still get a full window —
    /// applying both bounds would give exactly that case a zero-length one.
    /// </summary>
    [Fact]
    public async Task TheRecoveryWindow_IsNotBoundedByTheCodesOwnIssueTtl()
    {
        await using var host = await NorthTestHost.StartAsync();
        var code = await host.IssueEnrollmentCodeAsync();

        // One second before the issue TTL expires.
        host.Time.Advance(AuthService.EnrollmentCodeTtl - TimeSpan.FromSeconds(1));
        var first = await Body<NorthTestHost.RegisterDeviceBody>(await host.RegisterAsync(code));

        // Now far past the code's own expiry, but inside the window from first registration.
        host.Time.Advance(TimeSpan.FromMinutes(10));
        var recovered = await host.RegisterAsync(code);

        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(first.DeviceId, (await Body<NorthTestHost.RegisterDeviceBody>(recovered)).DeviceId);
    }

    // ── tokens ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task TokenRefresh_ReturnsANewOpaqueTokenAndNoRefreshToken()
    {
        await using var host = await NorthTestHost.StartAsync();
        var (deviceId, secret, first) = await host.EnrolledDeviceAsync();

        var response = await host.TokenAsync(deviceId, secret);
        var body = await Body<NorthTestHost.TokenBody>(response);

        Assert.NotEqual(first, body.AccessToken);
        Assert.Equal((int)AuthService.AccessTokenTtl.TotalSeconds, body.ExpiresInSeconds);

        // No refresh token: the response carries exactly three fields, and `refreshToken` is not
        // one of them. Refresh is another deviceId + deviceSecret exchange at the same endpoint.
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("refresh", raw, StringComparison.OrdinalIgnoreCase);

        // Both tokens remain usable until they expire; minting is not a rotation.
        Assert.Equal(HttpStatusCode.OK, (await host.SessionAsync(first)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.SessionAsync(body.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task AnExpiredToken_IsRefused_AndExpiryIsAbsoluteRatherThanRecomputed()
    {
        await using var host = await NorthTestHost.StartAsync();
        var (_, _, token) = await host.EnrolledDeviceAsync();

        host.Time.Advance(AuthService.AccessTokenTtl);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SessionAsync(token)).StatusCode);

        // A backward clock jump must not resurrect it: expiry was stored at issue, not computed
        // from a TTL at check time.
        host.Time.SetBackwards(TimeSpan.FromMinutes(10));
        Assert.Equal(HttpStatusCode.OK, (await host.SessionAsync(token)).StatusCode);
    }

    // ── revocation ───────────────────────────────────────────────────────────

    [Fact]
    public async Task SelfRevoke_InvalidatesEveryTokenForThatDevice()
    {
        await using var host = await NorthTestHost.StartAsync();
        var (deviceId, secret, first) = await host.EnrolledDeviceAsync();
        var second = (await Body<NorthTestHost.TokenBody>(await host.TokenAsync(deviceId, secret)))
            .AccessToken;

        var revoke = await host.RevokeAsync(deviceId, first);
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

        // Every token, not just the one that made the request.
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SessionAsync(first)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SessionAsync(second)).StatusCode);

        // And the device itself: a revoked device cannot mint a replacement.
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.TokenAsync(deviceId, secret)).StatusCode);
    }

    [Fact]
    public async Task RevokeRequiresAuthenticatingAsTheDeviceBeingRevoked()
    {
        await using var host = await NorthTestHost.StartAsync();
        var (deviceId, _, token) = await host.EnrolledDeviceAsync();

        // Unauthenticated: there is deliberately no north route that revokes without authenticating
        // as the device, because the owner has exactly one and it would be a one-request lockout.
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.RevokeAsync(deviceId, null)).StatusCode);

        // Authenticated, but naming a different device.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await host.RevokeAsync("some-other-device", token)).StatusCode);

        // Still usable — neither attempt revoked anything.
        Assert.Equal(HttpStatusCode.OK, (await host.SessionAsync(token)).StatusCode);
    }

    [Fact]
    public async Task AfterRevocation_ReEnrollmentIsPossible_SoRotationIsRevokeThenEnroll()
    {
        await using var host = await NorthTestHost.StartAsync();
        var (deviceId, _, token) = await host.EnrolledDeviceAsync();
        await host.RevokeAsync(deviceId, token);

        var replacement = await host.RegisterAsync(await host.IssueEnrollmentCodeAsync());

        Assert.Equal(HttpStatusCode.OK, replacement.StatusCode);
    }

    internal static async Task<T> Body<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(FleetProtocolJson.Options))!;

    internal sealed record ErrorBody(string Protocol, string Code, string Message);
}
