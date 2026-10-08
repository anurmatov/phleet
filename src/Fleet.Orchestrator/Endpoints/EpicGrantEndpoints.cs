using System.Text;
using System.Text.Json;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;

namespace Fleet.Orchestrator.Endpoints;

/// <summary>
/// The <c>/api/epic-grants</c> surface (#436).
/// </summary>
/// <remarks>
/// <para>
/// GETs are unauthenticated like the other <c>/api/*</c> reads; every POST lands on the existing
/// method-based bearer middleware (<see cref="OrchestratorAuth"/>). Create and revoke are dashboard
/// actions by convention, the decisions route is called by the Temporal bridge only, and no MCP
/// tool reaches any of them.
/// </para>
/// <para>
/// There is deliberately no PUT, PATCH or DELETE: a grant's scope is immutable, and changing it
/// means revoking and creating a new one. Decision rows are never deleted.
/// </para>
/// </remarks>
public static class EpicGrantEndpoints
{
    private static readonly JsonSerializerOptions DecisionJson = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapEpicGrantEndpoints(this IEndpointRouteBuilder app)
    {
        // Every grant, newest first.
        app.MapGet("/api/epic-grants", async (IServiceScopeFactory scopes, CancellationToken ct) =>
        {
            using var scope = scopes.CreateScope();
            if (Service(scope) is not { } service) return NoDatabase();
            return Results.Ok(await service.ListAsync(ct));
        });

        // One grant with its parsed scope and its decision rows.
        app.MapGet("/api/epic-grants/{id}", async (string id, IServiceScopeFactory scopes, CancellationToken ct) =>
        {
            using var scope = scopes.CreateScope();
            if (Service(scope) is not { } service) return NoDatabase();
            var grant = await service.GetAsync(id, ct);
            return grant is null
                ? Results.NotFound(new { error = $"epic grant '{id}' not found" })
                : Results.Ok(grant);
        });

        // Dry run of creation: every check, nothing stored. 200 whenever the body is JSON.
        app.MapPost("/api/epic-grants/validate", async (HttpRequest request, IServiceScopeFactory scopes, CancellationToken ct) =>
        {
            using var scope = scopes.CreateScope();
            if (Service(scope) is not { } service) return NoDatabase();
            var body = await ReadBodyAsync(request, ct);
            if (body is null || !EpicScopeParser.IsJson(body))
                return Results.BadRequest(NotJson(body));
            return Results.Ok(await service.ValidateAsync(body, ct));
        });

        // Create and activate in one human action. The body text is stored verbatim.
        app.MapPost("/api/epic-grants", async (HttpRequest request, IServiceScopeFactory scopes, CancellationToken ct) =>
        {
            using var scope = scopes.CreateScope();
            if (Service(scope) is not { } service) return NoDatabase();
            if (!service.IsEnabled)
                return Results.Json(new { error = "epic grants are disabled" }, statusCode: StatusCodes.Status503ServiceUnavailable);

            var body = await ReadBodyAsync(request, ct);
            if (body is null || !EpicScopeParser.IsJson(body))
                return Results.BadRequest(NotJson(body));

            var created = await service.CreateAsync(body, ct);
            return created.Grant is { } grant
                ? Results.Created($"/api/epic-grants/{grant.Id}", grant)
                : Results.BadRequest(created.Report);
        });

        // Revoke: stops every decision whose reservation commits after it.
        app.MapPost("/api/epic-grants/{id}/revoke", async (string id, HttpRequest request, IServiceScopeFactory scopes, CancellationToken ct) =>
        {
            using var scope = scopes.CreateScope();
            if (Service(scope) is not { } service) return NoDatabase();

            string? reason = null;
            var body = await ReadBodyAsync(request, ct);
            if (!string.IsNullOrWhiteSpace(body))
            {
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.ValueKind != JsonValueKind.Object)
                        return Results.BadRequest(new { error = "body must be a JSON object" });
                    if (doc.RootElement.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String)
                        reason = r.GetString();
                }
                catch (JsonException)
                {
                    return Results.BadRequest(new { error = "body is not valid JSON" });
                }
            }

            var (outcome, grant) = await service.RevokeAsync(id, reason, ct);
            return outcome switch
            {
                EpicGrantRevokeOutcome.Revoked => Results.Ok(grant),
                EpicGrantRevokeOutcome.AlreadyRevoked => Results.Conflict(new { error = "epic grant is already revoked", grant }),
                _ => Results.NotFound(new { error = $"epic grant '{id}' not found" }),
            };
        });

        // Bridge forward of the configured CTO's delegated approval. D1–D11, then one signal.
        app.MapPost("/api/epic-grants/{id}/decisions", async (string id, HttpRequest request, IServiceScopeFactory scopes, CancellationToken ct) =>
        {
            using var scope = scopes.CreateScope();
            if (Service(scope) is not { } service)
                return DecisionResponse(EpicGrantDecisionResult.Refused(EpicGrantRefusal.Unknown));

            EpicGrantDecisionRequest? decision = null;
            var body = await ReadBodyAsync(request, ct);
            if (body is not null)
            {
                try
                {
                    decision = JsonSerializer.Deserialize<EpicGrantDecisionRequest>(body, DecisionJson);
                }
                catch (JsonException)
                {
                    decision = null;
                }
            }

            if (decision is null)
            {
                var refused = service.RefuseUnparsable(id);
                return Results.Json(new { result = refused.Result, reason = refused.Reason },
                    statusCode: StatusCodes.Status400BadRequest);
            }

            return DecisionResponse(await service.DecideAsync(id, decision, ct));
        });

        return app;
    }

    private static EpicGrantService? Service(IServiceScope scope) =>
        scope.ServiceProvider.GetService<OrchestratorDbContext>() is null
            ? null
            : scope.ServiceProvider.GetRequiredService<EpicGrantService>();

    private static IResult NoDatabase() => Results.Problem("Database is not configured on this orchestrator");

    private static IResult DecisionResponse(EpicGrantDecisionResult result) => result.Result switch
    {
        EpicGrantDecisionResults.Sent =>
            Results.Json(new { result = result.Result, decisionId = result.DecisionId, reason = (string?)null }),
        EpicGrantDecisionResults.SendFailed =>
            Results.Json(new { result = result.Result, decisionId = result.DecisionId, reason = result.Reason },
                statusCode: StatusCodes.Status502BadGateway),
        _ => Results.Json(new { result = result.Result, reason = result.Reason },
                statusCode: StatusCodes.Status409Conflict),
    };

    private static ScopeValidationReport NotJson(string? body)
    {
        var report = new ScopeValidationReport { Valid = false };
        report.Errors.Add(body is null
            ? $"scope body is larger than {EpicGrantService.MaxScopeBytes} bytes"
            : "scope body is not valid JSON");
        return report;
    }

    /// <summary>The request body as UTF-8 text; null when it exceeds <see cref="EpicGrantService.MaxScopeBytes"/>.</summary>
    private static async Task<string?> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength > EpicGrantService.MaxScopeBytes) return null;
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > EpicGrantService.MaxScopeBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false)
            .GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
