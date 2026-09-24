using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Fleet.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace Fleet.Agent.Services.HostedProviders;

/// <summary>
/// Relays Codex's Responses requests to the hosted vendor unchanged, attaching the subscription key
/// (#335 "Forwarder contract").
/// </summary>
/// <remarks>
/// <para>
/// It exists for key isolation only. Codex never holds the key, so the model's shell cannot read
/// it; the forwarder holds it and swaps it into <c>Authorization</c>. Nothing else changes: the
/// request body is streamed through as bytes with the same <c>Content-Length</c>, every end-to-end
/// header passes with its value (so <c>User-Agent</c> and <c>originator</c> arrive as Codex sent
/// them), and the response status, headers and body come back the same way.
/// </para>
/// <para>
/// A request is relayed only if it carries the per-start token (D10), which only Codex is given.
/// That turns away a plain request from elsewhere in the container. It is a mitigation, not access
/// control: a root process in the container can still obtain the token from Codex.
/// </para>
/// <para>
/// No body, header value, key or token is ever logged (MUST NOT 9, 14).
/// </para>
/// </remarks>
public sealed class HostedProviderForwarder
{
    /// <summary>The header Codex sends the per-start token in, through its provider <c>http_headers</c>.</summary>
    internal const string TokenHeader = "x-phleet-forwarder-token";

    private const int CopyBufferSize = 16 * 1024;

