using Fleet.Comms.Auth;
using Fleet.Comms.Configuration;
using Fleet.Comms.Contracts;
using Fleet.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Fleet.Comms.Routes;

/// <summary>
/// The complete north route table for Slice 1 (docs/first-party-api.md §5, issue #292 scope 2).
///
/// <para><b>This is the whole surface a device can reach.</b> No south store endpoint is mapped
/// here, and none may be: <c>/events:append</c> on a device-reachable listener would let a client
/// write arbitrary events — including forged terminals — into the durable log. Separation is by
/// listener, so the guarantee is that this method is the only thing that ever runs on the north
/// app, and <c>NorthRouteTableTests</c> asserts the registered set is exactly these four.</para>
/// </summary>
public static class NorthEndpoints
{
    /// <summary>The routes this slice serves. Slices 2–4 add to this list, never to a south app.</summary>
    public static readonly IReadOnlyList<string> Routes =
    [
        "POST /v1/auth/devices",
        "POST /v1/auth/token",
        "POST /v1/auth/devices/{deviceId}:revoke",
        "GET /v1/session",
    ];

    public static IEndpointRouteBuilder MapNorthApi(this IEndpointRouteBuilder app)
    {
        // Every route here verifies a credential, and every verification costs one Argon2id
        // evaluation — including the ones that fail. So the limiter goes on all four, not only on
        // the two that take a secret in the body: a bogus bearer on `GET /v1/session` buys exactly
        // the same work as a bogus secret on `POST /v1/auth/token`.
        app.MapPost("/v1/auth/devices", RegisterDeviceAsync)
            .RequireRateLimiting(AuthRateLimits.PolicyName);
        app.MapPost("/v1/auth/token", MintTokenAsync)
            .RequireRateLimiting(AuthRateLimits.PolicyName);

        // `:revoke` is a literal suffix on the id segment, matching §5. Self-revoke only: the
        // lost-device path is operator-side and out of band, because an unauthenticated revoke
        // route would be a one-request denial of service against the owner's only device.
        app.MapPost("/v1/auth/devices/{deviceId}:revoke", RevokeDeviceAsync)
            .RequireRateLimiting(AuthRateLimits.PolicyName);

        app.MapGet("/v1/session", GetSessionAsync)
            .RequireRateLimiting(AuthRateLimits.PolicyName);
        return app;
    }

    private static async Task<IResult> RegisterDeviceAsync(
        HttpContext http, AuthService auth, CancellationToken ct)
    {
        var request = await ReadAsync<RegisterDeviceRequest>(http, ct);
        if (request is null)
            return Error(ProtocolErrorCode.UnsupportedKind, StatusCodes.Status400BadRequest);

        if (!ProtocolVersion.IsSupported(request.Protocol))
            return Error(ProtocolErrorCode.UnsupportedProtocol, StatusCodes.Status400BadRequest);

        var result = await auth.RegisterDeviceAsync(request.EnrollmentCode, ct);
        if (!result.Succeeded)
        {
            // device_limit is 409; every other failure here is an indistinguishable 401.
            return result.Error == ProtocolErrorCode.DeviceLimit
                ? Error(ProtocolErrorCode.DeviceLimit, StatusCodes.Status409Conflict)
                : Unauthorized();
        }

        var device = result.Value!;
        return Json(new RegisterDeviceResponse
        {
            DeviceId = device.DeviceId,
            DeviceSecret = device.DeviceSecret,
        }, StatusCodes.Status200OK);
    }

    private static async Task<IResult> MintTokenAsync(
        HttpContext http, AuthService auth, CancellationToken ct)
    {
        var request = await ReadAsync<TokenRequest>(http, ct);
        if (request is null)
            return Error(ProtocolErrorCode.UnsupportedKind, StatusCodes.Status400BadRequest);

        if (!ProtocolVersion.IsSupported(request.Protocol))
            return Error(ProtocolErrorCode.UnsupportedProtocol, StatusCodes.Status400BadRequest);

        var result = await auth.MintTokenAsync(request.DeviceId, request.DeviceSecret, ct);
        if (!result.Succeeded)
            return Unauthorized();

        var token = result.Value!;
        return Json(new TokenResponse
        {
            AccessToken = token.AccessToken,
            ExpiresInSeconds = token.ExpiresInSeconds,
        }, StatusCodes.Status200OK);
    }

    private static async Task<IResult> RevokeDeviceAsync(
        HttpContext http, string deviceId, AuthService auth, CancellationToken ct)
    {
        var caller = await AuthenticateAsync(http, auth, ct);
        if (caller is null)
            return Unauthorized();

        var result = await auth.RevokeSelfAsync(caller, deviceId, ct);
        return result.Succeeded
            ? Json(new RevokeDeviceResponse { Revoked = true }, StatusCodes.Status200OK)
            : Unauthorized();
    }

    private static async Task<IResult> GetSessionAsync(
        HttpContext http, AuthService auth, IOptions<CommsOptions> options, CancellationToken ct)
    {
        var caller = await AuthenticateAsync(http, auth, ct);
        if (caller is null)
            return Unauthorized();

        return Json(new SessionResponse
        {
            // Derived from the device record. Never read from the request.
            PrincipalId = caller.PrincipalId,
            AgentLabel = options.Value.AgentLabel,
            Limits = new SessionLimits
            {
                InboundTextBytes = ProtocolLimits.MaxInboundTextBytes,
                CatchUpLimitDefault = CommsLimits.CatchUpLimitDefault,
                CatchUpLimitMax = CommsLimits.CatchUpLimitMax,
                IdentifierMaxLength = CommsLimits.IdentifierMaxLength,
                OutboundBufferEvents = CommsLimits.OutboundBufferEvents,
            },
        }, StatusCodes.Status200OK);
    }

