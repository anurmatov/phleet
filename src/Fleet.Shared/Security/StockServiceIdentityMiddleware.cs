using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fleet.Shared.Security;

/// <summary>
/// A receiver's own fresh check after identity verification (memory: verified ACL snapshot and
/// endpoint/tool grants). Admit by awaiting <paramref name="proceed"/>; deny by throwing
/// <see cref="StockIdentityDenied"/> or by setting a status without proceeding.
/// </summary>
public delegate Task StockServiceAdmission(HttpContext context, StockEnrollmentAuthority record, string? tool,
    CancellationToken admission, Func<Task> proceed);

/// <summary>
/// Dedicated opt-in receiving boundary for a fixed first-party HTTP MCP service (#492). In this mode
/// legacy/internal routes are inaccessible and a caller-supplied query or <c>X-Fleet-*</c> identity is
/// refused. Only a verified outside assertion, bound to this receiving service and selected by its
/// sandbox hint, supplies the <c>?agent=</c> identity the service's existing tools and checks consume.
/// Sessions/nonces are local to this receiver process; restart requires authenticated initialize.
/// </summary>
public sealed class StockServiceIdentityMiddleware
{
    public const string AuthorityFileKey = "StockOpenShell:AuthorityFile";
    public const string AuthorityDirectoryKey = "StockOpenShell:AuthorityDirectory";

    private readonly string _service;
    private readonly string _authorityFile;
    private readonly string? _authorityDirectory;
    private readonly ILogger? _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _nonces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Owner, DateTimeOffset Expires)> _sessions = new(StringComparer.Ordinal);

    public StockServiceIdentityMiddleware(string service, string authorityFile, string? authorityDirectory = null, ILogger? logger = null)
    {
        if (!StockServices.IsReceiving(service)) throw new ArgumentException("stock_service_unsupported", nameof(service));
        _service = service; _authorityFile = authorityFile; _authorityDirectory = authorityDirectory; _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next, StockServiceAdmission? admit = null)
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
            var assertion = context.Request.Headers[StockRequestProof.Header].FirstOrDefault() ?? throw new StockIdentityDenied("assertion_missing");
            var record = StockAuthoritySource.Resolve(_authorityFile, _authorityDirectory, StockAuthoritySource.SandboxHint(assertion));
            // Memory keeps its fixed read-only tools; another service accepts exactly the record's grants.
            StockMemoryRequest.Validate(context.Request.Method, bytes, session, record.ServiceTools(_service));
            if (context.Request.Host.Host != record.ServiceHost(_service) || context.Request.Host.Port is not (null or 443))
                throw new StockIdentityDenied("receiver_host");
            var proof = StockRequestProof.Verify(record, _service, assertion, context.Request.Method, bytes, session);
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
            string? tool = null;
            if (context.Request.Method == "POST")
            {
                using var rpc = System.Text.Json.JsonDocument.Parse(bytes);
                if (rpc.RootElement.GetProperty("method").GetString() == "tools/call")
                    tool = rpc.RootElement.GetProperty("params").GetProperty("name").GetString();
            }

            async Task Proceed()
            {
                // Attribution comes only from the verified record; the service's own checks run for that agent.
                context.Request.Headers.Remove(StockRequestProof.Header);
                var fixedQuery = _service == StockServices.Memory ? "" : record.Services![_service].Query;
                context.Request.QueryString = QueryString.Create("agent", record.Agent)
                    + (fixedQuery == "" ? QueryString.Empty : new QueryString("?" + fixedQuery));
                context.Request.Body = new MemoryStream(bytes, writable: false);
                if (session is null)
                {
                    context.Response.OnStarting(() =>
                    {
                        var current = StockAuthoritySource.Resolve(_authorityFile, _authorityDirectory, record.SandboxId);
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

            if (admit is null) await Proceed();
            else await admit(context, record, tool, admission.Token, Proceed);
        }
        catch (Exception ex) when (ex is StockIdentityDenied or System.Text.Json.JsonException or KeyNotFoundException
            or InvalidOperationException or OperationCanceledException or IOException)
        {
            if (!context.Response.HasStarted) context.Response.StatusCode = 403;
            _logger?.LogWarning("Stock {Service} request denied: {Reason}", _service,
                ex is StockIdentityDenied ? ex.Message : "request_unavailable");
            // No assertion/body/config value or arbitrary exception message is logged.
        }
    }
}

public static class StockServiceIdentityExtensions
{
    /// <summary>
    /// Engages the dedicated receiver exactly like the memory receiver: only when the startup-only
    /// <c>StockOpenShell:AuthorityFile</c> is set (fully qualified), with optional per-agent selection
    /// by <c>StockOpenShell:AuthorityDirectory</c>. Unset changes nothing. Returns true when engaged,
    /// so the caller maps MCP only at the dedicated <c>/mcp</c> path.
    /// </summary>
    public static bool UseStockServiceIdentity(this IApplicationBuilder app, IConfiguration configuration, string service,
        StockServiceAdmission? admit = null)
    {
        var file = configuration[StockServiceIdentityMiddleware.AuthorityFileKey];
        if (string.IsNullOrEmpty(file)) return false;
        if (!Path.IsPathFullyQualified(file)) throw new InvalidOperationException($"stock_{service}_requires_authority");
        var receiver = new StockServiceIdentityMiddleware(service, file, configuration[StockServiceIdentityMiddleware.AuthorityDirectoryKey],
            app.ApplicationServices.GetService<ILoggerFactory>()?.CreateLogger<StockServiceIdentityMiddleware>());
        app.Use(next => context => receiver.InvokeAsync(context, next, admit));
        return true;
    }
}
