using Fleet.Orchestrator.Auth;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Endpoints;

/// <summary>
/// The memory surface: the dashboard's Memory page, proxied from fleet-memory's <c>/internal/*</c>
/// routes, and the project-access map fleet-memory reads from this service.
/// </summary>
public static class MemoryEndpoints
{
    public static IEndpointRouteBuilder MapMemoryEndpoints(this IEndpointRouteBuilder app)
    {
        var callerAuthenticator = app.ServiceProvider.GetRequiredService<CallerAuthenticator>();

        // GET /internal/agent-project-access — Docker-network-only, no auth (mirrors /internal/* pattern from PR #83).
        // Returns the full ACL map consumed by fleet-memory's AclCacheService.
        app.MapGet("/internal/agent-project-access", async (IServiceScopeFactory scopeFactory, CancellationToken ct) =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var rows = await db.AgentProjectAccess.AsNoTracking().ToListAsync(ct);
            var acl = rows
                .GroupBy(r => r.AgentName)
                .ToDictionary(g => g.Key, g => g.Select(r => r.Project).ToList());
            return Results.Ok(new { acl });
        });

        // ── Memory API (proxied from fleet-memory /internal/*) ───────────────────────
        // All endpoints require bearer-token auth (checked inline since GETs are normally exempt).

        // Any verified caller: the service bearer, a signed-in browser or an enrolled app. With no
        // Orchestrator:AuthToken these routes stay closed, as they always were — memory content is not
        // served by a deployment that gates nothing.
        bool CheckMemoryAuth(HttpRequest req) =>
            callerAuthenticator.IsRequired && CallerIdentity.Of(req.HttpContext).Kind != CallerKind.None;

        app.MapGet("/api/memory", async (HttpRequest req, MemoryProxyService memProxy, CancellationToken ct) =>
        {
            if (!CheckMemoryAuth(req)) return Results.Unauthorized();
            var resp = await memProxy.GetListAsync(ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            return Results.Content(body, "application/json", null, (int)resp.StatusCode);
        });

        app.MapGet("/api/memory/ids", async (HttpRequest req, MemoryProxyService memProxy, CancellationToken ct) =>
        {
            if (!CheckMemoryAuth(req)) return Results.Unauthorized();
            var resp = await memProxy.GetIdsAsync(ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            return Results.Content(body, "application/json", null, (int)resp.StatusCode);
        });

        app.MapGet("/api/memory/search", async (HttpRequest req, string? q, MemoryProxyService memProxy, CancellationToken ct) =>
        {
            if (!CheckMemoryAuth(req)) return Results.Unauthorized();
            var resp = await memProxy.SearchAsync(q ?? "", ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            return Results.Content(body, "application/json", null, (int)resp.StatusCode);
        });

        app.MapGet("/api/memory/stats/reads", async (HttpRequest req, MemoryProxyService memProxy, CancellationToken ct) =>
        {
            if (!CheckMemoryAuth(req)) return Results.Unauthorized();
            var resp = await memProxy.GetReadStatsAsync(ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            return Results.Content(body, "application/json", null, (int)resp.StatusCode);
        });

        app.MapGet("/api/memory/{id}", async (HttpRequest req, string id, MemoryProxyService memProxy, CancellationToken ct) =>
        {
            if (!CheckMemoryAuth(req)) return Results.Unauthorized();
            var resp = await memProxy.GetAsync(id, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            return Results.Content(body, "application/json", null, (int)resp.StatusCode);
        });

        app.MapPut("/api/memory/{id}", async (HttpRequest req, string id, MemoryProxyService memProxy, CancellationToken ct) =>
        {
            // Auth checked by the existing mutating-method middleware
            object? body;
            try { body = await req.ReadFromJsonAsync<object>(ct); }
            catch { return Results.BadRequest(new { error = "invalid_json" }); }
            if (body is null) return Results.BadRequest(new { error = "empty_payload" });

            var resp = await memProxy.UpdateAsync(id, body, ct);
            var respBody = await resp.Content.ReadAsStringAsync(ct);
            return Results.Content(respBody, "application/json", null, (int)resp.StatusCode);
        });

        app.MapDelete("/api/memory/{id}", async (string id, MemoryProxyService memProxy, CancellationToken ct) =>
        {
            // Auth checked by the existing mutating-method middleware
            var resp = await memProxy.DeleteAsync(id, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            return Results.Content(body, "application/json", null, (int)resp.StatusCode);
        });

        return app;
    }
}
