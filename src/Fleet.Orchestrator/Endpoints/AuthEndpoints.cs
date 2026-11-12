using Fleet.Orchestrator.Auth;
using Fleet.Orchestrator.Services;
using Fleet.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Endpoints;

/// <summary>
/// <c>/api/auth</c>: how a browser signs in, how an app is enrolled, and how devices are listed
/// and revoked (docs/sign-in.md).
/// </summary>
/// <remarks>
/// <para>
/// Three routes need no credential, because they are how one is obtained: signing a browser in,
/// registering a device with a one-time code, and minting an access token. They are rate limited
/// (<see cref="RateLimitPolicy"/>). Each failure on them is one answer, <c>401</c>, whatever was
/// wrong.
/// </para>
/// <para>
/// Managing devices — issuing a code, listing, revoking another device — is for a signed-in
/// browser or the service bearer. An enrolled app may revoke itself and nothing else: a phone that
/// is lost must not be able to enroll its replacement or sign the other devices out.
/// </para>
/// <para>
/// No route returns a secret after the response that issues it, and nothing here logs one.
/// </para>
/// </remarks>
public static class AuthEndpoints
{
    /// <summary>Where the chat service asks who a caller is. Not proxied; carries its own bearer.</summary>
    public const string CheckPath = SignIn.CheckPath;

    /// <summary>The fixed-window limit on the routes that take no credential.</summary>
    public const string RateLimitPolicy = "auth-public";

    /// <summary>The purpose and subject of the token the chat service presents on <see cref="CheckPath"/>.</summary>
    public const string CheckTokenPurpose = SignIn.CheckTokenPurpose;
    public const string CheckTokenSubject = SignIn.CheckTokenSubject;

    public sealed record BrowserSessionBody(string? Token, string? DeviceName);
    public sealed record EnrollmentBody(string? DeviceName);
    public sealed record RegisterDeviceBody(string? EnrollmentCode, string? DeviceName);
    public sealed record TokenBody(string? DeviceId, string? DeviceSecret);
    public sealed record CheckBody(string? Authorization, string? Cookie, string? Agent);

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // ── who am I ─────────────────────────────────────────────────────────

        app.MapGet("/api/auth/session", async (HttpContext http, [FromServices] CallerAuthenticator authenticator,
            [FromServices] AuthService auth, CancellationToken ct) =>
        {
            var caller = CallerIdentity.Of(http);
            if (caller.Kind == CallerKind.None)
            {
                // With nothing gated there is nothing to be refused: the page is told it may carry
                // on, and that it has no session of its own yet.
                return authenticator.IsRequired
                    ? Results.Json(new { error = "Unauthorized", authRequired = true }, statusCode: StatusCodes.Status401Unauthorized)
                    : Results.Ok(new { authRequired = false, signedIn = false });
            }

            var user = caller.PrincipalId is null ? null : await auth.FindUserAsync(caller.PrincipalId, ct);
            return Results.Ok(new
            {
                authRequired = authenticator.IsRequired,
                signedIn = true,
                kind = KindName(caller.Kind),
                userId = caller.PrincipalId,
                displayName = user?.DisplayName,
                deviceId = caller.DeviceId,
            });
        });

        // ── browsers ─────────────────────────────────────────────────────────

        app.MapPost("/api/auth/browser-session", async (HttpContext http, [FromBody] BrowserSessionBody? body,
            [FromServices] CallerAuthenticator authenticator, [FromServices] AuthService auth, CancellationToken ct) =>
        {
            // The bootstrap token is the deployment's own secret. With none configured the
            // orchestrator gates nothing, and a session is issued to whoever asks: it adds no
            // access, it only gives the page a credential to present to the chat service.
            if (authenticator.IsRequired && !authenticator.IsServiceToken(body?.Token?.Trim()))
                return Unauthorized();

            var result = await auth.SignInBrowserAsync(UserRecord.OwnerId, body?.DeviceName, ct);
            if (!result.Succeeded)
                return result.Error == AuthError.DeviceLimit ? DeviceLimit() : Unauthorized();

            var session = result.Value!;
            SessionCookies.Set(http, session.SessionToken, session.ExpiresAt);
            return Results.Ok(new { signedIn = true, kind = "browser", userId = UserRecord.OwnerId, deviceId = session.DeviceId });
        }).RequireRateLimiting(RateLimitPolicy);

        app.MapDelete("/api/auth/browser-session", async (HttpContext http,
            [FromServices] CallerAuthenticator authenticator, [FromServices] AuthService auth, CancellationToken ct) =>
        {
            var caller = CallerIdentity.Of(http);
            if (caller is { Kind: CallerKind.Browser, PrincipalId: not null, DeviceId: not null })
            {
                await auth.RevokeDeviceAsync(caller.PrincipalId, caller.DeviceId, ct);
                authenticator.Forget();
            }

            // Cleared whether or not the cookie still named a session: the person asked to be
            // signed out of this browser, and a stale cookie is not signed in either.
            SessionCookies.Clear(http);
            return Results.NoContent();
        });

