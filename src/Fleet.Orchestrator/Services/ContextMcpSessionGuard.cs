using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// The two MCP routes and the one predicate that tells them apart (#347 D6).
/// </summary>
/// <remarks>
/// <c>/mcp</c> is the admin surface: every orchestrator tool, unauthenticated, never auto-granted.
/// <c>/mcp/context</c> is the fallback surface auto-injected into card agents: sessions created there
/// hold only <c>get_project_context</c> and are bound to the agent that created them. Segment
/// matching is case-insensitive, like ASP.NET routing, so no casing of the path can reach the
/// context endpoint without also being recognised as the context route here.
/// </remarks>
public static class ContextMcpRoute
{
    public const string AdminPath = "/mcp";
    public const string ContextPath = "/mcp/context";

    /// <summary>The only tool a context session holds.</summary>
    public const string FallbackToolName = "get_project_context";

    public const string SessionIdHeader = "Mcp-Session-Id";

    private static readonly Regex AgentPattern = new(@"^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant);

    /// <summary>Exactly <c>/mcp/context</c> or <c>/mcp/context/…</c>.</summary>
    public static bool IsContextPath(PathString path) => path.StartsWithSegments(ContextPath);

    /// <summary>The single <c>?agent=</c> value when it is well formed; otherwise <c>null</c>.</summary>
    public static string? ReadAgent(HttpRequest request)
    {
        var values = request.Query["agent"];
        if (values.Count != 1) return null;
        var agent = values[0];
        return agent is not null && AgentPattern.IsMatch(agent) ? agent : null;
    }

    /// <summary>
    /// The request's <c>Mcp-Session-Id</c>, or <c>null</c> when absent or empty (a request the SDK
    /// serves on a new session). Several header values are joined, so they can never match a
    /// single registered id.
    /// </summary>
    public static string? ReadSessionId(HttpRequest request)
    {
        var values = request.Headers[SessionIdHeader];
        return values.Count switch
        {
            0 => null,
            1 => string.IsNullOrEmpty(values[0]) ? null : values[0],
            _ => values.ToString(),
        };
    }

    /// <summary>
    /// <see cref="ModelContextProtocol.AspNetCore.HttpServerTransportOptions.ConfigureSessionOptions"/>:
    /// a session initiated on the context route gets a tool collection holding only
    /// <see cref="FallbackToolName"/>. Every other path is untouched.
    /// </summary>
    /// <remarks>
    /// The SDK hands this a fresh <see cref="McpServerOptions"/> per session, so replacing the
    /// collection cannot leak into another session. Prompts, resources and custom handlers are
    /// cleared too, so the context session exposes nothing that a later registration could add
    /// to the admin server.
    /// </remarks>
    public static Task ConfigureSessionOptionsAsync(HttpContext context, McpServerOptions options, CancellationToken ct)
    {
        if (!IsContextPath(context.Request.Path))
            return Task.CompletedTask;

        var only = new McpServerPrimitiveCollection<McpServerTool>();
        if (options.ToolCollection is { } all && all.TryGetPrimitive(FallbackToolName, out var tool))
            only.Add(tool);

        options.ToolCollection = only;
        options.PromptCollection = null;
        options.ResourceCollection = null;
        options.Handlers = new McpServerHandlers();
        return Task.CompletedTask;
    }

    /// <summary>A 6-character hash of a session id — enough to correlate log lines, never the id.</summary>
    public static string HashSessionId(string sessionId) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId)))[..6];
}

