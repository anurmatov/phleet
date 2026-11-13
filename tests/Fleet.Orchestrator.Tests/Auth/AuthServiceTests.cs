using Fleet.Orchestrator.Auth;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Orchestrator.Tests.Auth;

/// <summary>
/// The rules of sign-in that are about time, limits and cost — asserted against the service, with
/// a clock the test moves by hand.
/// </summary>
public class AuthServiceTests
{
    private const string Owner = UserRecord.OwnerId;

    private sealed class Fixture
    {
        public TestTimeProvider Time { get; } = new();
        public InMemoryAuthStore Store { get; } = new();
        public CountingSecretHasher Hasher { get; } = new(new Argon2idSecretHasher(memoryKib: 64, iterations: 1));
        public AuthService Auth { get; }

        public Fixture()
        {
            Auth = new AuthService(Store, Hasher, new MonotonicClock(Time), NullLogger<AuthService>.Instance);
        }

        public static async Task<Fixture> StartAsync()
        {
            var fixture = new Fixture();
            await fixture.Auth.EnsureOwnerAsync(null);
            return fixture;
        }

        public async Task<string> CodeAsync(string? name = null) =>
            (await Auth.IssueEnrollmentCodeAsync(Owner, name)).Value!.EnrollmentCode;

        public async Task<(string DeviceId, string Secret, string Token)> EnrolledAsync()
        {
            var device = (await Auth.RegisterDeviceAsync(await CodeAsync(), null)).Value!;
            var token = (await Auth.MintTokenAsync(device.DeviceId, device.DeviceSecret)).Value!;
            return (device.DeviceId, device.DeviceSecret, token.AccessToken);
        }

        public async Task<bool> AccessWorksAsync(string token) =>
            (await Auth.AuthenticateAsync(token, TokenKind.Access)).Succeeded;

        public async Task<bool> SessionWorksAsync(string token) =>
            (await Auth.AuthenticateAsync(token, TokenKind.Session)).Succeeded;
    }

    // ── the owner ────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheOwnerIsCreatedOnce_AndFollowsTheDeploymentsTelegramId()
    {
        var f = new Fixture();

        await f.Auth.EnsureOwnerAsync(null);
        var created = await f.Auth.FindUserAsync(Owner);
        Assert.NotNull(created);
        Assert.Null(created.TelegramUserId);

        await f.Auth.EnsureOwnerAsync(4242);
        Assert.Equal(4242, (await f.Auth.FindUserAsync(Owner))!.TelegramUserId);

        // Unset later: the stored value stays. A blank setting is "not told", not "forget".
        await f.Auth.EnsureOwnerAsync(null);
        var kept = await f.Auth.FindUserAsync(Owner);
        Assert.Equal(4242, kept!.TelegramUserId);
        Assert.Equal(created.CreatedAt, kept.CreatedAt);
    }

    [Fact]
    public async Task NothingIsIssuedForAUserThatDoesNotExist()
    {
        var f = await Fixture.StartAsync();

        Assert.Equal(AuthError.Unauthorized, (await f.Auth.IssueEnrollmentCodeAsync("nobody", null)).Error);
        Assert.Equal(AuthError.Unauthorized, (await f.Auth.SignInBrowserAsync("nobody", null)).Error);
        Assert.Empty(f.Store.Enrollments);
        Assert.Empty(f.Store.Devices);
    }

    // ── enrollment and its recovery window ───────────────────────────────────

    [Fact]
    public async Task AnExpiredCode_IsRefused_AndABackwardClockJumpDoesNotResurrectIt()
    {
        var f = await Fixture.StartAsync();
        var code = await f.CodeAsync();

        f.Time.Advance(AuthService.EnrollmentCodeTtl);
        Assert.Equal(AuthError.Unauthorized, (await f.Auth.RegisterDeviceAsync(code, null)).Error);

        f.Time.SetBackwards(TimeSpan.FromHours(1));
        Assert.Equal(AuthError.Unauthorized, (await f.Auth.RegisterDeviceAsync(code, null)).Error);
        Assert.Empty(f.Store.Devices);
    }