        // ── apps ─────────────────────────────────────────────────────────────

        app.MapPost("/api/auth/enrollments", async (HttpContext http, [FromBody] EnrollmentBody? body,
            [FromServices] CallerAuthenticator authenticator, [FromServices] AuthService auth, CancellationToken ct) =>
        {
            var principal = ManagingPrincipal(http, authenticator);
            if (principal is null) return Forbidden();

            var result = await auth.IssueEnrollmentCodeAsync(principal, body?.DeviceName, ct);
            if (!result.Succeeded)
                return result.Error == AuthError.DeviceLimit ? DeviceLimit() : Unauthorized();

            return Results.Ok(new
            {
                enrollmentCode = result.Value!.EnrollmentCode,
                expiresInSeconds = result.Value.ExpiresInSeconds,
            });
        });

        app.MapPost("/api/auth/devices", async ([FromBody] RegisterDeviceBody? body,
            [FromServices] AuthService auth, CancellationToken ct) =>
        {
            var result = await auth.RegisterDeviceAsync(body?.EnrollmentCode, body?.DeviceName, ct);
            if (!result.Succeeded)
                return result.Error == AuthError.DeviceLimit ? DeviceLimit() : Unauthorized();

            return Results.Ok(new { deviceId = result.Value!.DeviceId, deviceSecret = result.Value.DeviceSecret });
        }).RequireRateLimiting(RateLimitPolicy);

        app.MapPost("/api/auth/token", async ([FromBody] TokenBody? body,
            [FromServices] AuthService auth, CancellationToken ct) =>
        {
            var result = await auth.MintTokenAsync(body?.DeviceId, body?.DeviceSecret, ct);
            if (!result.Succeeded)
                return Unauthorized();

            return Results.Ok(new { accessToken = result.Value!.AccessToken, expiresInSeconds = result.Value.ExpiresInSeconds });
        }).RequireRateLimiting(RateLimitPolicy);

        // ── devices ──────────────────────────────────────────────────────────

        app.MapGet("/api/auth/devices", async (HttpContext http,
            [FromServices] CallerAuthenticator authenticator, [FromServices] AuthService auth, CancellationToken ct) =>
        {
            var principal = ManagingPrincipal(http, authenticator);
            if (principal is null) return Forbidden();

            var caller = CallerIdentity.Of(http);
            var devices = await auth.ListDevicesAsync(principal, ct);
            return Results.Ok(new
            {
                devices = devices.Select(d => new
                {
                    deviceId = d.DeviceId,
                    kind = d.Kind == DeviceKind.Browser ? "browser" : "app",
                    name = d.Name,
                    registeredAt = d.RegisteredAt,
                    lastSeenAt = d.LastSeenAt,
                    revokedAt = d.RevokedAt,
                    current = string.Equals(d.DeviceId, caller.DeviceId, StringComparison.Ordinal),
                }),
            });
        });

        app.MapPost("/api/auth/devices/{deviceId}/revoke", async (string deviceId, HttpContext http,
            [FromServices] CallerAuthenticator authenticator, [FromServices] AuthService auth, CancellationToken ct) =>
        {
            var caller = CallerIdentity.Of(http);
            var principal = ManagingPrincipal(http, authenticator);

            // An app may end its own registration: that is "sign out of this phone".
            if (principal is null
                && caller is { Kind: CallerKind.App, PrincipalId: not null }
                && string.Equals(caller.DeviceId, deviceId, StringComparison.Ordinal))
            {
                principal = caller.PrincipalId;
            }

            if (principal is null) return Forbidden();

            var revoked = await auth.RevokeDeviceAsync(principal, deviceId, ct);
            authenticator.Forget();
            if (!revoked)
                return Results.NotFound(new { error = "device_not_found" });

            if (caller.Kind == CallerKind.Browser && string.Equals(caller.DeviceId, deviceId, StringComparison.Ordinal))
                SessionCookies.Clear(http);

            return Results.NoContent();
        });

        // ── the chat service asks who a caller is ────────────────────────────

