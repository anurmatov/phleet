using Fleet.Orchestrator.Services;

namespace Fleet.Orchestrator.Endpoints;

/// <summary>
/// The REST surface over the deployment's <c>.env</c>: read chosen values, list every key that is
/// not denylisted, write values, and reload the file.
/// </summary>
/// <remarks>
/// <c>/api/config</c> keeps its own token (<see cref="OrchestratorAuth"/>); the two reads are
/// rate-limited because they return secrets.
/// </remarks>
public static class ConfigEndpoints
{
    public static IEndpointRouteBuilder MapConfigEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /api/config/values?keys=KEY1,KEY2,...
        // Returns literal and agent-derived values for the requested keys.
        // Rate-limited to 10 req/min per bearer token to limit secret enumeration surface.
        app.MapGet("/api/config/values", async (HttpRequest request, ConfigService configService, CancellationToken ct) =>
        {
            var keysParam = request.Query["keys"].FirstOrDefault() ?? "";
            var keys = keysParam
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var result = await configService.GetValuesAsync(keys, ct);
            return Results.Ok(new { literals = result.Literals, agentDerived = result.AgentDerived });
        }).RequireRateLimiting("reveal-rate-limit");

        // GET /api/config/all — returns all non-denylisted .env keys (for dashboard credentials table)
        // Rate-limited to 10 req/min per bearer token to limit secret enumeration surface.
        app.MapGet("/api/config/all", (ConfigService configService) =>
        {
            var all = configService.GetAll();
            return Results.Ok(all);
        }).RequireRateLimiting("reveal-rate-limit");

        // POST /api/config/reload — re-reads .env, diffs, publishes config.changed
        app.MapPost("/api/config/reload", async (ConfigService configService, CancellationToken ct) =>
        {
            var changed = await configService.ReloadAsync(ct);
            return Results.Ok(new { changedKeys = changed, message = $"{changed.Count} key(s) changed, peers notified" });
        });

        // PUT /api/config/values — atomically writes key-value pairs to .env + broadcasts config.changed
        app.MapPut("/api/config/values", async (HttpRequest request, ConfigService configService, CancellationToken ct) =>
        {
            Dictionary<string, string>? kvs;
            try { kvs = await request.ReadFromJsonAsync<Dictionary<string, string>>(ct); }
            catch { return Results.BadRequest(new { error = "invalid_json" }); }

            if (kvs is null || kvs.Count == 0)
                return Results.BadRequest(new { error = "empty_payload" });

            try
            {
                var changed = await configService.PutValuesAsync(kvs, ct);
                return Results.Ok(new { changedKeys = changed, message = $"{changed.Count} key(s) changed, peers notified" });
            }
            catch (DenylistedException ex)
            {
                return Results.Json(new { error = "denylisted", detail = ex.Message }, statusCode: 403);
            }
            catch (TimeoutException ex)
            {
                return Results.Problem(ex.Message, statusCode: 503);
            }
            catch (Exception ex)
            {
                return Results.Problem($"Write failed: {ex.Message}", statusCode: 500);
            }
        });

        return app;
    }
}
