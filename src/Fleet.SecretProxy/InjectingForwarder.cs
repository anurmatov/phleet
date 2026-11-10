using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fleet.Shared;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;

namespace Fleet.SecretProxy;

/// <summary>
/// The whole request path of <c>fleet-secrets</c>: take a request an agent addressed to a bound host,
/// swap each placeholder in the bound header for the real value, and relay it to that same host.
/// </summary>
/// <remarks>
/// <para>
/// The destination is never chosen by the request. The host must be one a binding names, it must be
/// the host the TLS handshake asked for, and the upstream connection goes to that host by name with
/// ordinary certificate validation. A redirect is returned to the agent, never followed.
/// </para>
/// <para>
/// A placeholder is honoured only when all of these hold: it is a grant in the current state, the
/// request comes from that grant's agent address, the host is one of the binding's hosts, and it sits
/// in the binding's header. Anything else is refused before any upstream call. A request that carries
/// no placeholder is relayed unchanged.
/// </para>
/// <para>
/// A secret is never written into a URL or a body, so it cannot be posted somewhere and read back.
/// Bodies are streamed, not inspected. No header value, URL, placeholder or secret is ever logged.
/// </para>
/// </remarks>
internal sealed partial class InjectingForwarder(
    StateStore store, GitHubAppTokens github, HttpMessageInvoker upstream, ILogger<InjectingForwarder> logger)
{
    internal const string ErrorHeader = "X-Fleet-Secrets-Error";

    private const int CopyBufferSize = 16 * 1024;

    // Never copied from the inbound request: Host and Content-Length are set on the outbound
    // message, and the rest are hop-by-hop.
    private static readonly HashSet<string> RequestHeadersNotForwarded = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Expect",
        "Connection", "Keep-Alive", "TE", "Trailer", "Transfer-Encoding", "Upgrade",
    };

    // Not relayed to the agent: hop-by-hop headers, and Alt-Svc, which would invite the client to
    // try another protocol or port that this forwarder does not answer.
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Alt-Svc",
    };

    // A GitHub App placeholder may carry ".{account}" to name the installation, as the agent image's
    // gh wrapper does for commands whose requests do not name the repository owner.
    [GeneratedRegex(@"\G\.([A-Za-z0-9][A-Za-z0-9-]{0,38})(?![A-Za-z0-9_.-])", RegexOptions.CultureInvariant)]
    private static partial Regex OwnerHint();

    private sealed record Outcome(int Status, string? Code, string? Agent, string? Key, string? Detail);

    public async Task HandleAsync(HttpContext context)
    {
        var ct = context.RequestAborted;
        var started = Stopwatch.StartNew();
        var host = context.Request.Host.Host.ToLowerInvariant();
        Outcome outcome;
        try
        {
            outcome = await ForwardAsync(context, host, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            outcome = new Outcome(499, null, null, null, "client_aborted");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            // The upstream failed mid-stream, after headers were sent.
            outcome = new Outcome(502, null, null, null, "upstream_stream_failed:" + ex.GetBaseException().GetType().Name);
            context.Abort();
        }
        catch (Exception ex)
        {
            // Anything else ends here, as a type name: an exception that reached the server's own
            // error logging would be written with its message, which can quote request text.
            outcome = new Outcome(500, null, null, null, "forwarder_error:" + ex.GetType().Name);
            if (context.Response.HasStarted)
                context.Abort();
            else
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        }

        logger.LogInformation(
            "SecretProxy host={Host} method={Method} agent={Agent} key={Key} status={Status} reason={Reason} durationMs={DurationMs}",
            // The host is logged only when it is a bound one; anything else is the client's text.
            store.Current?.IsBoundHost(host) == true ? host : "-",
            HttpMethods.IsGet(context.Request.Method) ? "GET" : HttpMethods.IsPost(context.Request.Method) ? "POST" : "OTHER",
            outcome.Agent ?? "-", outcome.Key ?? "-", outcome.Status,
            outcome.Detail ?? outcome.Code ?? "-", started.ElapsedMilliseconds);
    }

    private async Task<Outcome> ForwardAsync(HttpContext context, string host, CancellationToken ct)
    {
        var inbound = context.Request;

        // 1. The state. Without one nothing is relayed, not even an unauthenticated request.
        var snapshot = store.Current;
        if (snapshot is null)
            return await RefuseAsync(context, 503, "secret_state_unavailable",
                "the forwarder has no readable state; ask the operator to check the orchestrator", ct);

        // 2. The destination: a bound host, and the one the TLS handshake named.
        var sni = context.Features.Get<ITlsHandshakeFeature>()?.HostName;
        if (inbound.Host.Port is not (null or 443)
            || !snapshot.IsBoundHost(host)
            || !string.Equals(sni, host, StringComparison.OrdinalIgnoreCase))
            return await RefuseAsync(context, 421, "secret_host_mismatch",
                "the request host is not the bound host this connection was opened for", ct);

        if (inbound.Headers.ContainsKey("Upgrade") || HttpMethods.IsConnect(inbound.Method) || HttpMethods.IsTrace(inbound.Method))
            return await RefuseAsync(context, 501, "secret_request_unsupported",
                "protocol upgrades, CONNECT and TRACE are not relayed to a bound host", ct);

        var rawTarget = context.Features.Get<IHttpRequestFeature>()?.RawTarget ?? "";
        if (!rawTarget.StartsWith('/')
            || !Uri.TryCreate("https://" + host + rawTarget, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 || uri.UserInfo.Length != 0
            || !string.Equals(uri.Host, host, StringComparison.Ordinal))
            return await RefuseAsync(context, 400, "secret_request_invalid", "the request target is not an origin-form path", ct);

        // 3. Every placeholder in the request, and where it sits.
        var remote = context.Connection.RemoteIpAddress;
        var plan = new Dictionary<string, List<Substitution>>(StringComparer.OrdinalIgnoreCase);
        string? agent = null, key = null;

        if (SecretPlaceholders.FindAll(rawTarget).Count > 0)
            return await RefuseAsync(context, 403, "secret_slot_mismatch",
                "a placeholder may only be sent in its binding's header, never in the URL", ct);

        foreach (var (name, values) in inbound.Headers)
        {
            if (name.StartsWith(':'))
                continue;
            for (var i = 0; i < values.Count; i++)
            {
                var value = values[i] ?? "";
                var basic = TryDecodeBasic(name, value);
                var searched = basic ?? value;
                var found = SecretPlaceholders.FindAll(searched);
                if (found.Count == 0)
                    continue;

                foreach (var placeholder in found.Distinct(StringComparer.Ordinal))
                {
                    var grant = snapshot.FindGrant(SecretPlaceholders.Hash(placeholder));
                    if (grant is null)
                        return await RefuseAsync(context, 403, "secret_grant_unknown",
                            "this placeholder is not a current grant; it was revoked or the agent was reprovisioned", ct,
                            detail: "grant_unknown");
                    if (!StateSnapshot.SourceMatches(grant, remote))
                        // The same answer as an unknown grant: a caller learns nothing about another agent's placeholder.
                        return await RefuseAsync(context, 403, "secret_grant_unknown",
                            "this placeholder is not a current grant; it was revoked or the agent was reprovisioned", ct,
                            detail: "source_mismatch", key: grant.Key);

                    agent ??= grant.Agent;
                    key ??= grant.Key;
                    var binding = snapshot.FindBinding(grant.Key);
                    if (binding is null || !binding.Hosts.Contains(host, StringComparer.Ordinal))
                        return await RefuseAsync(context, 403, "secret_destination_mismatch",
                            "this secret is not bound to this host", ct, agent: grant.Agent, key: grant.Key);
                    if (!string.Equals(binding.Header, name, StringComparison.OrdinalIgnoreCase))
                        return await RefuseAsync(context, 403, "secret_slot_mismatch",
                            "a placeholder may only be sent in its binding's header", ct, agent: grant.Agent, key: grant.Key);

                    if (!plan.TryGetValue(name, out var list))
                        plan[name] = list = [];
                    list.Add(new Substitution(i, basic is not null, placeholder, binding));
                }
            }
        }

        // 4. Resolve the values, before any upstream call.
        var injected = new List<string>();
        var rewritten = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, substitutions) in plan)
        {
            var values = inbound.Headers[name].Select(v => v ?? "").ToArray();
            foreach (var group in substitutions.GroupBy(s => s.ValueIndex))
            {
                var isBasic = group.First().Basic;
                var text = isBasic ? TryDecodeBasic(name, values[group.Key])! : values[group.Key];
                foreach (var substitution in group)
                {
                    var (replaced, fault) = await ReplaceAsync(text, substitution, uri, ct);
                    if (replaced is null)
                        return await RefuseAsync(context, 503, "secret_unavailable",
                            "the secret has no usable value right now; ask the operator to check the binding", ct,
                            agent: agent, key: substitution.Binding.Key, detail: fault);
                    text = replaced.Value.Text;
                    injected.Add(replaced.Value.Secret);
                }
                values[group.Key] = isBasic
                    ? "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(text))
                    : text;
            }
            rewritten[name] = values;
        }

        // 5. Relay.
        using var request = BuildUpstreamRequest(context, uri, rewritten);
        HttpResponseMessage response;
        try
        {
            response = await upstream.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException
                                   || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return await RefuseAsync(context, 502, "secret_upstream_unreachable",
                "the bound host could not be reached", ct, agent: agent, key: key,
                detail: "upstream_unreachable:" + ex.GetBaseException().GetType().Name);
        }

        using (response)
        {
            // A host that echoes the credential back in a header would hand it to the agent.
            if (injected.Count > 0
                && response.Headers.Concat(response.Content.Headers)
                    .Any(h => h.Value.Any(v => injected.Any(secret => v.Contains(secret, StringComparison.Ordinal)))))
                return await RefuseAsync(context, 502, "secret_reflected",
                    "the bound host returned the credential in a response header; the response was withheld", ct,
                    agent: agent, key: key);

            var outbound = context.Response;
            outbound.StatusCode = (int)response.StatusCode;
            var connectionNamed = ConnectionNamedHeaders(response.Headers.Connection);
            foreach (var header in response.Headers.Concat(response.Content.Headers))
            {
                if (HopByHopHeaders.Contains(header.Key)
                    || header.Key.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase)
                    || connectionNamed.Contains(header.Key))
                    continue;
                outbound.Headers[header.Key] = header.Value.ToArray();
            }

            await outbound.StartAsync(ct);
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[CopyBufferSize];
            int read;
            while ((read = await body.ReadAsync(buffer, ct)) > 0)
            {
                await outbound.Body.WriteAsync(buffer.AsMemory(0, read), ct);
                await outbound.Body.FlushAsync(ct);
            }
            return new Outcome((int)response.StatusCode, null, agent, key, injected.Count > 0 ? "injected" : "relayed");
        }
    }

    private sealed record Substitution(int ValueIndex, bool Basic, string Placeholder, SecretProxyBinding Binding);

    /// <summary>
    /// <paramref name="text"/> with every occurrence of the placeholder replaced by the binding's
    /// value, or null with a fixed fault when there is no usable value.
    /// </summary>
    private async Task<((string Text, string Secret)? Replaced, string? Fault)> ReplaceAsync(
        string text, Substitution substitution, Uri uri, CancellationToken ct)
    {
        var binding = substitution.Binding;
        if (binding.Kind == SecretBindingRules.KindStatic)
        {
            if (string.IsNullOrEmpty(binding.Value) || binding.Value == "<secret>")
                return (null, "value_missing");
            return ((text.Replace(substitution.Placeholder, binding.Value, StringComparison.Ordinal), binding.Value), null);
        }

        // GitHub App: each occurrence may name its own installation.
        var result = new StringBuilder(text.Length + 64);
        var position = 0;
        string? last = null;
        while (true)
        {
            var index = text.IndexOf(substitution.Placeholder, position, StringComparison.Ordinal);
            if (index < 0)
                break;
            var end = index + substitution.Placeholder.Length;
            var hint = OwnerHint().Match(text, end);
            var owner = hint.Success ? hint.Groups[1].Value : GitHubAppTokens.OwnerOf(uri);
            var (token, reason) = await github.GetTokenAsync(binding, owner, ct);
            if (token is null)
                return (null, reason);
            result.Append(text, position, index - position).Append(token);
            position = hint.Success ? end + hint.Length : end;
            last = token;
        }
        result.Append(text, position, text.Length - position);
        return last is null ? (null, "value_missing") : ((result.ToString(), last), null);
    }

    /// <summary>The decoded <c>user:password</c> of a Basic <c>Authorization</c> value, or null.</summary>
    private static string? TryDecodeBasic(string headerName, string value)
    {
        if (!string.Equals(headerName, "Authorization", StringComparison.OrdinalIgnoreCase)
            || !value.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value[6..].Trim()));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static HttpRequestMessage BuildUpstreamRequest(
        HttpContext context, Uri uri, Dictionary<string, string[]> rewritten)
    {
        var inbound = context.Request;
        var request = new HttpRequestMessage(HttpMethod.Parse(inbound.Method), uri);

        // Streamed, never buffered. A known length is kept so the upstream sees the same framing.
        if (context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
        {
            var content = new StreamContent(inbound.Body, CopyBufferSize);
            if (inbound.ContentLength is { } length)
                content.Headers.ContentLength = length;
            request.Content = content;
        }

        var connectionNamed = ConnectionNamedHeaders(inbound.Headers.Connection);
        foreach (var (name, values) in inbound.Headers)
        {
            if (name.StartsWith(':')
                || RequestHeadersNotForwarded.Contains(name)
                || name.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase)
                || connectionNamed.Contains(name))
                continue;

            IEnumerable<string?> headerValues = rewritten.TryGetValue(name, out var replaced) ? replaced : values;
            if (!request.Headers.TryAddWithoutValidation(name, headerValues))
                (request.Content ??= new ByteArrayContent([])).Headers.TryAddWithoutValidation(name, headerValues);
        }

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

    /// <summary>A refusal the agent can tell apart from the bound host's own answer.</summary>
    private static async Task<Outcome> RefuseAsync(
        HttpContext context, int status, string code, string message, CancellationToken ct,
        string? agent = null, string? key = null, string? detail = null)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        context.Response.Headers[ErrorHeader] = code;
        var payload = new JsonObject
        {
            ["error"] = new JsonObject { ["code"] = code, ["message"] = "fleet-secrets: " + message },
        };
        await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(payload.ToJsonString()), ct);
        return new Outcome(status, code, agent, key, detail);
    }
}