    // Never copied from the inbound request. Authorization is replaced by the key, Host and
    // Content-Length are set on the outbound message, and the rest are hop-by-hop.
    private static readonly HashSet<string> RequestHeadersNotForwarded = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", TokenHeader, "Host", "Content-Length", "Expect",
        "Connection", "Keep-Alive", "TE", "Trailer", "Transfer-Encoding", "Upgrade",
    };

    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "TE", "Trailer", "Transfer-Encoding", "Upgrade",
    };

    private readonly HostedModelProvider _provider;
    private readonly Func<string> _key;
    private readonly byte[] _token;
    private readonly HttpMessageInvoker _upstream;
    private readonly ILogger _logger;
    private readonly Uri _responsesUri;

    internal HostedProviderForwarder(
        HostedModelProvider provider, Func<string> key, string token, HttpMessageInvoker upstream, ILogger logger)
    {
        _provider = provider;
        _key = key;
        _token = Encoding.UTF8.GetBytes(token);
        _upstream = upstream;
        _logger = logger;
        _responsesUri = ResponsesUri(provider);
    }

    /// <summary>The only path the forwarder serves: <c>/{prefix}/responses</c>.</summary>
    internal string RoutePath => $"/{_provider.Prefix}/responses";

    internal static Uri ResponsesUri(HostedModelProvider provider)
    {
        if (provider.Upstream.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException(
                $"Hosted provider '{provider.Prefix}' upstream must be https (MUST NOT 3).");
        return new Uri(provider.Upstream.AbsoluteUri.TrimEnd('/') + "/responses");
    }

    public async Task HandleAsync(HttpContext context)
    {
        var ct = context.RequestAborted;
        var started = Stopwatch.StartNew();
        var status = 0;
        // Counted as bytes are relayed, so an aborted request still logs what reached Codex.
        var responseBytes = new StrongBox<long>();
        try
        {
            status = await ForwardAsync(context, responseBytes, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Codex went away and the upstream request was cancelled through the same token. That
            // is a /cancel, an interrupt or a restart — or, routinely, Codex closing the stream
            // once it has read response.completed, before the upstream's trailing [DONE].
            status = 499;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            // The upstream failed mid-stream, after headers were sent. Codex sees a broken stream
            // and applies its own stream retry budget.
            status = 502;
            _logger.LogWarning(
                "HostedProviderAdapter provider={Provider} upstream stream failed: {Error}",
                _provider.Prefix, ex.GetBaseException().GetType().Name);
            context.Abort();
        }
        finally
        {
            _logger.LogInformation(
                "HostedProviderAdapter provider={Provider} status={Status} durationMs={DurationMs} "
                + "requestBytes={RequestBytes} responseBytes={ResponseBytes}",
                _provider.Prefix, status, started.ElapsedMilliseconds,
                context.Request.ContentLength ?? 0, responseBytes.Value);
        }
    }

    private async Task<int> ForwardAsync(HttpContext context, StrongBox<long> responseBytes, CancellationToken ct)
    {
        var inbound = context.Request;

        // 1. Route. Any other method or path, including Codex's /models refresh, never reaches the
        //    upstream; Codex then falls back to its unknown-model defaults.
        if (!HttpMethods.IsPost(inbound.Method)
            || !string.Equals(inbound.Path.Value, RoutePath, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return StatusCodes.Status404NotFound;
        }

        // 2. Token (D10): exactly one header, compared in constant time.
        var presented = inbound.Headers[TokenHeader];
        if (presented.Count != 1
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented[0] ?? ""), _token))
        {
            return await WriteErrorAsync(context, responseBytes, StatusCodes.Status401Unauthorized,
                "phleet adapter: missing or invalid forwarder token", ct);
        }

        // 3. Framing. The upstream must see the fixed length Codex sent, so a request without one
        //    (chunked) is refused rather than re-framed (MUST NOT 5).
        if (inbound.ContentLength is not { } contentLength || inbound.Headers.ContainsKey("Transfer-Encoding"))
        {
            return await WriteErrorAsync(context, responseBytes, StatusCodes.Status411LengthRequired,
                "phleet adapter: Content-Length required", ct);
        }

        // Kestrel would only notice an oversized body on the first read, after the upstream
        // request had started. Refuse it here, before any upstream call.
        var maxBody = context.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize;
        if (contentLength > maxBody)
        {
            return await WriteErrorAsync(context, responseBytes, StatusCodes.Status413PayloadTooLarge,
                "phleet adapter: request body too large", ct);
        }

        using var request = BuildUpstreamRequest(inbound, contentLength);

        HttpResponseMessage upstream;
        try
        {
            upstream = await _upstream.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException
                                   || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            // DNS, TLS, refused connection, or the handler's ConnectTimeout (which surfaces as a
            // cancellation that is not ours).
            return await WriteErrorAsync(context, responseBytes, StatusCodes.Status502BadGateway,
                $"phleet adapter: upstream {_provider.Prefix} unreachable: {ex.GetBaseException().GetType().Name}", ct);
        }

        using (upstream)
        {
            var status = (int)upstream.StatusCode;
            var response = context.Response;
            response.StatusCode = status;

            var connectionNamed = ConnectionNamedHeaders(upstream.Headers.Connection);
            foreach (var header in upstream.Headers.Concat(upstream.Content.Headers))
            {
                if (HopByHopHeaders.Contains(header.Key)
                    || header.Key.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase)
                    || connectionNamed.Contains(header.Key))
                {
                    continue;
                }
                response.Headers[header.Key] = header.Value.ToArray();
            }

            // Send the status and headers now, as the upstream did, rather than with the first
            // body bytes: an upstream that answers and then streams slowly must not look silent.
            await response.StartAsync(ct);
            await response.Body.FlushAsync(ct);

            // Bytes as they arrive, flushed after every read: an SSE event reaches Codex as soon
            // as the upstream sends it. Never buffered, never parsed (MUST NOT 11). Non-2xx bodies
            // take the same path, so the turn fails with the vendor's own message.
            await using var upstreamBody = await upstream.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[CopyBufferSize];
            int read;
            while ((read = await upstreamBody.ReadAsync(buffer, ct)) > 0)
            {
                await response.Body.WriteAsync(buffer.AsMemory(0, read), ct);
                await response.Body.FlushAsync(ct);
                responseBytes.Value += read;
            }
            return status;
        }
    }

    private HttpRequestMessage BuildUpstreamRequest(HttpRequest inbound, long contentLength)
    {
        // Streamed, not buffered or re-encoded; the inbound length is copied so the upstream sees a
        // fixed-length body, not a chunked one.
        var content = new StreamContent(inbound.Body, CopyBufferSize);
        content.Headers.ContentLength = contentLength;

        var request = new HttpRequestMessage(HttpMethod.Post, _responsesUri) { Content = content };

        var connectionNamed = ConnectionNamedHeaders(inbound.Headers.Connection);
        foreach (var (name, values) in inbound.Headers)
        {
            if (name.StartsWith(':')
                || RequestHeadersNotForwarded.Contains(name)
                || name.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase)
                || connectionNamed.Contains(name))
            {
                continue;
            }

            IEnumerable<string?> headerValues = values;
            if (!request.Headers.TryAddWithoutValidation(name, headerValues))
                content.Headers.TryAddWithoutValidation(name, headerValues);
        }

        // The only credential the upstream sees. An inbound Authorization was dropped above.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key());
        return request;
    }

    /// <summary>The header names listed in a <c>Connection</c> value, which are hop-by-hop too.</summary>
    private static HashSet<string> ConnectionNamedHeaders(IEnumerable<string?> connectionValues)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in connectionValues)
        {
            if (value is null)
                continue;
            foreach (var token in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                names.Add(token);
        }
        return names;
    }

    private static async Task<int> WriteErrorAsync(
        HttpContext context, StrongBox<long> responseBytes, int status, string message, CancellationToken ct)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        var payload = new JsonObject { ["error"] = new JsonObject { ["message"] = message } };
        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        await context.Response.Body.WriteAsync(bytes, ct);
        responseBytes.Value = bytes.Length;
        return status;
    }
}
