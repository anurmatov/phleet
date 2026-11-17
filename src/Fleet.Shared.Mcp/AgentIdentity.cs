using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fleet.Shared.Mcp;

/// <summary>Required signing keys, read once at service startup.</summary>
public sealed class AgentIdentitySettings
{
    public const string Section = "AgentIdentity";
    public IReadOnlyList<byte[]> Keys { get; }

    public AgentIdentitySettings(IReadOnlyList<byte[]> keys)
    {
        if (keys.Count == 0 || keys.Any(k => k.Length < AgentIdentityTokens.MinimumKeyBytes))
            throw new InvalidOperationException("agent_identity_misconfigured: a valid AgentIdentity:Key is required.");
        Keys = keys;
    }

    public static AgentIdentitySettings From(IConfiguration configuration)
    {
        try { return new AgentIdentitySettings(AgentIdentityTokens.ParseKeys(configuration[$"{Section}:Key"])); }
        catch (FormatException) { throw new InvalidOperationException("agent_identity_misconfigured: invalid signing key."); }
    }

    public string ServiceToken(string service) =>
        AgentIdentityTokens.Mint(Keys[0], AgentIdentityTokens.PurposeService, service);

    /// <summary>Fixed bootstrap destination; invalid configuration never echoes a URL or credentials.</summary>
    public static Uri BootstrapUrl(string? baseUrl, string path)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidOperationException("agent_identity_misconfigured: invalid authorization bootstrap origin.");
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + path);
    }
}

/// <summary>The caller of the current request, as <see cref="AgentIdentityApp.UseAgentIdentity"/> resolved it.</summary>
public static class AgentCallerHttp
{
    private const string ItemKey = "fleet.agent-caller";

    /// <summary>Who is calling. <see cref="AgentCaller.None"/> outside a request and before the middleware ran.</summary>
    public static AgentCaller Of(HttpContext? context) =>
        context?.Items.TryGetValue(ItemKey, out var value) == true && value is AgentCaller caller ? caller : AgentCaller.None;

    public static AgentCaller Of(IHttpContextAccessor? accessor) => Of(accessor?.HttpContext);

    /// <summary>
    /// The agent this call is made as, or null when the caller is not an agent: what a tool that
    /// acts for an agent asks. Never read from the URL or from a tool's arguments.
    /// </summary>
    public static string? AgentNameOf(IHttpContextAccessor? accessor) =>
        Of(accessor).AgentName is { Length: > 0 } name ? name : null;

    /// <summary>
    /// The name something is attributed to: the calling agent's when the call is made as an agent,
    /// and <paramref name="argument"/> (what the model typed) only when it is not. A model once
    /// chose this name itself, and so could put any agent's on what it did.
    /// </summary>
    public static string AgentNameOr(IHttpContextAccessor? accessor, string? argument) =>
        AgentNameOf(accessor) ?? argument ?? "";

    /// <summary>Records who is calling. The middleware does; a test of a tool does the same in its place.</summary>
    public static void Set(HttpContext context, AgentCaller caller) => context.Items[ItemKey] = caller;
}

public static class AgentIdentityApp
{
    /// <summary>The body of a refusal. Fixed codes: nothing of the request is echoed.</summary>
    public const string RequiredCode = "agent_identity_required";
    public const string InvalidCode = "agent_identity_invalid";

    public static IServiceCollection AddAgentIdentity(this IServiceCollection services, IConfiguration configuration)
    {
        // Parsed here, so a key that cannot be read stops the service before it serves anything.
        var settings = AgentIdentitySettings.From(configuration);
        services.AddSingleton(settings);
        services.AddHttpContextAccessor();
        return services;
    }

