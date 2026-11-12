using Fleet.Comms.Auth;
using Fleet.Comms.Configuration;
using Fleet.Comms.Contracts;
using Fleet.Protocol;
using Fleet.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Fleet.Comms.Routes;

/// <summary>
/// The session route and the authentication every north route shares (docs/first-party-api.md §5,
/// #530 D7).
///
/// <para><b>Sign-in is not here.</b> The orchestrator owns users, devices and credentials; this
/// service asks it who a caller is on every request (<see cref="ISignInCheck"/>), and keeps no
/// credential of its own. The north surface is this route plus <see cref="ConversationEndpoints"/>,
/// and no south store endpoint is mapped on it: <c>/events:append</c> on a device-reachable listener
/// would let a client write arbitrary events — including forged terminals — into the durable log.</para>
/// </summary>
public static class NorthEndpoints
{
    /// <summary>The routes this class serves.</summary>
    public static readonly IReadOnlyList<string> Routes =
    [
        "GET /v1/session",
    ];

    public static IEndpointRouteBuilder MapNorthApi(this IEndpointRouteBuilder app)
    {
        // The conversation limiter, which partitions on the credential: behind the dashboard's proxy
        // every caller arrives from one address.
        app.MapGet("/v1/session", GetSessionAsync)
            .RequireRateLimiting(ConversationRateLimits.PolicyName);
        return app;
    }

    private static async Task<IResult> GetSessionAsync(
        HttpContext http, ISignInCheck signIn, CancellationToken ct)
    {
        var caller = await AuthenticateAsync(http, signIn, ct);
        if (caller is null)
            return Unauthorized();

        return Json(new SessionResponse
        {
            // From the credential, through the orchestrator. Never read from the request.
            PrincipalId = caller.PrincipalId,
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
    /// Who the request's credential names, or null when it names nobody.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The credential is the Authorization header or the browser's session cookie, and nothing
    /// else: a token in a query string, path segment or fragment is a token in an access log and in
    /// a crash report, and this boundary has no code path that reads one from there (§3.7, #530
    /// MUST NOT 6).
    /// </para>
    /// <para>
    /// A browser's write must also carry <see cref="SignIn.RequestHeader"/> (#530 D6), and one that
    /// does not is refused like a missing credential. With the cookie SameSite=Strict, that closes
    /// cross-site request forgery: another site's page cannot add the header.
    /// </para>
    /// <para>
    /// An answer that could not be had throws <see cref="SignInUnavailableException"/> or
    /// <see cref="SignInBusyException"/>, which the north edge turns into 503 or 429. It is never a
    /// pass (#530 MUST NOT 3).
    /// </para>
    /// </remarks>
    internal static async Task<AuthenticatedPrincipal?> AuthenticateAsync(
        HttpContext http, ISignInCheck signIn, CancellationToken ct) =>
        (await CheckAsync(http, signIn, agent: null, ct))?.Caller;

    /// <summary>
    /// <see cref="AuthenticateAsync"/>, also asking whether <paramref name="agent"/> accepts chat.
    /// Null when the credential names nobody.
    /// </summary>
    internal static async Task<SignInAnswer?> CheckAsync(
        HttpContext http, ISignInCheck signIn, string? agent, CancellationToken ct)
    {
        // Nothing presented costs no question.
        if (PresentedCredential.Of(http.Request) is not { } credential) return null;

        var answer = await signIn.CheckAsync(credential, agent, fresh: false, ct);

        switch (answer.Outcome)
        {
            case SignInOutcome.Unavailable: throw new SignInUnavailableException();
            case SignInOutcome.Busy: throw new SignInBusyException();
            case SignInOutcome.Signed when answer.Caller is not null: break;
            default: return null;
        }

        if (SignIn.IsBrowserWriteWithoutHeader(
                http.Request.Method, answer.Caller.IsBrowser,
                http.Request.Headers.ContainsKey(SignIn.RequestHeader)))
        {
            return null;
        }

        return answer;
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
