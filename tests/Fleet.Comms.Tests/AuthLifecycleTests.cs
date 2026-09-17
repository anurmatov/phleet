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
    public async Task AnExpiredToken_IsRefused_AndABackwardClockJumpDoesNotResurrectIt()
    {
        await using var host = await NorthTestHost.StartAsync();
        var (_, _, token) = await host.EnrolledDeviceAsync();

        host.Time.Advance(AuthService.AccessTokenTtl);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SessionAsync(token)).StatusCode);

        // Storing an absolute deadline is necessary and NOT sufficient: the deadline is fixed, but
        // the value it is compared against is wall time, and wall time moves backwards. A refused
        // credential that authenticates again after an NTP step is the one direction this boundary
        // must never move, so the comparison runs against MonotonicClock and the token is burned
        // in the store the first time it is seen expired.
        host.Time.SetBackwards(TimeSpan.FromMinutes(10));
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SessionAsync(token)).StatusCode);

        // Far enough back that the original issue instant is in the future, which is the shape a
        // restored snapshot produces.
        host.Time.SetBackwards(TimeSpan.FromHours(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SessionAsync(token)).StatusCode);
    }

    /// <summary>
    /// The other half of the rollback problem, and the half the burn cannot reach: a credential
    /// that has <b>not yet</b> been observed expired.
    ///
    /// <para>Fifteen real minutes pass, but the host's clock is stepped back in the middle of them,
    /// so by wall time the token looks five minutes old. Compared against wall time it authenticates
    /// — a stolen token whose life an attacker can extend by however far the clock moves. Only the
    /// monotonic reading refuses it, because only the monotonic reading knows time actually passed.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ABackwardClockJumpDoesNotExtendTheLifeOfAStillValidToken()
    {
        await using var host = await NorthTestHost.StartAsync();
        var (_, _, token) = await host.EnrolledDeviceAsync();

        host.Time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(HttpStatusCode.OK, (await host.SessionAsync(token)).StatusCode);

        // The clock is corrected ten minutes backwards, then five more real minutes pass.
        host.Time.SetBackwards(TimeSpan.FromMinutes(10));
        host.Time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        // Wall time says the token is five minutes old. Fifteen minutes and a second of real time
        // have elapsed since it was issued.
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SessionAsync(token)).StatusCode);
    }

    // ── forward correction, then rollback: all three deadlines ───────────────
    //
    // The sequence that froze effective time: correct the host FORWARD (wall only), issue or
    // consume the credential, then roll back and let real time pass. The clock adopted the forward
    // jump without moving the point elapsed time was measured from, so afterwards the projection
    // trailed by the size of the jump and effective time advanced at zero — every deadline below
    // simply stopped arriving. Each credential window gets its own test because each is compared in
    // a different method, and one of them being right proves nothing about the other two.

    [Fact]
    public async Task AccessTokenExpiry_SurvivesAForwardCorrectionFollowedByARollback()
    {
        await using var host = await NorthTestHost.StartAsync();

        host.Time.SetForward(TimeSpan.FromHours(3));
        var (_, _, token) = await host.EnrolledDeviceAsync();

        host.Time.SetBackwards(TimeSpan.FromHours(3));
        host.Time.Advance(AuthService.AccessTokenTtl + TimeSpan.FromMinutes(1));

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SessionAsync(token)).StatusCode);
    }

    [Fact]
    public async Task UnusedEnrollmentCodeExpiry_SurvivesAForwardCorrectionFollowedByARollback()
    {
        await using var host = await NorthTestHost.StartAsync();

        host.Time.SetForward(TimeSpan.FromHours(3));
        var code = await host.IssueEnrollmentCodeAsync();

        host.Time.SetBackwards(TimeSpan.FromHours(3));
        host.Time.Advance(AuthService.EnrollmentCodeTtl + TimeSpan.FromMinutes(1));

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.RegisterAsync(code)).StatusCode);
        Assert.Empty(host.Store.Devices);
    }

    [Fact]
    public async Task RecoveryWindowExpiry_SurvivesAForwardCorrectionFollowedByARollback()
    {
        await using var host = await NorthTestHost.StartAsync();

        host.Time.SetForward(TimeSpan.FromHours(3));
        var code = await host.IssueEnrollmentCodeAsync();
        var first = await Body<NorthTestHost.RegisterDeviceBody>(await host.RegisterAsync(code));

        host.Time.SetBackwards(TimeSpan.FromHours(3));
        host.Time.Advance(AuthService.RegistrationRecoveryWindow + TimeSpan.FromMinutes(1));

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.RegisterAsync(code)).StatusCode);

        // The device registered before the correction keeps working: the window closed, it was not
        // reopened and the secret was not rotated out from under a client still holding it.
        Assert.Equal(HttpStatusCode.OK,
            (await host.TokenAsync(first.DeviceId, first.DeviceSecret)).StatusCode);
    }

    /// <summary>
    /// The burn is what carries the refusal across a restart, after which the monotonic clock has
    /// no choice but to re-anchor to whatever the host says. Asserted on the stored record, since
    /// that is the only part of the decision that outlives the process.
    /// </summary>
    [Fact]
    public async Task AnExpiredTokenIsBurnedInTheStore_NotMerelyRefused()
    {
        await using var host = await NorthTestHost.StartAsync();
        var (_, _, token) = await host.EnrolledDeviceAsync();

        Assert.All(host.Store.Tokens, t => Assert.Null(t.RevokedAt));

        host.Time.Advance(AuthService.AccessTokenTtl);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SessionAsync(token)).StatusCode);

        Assert.All(host.Store.Tokens, t => Assert.NotNull(t.RevokedAt));
    }

    [Fact]
    public async Task AnExpiredEnrollmentCode_IsNotResurrectedByABackwardClockJump()
    {
        await using var host = await NorthTestHost.StartAsync();
        var code = await host.IssueEnrollmentCodeAsync();

        host.Time.Advance(AuthService.EnrollmentCodeTtl);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.RegisterAsync(code)).StatusCode);

        host.Time.SetBackwards(AuthService.EnrollmentCodeTtl);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.RegisterAsync(code)).StatusCode);
        Assert.Empty(host.Store.Devices);

        // Burned, so the refusal is a fact in the store rather than an opinion of the clock.
        Assert.All(host.Store.Enrollments, e => Assert.NotNull(e.RevokedAt));
    }

    [Fact]
    public async Task AnExpiredRecoveryWindow_IsNotResurrectedByABackwardClockJump()
    {
        await using var host = await NorthTestHost.StartAsync();
        var code = await host.IssueEnrollmentCodeAsync();
        var first = await Body<NorthTestHost.RegisterDeviceBody>(await host.RegisterAsync(code));

        host.Time.Advance(AuthService.RegistrationRecoveryWindow);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.RegisterAsync(code)).StatusCode);

        host.Time.SetBackwards(AuthService.RegistrationRecoveryWindow);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.RegisterAsync(code)).StatusCode);

        // And the device that was registered keeps the secret it already had — a re-opened window
        // would have rotated it out from under a client that is still using it.
        Assert.Equal(HttpStatusCode.OK,
            (await host.TokenAsync(first.DeviceId, first.DeviceSecret)).StatusCode);
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
