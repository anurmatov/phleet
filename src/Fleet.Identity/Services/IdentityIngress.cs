using System.Net;
using Fleet.Identity.Configuration;
using Fleet.Shared.Security;
using Microsoft.AspNetCore.Http.Features;

namespace Fleet.Identity.Services;

public sealed class IdentityIngress(IdentityOptions options, FleetRequestAssertionVerifier verifier,
    LaunchIdentityStore launches, ReplayStore replay, HttpClient receiver, ILogger<IdentityIngress> logger)
{
    public async Task HandleAsync(HttpContext context)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            if (!context.Request.IsHttps || context.Request.Host.Host != options.IngressHost
                || context.Request.Host.Port != options.IngressPort || context.Request.ContentLength > RequestRules.MaxBody)
                throw new IdentityDeniedException("ingress_scope");
            var request = context.Request;
            var headers = request.Headers.SelectMany(h => h.Value.Select(v => new KeyValuePair<string, string>(h.Key, v ?? ""))).ToArray();
            if (headers.Length > 128 || headers.Sum(h => h.Key.Length + h.Value.Length) > 65536)
                throw new IdentityDeniedException("request_headers");
            using var buffered = new MemoryStream(); var buffer = new byte[8192]; int read;
            while ((read = await request.Body.ReadAsync(buffer, deadline.Token)) > 0)
            {
                if (buffered.Length + read > RequestRules.MaxBody) throw new IdentityDeniedException("request_size");
                buffered.Write(buffer, 0, read);
            }
            var body = buffered.ToArray();
            var target = context.Features.Get<IHttpRequestFeature>()?.RawTarget ?? request.Path + request.QueryString;
            RequestRules.Validate(request.Method, target, body, headers, true);
            var assertion = request.Headers["X-Fleet-Assertion"];
            if (assertion.Count != 1 || string.IsNullOrWhiteSpace(assertion[0])) throw new IdentityDeniedException("assertion_required");
            var session = request.Headers["Mcp-Session-Id"].FirstOrDefault();
            var binding = RequestRules.Bind(request.Method, target, body, session);
            var (identity, jti) = verifier.Verify(assertion[0]!, binding, "memory", "/mcp");
            if (identity.GatewayId != options.GatewayId) throw new IdentityDeniedException("gateway_scope");
            await replay.ConsumeAsync(identity, jti, assertion[0]!, session, deadline.Token).WaitAsync(deadline.Token);
            using var upstream = new HttpRequestMessage(new HttpMethod(request.Method), options.MemoryReceiver);
            if (request.Method == "POST")
            {
                upstream.Content = new ByteArrayContent(body);
                upstream.Content.Headers.ContentType = new("application/json");
            }
            // No caller-selected endpoint, authority, Authorization, forwarding or routing headers.
            foreach (var name in new[] { "Accept", "Mcp-Session-Id", "MCP-Protocol-Version", "Last-Event-ID" })
                if (request.Headers.TryGetValue(name, out var value)) upstream.Headers.TryAddWithoutValidation(name, value.ToArray());
            upstream.Headers.Add("X-Fleet-Assertion", assertion[0]);
            using var response = await receiver.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (session is null && response.IsSuccessStatusCode)
            {
                if (!response.Headers.TryGetValues("Mcp-Session-Id", out var returned)) throw new IdentityDeniedException("session_missing");
                var sessions = returned.ToArray();
                if (sessions.Length != 1) throw new IdentityDeniedException("session_shape");
                await launches.BindSessionAsync(identity, sessions[0], deadline.Token).WaitAsync(deadline.Token);
            }
            else if (response.Headers.TryGetValues("Mcp-Session-Id", out var echoed)
                && (echoed.Count() != 1 || echoed.Single() != session)) throw new IdentityDeniedException("session_changed");
            if (request.Method == "DELETE" && response.IsSuccessStatusCode)
                await launches.EndSessionAsync(identity, session!, deadline.Token).WaitAsync(deadline.Token);
            context.Response.StatusCode = (int)response.StatusCode;
            foreach (var name in new[] { "Mcp-Session-Id", "MCP-Protocol-Version", "Cache-Control" })
                if (response.Headers.TryGetValues(name, out var values)) context.Response.Headers[name] = values.ToArray();
            context.Response.ContentType = response.Content.Headers.ContentType?.ToString();
            // Two seconds bounds authorization and response headers, not a successfully admitted SSE lifetime.
            // Stream responses without buffering; caller disconnect cancels the receiver read as well.
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning("Identity ingress refused: {Reason}", ex is IdentityDeniedException denied ? denied.Code : "verification_or_dependency");
            if (context.Response.HasStarted || context.RequestAborted.IsCancellationRequested) context.Abort();
            else context.Response.StatusCode = ex is IdentityDeniedException or System.Security.Cryptography.CryptographicException ? 403 : 503;
        }
    }
}