/// <summary>
/// Binds MCP sessions created on <c>/mcp/context</c> to that route and agent, and rejects any request
/// that would cross a route or an agent (#347 D6). Runs before <c>MapMcp</c>.
/// </summary>
/// <remarks>
/// <list type="table">
/// <listheader><term>Request</term><description>Action</description></listheader>
/// <item><term><c>/mcp/context</c>, <c>agent</c> missing, blank or not <c>^[A-Za-z0-9_-]{1,64}$</c></term><description>403</description></item>
/// <item><term><c>/mcp/context</c>, session bound to the same agent</term><description>pass; refresh <c>lastSeen</c></description></item>
/// <item><term><c>/mcp/context</c>, session bound to another agent</term><description>403</description></item>
/// <item><term><c>/mcp/context</c>, session not in the registry (admin, expired, evicted)</term><description>404 → the client re-initializes</description></item>
/// <item><term><c>/mcp/context</c>, no session id (initialize, or the SDK's implicit session)</term><description>pass; <c>Response.OnStarting</c> binds the returned id to this agent</description></item>
/// <item><term><c>/mcp/context</c> <c>DELETE</c> for a bound session</term><description>pass, then remove the binding</description></item>
/// <item><term><c>/mcp/context/…</c> (the SDK's legacy SSE endpoints)</term><description>404 — see below</description></item>
/// <item><term><c>/mcp</c> with a session id present in the registry</term><description>403: a context session never crosses to admin</description></item>
/// </list>
/// <para>
/// The legacy SSE transport (<c>/sse</c> + <c>/message?sessionId=</c>) carries its session id in the
/// query string and the event stream, never in <c>Mcp-Session-Id</c>, so no binding can cover it —
/// and an admin SSE session posted to <c>/mcp/context/message</c> would keep every admin tool.
/// Provisioning only ever injects the streamable HTTP endpoint, so the context route refuses
/// everything below <c>/mcp/context</c> instead of half-guarding it.
/// </para>
/// </remarks>
public sealed class ContextMcpSessionGuard(
    RequestDelegate next,
    ContextSessionRegistry registry,
    ILogger<ContextMcpSessionGuard> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        if (!request.Path.StartsWithSegments(ContextMcpRoute.AdminPath))
        {
            await next(context);
            return;
        }

        var sessionId = ContextMcpRoute.ReadSessionId(request);

        if (!ContextMcpRoute.IsContextPath(request.Path))
        {
            // Admin route: unchanged, except that a context session can never be replayed here —
            // by any header value, or by the legacy transport's ?sessionId= for good measure.
            foreach (var id in request.Headers[ContextMcpRoute.SessionIdHeader].Concat(request.Query["sessionId"]))
            {
                if (id is not null && registry.Contains(id))
                {
                    await RejectAsync(context, StatusCodes.Status403Forbidden, "context_session_on_admin", id);
                    return;
                }
            }

            await next(context);
            return;
        }

        if (!request.Path.Equals(ContextMcpRoute.ContextPath, StringComparison.OrdinalIgnoreCase) &&
            !request.Path.Equals(ContextMcpRoute.ContextPath + "/", StringComparison.OrdinalIgnoreCase))
        {
            await RejectAsync(context, StatusCodes.Status404NotFound, "unsupported_transport", sessionId);
            return;
        }

        var agent = ContextMcpRoute.ReadAgent(request);
        if (agent is null)
        {
            await RejectAsync(context, StatusCodes.Status403Forbidden, "no_agent", sessionId);
            return;
        }

        if (sessionId is null)
        {
            // No session yet: an initialize, or a request the SDK will serve on an implicit session.
            // Either way the response carries the new id, and it is bound to this agent before a
            // single byte reaches the client — so a stateless call is a one-request session bound
            // like any other.
            context.Response.OnStarting(() =>
            {
                var created = context.Response.Headers[ContextMcpRoute.SessionIdHeader].ToString();
                if (!string.IsNullOrEmpty(created))
                {
                    registry.Bind(created, agent);
                    logger.LogInformation(
                        "ContextMcpGuard bound agent={Agent} session={Session}",
                        agent, ContextMcpRoute.HashSessionId(created));
                }
                return Task.CompletedTask;
            });

            await next(context);
            return;
        }

        if (!registry.TryGetAgent(sessionId, out var boundAgent))
        {
            await RejectAsync(context, StatusCodes.Status404NotFound, "unbound_session", sessionId);
            return;
        }

        if (!string.Equals(boundAgent, agent, StringComparison.Ordinal))
        {
            await RejectAsync(context, StatusCodes.Status403Forbidden, "agent_mismatch", sessionId);
            return;
        }

        registry.Touch(sessionId);

        if (HttpMethods.IsDelete(request.Method))
        {
            try
            {
                await next(context);
            }
            finally
            {
                registry.Remove(sessionId);
            }
            return;
        }

        await next(context);
    }

    private async Task RejectAsync(HttpContext context, int status, string reason, string? sessionId)
    {
        var hash = string.IsNullOrEmpty(sessionId) ? "-" : ContextMcpRoute.HashSessionId(sessionId);
        logger.LogWarning(
            "ContextMcpGuard rejected status={Status} reason={Reason} path={Path} session={Session}",
            status, reason, context.Request.Path.Value, hash);

        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { error = reason });
    }
}

/// <summary>
/// The orchestrator's MCP registration, shared by <c>Program.cs</c> and the route tests so they
/// exercise the production wiring rather than a copy of it.
/// </summary>
public static class FleetMcpRegistration
{
    /// <summary>
    /// The MCP server, its HTTP transport and every orchestrator tool, plus the context-session
    /// registry and its pruner. <c>Stateless</c> and <c>PerSessionExecutionContext</c> stay at the
    /// SDK defaults (<c>false</c>): the binding needs session ids, and the per-call check in
    /// <c>get_project_context</c> needs the CURRENT request's <c>HttpContext</c>.
    /// </summary>
    public static IMcpServerBuilder AddFleetMcpServer(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ContextSessionRegistry>();
        services.AddHostedService<ContextSessionPruneService>();

        return services
            .AddMcpServer()
            .WithHttpTransport(o => o.ConfigureSessionOptions = ContextMcpRoute.ConfigureSessionOptionsAsync)
            .WithToolsFromAssembly(typeof(FleetMcpRegistration).Assembly);
    }

    /// <summary>The guard, then both routes. Call after the bearer middleware, in place of <c>MapMcp</c>.</summary>
    public static void MapFleetMcp(this WebApplication app)
    {
        app.UseMiddleware<ContextMcpSessionGuard>();
        app.MapMcp(ContextMcpRoute.ContextPath);
        app.MapMcp(ContextMcpRoute.AdminPath);
    }
}
