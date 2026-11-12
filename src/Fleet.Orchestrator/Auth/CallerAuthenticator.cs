using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Fleet.Shared;

namespace Fleet.Orchestrator.Auth;

/// <summary>How a caller proved who it is.</summary>
public enum CallerKind
{
    /// <summary>No credential, or one that did not verify.</summary>
    None,

    /// <summary>The deployment's service bearer (<c>Orchestrator:AuthToken</c>): automation and internal services.</summary>
    Service,

    /// <summary>An enrolled app presenting an access token as a bearer.</summary>
    App,

    /// <summary>A signed-in browser presenting its session cookie.</summary>
    Browser,
}

/// <summary>Who is calling. Derived from the credential on the request and from nothing else.</summary>
/// <param name="SessionExpiresAt">
/// Set when a browser's session was extended by this request, so the cookie is sent again.
/// </param>
public sealed record CallerIdentity(
    CallerKind Kind,
    string? PrincipalId = null,
    string? DeviceId = null,
    string? CredentialId = null,
    DateTimeOffset? SessionExpiresAt = null)
{
    public static readonly CallerIdentity Anonymous = new(CallerKind.None);

    /// <summary>The service bearer acts for the owner: it is the deployment's own secret.</summary>
    public static readonly CallerIdentity ServiceCaller = new(CallerKind.Service, UserRecord.OwnerId);

    private const string ItemKey = "fleet.caller";

    public static CallerIdentity Of(HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out var value) && value is CallerIdentity caller
            ? caller
            : Anonymous;

    public void AttachTo(HttpContext context) => context.Items[ItemKey] = this;
}

/// <summary>Too many unverified credentials were presented at once. Answered as 429.</summary>
public sealed class AuthBusyException() : Exception("Too many sign-in checks at once.");

/// <summary>
/// Turns the credential on a request into a <see cref="CallerIdentity"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>One rule about which credential counts.</b> A request with an <c>Authorization: Bearer</c>
/// header is judged by that bearer alone, and its cookies are ignored. Only a request with no
/// bearer is judged by its session cookie. A cookie is sent by a browser whether or not the page
/// asked for it; a bearer is put there on purpose. Letting a failed bearer fall back to the cookie
/// would let any page that can make the browser send a request choose which identity it carries.
/// </para>
/// <para>
/// <b>A verified credential is remembered briefly.</b> Verification is one Argon2id evaluation, so
/// a dashboard that polls several routes would otherwise spend it on every request. A credential
/// that verified is remembered for <see cref="CacheLifetime"/> under a hash of its value, and
/// never past its own deadline. <see cref="Forget"/> empties the memory, and every revocation
/// calls it, so on this process a revoked device stops authenticating at once.
/// </para>
/// <para>
/// <b>Unverified credentials are budgeted.</b> A credential that is not in the memory costs a KDF
/// whether or not it is genuine. <see cref="MissesPerWindow"/> of those are allowed per
/// <see cref="MissWindow"/> for the whole process; past that the caller is told to come back
/// (<see cref="AuthBusyException"/>), which bounds what a stream of made-up tokens can cost.
/// </para>
/// </remarks>
public sealed class CallerAuthenticator : IDisposable
{
    /// <summary>The cookie a signed-in browser carries.</summary>
    public const string SessionCookie = SignIn.SessionCookie;

    public static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MissWindow = TimeSpan.FromSeconds(10);
    public const int MissesPerWindow = 40;

    private const int MaxRemembered = 512;

    private readonly AuthService _auth;
    private readonly TimeProvider _time;
    private readonly byte[]? _serviceTokenDigest;
    private readonly ConcurrentDictionary<string, Remembered> _remembered = new(StringComparer.Ordinal);
    private readonly FixedWindowRateLimiter _misses;

    private sealed record Remembered(AuthenticatedDevice Device, DateTimeOffset Until);

    public CallerAuthenticator(AuthService auth, IConfiguration configuration, TimeProvider time)
    {
        _auth = auth;
        _time = time;

        var serviceToken = configuration["Orchestrator:AuthToken"];
        _serviceTokenDigest = string.IsNullOrWhiteSpace(serviceToken) ? null : Digest(serviceToken);
        _misses = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = MissesPerWindow,
            Window = MissWindow,
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    }

