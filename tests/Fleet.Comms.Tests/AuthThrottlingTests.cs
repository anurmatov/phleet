using System.Net;
using Fleet.Comms.Auth;
using Fleet.Comms.Configuration;
using Fleet.Protocol;

namespace Fleet.Comms.Tests;

/// <summary>
/// §5.4 and §12: credential abuse is answered with `429 rate_limited` and an integer
/// `Retry-After`, and the refusal happens before the request costs anything.
///
/// <para>The cost is the reason this exists. Every credential check spends one Argon2id evaluation
/// at 19 MiB — including the failures, deliberately, so a rejection cannot be timed. That makes an
/// unauthenticated request an amplifier, and an amplifier without an admission bound is a way to
/// push the owner's own token refresh out of the way with junk.</para>
/// </summary>
public class AuthThrottlingTests
{
    [Fact]
    public void TheLimitsAreExplicitAndQueueingIsOff()
    {
        Assert.Equal(30, AuthRateLimits.PermitsPerWindow);
        Assert.Equal(60, AuthRateLimits.WindowSeconds);

        // Zero, not "small". A queue holds the rejected caller's request open and turns the backlog
        // itself into the resource being exhausted; §5.4 hands the caller a delay instead.
        Assert.Equal(0, AuthRateLimits.QueueLimit);
    }

    [Fact]
    public async Task ABurstOfWrongSecrets_IsThrottledWithTheFixedBodyAndAnIntegerRetryAfter()
    {
        var hasher = new CountingSecretHasher(new Argon2idSecretHasher());
        await using var host = await NorthTestHost.StartAsync(hasher);
        var (deviceId, _, _) = await host.EnrolledDeviceAsync();

        var admitted = 0;
        var throttled = 0;
        var costAtFirstThrottle = -1;

        for (var attempt = 0; attempt < AuthRateLimits.PermitsPerWindow * 2; attempt++)
        {
            var before = hasher.Verifications;
            var response = await host.TokenAsync(deviceId, Credentials.NewSecret());

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                admitted++;
                continue;
            }

            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            throttled++;

            // The whole point of putting admission in middleware: a throttled request must not
            // reach the hasher. If this ever becomes non-zero the limiter has been moved below the
            // endpoint and is protecting nothing.
            if (costAtFirstThrottle < 0)
                costAtFirstThrottle = hasher.Verifications - before;
            Assert.Equal(before, hasher.Verifications);

            var body = await AuthLifecycleTests.Body<AuthLifecycleTests.ErrorBody>(response);
            Assert.Equal("rate_limited", body.Code);
            Assert.Equal(ProtocolErrors.RateLimited, body.Message);
            Assert.Equal(ProtocolVersion.Current, body.Protocol);

            // §5.4: seconds, as a non-negative integer, never the HTTP-date form.
            var retryAfter = Assert.Single(response.Headers.GetValues("Retry-After"));
            Assert.True(int.TryParse(retryAfter, out var seconds),
                $"Retry-After was '{retryAfter}', which is not an integer.");
            Assert.True(seconds >= 0, $"Retry-After was {seconds}.");
        }

        Assert.True(throttled > 0, "The burst was never throttled.");
        Assert.Equal(0, costAtFirstThrottle);

