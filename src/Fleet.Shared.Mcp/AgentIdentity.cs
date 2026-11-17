using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fleet.Shared.Mcp;

/// <summary>
/// A server's agent identity settings, read once at startup: <c>AgentIdentity:Key</c>
/// (<c>FLEET_AGENT_IDENTITY_KEY</c>) and <c>AgentIdentity:Enforce</c>
/// (<c>FLEET_AGENT_IDENTITY_ENFORCE</c>).
/// </summary>
/// <remarks>
/// Three states. No key: a deployment from before tokens, where callers are believed as they
/// always were. A key without enforcement: a token is verified when one is sent and a caller
/// without one is still served, which is what lets services be deployed and agents reprovisioned
/// one at a time. A key with enforcement: a caller without a valid token is refused.
/// </remarks>
public sealed class AgentIdentitySettings
{
    public const string Section = "AgentIdentity";

    public IReadOnlyList<byte[]> Keys { get; }
    public bool Enforce { get; }

    /// <summary>Whether tokens can be verified at all.</summary>
    public bool HasKey => Keys.Count > 0;

    public AgentIdentitySettings(IReadOnlyList<byte[]> keys, bool enforce)
    {
        if (enforce && keys.Count == 0)
            throw new InvalidOperationException(
                "agent_identity_misconfigured: AgentIdentity:Enforce is on and AgentIdentity:Key is empty. "
                + "Set FLEET_AGENT_IDENTITY_KEY, or turn enforcement off.");
        Keys = keys;
        Enforce = enforce;
    }

    /// <exception cref="InvalidOperationException">
    /// A key that cannot be read, or enforcement without a key. The service must not start: it
    /// would either refuse every agent or believe every caller while saying it does not.
    /// </exception>
    public static AgentIdentitySettings From(IConfiguration configuration)
    {
        IReadOnlyList<byte[]> keys;
        try { keys = AgentIdentityTokens.ParseKeys(configuration[$"{Section}:Key"]); }
        catch (FormatException ex) { throw new InvalidOperationException("agent_identity_misconfigured: " + ex.Message); }
        return new AgentIdentitySettings(keys, ReadSwitch(configuration[$"{Section}:Enforce"]));
    }

    /// <summary>The first key's token for a fleet service calling another, or null without a key.</summary>
    public string? ServiceToken(string service) =>
        HasKey ? AgentIdentityTokens.Mint(Keys[0], AgentIdentityTokens.PurposeService, service) : null;

    private static bool ReadSwitch(string? raw)
    {
        var value = (raw ?? "").Trim().Trim('"', '\'');
        if (value.Length == 0) return false;
        if (bool.TryParse(value, out var on)) return on;
        throw new InvalidOperationException(
            $"agent_identity_misconfigured: {Section}:Enforce must be true or false.");
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

    internal static void Set(HttpContext context, AgentCaller caller) => context.Items[ItemKey] = caller;
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
    /// MUST NOT log the Authorization header or any part of it. A caller that is served without a
    /// token is logged by the name it claims, at most once in ten minutes per name: enough to see
    /// which agents still need reprovisioning before enforcement is turned on, without a line per call.
    /// </remarks>
    public static IApplicationBuilder UseAgentIdentity(
        this IApplicationBuilder app, Func<HttpContext, bool> appliesTo, Func<HttpContext, bool>? isOperator = null)
    {
        var settings = app.ApplicationServices.GetRequiredService<AgentIdentitySettings>();
        var logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger("Fleet.AgentIdentity");
        var lastSaid = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        if (!settings.HasKey)
            logger.LogWarning("agent_identity_unverified: no AgentIdentity:Key, so callers are believed by the name they give");
        else if (!settings.Enforce)
            logger.LogWarning("agent_identity_not_enforced: tokens are verified when sent, and a caller without one is still served");

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
                context.Request.Headers.Authorization.ToString(),
                context.Request.Query["agent"].FirstOrDefault(),
                settings.Keys, settings.Enforce);

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

            if (settings.HasKey && !caller.IsVerified && Said(lastSaid, "unverified:" + caller.Name))
                logger.LogInformation(
                    "agent_identity_unverified_call: served without a token, claiming to be '{Agent}'",
                    caller.Name.Length == 0 ? "(nobody)" : caller.Name);

            AgentCallerHttp.Set(context, caller);
            await next(context);
        });
    }

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
