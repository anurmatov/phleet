using Fleet.Comms;
using System.Collections.Concurrent;
using Fleet.Comms.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fleet.Comms.Tests;

/// <summary>
/// The north application, in process, over <see cref="TestServer"/>.
///
/// <para>It is built through <see cref="CommsApp.BuildNorthApp"/> — the same composition
/// <c>Program</c> runs — so these tests exercise the production registration graph rather than a
/// hand-wired approximation. Only the clock, the conversation store and the sign-in check are
/// substituted. The sign-in check is <see cref="FakeSignIn"/>: the orchestrator owns sign-in
/// (#530), and what these tests ask is what this service does with each answer it can get.
/// <see cref="OrchestratorSignInCheckTests"/> covers the real client.</para>
/// </summary>
internal sealed class NorthTestHost : IAsyncDisposable
{
    /// <summary>The chat-enabled agent every test conversation is with unless it says otherwise.</summary>
    public const string ChatAgent = "example-agent";

    private readonly WebApplication _app;

    private NorthTestHost(WebApplication app, FakeSignIn signIn, TestTimeProvider time, HttpClient client)
    {
        _app = app;
        SignIn = signIn;
        Time = time;
        Client = client;
    }

    public FakeSignIn SignIn { get; }
    public TestTimeProvider Time { get; }
    public HttpClient Client { get; }

    /// <summary>The running app's services, so a test can inspect the real registration graph.</summary>
    public IServiceProvider Services => _app.Services;

    /// <summary>
    /// A WebSocket client against the same in-process server, for the stream tests.
    /// </summary>
    /// <remarks>
    /// <see cref="TestServer"/> speaks the real upgrade handshake, so the frames a test reads here
    /// are the frames the endpoint wrote — not a shortcut around the socket.
    /// </remarks>
    public WebSocketClient WebSocketClient() => _app.GetTestServer().CreateWebSocketClient();