    /// <summary>
    /// Resolve the bearer token on the request.
    ///
    /// <para>Header only. A token in a query string, path segment or fragment is a token in an
    /// access log and in a crash report, and this boundary has no code path that reads one from
    /// there (§3.7, MUST NOT 2).</para>
    /// </summary>
    internal static async Task<AuthenticatedPrincipal?> AuthenticateAsync(
        HttpContext http, AuthService auth, CancellationToken ct)
    {
        var header = http.Request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        if (!header.StartsWith(scheme, StringComparison.Ordinal))
            return null;

        var result = await auth.AuthenticateAsync(header[scheme.Length..], ct);
        return result.Succeeded ? result.Value : null;
    }

    /// <summary>
    /// Read a request body, or return null so the caller answers `400 unsupported_kind`.
    ///
    /// <para><b>The content-type check has to be here rather than left to
    /// <c>ReadFromJsonAsync</c>.</b> That method throws <see cref="InvalidOperationException"/> —
    /// not <see cref="System.Text.Json.JsonException"/> — for an unsupported or absent media type,
    /// so a `text/plain` body or a bodiless POST fell through to the edge handler and came back as
    /// `500 internal`. §5.2 tells a client that 500 means "the server is broken, report this",
    /// which is precisely the wrong instruction for a request the client got wrong.</para>
    /// </summary>
    internal static Task<T?> ReadBodyAsync<T>(HttpContext http, CancellationToken ct) where T : class =>
        ReadAsync<T>(http, ct);

    private static async Task<T?> ReadAsync<T>(HttpContext http, CancellationToken ct) where T : class
    {
        if (!http.Request.HasJsonContentType())
            return null;

        if (!TryNormalizeCharset(http.Request))
            return null;

        // An explicitly empty body would deserialize to null anyway; short-circuiting keeps that
        // answer a client error rather than depending on which exception the reader picks.
        if (http.Request.ContentLength == 0)
            return null;

        try
        {
            return await http.Request.ReadFromJsonAsync<T>(FleetProtocolJson.Options, ct);
        }
        catch (System.Text.Json.JsonException)
        {
            // A malformed body is a protocol error, never a 500 and never an unhandled exception
            // whose message could reach the client.
            return null;
        }
    }

    /// <summary>
    /// Whether the declared charset is one this boundary reads.
    ///
    /// <para><c>HasJsonContentType()</c> checks the media type and <b>ignores the charset</b>, so
    /// `application/json; charset=not-a-real-encoding` passed it and then failed inside the JSON
    /// reader with a non-<c>JsonException</c> — reaching the client as `500 internal`, which tells
    /// them the server is broken over a parameter they chose.</para>
    ///
    /// <para>Absent or UTF-8 only. RFC 8259 §8.1 requires UTF-8 for JSON exchanged between
    /// systems, so anything else is a client error rather than a capability worth carrying: one
    /// accepted encoding means one decoder and no argument about which byte order mark wins.</para>
    ///
    /// <para><b>The value is unquoted, and then the header is rewritten.</b> RFC 9110 §5.6.6 lets a
    /// parameter value be either a token or a quoted-string, so <c>charset="utf-8"</c> is exactly as
    /// legal as <c>charset=utf-8</c> and clients do send it. Two separate things had to change for
    /// that to work:</para>
    ///
    /// <list type="number">
    ///   <item><description><c>MediaTypeHeaderValue.Charset</c> returns the raw segment with the
    ///     quotes still attached, so comparing it directly rejected the quoted form — a validation
    ///     meant to catch a client mistake inventing one instead.</description></item>
    ///   <item><description><c>ReadFromJsonAsync</c> does not unquote it either: it resolves the
    ///     charset to an <see cref="System.Text.Encoding"/> and throws for <c>"utf-8"</c> with the
    ///     quotes, which is a non-<c>JsonException</c> and so came back as `500`. Accepting the
    ///     header at the gate is therefore not enough on its own — the reader has to be handed a
    ///     value it can parse.</description></item>
    /// </list>
    ///
    /// <para>So an accepted charset is rewritten to its canonical unquoted form before the read.
    /// The media type is preserved rather than hard-coded, because <c>HasJsonContentType()</c> also
    /// admits the <c>+json</c> structured suffix.</para>
    ///
    /// <para>Rejecting a legal header is the worse of the two failures available here: an
    /// unsupported charset is something the client chose and can change, but a client sending a
    /// spelling the RFC permits has no way to discover which one this server wanted.</para>
    /// </summary>
    private static bool TryNormalizeCharset(HttpRequest request)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var parsed))
            return false;

        var charset = HeaderUtilities.RemoveQuotes(parsed.Charset);
        if (!StringSegment.IsNullOrEmpty(charset)
            && !charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        request.ContentType = new MediaTypeHeaderValue(parsed.MediaType) { Charset = "utf-8" }
            .ToString();
        return true;
    }

    private static IResult Unauthorized() =>
        Error(ProtocolErrorCode.Unauthorized, StatusCodes.Status401Unauthorized);

    private static IResult Error(ProtocolErrorCode code, int status) =>
        Json(ErrorResponse.For(code), status);

    private static IResult Json<T>(T body, int status) =>
        Results.Json(body, FleetProtocolJson.Options, statusCode: status);
}