    [Fact]
    public async Task RePresentingTheCodeInsideTheWindow_ReturnsTheSameDeviceWithARotatedSecret()
    {
        var f = await Fixture.StartAsync();
        var code = await f.CodeAsync();

        var first = (await f.Auth.RegisterDeviceAsync(code, null)).Value!;
        var again = (await f.Auth.RegisterDeviceAsync(code, null)).Value!;

        Assert.Equal(first.DeviceId, again.DeviceId);
        Assert.NotEqual(first.DeviceSecret, again.DeviceSecret);
        // The lost response's secret is dead; only the one the client actually received works.
        Assert.False((await f.Auth.MintTokenAsync(first.DeviceId, first.DeviceSecret)).Succeeded);
        Assert.True((await f.Auth.MintTokenAsync(again.DeviceId, again.DeviceSecret)).Succeeded);
        Assert.Single(f.Store.Devices);
    }

    [Fact]
    public async Task TheRecoveryWindow_ClosesOnTheFirstTokenMint()
    {
        var f = await Fixture.StartAsync();
        var code = await f.CodeAsync();
        var device = (await f.Auth.RegisterDeviceAsync(code, null)).Value!;

        Assert.True((await f.Auth.MintTokenAsync(device.DeviceId, device.DeviceSecret)).Succeeded);

        // The device has proven it holds the secret; another copy could only help someone else.
        Assert.Equal(AuthError.Unauthorized, (await f.Auth.RegisterDeviceAsync(code, null)).Error);
        Assert.True((await f.Auth.MintTokenAsync(device.DeviceId, device.DeviceSecret)).Succeeded);
    }

    [Fact]
    public async Task TheRecoveryWindow_AlsoClosesOnTheTimer_AndIsNotCutShortByTheCodesOwnLifetime()
    {
        var f = await Fixture.StartAsync();
        var code = await f.CodeAsync();

        // Registered one minute before the code would have expired unused.
        f.Time.Advance(AuthService.EnrollmentCodeTtl - TimeSpan.FromMinutes(1));
        var first = (await f.Auth.RegisterDeviceAsync(code, null)).Value!;

        // Past the code's issue lifetime, still inside the window measured from registration.
        f.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(first.DeviceId, (await f.Auth.RegisterDeviceAsync(code, null)).Value!.DeviceId);

        f.Time.Advance(AuthService.RegistrationRecoveryWindow);
        Assert.Equal(AuthError.Unauthorized, (await f.Auth.RegisterDeviceAsync(code, null)).Error);
    }

    // ── access tokens ────────────────────────────────────────────────────────

    [Fact]
    public async Task AnAccessTokenExpires_IsBurned_AndIsNeverExtendedByUse()
    {
        var f = await Fixture.StartAsync();
        var (_, _, token) = await f.EnrolledAsync();

        // Used right up to its deadline: use does not move the deadline.
        f.Time.Advance(AuthService.AccessTokenTtl - TimeSpan.FromSeconds(1));
        Assert.True(await f.AccessWorksAsync(token));

        f.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(await f.AccessWorksAsync(token));
        Assert.Contains(f.Store.Tokens, t => t.Kind == TokenKind.Access && t.RevokedAt is not null);

        f.Time.SetBackwards(TimeSpan.FromHours(1));
        Assert.False(await f.AccessWorksAsync(token));
    }

    [Fact]
    public async Task ABackwardClockJumpDoesNotExtendTheLifeOfAStillValidToken()
    {
        var f = await Fixture.StartAsync();
        var (_, _, token) = await f.EnrolledAsync();

        // Fifteen real minutes pass, but the host clock is stepped back in the middle of them, so
        // by wall time the token looks five minutes old.
        f.Time.Advance(TimeSpan.FromMinutes(5));
        f.Time.SetBackwards(TimeSpan.FromMinutes(10));
        f.Time.Advance(TimeSpan.FromMinutes(10));

        Assert.False(await f.AccessWorksAsync(token));
    }