    public static async Task<NorthTestHost> StartAsync(
        bool? trustForwardedHeaders = null,
        Fleet.Conversations.Contracts.IConversationStore? conversations = null,
        string? attachmentRoot = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        if (trustForwardedHeaders is not null)
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Comms:TrustForwardedHeaders"] = trustForwardedHeaders.Value ? "true" : "false",
            });
        }

        // Enabling the feature is a CONFIGURATION fact, not a registration one: the composition
        // decides from `ConversationConnectionString` alone, so a test that registered a store
        // without setting it would prove nothing about how a deployment turns this on.
        //
        // The substitute is registered FIRST, so TryAddSingleton keeps it and no connection is ever
        // attempted — the value below is never dialled.
        if (conversations is not null)
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Comms:ConversationConnectionString"] = "Server=unused-by-this-test;",
            });

            builder.Services.AddSingleton(conversations);
        }

        // Attachments are a SEPARATE configuration fact from conversations (#308), so a test that
        // wants the three attachment routes has to set the root — exactly as a deployment does.
        // Without it the routes are not mapped and every attachment path is a router 404.
        if (attachmentRoot is not null)
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Comms:AttachmentRootPath"] = attachmentRoot,
            });
        }

        var signIn = new FakeSignIn();
        var time = new TestTimeProvider();

        builder.Services.AddSingleton<ISignInCheck>(signIn);
        builder.Services.AddSingleton<TimeProvider>(time);

        var app = CommsApp.BuildNorthApp(builder);

        // The production composition registers concrete defaults; TryAdd semantics mean the
        // substitutions above win only because they were registered first. Assert that rather than
        // assume it — a silent fallback would make every test here ask a real orchestrator.
        await app.StartAsync();
        if (!ReferenceEquals(app.Services.GetRequiredService<TimeProvider>(), time))
            throw new InvalidOperationException("The test clock was not the resolved TimeProvider.");

        if (conversations is not null
            && !ReferenceEquals(app.Services.GetRequiredService<ISignInCheck>(), signIn))
            throw new InvalidOperationException("The fake sign-in check was not the resolved ISignInCheck.");

        return new NorthTestHost(app, signIn, time, app.GetTestClient());
    }

    public Task<HttpResponseMessage> SessionAsync(string? bearer, string? forwardedFor = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/session");
        if (bearer is not null)
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearer}");
        if (forwardedFor is not null)
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
        return Client.SendAsync(request);
    }

    /// <summary>
    /// A signed-in app — the ordinary starting state. The secret is not this service's business any
    /// more; it is returned empty so the tuple shape older tests use still reads.
    /// </summary>
    public Task<(string DeviceId, string DeviceSecret, string Token)> EnrolledDeviceAsync(
        string principalId = FakeSignIn.Owner)
    {
        var (deviceId, token) = SignIn.AddApp(principalId);
        return Task.FromResult((deviceId, string.Empty, token));
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>
/// The orchestrator's answers, held in memory: which bearer and which session cookie name whom, and
/// which agents accept chat. Records every question it is asked.
/// </summary>
internal sealed class FakeSignIn : ISignInCheck
{
    public const string Owner = "p_owner";

    private readonly ConcurrentDictionary<string, AuthenticatedPrincipal> _bearers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AuthenticatedPrincipal> _cookies = new(StringComparer.Ordinal);
    private int _next;

    /// <summary>The agents that accept chat.</summary>
    public ConcurrentDictionary<string, bool> ChatAgents { get; } =
        new(StringComparer.Ordinal) { [NorthTestHost.ChatAgent] = true };

    /// <summary>When set, every question gets this answer instead: an outage, or a throttle.</summary>
    public SignInAnswer? Override { get; set; }

    /// <summary>Every question, in order.</summary>
    public ConcurrentQueue<(PresentedCredential Credential, string? Agent, bool Fresh)> Asked { get; } = new();

    /// <summary>An enrolled app: a device id and the bearer it presents.</summary>
    public (string DeviceId, string Token) AddApp(string principalId = Owner)
    {
        var n = Interlocked.Increment(ref _next);
        var device = $"d_app_{n:D4}";
        var token = $"access-token-{n:D4}-{Guid.NewGuid():N}";
        _bearers[token] = new AuthenticatedPrincipal(principalId, device, $"t_{n:D4}", IsBrowser: false);
        return (device, token);
    }

    /// <summary>A signed-in browser: the value of its session cookie.</summary>
    public string AddBrowser(string principalId = Owner)
    {
        var n = Interlocked.Increment(ref _next);
        var cookie = $"session-{n:D4}-{Guid.NewGuid():N}";
        _cookies[cookie] = new AuthenticatedPrincipal(principalId, $"d_browser_{n:D4}", $"s_{n:D4}", IsBrowser: true);
        return cookie;
    }

    /// <summary>Revoke a bearer or a session cookie.</summary>
    public void Revoke(string tokenOrCookie)
    {
        _bearers.TryRemove(tokenOrCookie, out _);
        _cookies.TryRemove(tokenOrCookie, out _);
    }

    public Task<SignInAnswer> CheckAsync(
        PresentedCredential credential, string? agent, bool fresh, CancellationToken ct)
    {
        Asked.Enqueue((credential, agent, fresh));

        if (Override is { } forced) return Task.FromResult(forced);

        AuthenticatedPrincipal? caller = null;
        var authorization = credential.Authorization;

        if (authorization is not null)
        {
            // As the orchestrator does: an Authorization header that is not a known bearer refuses,
            // whatever cookie came with it.
            if (authorization.StartsWith("Bearer ", StringComparison.Ordinal))
                _bearers.TryGetValue(authorization["Bearer ".Length..], out caller);
        }
        else if (credential.SessionCookie is { } cookie)
        {
            _cookies.TryGetValue(cookie, out caller);
        }

        if (caller is null) return Task.FromResult(SignInAnswer.Refused);

        bool? chat = agent is null ? null : ChatAgents.TryGetValue(agent, out var on) && on;
        return Task.FromResult(new SignInAnswer(SignInOutcome.Signed, caller, chat));
    }
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

    /// <summary>
    /// A <b>forward</b> wall-clock jump, and not the same thing as <see cref="Advance"/>.
    ///
    /// <para><c>Advance</c> is time passing: both readings move. This is the host being corrected
    /// forward — an NTP step, a resumed VM — so only the wall reading moves. The distinction is not
    /// pedantry: a forward correction followed by a rollback is the sequence that froze effective
    /// time, and a test written with <c>Advance</c> cannot express it.</para>
    /// </summary>
    public void SetForward(TimeSpan delta) => _now += delta;
}
