using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fleet.Conversations.Journal;
using Fleet.Shared;

namespace Fleet.Comms.Auth;

/// <summary>Who a request's credential names, as the orchestrator answered it (#530 D7).</summary>
/// <param name="IsBrowser">
/// The credential is a browser's session cookie. Such a caller's writes must also carry
/// <see cref="SignIn.RequestHeader"/>.
/// </param>
public sealed record AuthenticatedPrincipal(
    string PrincipalId, string DeviceId, string CredentialId, bool IsBrowser);

public enum SignInOutcome
{
    /// <summary>The credential names a signed-in device or browser.</summary>
    Signed,

    /// <summary>Absent, wrong, expired or revoked. One answer for all four.</summary>
    Refused,

    /// <summary>The question could not be answered. Never a pass (MUST NOT 3).</summary>
    Unavailable,

    /// <summary>The orchestrator is throttling sign-in.</summary>
    Busy,
}

/// <param name="AgentChatEnabled">
/// Whether the agent named in the question accepts chat; null when no agent was named.
/// </param>
public sealed record SignInAnswer(
    SignInOutcome Outcome, AuthenticatedPrincipal? Caller = null, bool? AgentChatEnabled = null)
{
    public static readonly SignInAnswer Refused = new(SignInOutcome.Refused);
    public static readonly SignInAnswer Unavailable = new(SignInOutcome.Unavailable);
    public static readonly SignInAnswer Busy = new(SignInOutcome.Busy);
}

/// <summary>
/// The credential a request presents, exactly as the client sent it: the Authorization header, or
/// the browser's session cookie. Never read from a query string, a path or a body (MUST NOT 6).
/// </summary>
public sealed record PresentedCredential(string? Authorization, string? SessionCookie)
{
    /// <summary>What <paramref name="request"/> presents, or null when it presents nothing.</summary>
    public static PresentedCredential? Of(HttpRequest request)
    {
        var authorization = request.Headers.Authorization.ToString();
        var cookie = request.Cookies[SignIn.SessionCookie];

        if (string.IsNullOrEmpty(authorization) && string.IsNullOrEmpty(cookie)) return null;

        return new PresentedCredential(
            string.IsNullOrEmpty(authorization) ? null : authorization,
            string.IsNullOrEmpty(cookie) ? null : cookie);
    }

    /// <summary>
    /// A digest of the credential, for a cache key or a rate-limiter partition. Never the
    /// credential itself: either one reaches memory dumps, metrics and diagnostics.
    /// </summary>
    public string Digest() => Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes($"{Authorization}\n{SessionCookie}")));
}

/// <summary>Asks who a caller is. The orchestrator owns sign-in; this service keeps no credential store.</summary>
public interface ISignInCheck
{
    /// <param name="agent">
    /// The agent the caller is about to chat with, when there is one; the answer then says whether
    /// it accepts chat.
    /// </param>
    /// <param name="fresh">
    /// Skip the short memory. An open stream asks this way on its ping tick, so revocation reaches
    /// it within one tick.
    /// </param>
    Task<SignInAnswer> CheckAsync(
        PresentedCredential credential, string? agent, bool fresh, CancellationToken ct);
}

/// <summary>Raised for <see cref="SignInOutcome.Unavailable"/>; the north edge answers 503.</summary>
public sealed class SignInUnavailableException() : Exception("sign-in could not be checked");

/// <summary>Raised for <see cref="SignInOutcome.Busy"/>; the north edge answers 429.</summary>
public sealed class SignInBusyException() : Exception("sign-in is busy");

/// <summary>
/// <see cref="ISignInCheck"/> over the orchestrator's <c>POST /internal/auth/check</c> (#530 D7).
/// </summary>
/// <remarks>
/// <para>
/// The call carries a <c>cj1.auth-check.fleet-comms</c> token under the key the two services share
/// (<c>FLEET_COMMS_JOURNAL_KEY</c>). It forwards the caller's Authorization header and session
/// cookie and nothing else.
/// </para>
/// <para>
/// A definite answer — signed or refused — is remembered for at most <see cref="CacheLifetime"/>.
/// Anything else is never remembered and is never a pass: a timeout, a refused connection, a 5xx,
/// a body that does not parse.
/// </para>
/// </remarks>
public sealed class OrchestratorSignInCheck : ISignInCheck, IDisposable
{
    /// <summary>The longest a remembered answer is believed.</summary>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(10);

    /// <summary>The longest one question may take.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Remembered answers kept before the memory is swept.</summary>
    public const int MaxRemembered = 4096;

    /// <summary>The largest answer read. The real one is a few hundred bytes.</summary>
    private const int MaxResponseBytes = 4096;

    private readonly HttpClient _http;
    private readonly Uri? _checkUri;
    private readonly string _token;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, (SignInAnswer Answer, long At)> _remembered = new(StringComparer.Ordinal);