    [Fact]
    public async Task ARefreshIsANewToken_AndTheOldOneLivesOutItsTime()
    {
        var f = await Fixture.StartAsync();
        var (deviceId, secret, first) = await f.EnrolledAsync();

        var second = (await f.Auth.MintTokenAsync(deviceId, secret)).Value!.AccessToken;

        Assert.NotEqual(first, second);
        Assert.True(await f.AccessWorksAsync(first));
        Assert.True(await f.AccessWorksAsync(second));
    }

    // ── browser sessions ─────────────────────────────────────────────────────

    [Fact]
    public async Task ASessionIsExtendedByUse_OnceItIsHalfSpent()
    {
        var f = await Fixture.StartAsync();
        var session = (await f.Auth.SignInBrowserAsync(Owner, "Laptop")).Value!;

        // Fresh: nothing to extend.
        var early = (await f.Auth.AuthenticateAsync(session.SessionToken, TokenKind.Session)).Value!;
        Assert.False(early.Extended);
        Assert.Equal(session.ExpiresAt, early.ExpiresAt);

        f.Time.Advance(AuthService.SessionTtl - AuthService.SessionExtendBelow + TimeSpan.FromMinutes(1));
        var later = (await f.Auth.AuthenticateAsync(session.SessionToken, TokenKind.Session)).Value!;
        Assert.True(later.Extended);
        Assert.Equal(f.Time.GetUtcNow() + AuthService.SessionTtl, later.ExpiresAt);

        // And it now outlives its original deadline.
        f.Time.Advance(AuthService.SessionExtendBelow);
        Assert.True(await f.SessionWorksAsync(session.SessionToken));
    }

    [Fact]
    public async Task ASessionLeftUnusedExpires_AndCannotBeRevivedByPresentingIt()
    {
        var f = await Fixture.StartAsync();
        var session = (await f.Auth.SignInBrowserAsync(Owner, null)).Value!;

        f.Time.Advance(AuthService.SessionTtl);

        Assert.False(await f.SessionWorksAsync(session.SessionToken));
        Assert.False(await f.SessionWorksAsync(session.SessionToken));
    }

    [Fact]
    public async Task ABrowserHasNoDeviceSecret_SoItsDeviceIdMintsNothing()
    {
        var f = await Fixture.StartAsync();
        var session = (await f.Auth.SignInBrowserAsync(Owner, null)).Value!;

        Assert.False((await f.Auth.MintTokenAsync(session.DeviceId, "")).Succeeded);
        Assert.False((await f.Auth.MintTokenAsync(session.DeviceId, Credentials.NewSecret())).Succeeded);
    }

    [Fact]
    public async Task EachTokenKindIsRefusedOnTheOtherCarrier()
    {
        var f = await Fixture.StartAsync();
        var (_, _, access) = await f.EnrolledAsync();
        var session = (await f.Auth.SignInBrowserAsync(Owner, null)).Value!.SessionToken;

        Assert.False(await f.SessionWorksAsync(access));
        Assert.False(await f.AccessWorksAsync(session));
        Assert.True(await f.AccessWorksAsync(access));
        Assert.True(await f.SessionWorksAsync(session));
    }