    /// <summary>
    /// Whether the orchestrator gates anything. With no <c>Orchestrator:AuthToken</c> it does not:
    /// whoever can reach it is the operator, as before sign-in existed.
    /// </summary>
    public bool IsRequired => _serviceTokenDigest is not null;

    /// <summary>Whether <paramref name="presented"/> is the deployment's service bearer.</summary>
    public bool IsServiceToken(string? presented) =>
        _serviceTokenDigest is not null
        && !string.IsNullOrEmpty(presented)
        // Digests, so the comparison is fixed-time and the lengths compared are always equal.
        && CryptographicOperations.FixedTimeEquals(Digest(presented), _serviceTokenDigest);

    /// <summary>Resolve the caller from the two header values that can carry a credential.</summary>
    /// <exception cref="AuthStoreUnavailableException">The lookup could not complete. Never a pass.</exception>
    /// <exception cref="AuthBusyException">The budget for unverified credentials is spent.</exception>
    public async Task<CallerIdentity> ResolveAsync(
        string? authorizationHeader, string? cookieHeader, CancellationToken ct = default)
    {
        var bearer = BearerOf(authorizationHeader);
        if (bearer is not null)
        {
            if (IsServiceToken(bearer))
                return CallerIdentity.ServiceCaller;

            var device = await VerifyAsync(bearer, TokenKind.Access, ct);
            return device is null
                ? CallerIdentity.Anonymous
                : new CallerIdentity(CallerKind.App, device.PrincipalId, device.DeviceId, device.TokenId);
        }

        var session = CookieOf(cookieHeader, SessionCookie);
        if (session is not null)
        {
            var device = await VerifyAsync(session, TokenKind.Session, ct);
            return device is null
                ? CallerIdentity.Anonymous
                : new CallerIdentity(CallerKind.Browser, device.PrincipalId, device.DeviceId, device.TokenId,
                    device.Extended ? device.ExpiresAt : null);
        }

        return CallerIdentity.Anonymous;
    }

    /// <summary>Forget every remembered credential. Called by anything that revokes.</summary>
    public void Forget() => _remembered.Clear();

    private async Task<AuthenticatedDevice?> VerifyAsync(string presented, TokenKind kind, CancellationToken ct)
    {
        // The kind is part of the key: the same value remembered as a bearer must not answer for a cookie.
        var key = $"{(int)kind}:{Convert.ToHexString(Digest(presented))}";
        var now = _time.GetUtcNow();

        if (_remembered.TryGetValue(key, out var hit))
        {
            if (now < hit.Until)
                return hit.Device;

            _remembered.TryRemove(key, out _);
        }

        using (var lease = _misses.AttemptAcquire())
        {
            if (!lease.IsAcquired)
                throw new AuthBusyException();
        }

        var result = await _auth.AuthenticateAsync(presented, kind, ct);
        if (!result.Succeeded)
            return null;

        var device = result.Value!;
        if (_remembered.Count >= MaxRemembered)
            _remembered.Clear();

        var until = now + CacheLifetime;
        // An extension is reported once, by the request that caused it; a remembered copy must not
        // make every request in the next half minute send the cookie again.
        _remembered[key] = new Remembered(
            device with { Extended = false },
            until < device.ExpiresAt ? until : device.ExpiresAt);
        return device;
    }

    /// <summary>The token of an <c>Authorization: Bearer</c> header, or null.</summary>
    public static string? BearerOf(string? authorizationHeader)
    {
        const string scheme = "Bearer ";
        if (authorizationHeader is null
            || !authorizationHeader.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = authorizationHeader[scheme.Length..].Trim();
        return token.Length == 0 ? null : token;
    }

    /// <summary>The value of one cookie in a raw <c>Cookie</c> header, or null.</summary>
    public static string? CookieOf(string? cookieHeader, string name)
    {
        if (string.IsNullOrEmpty(cookieHeader))
            return null;

        foreach (var part in cookieHeader.Split(';'))
        {
            var pair = part.Trim();
            var equals = pair.IndexOf('=');
            if (equals <= 0)
                continue;

            if (string.Equals(pair[..equals], name, StringComparison.Ordinal))
            {
                var value = pair[(equals + 1)..].Trim();
                return value.Length == 0 ? null : value;
            }
        }

        return null;
    }

    private static byte[] Digest(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    public void Dispose() => _misses.Dispose();
}
