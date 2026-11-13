using System.Threading.RateLimiting;
using Fleet.Orchestrator.Endpoints;
using Fleet.Orchestrator.Services;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Fleet.Orchestrator.Auth;

/// <summary>
/// Registration and request pipeline for sign-in, as two calls, so a test host is gated by exactly
/// what the orchestrator runs rather than by a copy of it.
/// </summary>
public static class FleetAccess
{
    /// <param name="durable">
    /// True when the orchestrator has a database. Without one, records live in memory and a restart
    /// signs everyone out.
    /// </param>
    public static IServiceCollection AddFleetSignIn(this IServiceCollection services, bool durable)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<MonotonicClock>();
        services.TryAddSingleton<ISecretHasher>(_ => new Argon2idSecretHasher());
        if (durable)
            services.TryAddSingleton<IAuthStore, EfAuthStore>();
        else
            services.TryAddSingleton<IAuthStore, InMemoryAuthStore>();
        services.AddSingleton<AuthService>();
        services.AddSingleton<CallerAuthenticator>();
        return services;
    }

    /// <summary>
    /// The limit on the sign-in routes that take no credential
    /// (<see cref="AuthEndpoints.RateLimitPolicy"/>). Per client address as the proxy reports it;
    /// behind a chain of proxies that is one address, and the limit is then simply a global one.
    /// </summary>
    public static RateLimiterOptions AddSignInPolicy(this RateLimiterOptions options)
    {
        options.AddPolicy(AuthEndpoints.RateLimitPolicy, context =>
        {
            var partitionKey = context.Request.Headers["X-Real-IP"].FirstOrDefault()
                ?? context.Connection.RemoteIpAddress?.ToString()
                ?? "anon";
            return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = SignInRequestsPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
        });
        return options;
    }

    public const int SignInRequestsPerMinute = 30;

    /// <summary>
    /// Resolve the caller, gate the request with <see cref="OrchestratorAuth.Decide"/>, and apply
    /// the config API's own rule.
    /// </summary>
    public static WebApplication UseFleetAccess(this WebApplication app)
    {
        var authenticator = app.Services.GetRequiredService<CallerAuthenticator>();

        app.Use(async (context, next) =>
        {
            var request = context.Request;
            var path = request.Path.Value ?? "";

            CallerIdentity caller;
            try
            {
                caller = await authenticator.ResolveAsync(
                    request.Headers.Authorization.FirstOrDefault(),
                    request.Headers.Cookie.ToString(),
                    context.RequestAborted);
            }
            catch (AuthStoreUnavailableException)
            {
                // A lookup that could not complete is never a pass.
                await RefuseAsync(context, StatusCodes.Status503ServiceUnavailable, "sign_in_unavailable");
                return;
            }
            catch (AuthBusyException)
            {
                await RefuseAsync(context, StatusCodes.Status429TooManyRequests, "too_many_requests");
                return;
            }

            if (context.WebSockets.IsWebSocketRequest && caller.Kind == CallerKind.Browser
                && !ControlSocketAccess.SameOrigin(request))
            {
                await RefuseAsync(context, StatusCodes.Status403Forbidden, "origin_refused");
                return;
            }
            caller.AttachTo(context);
            if (caller.SessionExpiresAt is { } extendedTo)
            {
                // The session was given a full lifetime again; the cookie that carries it follows.
                var cookie = CallerAuthenticator.CookieOf(request.Headers.Cookie.ToString(), CallerAuthenticator.SessionCookie);
                if (cookie is not null) SessionCookies.Set(context, cookie, extendedTo);
            }

            var decision = OrchestratorAuth.Decide(
                authenticator.IsRequired,
                request.Method,
                path,
                isEdge: request.Headers[OrchestratorAuth.EdgeHeader].FirstOrDefault() == "1",
                caller.Kind,
                hasRequestHeader: request.Headers.ContainsKey(OrchestratorAuth.RequestHeader));

            switch (decision)
            {
                case AccessDecision.Unauthorized:
                    await RefuseAsync(context, StatusCodes.Status401Unauthorized, "Unauthorized");
                    return;
                case AccessDecision.RequestHeaderRequired:
                    await RefuseAsync(context, StatusCodes.Status403Forbidden, "request_header_required");
                    return;
            }

            await next(context);
        });

        // Config API auth — ORCHESTRATOR_CONFIG_TOKEN gates /api/config/* (all methods).
        // Separate from ORCHESTRATOR_AUTH_TOKEN so peers can read config without full admin access.
        // Fails CLOSED: if ORCHESTRATOR_CONFIG_TOKEN is not configured all /api/config/* requests
        // return 503 so the endpoint is never accidentally open on a misconfigured deployment.
        // A signed-in browser is accepted in place of the token: the dashboard no longer carries
        // it. An enrolled app is not — values in .env stay out of reach of a phone.
        var configToken = app.Configuration["Orchestrator:ConfigToken"] ?? "";
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? "";
            if (!OrchestratorAuth.IsConfigApi(path))
            {
                await next(context);
                return;
            }

            if (string.IsNullOrWhiteSpace(configToken))
            {
                await RefuseAsync(context, StatusCodes.Status503ServiceUnavailable,
                    "Config API unavailable: ORCHESTRATOR_CONFIG_TOKEN is not configured on this orchestrator");
                return;
            }

            var caller = CallerIdentity.Of(context);
            if (authenticator.IsRequired && caller.Kind == CallerKind.Browser)
            {
                if (OrchestratorAuth.IsBrowserWriteWithoutHeader(
                        context.Request.Method, caller.Kind,
                        context.Request.Headers.ContainsKey(OrchestratorAuth.RequestHeader)))
                {
                    await RefuseAsync(context, StatusCodes.Status403Forbidden, "request_header_required");
                    return;
                }

                await next(context);
                return;
            }

            var token = CallerAuthenticator.BearerOf(context.Request.Headers.Authorization.FirstOrDefault());
            if (token != configToken)
            {
                await RefuseAsync(context, StatusCodes.Status401Unauthorized, "Unauthorized");
                return;
            }

            await next(context);
        });

        return app;
    }

    private static async Task RefuseAsync(HttpContext context, int status, string error)
    {
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { error });
    }
}