    // ── devices ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheDeviceLimit_RefusesTheNextDevice_AndNeverDisplacesAnExistingOne()
    {
        var f = await Fixture.StartAsync();
        var heldBack = await f.CodeAsync();
        for (var i = 0; i < AuthService.MaxActiveDevices; i++)
            Assert.True((await f.Auth.SignInBrowserAsync(Owner, $"browser {i}")).Succeeded);

        Assert.Equal(AuthError.DeviceLimit, (await f.Auth.SignInBrowserAsync(Owner, "one too many")).Error);
        Assert.Equal(AuthError.DeviceLimit, (await f.Auth.IssueEnrollmentCodeAsync(Owner, null)).Error);
        // A code issued while there was room, presented when there is none.
        Assert.Equal(AuthError.DeviceLimit, (await f.Auth.RegisterDeviceAsync(heldBack, null)).Error);

        Assert.Equal(AuthService.MaxActiveDevices, f.Store.Devices.Count(d => d.IsActive));

        // Making room is an explicit revoke, after which the same code still registers.
        var one = f.Store.Devices.First(d => d.IsActive);
        Assert.True(await f.Auth.RevokeDeviceAsync(Owner, one.DeviceId));
        Assert.True((await f.Auth.RegisterDeviceAsync(heldBack, null)).Succeeded);
    }

    [Fact]
    public async Task ABrowserWhoseSessionsAllLapsed_NoLongerHoldsASlot()
    {
        var f = await Fixture.StartAsync();
        for (var i = 0; i < AuthService.MaxActiveDevices; i++)
            await f.Auth.SignInBrowserAsync(Owner, $"old browser {i}");
        Assert.Equal(AuthError.DeviceLimit, (await f.Auth.IssueEnrollmentCodeAsync(Owner, null)).Error);

        // Nobody came back to any of them.
        f.Time.Advance(AuthService.SessionTtl);

        Assert.True((await f.Auth.SignInBrowserAsync(Owner, "today's browser")).Succeeded);
        Assert.Single(f.Store.Devices, d => d.IsActive);
    }

    [Fact]
    public async Task RevokingADevice_EndsEveryTokenItHolds_AndIsScopedToItsUser()
    {
        var f = await Fixture.StartAsync();
        var (deviceId, secret, token) = await f.EnrolledAsync();
        var second = (await f.Auth.MintTokenAsync(deviceId, secret)).Value!.AccessToken;

        // Someone else's device is answered exactly as one that does not exist.
        Assert.False(await f.Auth.RevokeDeviceAsync("someone-else", deviceId));
        Assert.True(await f.AccessWorksAsync(token));

        Assert.True(await f.Auth.RevokeDeviceAsync(Owner, deviceId));
        Assert.False(await f.AccessWorksAsync(token));
        Assert.False(await f.AccessWorksAsync(second));
        Assert.False((await f.Auth.MintTokenAsync(deviceId, secret)).Succeeded);

        // A state, not a transition: asking again succeeds.
        Assert.True(await f.Auth.RevokeDeviceAsync(Owner, deviceId));
        Assert.False(await f.Auth.RevokeDeviceAsync(Owner, "nosuchdevice"));
    }

    [Fact]
    public async Task ADeviceIsMarkedSeen_ButNotRewrittenOnEveryCheck()
    {
        var f = await Fixture.StartAsync();
        var (deviceId, _, token) = await f.EnrolledAsync();
        var atMint = f.Store.Devices.Single(d => d.DeviceId == deviceId).LastSeenAt;

        f.Time.Advance(TimeSpan.FromMinutes(1));
        await f.AccessWorksAsync(token);
        Assert.Equal(atMint, f.Store.Devices.Single(d => d.DeviceId == deviceId).LastSeenAt);

        f.Time.Advance(AuthService.LastSeenResolution);
        await f.AccessWorksAsync(token);
        Assert.Equal(f.Time.GetUtcNow(), f.Store.Devices.Single(d => d.DeviceId == deviceId).LastSeenAt);
    }

