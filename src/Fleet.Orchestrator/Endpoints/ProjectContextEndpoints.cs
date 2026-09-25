using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Endpoints;

/// <summary>
/// The <c>/api/project-contexts</c> surface for FULL contexts: list, detail, create, new version,
/// rollback and toggle-active. The card and route routes live in <see cref="ProjectCardEndpoints"/>.
/// </summary>
/// <remarks>
/// Lifted out of <c>Program.cs</c> (#347) — the output-style precedent — so the endpoint tests map
/// the SAME handlers a live orchestrator maps. The handlers are as before plus the #347 additions:
/// the card fields on list and detail, keep-marker validation on create and new version, the
/// <c>card</c> block on a full write when the project has a card, and the rollback warning. #346
/// adds the <c>size</c> report (<see cref="PromptSizePolicy"/>) to the three writes, after the commit.
/// Writes are gated by the method-based bearer middleware (<see cref="OrchestratorAuth"/>).
/// </remarks>
public static class ProjectContextEndpoints
{
    private const int MaxVersions = 20;

    public static IEndpointRouteBuilder MapProjectContextEndpoints(this IEndpointRouteBuilder app)
    {
        // REST: list all project contexts with version summary + agent assignments
        app.MapGet("/api/project-contexts", async (IServiceScopeFactory scopeFactory) =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
            if (db is null)
                return Results.Problem("Database is not configured on this orchestrator");

            var contexts = await db.ProjectContexts
                .AsNoTracking()
                .OrderBy(p => p.Name)
                .Select(p => new
                {
                    p.Id,
                    p.Name,
                    p.CurrentVersion,
                    p.IsActive,
                    p.CurrentCardVersion,
                    TotalVersions = db.ProjectContextVersions.Count(v => v.ProjectContextId == p.Id),
                })
                .ToListAsync();

            // BasedOnFullVersion of each current card, for cardStale. Content is not needed here.
            var cardBases = await db.ProjectContextCardVersions
                .AsNoTracking()
                .Select(v => new { v.ProjectContextId, v.VersionNumber, v.BasedOnFullVersion })
                .ToListAsync();

            // Agent assignments via agent_projects (name-keyed join)
            var agents = await db.Agents
                .Include(a => a.Projects)
                .AsNoTracking()
                .ToListAsync();

            return Results.Ok(contexts.Select(p =>
            {
                var card = p.CurrentCardVersion is { } cv
                    ? cardBases.FirstOrDefault(v => v.ProjectContextId == p.Id && v.VersionNumber == cv)
                    : null;

                return new
                {
                    p.Name,
                    p.CurrentVersion,
                    p.IsActive,
                    p.TotalVersions,
                    Agents = agents
                        .Where(a => a.Projects.Any(pr => pr.ProjectName.Equals(p.Name, StringComparison.OrdinalIgnoreCase)))
                        .Select(a => a.Name)
                        .OrderBy(n => n),
                    CardVersion = card?.VersionNumber,
                    CardStale = card is not null && ProjectCardService.IsStale(card.BasedOnFullVersion, p.CurrentVersion),
                    // The holders to reprovision after a card edit or rollback.
                    CardAssignments = agents
                        .Where(a => a.Projects.Any(pr =>
                            pr.ProjectName.Equals(p.Name, StringComparison.OrdinalIgnoreCase) &&
                            pr.ContextMode == ProjectContextMode.Card))
                        .Select(a => a.Name)
                        .OrderBy(n => n, StringComparer.Ordinal)
                        .ToList(),
                };
            }));
        });

        // REST: get full project context with all versions and content, its card and its routes
        app.MapGet("/api/project-contexts/{name}", async (string name, IServiceScopeFactory scopeFactory) =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
            if (db is null)
                return Results.Problem("Database is not configured on this orchestrator");

            var ctx = await db.ProjectContexts
                .Include(p => p.Versions.OrderByDescending(v => v.VersionNumber))
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Name == name);

            if (ctx is null)
                return Results.NotFound(new { error = $"Project context '{name}' not found" });

            var cardVersions = await db.ProjectContextCardVersions
                .AsNoTracking()
                .Where(v => v.ProjectContextId == ctx.Id)
                .OrderByDescending(v => v.VersionNumber)
                .ToListAsync();

            var routes = await ProjectCardService.ListRoutesAsync(db, ctx.Id);

            var fullContent = ctx.Versions.FirstOrDefault(v => v.VersionNumber == ctx.CurrentVersion)?.Content ?? "";
            var currentCard = ctx.CurrentCardVersion is { } cv
                ? cardVersions.FirstOrDefault(v => v.VersionNumber == cv)
                : null;
            var state = currentCard is null
                ? null
                : ProjectCardService.Evaluate(
                    currentCard.VersionNumber, currentCard.BasedOnFullVersion, currentCard.Content,
                    ctx.CurrentVersion, fullContent);