        app.MapPost(CheckPath, async (HttpContext http, [FromBody] CheckBody? body,
            [FromServices] JournalTokenService tokens, [FromServices] CallerAuthenticator authenticator,
            [FromServices] IServiceScopeFactory scopes, CancellationToken ct) =>
        {
            var presented = CallerAuthenticator.BearerOf(http.Request.Headers.Authorization.FirstOrDefault());
            if (!tokens.TryVerify(presented, CheckTokenPurpose, out var subject)
                || !string.Equals(subject, CheckTokenSubject, StringComparison.Ordinal))
            {
                return Unauthorized();
            }

            if (body is null) return Results.BadRequest(new { error = "body_required" });

            CallerIdentity caller;
            try
            {
                caller = await authenticator.ResolveAsync(body.Authorization, body.Cookie, ct);
            }
            catch (AuthStoreUnavailableException)
            {
                // Said as itself. The chat service answers its own caller 503, never a pass.
                return Results.Json(new { error = "sign_in_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (AuthBusyException)
            {
                return Results.Json(new { error = "too_many_requests" }, statusCode: StatusCodes.Status429TooManyRequests);
            }

            // A device or a browser. The service bearer is the deployment's secret for automation
            // against this API; it names no device, and a conversation belongs to one.
            if (caller.Kind is not (CallerKind.App or CallerKind.Browser))
                return Unauthorized();

            // Asked only when the chat service is about to open or use a conversation with an
            // agent. Answered for a verified caller alone, so the route says nothing about which
            // agents exist to anyone else. Null when no agent was named.
            bool? agentChatEnabled = null;
            if (!string.IsNullOrEmpty(body.Agent))
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetService<Data.OrchestratorDbContext>();
                agentChatEnabled = db is not null
                    && ChatAgents.IsAvailable(tokens)
                    && ChatAgents.IsUsableName(body.Agent)
                    // Ordinal, after the query: the database compares names without regard to
                    // case, and an agent's chat identity is its exact name.
                    && (await db.Agents.AsNoTracking()
                            .Where(a => a.Name == body.Agent && a.ChatEnabled)
                            .Select(a => a.Name)
                            .ToListAsync(ct))
                        .Any(name => string.Equals(name, body.Agent, StringComparison.Ordinal));
            }

            return Results.Ok(new
            {
                principalId = caller.PrincipalId,
                deviceId = caller.DeviceId,
                credentialId = caller.CredentialId,
                kind = KindName(caller.Kind),
                agentChatEnabled,
            });
        });

        // ── which agents a person can chat with ──────────────────────────────

        app.MapGet("/api/chat/agents", async ([FromServices] JournalTokenService tokens,
            [FromServices] IServiceScopeFactory scopes, CancellationToken ct) =>
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetService<Data.OrchestratorDbContext>();
            if (db is null || !ChatAgents.IsAvailable(tokens))
                return Results.Ok(new { available = false, agents = Array.Empty<object>() });

            var rows = await db.Agents.AsNoTracking()
                .Where(a => a.ChatEnabled)
                .OrderBy(a => a.Name)
                .Select(a => new { a.Name, a.DisplayName })
                .ToListAsync(ct);

            return Results.Ok(new
            {
                available = true,
                agents = rows
                    .Where(a => ChatAgents.IsUsableName(a.Name))
                    .Select(a => new
                    {
                        agentName = a.Name,
                        displayName = string.IsNullOrWhiteSpace(a.DisplayName) ? a.Name : a.DisplayName,
                    }),
            });
        });

        return app;
    }

    /// <summary>
    /// The user on whose behalf devices may be managed by this caller, or null when it may not.
    /// A signed-in browser manages its own user's devices; the service bearer, and anyone at all
    /// when nothing is gated, manages the owner's.
    /// </summary>
    private static string? ManagingPrincipal(HttpContext http, CallerAuthenticator authenticator)
    {
        var caller = CallerIdentity.Of(http);
        return caller.Kind switch
        {
            CallerKind.Browser or CallerKind.Service => caller.PrincipalId,
            CallerKind.None when !authenticator.IsRequired => UserRecord.OwnerId,
            _ => null,
        };
    }

    private static string KindName(CallerKind kind) => kind switch
    {
        CallerKind.Browser => "browser",
        CallerKind.App => "app",
        CallerKind.Service => "service",
        _ => "none",
    };

    private static IResult Unauthorized() =>
        Results.Json(new { error = "Unauthorized" }, statusCode: StatusCodes.Status401Unauthorized);

    private static IResult Forbidden() =>
        Results.Json(new { error = "forbidden" }, statusCode: StatusCodes.Status403Forbidden);

    private static IResult DeviceLimit() =>
        Results.Json(new { error = "device_limit" }, statusCode: StatusCodes.Status409Conflict);
}

/// <summary>The session cookie's attributes, in one place.</summary>
public static class SessionCookies
{
    /// <summary>
    /// <c>HttpOnly</c> so page script cannot read it, <c>SameSite=Strict</c> so no other site's
    /// request carries it, and <c>Secure</c> whenever the request arrived over HTTPS — directly,
    /// or as the proxy in front reports it. A dashboard opened over plain HTTP on a private
    /// network still works; its cookie is simply not marked.
    /// </summary>
    public static void Set(HttpContext http, string sessionToken, DateTimeOffset expiresAt) =>
        http.Response.Cookies.Append(CallerAuthenticator.SessionCookie, sessionToken, Options(http, expiresAt));

    public static void Clear(HttpContext http) =>
        http.Response.Cookies.Delete(CallerAuthenticator.SessionCookie, Options(http, null));

    private static CookieOptions Options(HttpContext http, DateTimeOffset? expiresAt) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Secure = http.Request.IsHttps
                 || string.Equals(http.Request.Headers["X-Forwarded-Proto"].FirstOrDefault(), "https", StringComparison.OrdinalIgnoreCase),
        Path = "/",
        Expires = expiresAt,
        IsEssential = true,
    };
}