    /// <summary>
    /// Resolves who is calling for every request <paramref name="appliesTo"/> selects (the MCP
    /// endpoint, not the health check), and refuses the ones the settings do not let through.
    /// </summary>
    /// <param name="isOperator">
    /// The server's own way of knowing an operator (the orchestrator's sign-in and service bearer).
    /// A request it accepts is served as a service whatever the token rule says.
    /// </param>
    /// <remarks>
    /// MUST NOT log the Authorization header or any part of it. Refusals are rate-limited and carry fixed codes.
    /// </remarks>
    public static IApplicationBuilder UseAgentIdentity(
        this IApplicationBuilder app, Func<HttpContext, bool> appliesTo, Func<HttpContext, bool>? isOperator = null)
    {
        var settings = app.ApplicationServices.GetRequiredService<AgentIdentitySettings>();
        var logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger("Fleet.AgentIdentity");
        var lastSaid = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        return app.Use(async (context, next) =>
        {
            if (!appliesTo(context))
            {
                await next(context);
                return;
            }

            if (isOperator?.Invoke(context) == true)
            {
                AgentCallerHttp.Set(context, new AgentCaller("operator", AgentCallerKind.Service));
                await next(context);
                return;
            }

            var (outcome, caller) = AgentIdentityRules.Resolve(
                context.Request.Headers.Authorization.ToString(), settings.Keys);

            if (outcome != AgentIdentityOutcome.Accepted)
            {
                var code = outcome == AgentIdentityOutcome.InvalidToken ? InvalidCode : RequiredCode;
                if (Said(lastSaid, "refused:" + code))
                    logger.LogWarning("{Code}: a call to {Path} was refused", code, context.Request.Path.Value);
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = AgentIdentityTokens.Scheme;
                await context.Response.WriteAsJsonAsync(new { error = code });
                return;
            }

            AgentCallerHttp.Set(context, caller);
            await next(context);
        });
    }

    /// <summary>
    /// For routes that are a fleet service's and never an agent's (fleet-memory's <c>/internal</c>
    /// REST API, which the orchestrator reads for the dashboard): only a service
    /// token is served. An agent's own token is refused there, valid as it is.
    /// </summary>
    public static IApplicationBuilder UseServiceIdentity(this IApplicationBuilder app, Func<HttpContext, bool> appliesTo)
    {
        var settings = app.ApplicationServices.GetRequiredService<AgentIdentitySettings>();
        var logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger("Fleet.AgentIdentity");
        var lastSaid = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        return app.Use(async (context, next) =>
        {
            if (!appliesTo(context))
            {
                await next(context);
                return;
            }

            var (outcome, caller) = AgentIdentityRules.Resolve(
                context.Request.Headers.Authorization.ToString(), settings.Keys);
            var refusal = outcome switch
            {
                AgentIdentityOutcome.InvalidToken => InvalidCode,
                AgentIdentityOutcome.TokenRequired => RequiredCode,
                _ when caller.Kind != AgentCallerKind.Service => ServiceRequiredCode,
                _ => null,
            };
            if (refusal is not null)
            {
                if (Said(lastSaid, "refused:" + refusal))
                    logger.LogWarning("{Code}: a call to {Path} was refused", refusal, context.Request.Path.Value);
                context.Response.StatusCode = refusal == ServiceRequiredCode
                    ? StatusCodes.Status403Forbidden : StatusCodes.Status401Unauthorized;
                if (refusal != ServiceRequiredCode) context.Response.Headers.WWWAuthenticate = AgentIdentityTokens.Scheme;
                await context.Response.WriteAsJsonAsync(new { error = refusal });
                return;
            }

            AgentCallerHttp.Set(context, caller);
            await next(context);
        });
    }

    /// <summary>A valid token that is an agent's, on a route only a service may call.</summary>
    public const string ServiceRequiredCode = "service_identity_required";

    private static readonly long SayEveryMs = (long)TimeSpan.FromMinutes(10).TotalMilliseconds;

    /// <summary>Whether this is worth a log line now: the first time, and then once in ten minutes.</summary>
    private static bool Said(ConcurrentDictionary<string, long> lastSaid, string what)
    {
        var now = Environment.TickCount64;
        if (lastSaid.Count > 2048) lastSaid.Clear();
        var said = true;
        lastSaid.AddOrUpdate(what, now, (_, last) =>
        {
            if (now - last < SayEveryMs) { said = false; return last; }
            return now;
        });
        return said;
    }
}