        // Enrollment state is untouched by a throttled burst: nothing was consumed, rotated or
        // burned on the way to a 429.
        Assert.Single(host.Store.Devices);
        Assert.All(host.Store.Enrollments, e => Assert.Null(e.RevokedAt));
    }

    [Fact]
    public async Task AThrottledSessionRequest_AlsoSkipsTheKdf()
    {
        var hasher = new CountingSecretHasher(new Argon2idSecretHasher());
        await using var host = await NorthTestHost.StartAsync(hasher);

        // A bogus bearer costs the same Argon2id evaluation as a real one, so `GET /v1/session`
        // carries the limiter too — not only the two routes that take a secret in the body.
        HttpResponseMessage? throttledResponse = null;
        for (var attempt = 0; attempt < AuthRateLimits.PermitsPerWindow * 2; attempt++)
        {
            var response = await host.SessionAsync(
                Credentials.Compose(Credentials.NewRecordId(), Credentials.NewSecret()));
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throttledResponse = response;
                break;
            }
        }

        Assert.NotNull(throttledResponse);

        var costBefore = hasher.Verifications;
        var again = await host.SessionAsync(
            Credentials.Compose(Credentials.NewRecordId(), Credentials.NewSecret()));
        Assert.Equal(HttpStatusCode.TooManyRequests, again.StatusCode);
        Assert.Equal(costBefore, hasher.Verifications);
    }

    // ── proxy trust ──────────────────────────────────────────────────────────
    //
    // Carried as a finding since round 2: the switch had no test at all, on either setting. The
    // failure it guards is asymmetric — off behind a proxy is one shared bucket, which is degraded
    // but bounded; ON without a proxy hands every caller the partition key, which is no limit at
    // all for anyone who reads the documentation.

    [Fact]
    public async Task WithTrustOff_AForwardedHeaderDoesNotCreateANewBudget()
    {
        await using var host = await NorthTestHost.StartAsync(trustForwardedHeaders: false);

        // Exhaust the budget while claiming to be one client...
        await ExhaustAsync(host, forwardedFor: "203.0.113.10");

        // ...then claim to be a different one. The header is ignored, so the budget is still spent.
        var response = await host.SessionAsync("a.b", forwardedFor: "203.0.113.99");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    [Fact]
    public async Task WithTrustOn_DistinctForwardedClientsGetIndependentBudgets()
    {
        await using var host = await NorthTestHost.StartAsync(trustForwardedHeaders: true);

        await ExhaustAsync(host, forwardedFor: "203.0.113.10");

        // A different client behind the same proxy must not inherit the throttle.
        var other = await host.SessionAsync("a.b", forwardedFor: "203.0.113.99");
        Assert.Equal(HttpStatusCode.Unauthorized, other.StatusCode);

        // And the throttled one stays throttled — a forged header must not reset a budget either.
        var throttled = await host.SessionAsync("a.b", forwardedFor: "203.0.113.10");
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
    }

    private static async Task ExhaustAsync(NorthTestHost host, string forwardedFor)
    {
        for (var attempt = 0; attempt < AuthRateLimits.PermitsPerWindow * 2; attempt++)
        {
            var response = await host.SessionAsync("a.b", forwardedFor);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return;
        }

        Assert.Fail("the budget was never exhausted");
    }

    /// <summary>
    /// The limit has to be above what the owner actually does, or the mitigation becomes the
    /// outage. An ordinary day is one enrollment and a token refresh every fifteen minutes; this
    /// walks the whole lifecycle — register, recover, mint, refresh, session, revoke, re-enroll —
    /// and expects every step to be admitted.
    /// </summary>
    [Fact]
    public async Task TheOrdinaryLifecycleStaysWellInsideTheLimit()
    {
        await using var host = await NorthTestHost.StartAsync();

        var code = await host.IssueEnrollmentCodeAsync();
        var device = await AuthLifecycleTests.Body<NorthTestHost.RegisterDeviceBody>(
            await host.RegisterAsync(code));

        // A lost response, recovered.
        var recovered = await AuthLifecycleTests.Body<NorthTestHost.RegisterDeviceBody>(
            await host.RegisterAsync(code));
        Assert.Equal(device.DeviceId, recovered.DeviceId);

        var first = await AuthLifecycleTests.Body<NorthTestHost.TokenBody>(
            await host.TokenAsync(recovered.DeviceId, recovered.DeviceSecret));

        // Four refreshes — an hour of use at the 15-minute token TTL.
        for (var refresh = 0; refresh < 4; refresh++)
        {
            Assert.Equal(HttpStatusCode.OK,
                (await host.TokenAsync(recovered.DeviceId, recovered.DeviceSecret)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await host.SessionAsync(first.AccessToken)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK,
            (await host.RevokeAsync(recovered.DeviceId, first.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await host.RegisterAsync(await host.IssueEnrollmentCodeAsync())).StatusCode);
    }
}