    public OrchestratorSignInCheck(
        HttpClient http, string orchestratorUrl, byte[] key, TimeProvider time, ILogger<OrchestratorSignInCheck> logger)
    {
        _http = http;
        _time = time;
        _logger = logger;
        _token = JournalTokens.Mint(key, SignIn.CheckTokenPurpose, SignIn.CheckTokenSubject);

        // Validated at startup (CommsOptions.ValidateConversations); an unusable value here is
        // answered as unavailable on every request rather than as a pass.
        if (Uri.TryCreate(orchestratorUrl, UriKind.Absolute, out var origin) && origin.Scheme is "http" or "https")
            _checkUri = new Uri(origin, SignIn.CheckPath);
    }

    public void Dispose() => _http.Dispose();

    public async Task<SignInAnswer> CheckAsync(
        PresentedCredential credential, string? agent, bool fresh, CancellationToken ct)
    {
        var key = $"{credential.Digest()}\n{agent}";

        if (!fresh && _remembered.TryGetValue(key, out var remembered)
            && _time.GetElapsedTime(remembered.At) < CacheLifetime)
        {
            return remembered.Answer;
        }

        var answer = await AskAsync(credential, agent, ct);

        if (answer.Outcome is SignInOutcome.Signed or SignInOutcome.Refused)
        {
            if (_remembered.Count >= MaxRemembered) Sweep();
            _remembered[key] = (answer, _time.GetTimestamp());
        }
        else
        {
            // A question that could not be answered also forgets the old answer, so the next one
            // asks again rather than trusting what was true before the outage.
            _remembered.TryRemove(key, out _);
        }

        return answer;
    }

    private void Sweep()
    {
        foreach (var (key, value) in _remembered)
        {
            if (_time.GetElapsedTime(value.At) >= CacheLifetime) _remembered.TryRemove(key, out _);
        }

        // Every entry still fresh: a flood of distinct credentials. Forgetting costs one question each.
        if (_remembered.Count >= MaxRemembered) _remembered.Clear();
    }

    private async Task<SignInAnswer> AskAsync(PresentedCredential credential, string? agent, CancellationToken ct)
    {
        if (_checkUri is null) return SignInAnswer.Unavailable;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(RequestTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _checkUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            request.Content = JsonContent.Create(new
            {
                authorization = credential.Authorization,
                cookie = credential.SessionCookie is null ? null : $"{SignIn.SessionCookie}={credential.SessionCookie}",
                agent,
            });

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);

            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    break;
                case HttpStatusCode.Unauthorized:
                    return SignInAnswer.Refused;
                case HttpStatusCode.TooManyRequests:
                    return SignInAnswer.Busy;
                case HttpStatusCode.Forbidden:
                    // The orchestrator did not accept THIS service's token: the two services hold
                    // different keys. Every caller would be refused, so it is said as an outage.
                    _logger.LogError(
                        "the orchestrator refused this service's sign-in check token: "
                        + "FLEET_COMMS_JOURNAL_KEY differs between fleet-comms and fleet-orchestrator");
                    return SignInAnswer.Unavailable;
                default:
                    _logger.LogWarning("sign-in check answered {Status}", (int)response.StatusCode);
                    return SignInAnswer.Unavailable;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            var buffer = new byte[MaxResponseBytes + 1];
            var count = 0;
            while (count < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(count), deadline.Token);
                if (read == 0) break;
                count += read;
            }

            if (count > MaxResponseBytes) return SignInAnswer.Unavailable;

            return Parse(buffer.AsSpan(0, count));
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException
                                      or JsonException or InvalidOperationException)
        {
            if (ct.IsCancellationRequested) throw;

            // Type only: a transport message can carry a host name.
            _logger.LogWarning("sign-in check failed: {Error}", e.GetType().Name);
            return SignInAnswer.Unavailable;
        }
    }

    private static SignInAnswer Parse(ReadOnlySpan<byte> body)
    {
        var reader = new Utf8JsonReader(body);
        using var json = JsonDocument.ParseValue(ref reader);
        var root = json.RootElement;

        var principal = StringOf(root, "principalId");
        var device = StringOf(root, "deviceId");
        var credential = StringOf(root, "credentialId");
        var kind = StringOf(root, "kind");

        if (principal is null || device is null || credential is null || kind is not ("app" or "browser"))
            return SignInAnswer.Unavailable;

        bool? chat = root.TryGetProperty("agentChatEnabled", out var value) ? value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        } : null;

        return new SignInAnswer(
            SignInOutcome.Signed,
            new AuthenticatedPrincipal(principal, device, credential, IsBrowser: kind == "browser"),
            chat);
    }

    private static string? StringOf(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } text
            ? text
            : null;
}
