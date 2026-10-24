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
        using var admissionDeadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        admissionDeadline.CancelAfter(TimeSpan.FromSeconds(2));
        using var executionDeadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var dispatched = false;
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
            while ((read = await request.Body.ReadAsync(buffer, admissionDeadline.Token)) > 0)
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
            await replay.ConsumeAsync(identity, jti, assertion[0]!, session, admissionDeadline.Token).WaitAsync(admissionDeadline.Token);
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
            admissionDeadline.Token.ThrowIfCancellationRequested();
            // Admission is complete. Never use its timer to cancel already admitted tool work.
            executionDeadline.CancelAfter(TimeSpan.FromSeconds(options.UpstreamTimeoutSeconds));
            dispatched = true;
            using var response = await receiver.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, executionDeadline.Token)
                .WaitAsync(executionDeadline.Token);
            using var sessionDeadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            sessionDeadline.CancelAfter(TimeSpan.FromSeconds(2));
            if (session is null && response.IsSuccessStatusCode)
            {
                if (!response.Headers.TryGetValues("Mcp-Session-Id", out var returned)) throw new IdentityDeniedException("session_missing");
                var sessions = returned.ToArray();
                if (sessions.Length != 1) throw new IdentityDeniedException("session_shape");
                await launches.BindSessionAsync(identity, sessions[0], sessionDeadline.Token).WaitAsync(sessionDeadline.Token);
            }
            else if (response.Headers.TryGetValues("Mcp-Session-Id", out var echoed)
                && (echoed.Count() != 1 || echoed.Single() != session)) throw new IdentityDeniedException("session_changed");
            if (request.Method == "DELETE" && response.IsSuccessStatusCode)
                await launches.EndSessionAsync(identity, session!, sessionDeadline.Token).WaitAsync(sessionDeadline.Token);
            context.Response.StatusCode = (int)response.StatusCode;
            foreach (var name in new[] { "Mcp-Session-Id", "MCP-Protocol-Version", "Cache-Control" })
                if (response.Headers.TryGetValues(name, out var values)) context.Response.Headers[name] = values.ToArray();
            context.Response.ContentType = response.Content.Headers.ContentType?.ToString();
            // SSE remains request-cancelled after headers; finite bodies retain the upstream operation bound.
            var stream = response.Content.Headers.ContentType?.MediaType == "text/event-stream";
            await response.Content.CopyToAsync(context.Response.Body, stream ? context.RequestAborted : executionDeadline.Token);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var upstreamUnconfirmed = dispatched && executionDeadline.IsCancellationRequested && !context.RequestAborted.IsCancellationRequested;
            logger.LogWarning("Identity ingress refused: {Reason}", upstreamUnconfirmed ? "upstream_unconfirmed"
                : ex is IdentitySessionExpiredException ? "session_expired"
                : ex is IdentityDeniedException denied ? denied.Code : "verification_or_dependency");
            if (context.Response.HasStarted || context.RequestAborted.IsCancellationRequested) context.Abort();
            else
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.StatusCode = upstreamUnconfirmed ? 504 : ex is IdentitySessionExpiredException ? 404
                    : ex is IdentityDeniedException or System.Security.Cryptography.CryptographicException ? 403 : 503;
            }
        }
    }
}
