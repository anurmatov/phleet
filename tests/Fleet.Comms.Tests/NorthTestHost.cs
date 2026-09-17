using System.Net.Http.Json;
using Fleet.Comms;
using Fleet.Comms.Auth;
using Fleet.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fleet.Comms.Tests;

/// <summary>
/// The north application, in process, over <see cref="TestServer"/>.
///
/// <para>It is built through <see cref="CommsApp.BuildNorthApp"/> — the same composition
/// <c>Program</c> runs — so these tests exercise the production registration graph rather than a
/// hand-wired approximation. Only the clock and the store instance are substituted, and the store
/// is the real <see cref="InMemoryAuthStore"/> rather than a mock, so the fail-closed paths run the
/// same code the request path does.</para>
/// </summary>
internal sealed class NorthTestHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private NorthTestHost(WebApplication app, InMemoryAuthStore store, TestTimeProvider time,
        AuthService auth, HttpClient client)
    {
        _app = app;
        Store = store;
        Time = time;
        Auth = auth;
        Client = client;
    }

    public InMemoryAuthStore Store { get; }
    public TestTimeProvider Time { get; }
    public AuthService Auth { get; }
    public HttpClient Client { get; }

    /// <summary>The running app's services, so a test can inspect the real registration graph.</summary>
    public IServiceProvider Services => _app.Services;

    public static async Task<NorthTestHost> StartAsync(ISecretHasher? hasher = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        var store = new InMemoryAuthStore();
        var time = new TestTimeProvider();

        builder.Services.AddSingleton<IAuthStore>(store);
        builder.Services.AddSingleton<TimeProvider>(time);
        if (hasher is not null)
            builder.Services.AddSingleton(hasher);

        var app = CommsApp.BuildNorthApp(builder);

        // The production composition registers concrete defaults; TryAdd semantics mean the
        // substitutions above win only because they were registered first. Assert that rather than
        // assume it — a silent fallback to the system clock would make every TTL test meaningless.
        await app.StartAsync();
        var resolvedTime = app.Services.GetRequiredService<TimeProvider>();
        if (!ReferenceEquals(resolvedTime, time))
            throw new InvalidOperationException("The test clock was not the resolved TimeProvider.");

        var client = app.GetTestClient();
        return new NorthTestHost(app, store, time,
            app.Services.GetRequiredService<AuthService>(), client);
    }

    /// <summary>Issue a code through the library path. There is no north route for this by design.</summary>
    public Task<string> IssueEnrollmentCodeAsync(string principalId = "p_owner") =>
        Auth.IssueEnrollmentCodeAsync(principalId);

    public Task<HttpResponseMessage> RegisterAsync(string enrollmentCode) =>
        Client.PostAsJsonAsync("/v1/auth/devices", new
        {
            protocol = ProtocolVersion.Current,
            enrollmentCode,
        });

    public Task<HttpResponseMessage> TokenAsync(string deviceId, string deviceSecret) =>
        Client.PostAsJsonAsync("/v1/auth/token", new
        {
            protocol = ProtocolVersion.Current,
            deviceId,
            deviceSecret,
        });

    public Task<HttpResponseMessage> SessionAsync(string? bearer)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/session");
        if (bearer is not null)
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearer}");
        return Client.SendAsync(request);
    }

    public Task<HttpResponseMessage> RevokeAsync(string deviceId, string? bearer)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/auth/devices/{deviceId}:revoke");
        if (bearer is not null)
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearer}");
        return Client.SendAsync(request);
    }

    /// <summary>Register a device and mint its first token — the ordinary starting state.</summary>
    public async Task<(string DeviceId, string DeviceSecret, string Token)> EnrolledDeviceAsync()
    {
        var code = await IssueEnrollmentCodeAsync();
        var registration = await RegisterAsync(code);
        var device = await registration.Content.ReadFromJsonAsync<RegisterDeviceBody>(
            FleetProtocolJson.Options);

        var token = await TokenAsync(device!.DeviceId, device.DeviceSecret);
        var minted = await token.Content.ReadFromJsonAsync<TokenBody>(FleetProtocolJson.Options);

        return (device.DeviceId, device.DeviceSecret, minted!.AccessToken);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    internal sealed record RegisterDeviceBody(string Protocol, string DeviceId, string DeviceSecret);

    internal sealed record TokenBody(string Protocol, string AccessToken, int ExpiresInSeconds);
}

/// <summary>
/// A clock the test moves by hand, with a wall reading and a monotonic counter that move
/// independently — because that is the only way to write the test that matters.
///
/// <para><see cref="Advance"/> moves both, as real time passing does. <see cref="SetBackwards"/>
/// moves <b>only the wall reading</b>, which is exactly what an NTP step or a restored snapshot
/// does to a host: the monotonic counter is, by definition, unaffected. A fake that moved both
/// together could not distinguish "time passed" from "someone changed the clock", and would
/// therefore pass whether or not the code under test handles a rollback at all.</para>
/// </summary>
internal sealed class TestTimeProvider(DateTimeOffset? start = null) : TimeProvider
{
    private DateTimeOffset _now = start ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private long _timestamp;

    public override DateTimeOffset GetUtcNow() => _now;

    public override long GetTimestamp() => _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan delta)
    {
        if (delta < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(delta), "Use SetBackwards for a clock jump.");
        _now += delta;
        _timestamp += delta.Ticks;
    }

    /// <summary>
    /// A backward wall-clock jump. The monotonic counter deliberately does not move — no clock
    /// change can rewind it, and a test that rewound it would be testing a machine that cannot
    /// exist.
    /// </summary>
    public void SetBackwards(TimeSpan delta) => _now -= delta;
}

/// <summary>
/// Wraps a real hasher and counts verifications, so the "every rejection costs the same" property
/// can be asserted as an exact call count rather than as a wall-clock measurement. A timing
/// assertion on a shared CI runner is a flake waiting to be muted; a count is not.
/// </summary>
internal sealed class CountingSecretHasher(ISecretHasher inner) : ISecretHasher
{
    private int _verifications;

    public int Verifications => Volatile.Read(ref _verifications);

    public string Hash(string secret) => inner.Hash(secret);

    public bool Verify(string secret, string encodedHash)
    {
        Interlocked.Increment(ref _verifications);
        return inner.Verify(secret, encodedHash);
    }

    public bool VerifyOrDummy(string secret, string? encodedHash)
    {
        Interlocked.Increment(ref _verifications);
        return inner.VerifyOrDummy(secret, encodedHash);
    }
}
