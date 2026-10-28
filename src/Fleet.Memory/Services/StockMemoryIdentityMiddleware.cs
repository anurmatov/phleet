using Fleet.Shared.Security;
using Microsoft.AspNetCore.Http.Features;

namespace Fleet.Memory.Services;

/// <summary>
/// Dedicated opt-in receiving boundary. In this mode legacy/internal routes are inaccessible,
/// and only a verified outside assertion supplies the query identity consumed by existing tools.
/// Sessions/nonces are local to this receiver process; restart requires authenticated initialize.
/// </summary>
public sealed class StockMemoryIdentityMiddleware(string authorityFile, RequestDelegate next,
    ILogger<StockMemoryIdentityMiddleware>? logger = null, string? authorityDirectory = null)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _nonces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Owner, DateTimeOffset Expires)> _sessions = new(StringComparer.Ordinal);

    public async Task InvokeAsync(HttpContext context, AclCacheService acl)
    {
        if (context.Request.Path == "/health") { await next(context); return; }
        try
        {
            if (!context.Request.IsHttps || context.Features.Get<IHttpRequestFeature>()?.RawTarget != "/mcp"
                || context.Request.Path != "/mcp" || context.Request.QueryString.HasValue
                || context.Request.Headers.ContainsKey("Authorization")
                || context.Request.Headers[StockRequestProof.Header].Count != 1
                || context.Request.Headers["Mcp-Session-Id"].Count > 1
                || context.Request.Headers.Keys.Any(h => h.StartsWith("X-Fleet-", StringComparison.OrdinalIgnoreCase)
                    && !h.Equals(StockRequestProof.Header, StringComparison.OrdinalIgnoreCase)))
                throw new StockIdentityDenied("receiver_scope");
            using var admission = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            admission.CancelAfter(TimeSpan.FromSeconds(2));
            using var body = new MemoryStream();
            var buffer = new byte[8192]; int count;
            while ((count = await context.Request.Body.ReadAsync(buffer, admission.Token)) > 0)
            {
                if (body.Length + count > 262144) throw new StockIdentityDenied("request_size");
                body.Write(buffer, 0, count);
            }
            var bytes = body.ToArray();
            var session = context.Request.Headers["Mcp-Session-Id"].FirstOrDefault();
            StockMemoryRequest.Validate(context.Request.Method, bytes, session);
            var assertion = context.Request.Headers[StockRequestProof.Header].FirstOrDefault() ?? throw new StockIdentityDenied("assertion_missing");
            var record = StockAuthoritySource.Resolve(authorityFile, authorityDirectory, StockAuthoritySource.SandboxHint(assertion));
            if (context.Request.Host.Host != record.MemoryHost || context.Request.Host.Port is not (null or 443))
                throw new StockIdentityDenied("receiver_host");
            var proof = StockRequestProof.Verify(record, context.Request.Headers[StockRequestProof.Header][0]!,
                context.Request.Method, bytes, session);
            var owner = $"{record.Agent}/{record.SandboxId}/{record.Epoch:D}";
            lock (_gate)
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var key in _nonces.Where(p => p.Value <= now.ToUnixTimeSeconds()).Select(p => p.Key).ToArray()) _nonces.Remove(key);
                foreach (var key in _sessions.Where(p => p.Value.Expires <= now).Select(p => p.Key).ToArray()) _sessions.Remove(key);
                if (_nonces.Count >= 4096 || !_nonces.TryAdd(proof.Nonce, proof.Expires)) throw new StockIdentityDenied("assertion_replayed");
                if (session is not null)
                {
                    if (!_sessions.TryGetValue(session, out var existing)) { context.Response.StatusCode = 404; return; }
                    if (existing.Owner != owner) throw new StockIdentityDenied("session_owner");
                }
                else if (_sessions.Count >= 1024) throw new StockIdentityDenied("session_capacity");
            }
            // Unlike the legacy cache policy, this boundary never admits on a failed/stale fetch.
            var snapshot = acl.IsAclEnabled ? await acl.LoadVerifiedSnapshotAsync(admission.Token) : null;
            if (snapshot is null) { context.Response.StatusCode = 503; return; }
            using var aclScope = acl.UseVerifiedSnapshot(snapshot);
            string? tool = null;
            if (context.Request.Method == "POST")
            {
                using var rpc = System.Text.Json.JsonDocument.Parse(bytes);
                if (rpc.RootElement.GetProperty("method").GetString() == "tools/call")
                    tool = rpc.RootElement.GetProperty("params").GetProperty("name").GetString();
            }
            var grants = await acl.CanUseVerifiedMemoryRouteAsync(record.Agent, record.MemoryHost, tool, admission.Token);
            if (!grants.Available) { context.Response.StatusCode = 503; return; }
            if (!grants.Allowed) throw new StockIdentityDenied("route_or_tool_not_granted");
            context.Request.Headers.Remove(StockRequestProof.Header);
            context.Request.QueryString = QueryString.Create("agent", record.Agent);
            context.Request.Body = new MemoryStream(bytes, writable: false);
            if (session is null)
            {
                context.Response.OnStarting(() =>
                {
                    var current = StockAuthoritySource.Resolve(authorityFile, authorityDirectory, record.SandboxId);
                    if (current.Agent != record.Agent || current.SandboxId != record.SandboxId || current.Epoch != record.Epoch)
                        throw new StockIdentityDenied("session_enrollment_changed");
                    var created = context.Response.Headers["Mcp-Session-Id"];
                    if (context.Response.StatusCode is >= 200 and < 300 && created.Count == 1 && created[0] is { Length: > 0 and <= 256 } id)
                    {
                        lock (_gate)
                        {
                            if (_sessions.Count >= 1024 || !_sessions.TryAdd(id, (owner, DateTimeOffset.UtcNow.AddHours(1))))
                                throw new StockIdentityDenied("session_collision");
                        }
                    }
                    return Task.CompletedTask;
                });
            }
            // Admission's two seconds do not become the upstream execution/header deadline.
            // The existing MCP host owns execution cancellation. No automatic request retry.
            await next(context);
            if (session is not null && context.Request.Method == "DELETE" && context.Response.StatusCode is >= 200 and < 300)
                lock (_gate) _sessions.Remove(session);
        }
        catch (Exception ex) when (ex is StockIdentityDenied or System.Text.Json.JsonException or KeyNotFoundException
            or InvalidOperationException or OperationCanceledException or IOException)
        {
            if (!context.Response.HasStarted) context.Response.StatusCode = 403;
            logger?.LogWarning("Stock memory request denied: {Reason}",
                ex is StockIdentityDenied ? ex.Message : "request_unavailable");
            // No assertion/body/config value or arbitrary exception message is logged.
        }
    }
}
