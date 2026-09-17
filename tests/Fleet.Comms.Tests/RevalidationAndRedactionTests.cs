using System.Reflection;
using Fleet.Comms.Auth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fleet.Comms.Tests;

/// <summary>
/// The 30-second revocation bound (§3.5, §9) and the redaction rules (§13).
///
/// <para>Neither is provable by reading the code, and the revocation bound in particular is the
/// number an operator is told to rely on — "eventually" would give them nothing to act on.</para>
/// </summary>
public class RevalidationAndRedactionTests
{
    // ── the 30-second bound ──────────────────────────────────────────────────

    /// <summary>
    /// The bound itself, as arithmetic. A timer-driven test of a 30-second interval would take 30
    /// seconds and would be muted the first time it flaked.
    /// </summary>
    [Fact]
    public void RevalidationIsDueAtThirtySecondsAndNotBefore()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), CredentialRevalidator.Interval);
        Assert.False(CredentialRevalidator.IsDue(TimeSpan.FromSeconds(29.999)));
        Assert.True(CredentialRevalidator.IsDue(TimeSpan.FromSeconds(30)));
        Assert.True(CredentialRevalidator.IsDue(TimeSpan.FromSeconds(31)));
    }

    /// <summary>
    /// The bound is on <b>elapsed</b> time, not on the difference between two wall-clock readings.
    ///
    /// <para>This is the case that breaks the naive version: the host's clock steps five minutes
    /// backwards, then thirty real seconds pass. A wall-clock subtraction says minus four and a
    /// half minutes and the next check never comes due — so a revoked device keeps its socket for
    /// as long as the skew lasts, on the one mechanism an operator is told they can rely on.</para>
    /// </summary>
    [Fact]
    public async Task ABackwardWallClockJumpDoesNotDelayTheNextRevalidation()
    {
        await using var host = await NorthTestHost.StartAsync();
        var revalidator = host.Services.GetRequiredService<CredentialRevalidator>();

        var openedAt = revalidator.Stamp();

        host.Time.SetBackwards(TimeSpan.FromMinutes(5));
        Assert.False(revalidator.IsDue(openedAt));

        host.Time.Advance(CredentialRevalidator.Interval);
        Assert.True(revalidator.IsDue(openedAt));
    }

    /// <summary>
    /// The behaviour the bound delivers: a connection opened with a valid token is rejected on its
    /// next scheduled re-check once the device is revoked. Together with the arithmetic above, this
    /// is the "within 30 seconds" promise.
    /// </summary>
    [Fact]
    public async Task ARevokedDevicesLiveConnectionIsRejectedOnItsNextRevalidation()
    {
        await using var host = await NorthTestHost.StartAsync();
        var (deviceId, _, token) = await host.EnrolledDeviceAsync();
        var revalidator = host.Services.GetRequiredService<CredentialRevalidator>();

        // Upgrade-time check: good.
        var openedAt = revalidator.Stamp();
        Assert.Equal(RevalidationOutcome.Valid, await revalidator.RevalidateAsync(token));

        await host.RevokeAsync(deviceId, token);

        // The connection is still open and its captured token has not changed — which is exactly
        // why trusting the upgrade-time value is not enough.
        host.Time.Advance(CredentialRevalidator.Interval);
        Assert.True(revalidator.IsDue(openedAt));
        Assert.Equal(RevalidationOutcome.Revoked, await revalidator.RevalidateAsync(token));
    }

    /// <summary>
    /// MUST NOT 18: an auth lookup that cannot complete is never a pass. On a live socket that
    /// means the connection closes rather than surviving on a check that did not happen.
    /// </summary>
    [Fact]
    public async Task RevalidationFailsClosedWhenTheStoreIsUnavailable()
    {
        await using var host = await NorthTestHost.StartAsync();
        var (_, _, token) = await host.EnrolledDeviceAsync();
        var revalidator = host.Services.GetRequiredService<CredentialRevalidator>();

        host.Store.FailEveryOperation = true;

        Assert.Equal(RevalidationOutcome.Revoked, await revalidator.RevalidateAsync(token));
    }

    /// <summary>
    /// Scope 6 forbids fabricating the Slice-4 stream or exposing a placeholder route. The
    /// component exists and is resolvable; no endpoint serves it.
    /// </summary>
    [Fact]
    public async Task TheRevalidatorIsRegisteredButNoStreamRouteExists()
    {
        await using var host = await NorthTestHost.StartAsync();

        Assert.NotNull(host.Services.GetRequiredService<CredentialRevalidator>());
        Assert.DoesNotContain(NorthEndpointsRoutes(), r => r.Contains("stream", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> NorthEndpointsRoutes() => Comms.Routes.NorthEndpoints.Routes;

    // ── redaction ────────────────────────────────────────────────────────────

    /// <summary>
    /// §13: no credential appears in any log line, and not a prefix of one either. Driven through
    /// the real service with a capturing logger, across every path that handles a credential —
    /// including the failures, which is where a "helpful" diagnostic would be added.
    /// </summary>
    [Fact]
    public async Task NoCredentialValueOrPrefixEverReachesALogLine()
    {
        var logger = new CapturingLogger<AuthService>();
        var store = new InMemoryAuthStore();
        var time = new TestTimeProvider();
        var auth = new AuthService(store, new Argon2idSecretHasher(), new MonotonicClock(time), logger);

        var code = await auth.IssueEnrollmentCodeAsync("p_owner");
        var registration = await auth.RegisterDeviceAsync(code);
        var device = registration.Value!;
        var minted = await auth.MintTokenAsync(device.DeviceId, device.DeviceSecret);
        var token = minted.Value!.AccessToken;

        var principal = (await auth.AuthenticateAsync(token)).Value!;

        // Every failure path too: these are where a diagnostic is most tempting.
        await auth.RegisterDeviceAsync(Credentials.Compose("nosuchid", Credentials.NewSecret()));
        await auth.MintTokenAsync(device.DeviceId, Credentials.NewSecret());
        await auth.AuthenticateAsync("garbage.value");
        await auth.RevokeSelfAsync(principal, "some-other-device");
        await auth.RevokeSelfAsync(principal, device.DeviceId);

        var log = string.Join("\n", logger.Lines);
        Assert.NotEmpty(logger.Lines);

        foreach (var secret in new[] { code, device.DeviceSecret, token })
        {
            Assert.DoesNotContain(secret, log, StringComparison.Ordinal);

            // Not a prefix either: §13 says the presence of a credential may be logged, never its
            // value and never part of it. Eight characters of a base64url secret is enough to
            // narrow a search and is exactly the "just for debugging" shape this forbids.
            Assert.DoesNotContain(secret[..8], log, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The same rule at the boundary: a secret must not survive in the stored record either. The
    /// server keeps a salted Argon2id hash and cannot reproduce what it issued.
    /// </summary>
    [Fact]
    public async Task NoRawCredentialIsEverStored()
    {
        await using var host = await NorthTestHost.StartAsync();
        var (_, secret, token) = await host.EnrolledDeviceAsync();

        var stored = string.Join("\n",
            host.Store.Enrollments.Select(e => e.CodeHash)
                .Concat(host.Store.Devices.Select(d => d.SecretHash))
                .Concat(host.Store.Tokens.Select(t => t.TokenHash)));

        Assert.DoesNotContain(secret, stored, StringComparison.Ordinal);
        Assert.DoesNotContain(token.Split('.')[1], stored, StringComparison.Ordinal);

        // What IS stored is an Argon2id encoding with a per-record salt, and no two records share
        // a salt — a shared salt would let one cracked record help with the next.
        var salts = host.Store.Devices.Select(d => d.SecretHash.Split('$')[4]).ToList();
        Assert.All(host.Store.Devices, d => Assert.StartsWith("$argon2id$v=19$", d.SecretHash, StringComparison.Ordinal));
        Assert.Equal(salts.Count, salts.Distinct(StringComparer.Ordinal).Count());
    }

    // ── structural boundary ──────────────────────────────────────────────────

    /// <summary>
    /// The agent runtime must not depend on the server-side auth implementation. If it ever did,
    /// the boundary would exist only by convention, and a change here could alter Telegram
    /// behaviour — which this slice must leave untouched.
    /// </summary>
    [Fact]
    public void FleetAgentDoesNotReferenceFleetComms()
    {
        var agent = typeof(Fleet.Agent.Services.TaskManager).Assembly;

        Assert.DoesNotContain(agent.GetReferencedAssemblies(),
            a => a.Name?.StartsWith("Fleet.Comms", StringComparison.Ordinal) == true);
    }

    /// <summary>The dependency runs the other way, and only onto the shared protocol contract.</summary>
    [Fact]
    public void FleetCommsDependsOnTheProtocolAndNotOnTheAgent()
    {
        var comms = typeof(AuthService).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToList();

        Assert.Contains("Fleet.Protocol", comms);
        Assert.DoesNotContain("Fleet.Agent", comms);
        Assert.DoesNotContain("Fleet.Telegram", comms);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Lines.Add(formatter(state, exception));
            if (exception is not null)
                Lines.Add(exception.ToString());
        }
    }
}
