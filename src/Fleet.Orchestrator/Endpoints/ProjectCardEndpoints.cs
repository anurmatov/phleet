using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;

namespace Fleet.Orchestrator.Endpoints;

/// <summary>
/// Project card and route writes (#347): <c>/api/project-contexts/{name}/card/…</c> and
/// <c>/api/project-contexts/{name}/routes…</c>. Reads come back on the context detail
/// (<see cref="ProjectContextEndpoints"/>).
/// </summary>
/// <remarks>
/// The rules live in <see cref="ProjectCardService"/>, shared with the MCP admin tools. The name
/// resolves to the context's <c>Id</c> in C# (<see cref="ProjectCardService.FindContextAsync"/>), so
/// DB collation never decides which context a card or route lands on. Every route here is a write
/// and lands on the method-based bearer middleware (<see cref="OrchestratorAuth"/>); there is no auth
/// code in this file.
/// </remarks>
public static class ProjectCardEndpoints
{
    public static IEndpointRouteBuilder MapProjectCardEndpoints(this IEndpointRouteBuilder app)
    {
        // Write a new card version. Rejected (nothing saved) when blank, when basedOnFullVersion is
        // missing or outside 1..current, when the card has an invalid keep candidate, or when it drops
        // a keep marker of the current full context.
        app.MapPost("/api/project-contexts/{name}/card/versions", async (string name, ProjectCardWriteRequest? body, IServiceScopeFactory scopeFactory, CancellationToken ct) =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
            if (db is null)
                return Results.Problem("Database is not configured on this orchestrator");

            var result = await ProjectCardService.WriteCardAsync(
                db, name, body?.Content, body?.BasedOnFullVersion, body?.Reason, body?.CreatedBy ?? "dashboard", ct);
            return ToResult(result);
        });

        // Roll the card back to a prior version's content and BasedOnFullVersion (a new version).
        // Exempt from the gate — the recovery path.
        app.MapPost("/api/project-contexts/{name}/card/rollback/{targetVersion:int}", async (string name, int targetVersion, IServiceScopeFactory scopeFactory, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
            if (db is null)
                return Results.Problem("Database is not configured on this orchestrator");

            var result = await ProjectCardService.RollbackCardAsync(
                db, name, targetVersion, loggerFactory.CreateLogger(typeof(ProjectCardEndpoints).FullName!), ct);
            return ToResult(result);
        });

        // Add a route. The value is validated and normalised by RouteSignalKind.Normalize.
        app.MapPost("/api/project-contexts/{name}/routes", async (string name, ProjectRouteCreateRequest? body, IServiceScopeFactory scopeFactory, CancellationToken ct) =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
            if (db is null)
                return Results.Problem("Database is not configured on this orchestrator");

            var result = await ProjectCardService.AddRouteAsync(
                db, name, body?.Kind, body?.Value, body?.CreatedBy ?? "dashboard", ct);
            if (result.Status != ProjectCardWriteStatus.Saved || result.Route is not { } route)
                return ToResult(result);

            return Results.Created(
                $"/api/project-contexts/{Uri.EscapeDataString(name)}/routes/{route.Id}",
                new { route.Id, Kind = route.SignalKind, Value = route.SignalValue });
        });

        // Remove a route. 404 also when the id belongs to a different context.
        app.MapDelete("/api/project-contexts/{name}/routes/{id:int}", async (string name, int id, IServiceScopeFactory scopeFactory, CancellationToken ct) =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
            if (db is null)
                return Results.Problem("Database is not configured on this orchestrator");

            var result = await ProjectCardService.RemoveRouteAsync(db, name, id, ct);
            return result.Saved ? Results.NoContent() : ToResult(result);
        });

        return app;
    }

    private static IResult ToResult(ProjectCardWriteResult result) => result.Status switch
    {
        ProjectCardWriteStatus.Saved    => Results.Ok(new { message = result.Message, version = result.Version }),
        ProjectCardWriteStatus.NotFound => Results.NotFound(new { error = result.Message }),
        ProjectCardWriteStatus.Conflict => Results.Conflict(new { error = result.Message }),
        _ => Results.BadRequest(RejectionBody(result)),
    };

    // missingKeeps / invalidKeeps appear only on the rejections that name them, so a client can
    // tell "the gate refused these slugs" from any other 400 by presence alone.
    private static Dictionary<string, object> RejectionBody(ProjectCardWriteResult result)
    {
        var body = new Dictionary<string, object> { ["error"] = result.Message };
        if (result.MissingKeeps is not null) body["missingKeeps"] = result.MissingKeeps;
        if (result.InvalidKeeps is not null) body["invalidKeeps"] = result.InvalidKeeps;
        return body;
    }
}

/// <summary>Card write payload. <c>BasedOnFullVersion</c> has no default — the author must state it.</summary>
public record ProjectCardWriteRequest(string? Content, int? BasedOnFullVersion, string? Reason, string? CreatedBy);

/// <summary>Route payload: <c>kind</c> is repo | workflow | chat.</summary>
public record ProjectRouteCreateRequest(string? Kind, string? Value, string? CreatedBy);