            return Results.Ok(new
            {
                ctx.Name,
                ctx.CurrentVersion,
                Versions = ctx.Versions.Select(v => new
                {
                    v.VersionNumber,
                    v.Content,
                    CreatedAt = v.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                    v.CreatedBy,
                    v.Reason,
                }),
                Card = state is null ? null : new
                {
                    state.CurrentVersion,
                    state.BasedOnFullVersion,
                    state.Stale,
                    state.MissingKeeps,
                    state.InvalidKeeps,
                    Versions = cardVersions.Select(v => new
                    {
                        v.VersionNumber,
                        v.BasedOnFullVersion,
                        v.Content,
                        CreatedAt = v.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                        v.CreatedBy,
                        v.Reason,
                    }),
                },
                Routes = routes.Select(r => new { r.Id, Kind = r.SignalKind, Value = r.SignalValue }),
            });
        });

        // REST: create new project context with initial v1 content
        app.MapPost("/api/project-contexts", async (HttpRequest request, IServiceScopeFactory scopeFactory, [FromServices] PromptSizePolicy sizePolicy) =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
            if (db is null)
                return Results.Problem("Database is not configured on this orchestrator");

            var body = await request.ReadFromJsonAsync<ProjectContextCreateRequest>();
            if (body is null || string.IsNullOrWhiteSpace(body.Name) || string.IsNullOrWhiteSpace(body.Content))
                return Results.BadRequest(new { error = "name and content are required" });

            if (!System.Text.RegularExpressions.Regex.IsMatch(body.Name, @"^[a-zA-Z0-9_-]+$"))
                return Results.BadRequest(new { error = "name must contain only letters, digits, hyphens, or underscores" });

            if (InvalidKeeps(body.Content) is { } invalidResult)
                return invalidResult;

            var exists = await db.ProjectContexts.AnyAsync(p => p.Name == body.Name);
            if (exists)
                return Results.Conflict(new { error = $"Project context '{body.Name}' already exists" });

            var ctx = new ProjectContext { Name = body.Name, CurrentVersion = 1 };
            db.ProjectContexts.Add(ctx);
            await db.SaveChangesAsync();

            db.ProjectContextVersions.Add(new ProjectContextVersion
            {
                ProjectContextId = ctx.Id,
                VersionNumber    = 1,
                Content          = body.Content,
                CreatedAt        = DateTime.UtcNow,
                CreatedBy        = body.CreatedBy ?? "dashboard",
                Reason           = "Initial creation",
            });
            await db.SaveChangesAsync();

            var size = sizePolicy.Evaluate(PromptSizeKind.ProjectContext, body.Name, previousContent: null, body.Content);
            sizePolicy.LogWrite(size, "rest", body.Name);
            return Results.Ok(new { message = $"Project context '{body.Name}' created at v1", size });
        });

        // REST: create new version of a project context
        app.MapPost("/api/project-contexts/{name}/versions", async (string name, HttpRequest request, IServiceScopeFactory scopeFactory, [FromServices] PromptSizePolicy sizePolicy) =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
            if (db is null)
                return Results.Problem("Database is not configured on this orchestrator");

            var body = await request.ReadFromJsonAsync<ProjectContextUpdateRequest>();
            if (body is null || string.IsNullOrWhiteSpace(body.Content))
                return Results.BadRequest(new { error = "content is required" });

            var ctx = await db.ProjectContexts
                .Include(p => p.Versions.OrderBy(v => v.VersionNumber))
                .FirstOrDefaultAsync(p => p.Name == name);

            if (ctx is null)
                return Results.NotFound(new { error = $"Project context '{name}' not found" });

            if (InvalidKeeps(body.Content) is { } invalidResult)
                return invalidResult;

            // Captured before Versions.Add so the new row is never mistaken for the previous one.
            var previousContent = CurrentContent(ctx);

            var newVersion = ctx.CurrentVersion + 1;
            ctx.Versions.Add(new ProjectContextVersion
            {
                ProjectContextId = ctx.Id,
                VersionNumber    = newVersion,
                Content          = body.Content,
                CreatedAt        = DateTime.UtcNow,
                CreatedBy        = body.CreatedBy ?? "dashboard",
                Reason           = body.Reason,
            });
            ctx.CurrentVersion = newVersion;
            ctx.UpdatedAt = DateTime.UtcNow;

            var excess = ctx.Versions.Count - MaxVersions;
            if (excess > 0)
                db.ProjectContextVersions.RemoveRange(ctx.Versions.OrderBy(v => v.VersionNumber).Take(excess));

            await db.SaveChangesAsync();

            var size = sizePolicy.Evaluate(PromptSizeKind.ProjectContext, name, previousContent, body.Content);
            sizePolicy.LogWrite(size, "rest", name);

            var message = $"Project context '{name}' updated to v{newVersion}";
            var card = await ProjectCardService.EvaluateCurrentAsync(db, ctx, body.Content);
            return card is null
                ? Results.Ok(new { message, version = newVersion, size })
                : Results.Ok(new { message, version = newVersion, card = CardSummary(card), size });
        });

        // REST: rollback project context to a prior version (creates new version with old content).
        // The recovery path: invalid keep markers in the restored content are allowed, with a Warning.
        app.MapPost("/api/project-contexts/{name}/rollback/{targetVersion:int}", async (string name, int targetVersion, IServiceScopeFactory scopeFactory, ILoggerFactory loggerFactory, [FromServices] PromptSizePolicy sizePolicy) =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
            if (db is null)
                return Results.Problem("Database is not configured on this orchestrator");

            var ctx = await db.ProjectContexts
                .Include(p => p.Versions.OrderBy(v => v.VersionNumber))
                .FirstOrDefaultAsync(p => p.Name == name);

            if (ctx is null)
                return Results.NotFound(new { error = $"Project context '{name}' not found" });

            var target = ctx.Versions.FirstOrDefault(v => v.VersionNumber == targetVersion);
            if (target is null)
                return Results.NotFound(new { error = $"Version {targetVersion} not found" });

            var content = target.Content;
            var previousContent = CurrentContent(ctx);
            var newVersion = ctx.CurrentVersion + 1;
            ctx.Versions.Add(new ProjectContextVersion
            {
                ProjectContextId = ctx.Id,
                VersionNumber    = newVersion,
                Content          = content,
                CreatedAt        = DateTime.UtcNow,
                CreatedBy        = "rollback",
                Reason           = $"rollback to v{targetVersion}",
            });
            ctx.CurrentVersion = newVersion;
            ctx.UpdatedAt = DateTime.UtcNow;

            var excess = ctx.Versions.Count - MaxVersions;
            if (excess > 0)
                db.ProjectContextVersions.RemoveRange(ctx.Versions.OrderBy(v => v.VersionNumber).Take(excess));

            await db.SaveChangesAsync();

            ProjectCardService.WarnOnInvalidFullRollback(
                loggerFactory.CreateLogger(typeof(ProjectContextEndpoints).FullName!), ctx.Name, targetVersion, content);

            var size = sizePolicy.Evaluate(PromptSizeKind.ProjectContext, name, previousContent, content);
            sizePolicy.LogWrite(size, "rest", name);

            var message = $"Rolled back '{name}' to v{targetVersion} content — saved as v{newVersion}";
            var card = await ProjectCardService.EvaluateCurrentAsync(db, ctx, content);
            return card is null
                ? Results.Ok(new { message, version = newVersion, size })
                : Results.Ok(new { message, version = newVersion, card = CardSummary(card), size });
        });

        // REST: toggle active/inactive on a project context
        app.MapPost("/api/project-contexts/{name}/toggle-active", async (string name, ToggleActiveRequest req, IServiceScopeFactory scopeFactory) =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
            if (db is null) return Results.Problem("Database is not configured");
            var ctx = await db.ProjectContexts.FirstOrDefaultAsync(p => p.Name == name);
            if (ctx is null) return Results.NotFound(new { error = $"Project context '{name}' not found" });
            ctx.IsActive = req.IsActive;
            ctx.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(new { ctx.Name, ctx.IsActive });
        });

        return app;
    }

    /// <summary>
    /// 400 naming each invalid keep candidate and the valid grammar, or null when there is none.
    /// The only new rule on full writes, and it only triggers on text containing a keep candidate.
    /// </summary>
    private static IResult? InvalidKeeps(string content)
    {
        var invalid = KeepMarkerParser.Parse(content).Invalid;
        return invalid.Count == 0
            ? null
            : Results.BadRequest(new
            {
                error = $"Project context rejected, {KeepMarkerParser.DescribeInvalid(invalid)} Nothing was saved.",
                invalidKeeps = invalid,
            });
    }

    /// <summary>
    /// Content of the version row <c>CurrentVersion</c> points at, or null when that row is missing —
    /// the size report then has no <c>previousBytes</c>.
    /// </summary>
    private static string? CurrentContent(ProjectContext ctx) =>
        ctx.Versions.FirstOrDefault(v => v.VersionNumber == ctx.CurrentVersion)?.Content;

    private static object CardSummary(ProjectCardState card) => new
    {
        stale = card.Stale,
        basedOnFullVersion = card.BasedOnFullVersion,
        missingKeeps = card.MissingKeeps,
    };
}

public record ProjectContextCreateRequest(string Name, string Content, string? CreatedBy);
public record ProjectContextUpdateRequest(string Content, string? Reason, string? CreatedBy);
