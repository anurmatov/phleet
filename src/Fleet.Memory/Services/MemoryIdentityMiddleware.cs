using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fleet.Memory.Configuration;
using Fleet.Shared.Security;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace Fleet.Memory.Services;

public sealed class MemoryIdentityMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IOptions<FleetIdentityOptions> options,
        MemoryIdentityTrust trust, IVerifiedLaunchReader launches, MemoryIdentitySessions sessions, AclCacheService acl, ILogger<MemoryIdentityMiddleware>? logger = null)
    {
        VerifiedMemoryRequest.Clear(context);
        var request = context.Request;
        if (!options.Value.Enabled) { await next(context); return; }
        if (context.Connection.LocalPort != 3101)
        {
            if (request.Path.StartsWithSegments("/mcp")) { context.Response.StatusCode = 403; return; }
            await next(context); return;
        }
        if (request.Path != "/mcp") { context.Response.StatusCode = 404; return; }
        if (!request.IsHttps || !trust.AcceptCertificate(await context.Connection.GetClientCertificateAsync(context.RequestAborted)))
        { context.Response.StatusCode = 401; return; }
        if (request.Method == "POST" && !(request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) ?? false))
        { context.Response.StatusCode = 415; return; }
        if (request.Headers.ContainsKey("Upgrade")) { context.Response.StatusCode = 403; return; }
        if (request.Method is not ("POST" or "GET" or "DELETE") || request.Headers.Any(h => h.Key.StartsWith("X-Fleet-", StringComparison.OrdinalIgnoreCase)
                && !h.Key.Equals("X-Fleet-Assertion", StringComparison.OrdinalIgnoreCase))
            || request.Query.Keys.Any(k => new[] { "agent", "identity", "assertion", "launch" }.Contains(k, StringComparer.OrdinalIgnoreCase))
            || request.Headers.ContainsKey("Authorization")) { context.Response.StatusCode = 403; return; }
        var proof = request.Headers["X-Fleet-Assertion"];
        var session = request.Headers["Mcp-Session-Id"];
        if (proof.Count != 1 || string.IsNullOrWhiteSpace(proof[0]) || session.Count > 1
            || session.ToString().Length > 256) { context.Response.StatusCode = 401; return; }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            if (request.ContentLength > 262144) { context.Response.StatusCode = 413; return; }
            request.EnableBuffering(bufferThreshold: 270336, bufferLimit: 270336);
            using var body = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await request.Body.ReadAsync(buffer, deadline.Token)) != 0)
            {
                if (body.Length + read > 262144) { context.Response.StatusCode = 413; return; }
                body.Write(buffer, 0, read);
            }
            request.Body.Position = 0;
            var bytes = body.ToArray();
            if (request.Method != "POST" && bytes.Length != 0) { context.Response.StatusCode = 400; return; }
            var binding = new FleetRequestBinding(request.Method, context.Features.Get<IHttpRequestFeature>()?.RawTarget
                ?? request.Path + request.QueryString, Hash(bytes), Hash(Encoding.UTF8.GetBytes(session.ToString())));
            var (identity, jti) = trust.Assertions.Verify(proof[0]!, binding, "memory", "/mcp");
            using var admission = sessions.TryEnter(identity.LaunchId);
            if (admission is null) { context.Response.StatusCode = 503; return; }
            if (!await launches.IsCurrentAsync(identity, jti, deadline.Token).WaitAsync(deadline.Token))
            { context.Response.StatusCode = 403; return; }
            if (session.Count == 0)
            {
                if (request.Method != "POST") { context.Response.StatusCode = 403; return; }
                using var json = JsonDocument.Parse(bytes);
                if (!json.RootElement.TryGetProperty("method", out var method) || method.GetString() != "initialize")
                { context.Response.StatusCode = 403; return; }
            }
            else if (!sessions.Matches(binding.SessionHash, identity)) { context.Response.StatusCode = 403; return; }
            var snapshot = await acl.GetVerifiedSnapshotAsync(deadline.Token);
            VerifiedMemoryRequest.Set(context, new(identity, snapshot));
            context.Response.OnStarting(() =>
            {
                var returned = context.Response.Headers["Mcp-Session-Id"];
                if (returned.Count != 0 && (returned.Count != 1 || returned.ToString().Length is 0 or > 256
                    || !sessions.Bind(Hash(Encoding.UTF8.GetBytes(returned.ToString())), identity)))
                { context.Response.StatusCode = 503; context.Response.Headers.Remove("Mcp-Session-Id"); }
                return Task.CompletedTask;
            });
            await next(context);
        }
        catch (CryptographicException) { context.Response.StatusCode = 401; }
        catch (JsonException) { context.Response.StatusCode = 400; }
        catch (Exception ex) when (!context.RequestAborted.IsCancellationRequested && ex is not OutOfMemoryException)
        {
            logger?.LogWarning("Verified memory authorization or request dependency unavailable; request denied");
            if (!context.Response.HasStarted) context.Response.StatusCode = 503; else context.Abort();
        }
    }
    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
