using Fleet.Comms.Auth;
using Fleet.Comms.Configuration;
using Fleet.Comms.Routes;
using Fleet.Comms.Contracts;
using Fleet.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Fleet.Comms;

/// <summary>
/// Composition for the north listener.
///
/// <para>Shared byte-for-byte between <c>Program</c> and the tests, so a test exercises the same
/// registration graph production runs. A test host that wires its own services proves things about
/// the test host.</para>
/// </summary>
public static class CommsApp
{
    /// <summary>Register everything the north boundary needs. No south service is registered here.</summary>
    public static IServiceCollection AddNorthBoundary(this IServiceCollection services)
    {
        // TryAdd, not Add. These are defaults: a host that registered a substitute before calling
        // this keeps it. With Add, the last registration wins and the substitute is silently
        // discarded — which would have made every TTL assertion run against the system clock and
        // pass vacuously.
        services.TryAddSingleton<IAuthStore, InMemoryAuthStore>();
        services.TryAddSingleton<ISecretHasher, Argon2idSecretHasher>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<AuthService>();

        // Registered so the stream slice can take it as a dependency without re-deciding the
        // 30-second bound. Nothing resolves it in a request pipeline yet — this slice ships the
        // component, not the socket.
        services.TryAddSingleton<CredentialRevalidator>();
        return services;
    }

    /// <summary>
    /// Build the north application.
    ///
    /// <para>The north and south surfaces are <b>separate listeners</b>, not one listener with two
    /// path prefixes (§1, D1). This method builds the north one and maps only
    /// <see cref="NorthEndpoints.MapNorthApi"/>; when the south surface lands it is its own
    /// application on its own listener, so a routing mistake cannot promote a device to a trusted
    /// service caller.</para>
    /// </summary>
    public static WebApplication BuildNorthApp(WebApplicationBuilder builder)
    {
        builder.Services.Configure<CommsOptions>(
            builder.Configuration.GetSection(CommsOptions.SectionName));
        builder.Services.AddNorthBoundary();

        var app = builder.Build();

        // Fail closed, at the edge. An auth-store outage is a 503 with the fixed Internal body and
        // is NEVER a pass (§16, MUST NOT 18); anything else unhandled is a 500 with the same body,
        // so no exception message can reach a client (§13). Both statuses carry identical bytes —
        // the distinction is the status line, and a client must not parse the body to tell them
        // apart (§5.2).
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (AuthStoreUnavailableException)
            {
                await WriteFixedError(context, StatusCodes.Status503ServiceUnavailable);
            }
            catch (Exception)
            {
                await WriteFixedError(context, StatusCodes.Status500InternalServerError);
            }
        });

        app.MapNorthApi();
        return app;
    }

    private static Task WriteFixedError(HttpContext context, int status)
    {
        if (context.Response.HasStarted)
            return Task.CompletedTask;

        context.Response.Clear();
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(
            ErrorResponse.For(ProtocolErrorCode.Internal), FleetProtocolJson.Options);
    }
}
