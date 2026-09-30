using Fleet.Conversations.Contracts;
using Fleet.Conversations.Journal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace Fleet.Comms.Routes;

/// <summary>
/// The journal's read tools as a streamable-HTTP MCP endpoint, <c>POST /journal/v1/mcp</c> (#394),
/// served on the JOURNAL listener and nowhere else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stateless.</b> Every POST is answered on its own, with no session the server has to keep, so
/// a Comms restart between two calls loses nothing — the page cursor carries its own position.
/// </para>
/// <para>
/// Never mapped on north, south or ops: a read token is a journal credential, and a routing mistake
/// must not put the journal's contents behind another surface's authentication. The listener exists
/// only when the journal is enabled, so with it off the endpoint does not exist and an agent that
/// was granted these tools simply fails to connect.
/// </para>
/// <para>
/// <see cref="JournalAuth"/> runs first, as for every journal route: no token, an ingest token or
/// any other failure gets the listener's one 401, before this code or the SDK sees the request.
/// </para>
/// </remarks>
public static class JournalMcp
{
    public const string Path = "/journal/v1/mcp";

    /// <summary>Registers the MCP server and the read tools on the journal listener's container.</summary>
    /// <param name="store">
    /// A factory, so the store is built once, on the listener's own container, with its logger.
    /// </param>
    public static void AddServices(
        IServiceCollection services, JournalReadGrants grants, JournalRuntimeStats stats,
        Func<IServiceProvider, IJournalReadStore> store)
    {
        services.AddHttpContextAccessor();
        services.AddSingleton(store);
        services.AddSingleton(grants);
        services.AddSingleton(stats);

        services
            .AddMcpServer(options => options.ServerInfo = new Implementation
            {
                Name = "fleet-comms-journal",
                Version = "1",
            })
            .WithHttpTransport(options => options.Stateless = true)
            .WithTools<JournalReadTools>();
    }

    /// <summary>Maps the endpoint. Call after <see cref="JournalAuth.Use"/>.</summary>
    public static void Map(WebApplication app)
    {
        // An authenticated GET or DELETE is a 405, decided here rather than left to whatever the SDK
        // maps: the SDK's stateless mode answers 405 today, but a 401 on these verbs would push an
        // MCP client into an OAuth flow, so the answer is pinned by this code, not by a package
        // version. No body: there is nothing a client needs from it.
        app.Use(async (context, next) =>
        {
            if (string.Equals(context.Request.Path.Value, Path, StringComparison.Ordinal)
                && (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsDelete(context.Request.Method)))
            {
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                context.Response.Headers.Allow = "POST";
                return;
            }

            await next(context);
        });

        app.MapMcp(Path);
    }
}

/// <summary>
/// Who may read everything: the parsed <c>Comms__Journal__ReadAllSubjects</c>. Every other valid
/// read token reads only what its own runtime observed.
/// </summary>
public sealed class JournalReadGrants(IReadOnlySet<string> allScopeSubjects)
{
    /// <summary>Sorted, for the status route.</summary>
    public IReadOnlyList<string> AllScopeSubjects { get; } =
        allScopeSubjects.Order(StringComparer.Ordinal).ToArray();

    public JournalReader ReaderFor(string subject) => new(
        subject,
        allScopeSubjects.Contains(subject) ? JournalReadScope.All : JournalReadScope.Observed);
}
