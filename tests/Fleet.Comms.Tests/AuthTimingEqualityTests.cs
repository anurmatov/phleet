using System.Diagnostics;
using System.Net;
using Fleet.Comms.Auth;

namespace Fleet.Comms.Tests;

/// <summary>
/// §12 promises that "all auth failures indistinguishable, so no oracle to grind against".
///
/// <para>Byte-identical bodies deliver half of that. The other half is elapsed time: a
/// deliberately slow KDF that runs only when a record was found turns every rejection into an
/// answer to "does this id exist?" — a `deviceId`, an enrollment id or a token id — which is
/// exactly the question the fixed bodies are there to refuse.</para>
///
/// <para>These assert it as a <b>call count</b> rather than a stopwatch reading. A timing
/// assertion across six code paths on a shared runner is a flake, and a flaky security test gets
/// muted rather than fixed. The one stopwatch below is a single coarse floor, three orders of
/// magnitude away from the boundary it checks.</para>
/// </summary>
public class AuthTimingEqualityTests
{
    [Fact]
    public async Task EveryCredentialRejection_SpendsExactlyOneKdfEvaluation()
    {
        var hasher = new CountingSecretHasher(new Argon2idSecretHasher());
        await using var host = await NorthTestHost.StartAsync(hasher);
        var (deviceId, secret, token) = await host.EnrolledDeviceAsync();

        // Each case is "one request, then how many verifications did that cost?". The comparison
        // that matters is between rows, not the absolute number.
        Assert.Equal(1, await CostOf(hasher, () => host.TokenAsync("nosuchdevice", secret)));
        Assert.Equal(1, await CostOf(hasher, () => host.TokenAsync(deviceId, Credentials.NewSecret())));
        Assert.Equal(1, await CostOf(hasher,
            () => host.RegisterAsync(Credentials.Compose("nosuchid", Credentials.NewSecret()))));
        Assert.Equal(1, await CostOf(hasher,
            () => host.SessionAsync(Credentials.Compose("nosuchtoken", Credentials.NewSecret()))));

        // A revoked device and a revoked token are lookups that DID find something, and are the
        // pair most easily broken by moving a cheap `IsActive` test above the verify.
        await host.RevokeAsync(deviceId, token);
        Assert.Equal(1, await CostOf(hasher, () => host.SessionAsync(token)));
        Assert.Equal(1, await CostOf(hasher, () => host.TokenAsync(deviceId, secret)));
    }

    /// <summary>
    /// The count above proves the call is made; this proves the call does the work. A short-circuit
    /// returning false without deriving anything would still be counted, and would come back in
    /// microseconds — 19 MiB of Argon2id cannot.
    /// </summary>
    [Fact]
    public void VerifyingAgainstNoRecord_ActuallyDerives()
    {
        var hasher = new Argon2idSecretHasher();
        var secret = Credentials.NewSecret();

        var stopwatch = Stopwatch.StartNew();
        var result = hasher.VerifyOrDummy(secret, encodedHash: null);
        stopwatch.Stop();

        Assert.False(result);

        // Deliberately far below the real cost (tens of milliseconds) and far above a short
        // circuit (microseconds), so the assertion has no opinion about how fast the machine is.
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(5),
            $"A no-record verification returned in {stopwatch.Elapsed.TotalMilliseconds:F3} ms, " +
            "which is too fast to have derived anything.");
    }

    [Fact]
    public void VerifyingAgainstNoRecord_IsAlwaysFalse_EvenForAWellFormedSecret()
    {
        var hasher = new Argon2idSecretHasher();
        var secret = Credentials.NewSecret();

        Assert.True(hasher.VerifyOrDummy(secret, hasher.Hash(secret)));
        Assert.False(hasher.VerifyOrDummy(secret, null));
    }

    /// <summary>
    /// The deliberate exception, stated so it is not mistaken for an oversight: a value that does
    /// not parse never reaches a lookup, so it cannot leak whether a record exists. Spending a KDF
    /// on unparseable input would only let an unauthenticated caller buy server work.
    /// </summary>
    [Fact]
    public async Task AMalformedCredential_IsRejectedWithoutSpendingTheKdf()
    {
        var hasher = new CountingSecretHasher(new Argon2idSecretHasher());
        await using var host = await NorthTestHost.StartAsync(hasher);

        var cost = await CostOf(hasher, () => host.SessionAsync("not-a-credential"));

        Assert.Equal(0, cost);
    }

    private static async Task<int> CostOf(
        CountingSecretHasher hasher, Func<Task<HttpResponseMessage>> request)
    {
        var before = hasher.Verifications;
        var response = await request();
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        return hasher.Verifications - before;
    }
}
