using System.Net;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Fleet.Orchestrator.Services.McpTools;

/// <summary>Asks one MCP server for its tools.</summary>
public interface IMcpToolLister
{
    Task<McpServerTools> ListAsync(McpListTarget target, CancellationToken ct);
}

/// <summary>
/// The MCP client side of the tool listing: connect, <c>initialize</c>, <c>tools/list</c>, close.
/// </summary>
/// <remarks>
/// MUST NOT log or return a header value, and nothing here logs at all: the client and the HTTP
/// handler are given no logger, because a request log would carry the address and the headers.
/// Redirects and off-origin requests are refused before adding saved headers. Every
/// failure becomes a status and a fixed sentence; an exception's own text is never returned.
/// </remarks>
public sealed class McpToolLister(Func<HttpMessageHandler>? handlerFactory = null, TimeSpan? deadline = null) : IMcpToolLister
{
    /// <summary>Total budget for connecting, listing and closing the session.</summary>
    public static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(10);

    private TimeSpan Deadline { get; } = deadline ?? DefaultDeadline;

    public const int MaxTools = 500;
    public const int MaxNameChars = 200;
    public const int MaxDescriptionChars = 2000;

    public async Task<McpServerTools> ListAsync(McpListTarget target, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Deadline);

        var handler = handlerFactory?.Invoke() ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        };
        using var confined = new SavedOriginHandler(target, limit.Token, handler);
        using var http = new HttpClient(confined, disposeHandler: false);
        // Also cancel HTTP response-body reads, which happen above the message handler.
        using var stopRequests = limit.Token.Register(http.CancelPendingRequests);

        try
        {
            var transport = new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = target.Url,
                    Name = target.Name,
                    TransportMode = target.Sse ? HttpTransportMode.Sse : HttpTransportMode.StreamableHttp,
                    ConnectionTimeout = Deadline,
                },
                http, loggerFactory: null, ownsHttpClient: false);

            var client = await McpClient.CreateAsync(
                transport,
                new McpClientOptions { ClientInfo = new Implementation { Name = "fleet-orchestrator", Version = "1" } },
                loggerFactory: null,
                cancellationToken: limit.Token);

            McpServerTools result;
            try
            {
                var offered = await client.ListToolsAsync(cancellationToken: limit.Token);
                var tools = offered
                    .Where(tool => !string.IsNullOrWhiteSpace(tool.Name) && tool.Name.Length <= MaxNameChars)
                    .OrderBy(tool => tool.Name, StringComparer.Ordinal)
                    .Take(MaxTools)
                    .Select(tool => new McpToolInfo(tool.Name, Bounded(tool.Description)))
                    .ToList();
                result = new McpServerTools(target.Name, McpServerTools.Ok, null, tools, Truncated: offered.Count > tools.Count);
            }
            finally
            {
                // SDK disposal sends session DELETE with its own token. The handler also links
                // that request to our original budget, so cleanup cannot get a fresh timeout.
                await client.DisposeAsync();
            }
            limit.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return NotAsked(target, McpServerTools.TimedOut, $"The server did not answer within {Deadline.TotalSeconds:0.#} seconds.");
        }
        catch (TimeoutException)
        {
            return NotAsked(target, McpServerTools.TimedOut, $"The server did not answer within {Deadline.TotalSeconds:0.#} seconds.");
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return NotAsked(target, McpServerTools.Refused,
                $"The server refused the request (HTTP {(int)ex.StatusCode.Value}). It may need a header this endpoint does not have.");
        }
        catch (HttpRequestException ex) when (ex.StatusCode is { } status)
        {
            return NotAsked(target, McpServerTools.Error, $"The server answered HTTP {(int)status}.");
        }
        catch (HttpRequestException)
        {
            return NotAsked(target, McpServerTools.Unreachable, "Nothing answered at this address from the orchestrator.");
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return NotAsked(target, McpServerTools.Error, "The address did not answer as an MCP server.");
        }
    }

    private sealed class SavedOriginHandler(
        McpListTarget target, CancellationToken budget, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri;
            if (uri is null || !uri.IsAbsoluteUri
                || !string.Equals(uri.Scheme, target.Url.Scheme, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(uri.IdnHost, target.Url.IdnHost, StringComparison.OrdinalIgnoreCase)
                || uri.Port != target.Url.Port)
                throw new HttpRequestException("The server requested a different origin.");

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, budget);
            linked.Token.ThrowIfCancellationRequested();
            foreach (var (name, value) in target.Headers)
                request.Headers.TryAddWithoutValidation(name, value);
            return await base.SendAsync(request, linked.Token);
        }
    }

    private static McpServerTools NotAsked(McpListTarget target, string status, string reason) =>
        McpServerTools.NotAsked(target.Name, status, reason);

    private static string? Bounded(string? description)
    {
        var text = description?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        return text.Length <= MaxDescriptionChars ? text : text[..MaxDescriptionChars] + "…";
    }
}