    [Fact]
    public async Task SpentRowsAreDeleted_AndLiveOnesAreNot()
    {
        var f = await Fixture.StartAsync();
        var (deviceId, secret, _) = await f.EnrolledAsync();
        var unused = await f.CodeAsync();

        f.Time.Advance(AuthService.SpentRetention + AuthService.AccessTokenTtl + TimeSpan.FromMinutes(1));
        var fresh = (await f.Auth.MintTokenAsync(deviceId, secret)).Value!.AccessToken;

        // The day-old token and both day-old codes are gone; the device and its new token remain.
        Assert.Single(f.Store.Tokens);
        Assert.Empty(f.Store.Enrollments);
        Assert.True(await f.AccessWorksAsync(fresh));
        Assert.Equal(AuthError.Unauthorized, (await f.Auth.RegisterDeviceAsync(unused, null)).Error);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    [InlineData("  Kitchen laptop  ", "Kitchen laptop")]
    [InlineData("line one\nline two\t!", "line oneline two!")]
    public void ADeviceNameIsOneBoundedLine(string? given, string expected) =>
        Assert.Equal(expected, AuthService.CleanName(given));

    [Fact]
    public void AnOverlongDeviceNameIsCut_NotRefused() =>
        Assert.Equal(AuthService.MaxDeviceNameLength, AuthService.CleanName(new string('n', 500)).Length);

    // ── cost ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EveryCredentialRejection_SpendsExactlyOneKdfEvaluation()
    {
        var f = await Fixture.StartAsync();
        var (deviceId, secret, token) = await f.EnrolledAsync();
        var session = (await f.Auth.SignInBrowserAsync(Owner, null)).Value!;

        // Each row is "one call, then how many verifications did that cost?". The comparison that
        // matters is between rows: a lookup that found nothing must cost what one that did costs.
        Assert.Equal(1, await CostOf(f, () => f.Auth.MintTokenAsync("nosuchdevice", secret)));
        Assert.Equal(1, await CostOf(f, () => f.Auth.MintTokenAsync(deviceId, Credentials.NewSecret())));
        Assert.Equal(1, await CostOf(f, () => f.Auth.MintTokenAsync(session.DeviceId, secret)));
        Assert.Equal(1, await CostOf(f, () => f.Auth.RegisterDeviceAsync(
            Credentials.Compose("nosuchid", Credentials.NewSecret()), null)));
        Assert.Equal(1, await CostOf(f, () => f.Auth.AuthenticateAsync(
            Credentials.Compose("nosuchtoken", Credentials.NewSecret()), TokenKind.Access)));
        Assert.Equal(1, await CostOf(f, () => f.Auth.AuthenticateAsync(token, TokenKind.Session)));

        // A revoked device and a revoked token are lookups that DID find something, and are the
        // pair most easily broken by moving a cheap state test above the verify.
        await f.Auth.RevokeDeviceAsync(Owner, deviceId);
        Assert.Equal(1, await CostOf(f, () => f.Auth.AuthenticateAsync(token, TokenKind.Access)));
        Assert.Equal(1, await CostOf(f, () => f.Auth.MintTokenAsync(deviceId, secret)));

        // Unparseable input is the caller's own mistake and buys no work at all.
        Assert.Equal(0, await CostOf(f, () => f.Auth.AuthenticateAsync("no-separator", TokenKind.Access)));
    }

    private static async Task<int> CostOf<T>(Fixture f, Func<Task<T>> call)
    {
        var before = f.Hasher.Verifications;
        await call();
        return f.Hasher.Verifications - before;
    }

    // ── atomicity ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ARegistrationThatFailsToCommit_LeavesTheCodeUnspent()
    {
        var f = await Fixture.StartAsync();
        var code = await f.CodeAsync();

        f.Store.FailOnCommit = true;
        await Assert.ThrowsAsync<AuthStoreUnavailableException>(() => f.Auth.RegisterDeviceAsync(code, null));
        f.Store.FailOnCommit = false;

        // Neither a device without a consumed code nor a consumed code without a device.
        Assert.Empty(f.Store.Devices);
        Assert.True((await f.Auth.RegisterDeviceAsync(code, null)).Succeeded);
    }
}
