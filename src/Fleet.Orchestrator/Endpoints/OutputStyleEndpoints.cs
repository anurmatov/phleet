using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Endpoints;

/// <summary>
/// The <c>/api/output-styles</c> surface (#317).
/// </summary>
/// <remarks>
/// <para>
/// Lifted out of <c>Program.cs</c> verbatim so a test host can map the SAME handlers a live
/// orchestrator maps. Re-declaring the routes in a test would prove only that the test's copy
/// behaves; a revert of the real ones would leave it green.
/// </para>
/// <para>
/// GETs are unauthenticated like the other <c>/api/*</c> reads; the mutating methods land on the
/// existing bearer middleware, which is method-based — see <see cref="OrchestratorAuth"/>. There
/// is no auth code here, and adding some would be a second answer to who may write.
/// </para>
/// </remarks>
public static class OutputStyleEndpoints
{
    public static IEndpointRouteBuilder MapOutputStyleEndpoints(this IEndpointRouteBuilder app)
    {
        // List the named output styles an agent can be switched to, with their bodies.
        // Read-only and unauthenticated — the dashboard populates its output-style select from
        // this, and a select with nothing in it is the same as no control.
        //
        // `agents` is the reverse of the picker's link: anyone editing a style is about to change
        // the voice of every agent on it, and that is the first thing they need to know.
        app.MapGet("/api/output-styles", async (IServiceScopeFactory scopeFactory, CancellationToken ct) =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
            if (db is null)
                return Results.Problem("Database is not configured on this orchestrator");

            var styles = await db.OutputStyles
                .AsNoTracking()
                .OrderBy(s => s.Name)
                .Select(s => new { s.Name, s.Description, s.Body })
                .ToListAsync(ct);

            var usage = await OutputStyleUsage.ByStyleAsync(db, ct);

            return Results.Ok(styles.Select(s => new
            {
                s.Name,
                s.Description,
                s.Body,
                Agents = OutputStyleUsage.For(usage, s.Name),
            }));
        });

        // One style with its full body — what the operator is about to impose on every message the
        // assigned agents send.
        app.MapGet("/api/output-styles/{name}", async (string name, IServiceScopeFactory scopeFactory, CancellationToken ct) =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
            if (db is null)
                return Results.Problem("Database is not configured on this orchestrator");

            var style = await db.OutputStyles.AsNoTracking().FirstOrDefaultAsync(s => s.Name == name, ct);
            if (style is null)
                return Results.NotFound(new { error = $"Output style '{name}' not found" });

            var usage = await OutputStyleUsage.ByStyleAsync(db, ct);

            return Results.Ok(new
            {
                style.Name,
                style.Description,
                style.Body,
                Agents = OutputStyleUsage.For(usage, style.Name),
            });
        });

        // Create a style. Bearer-gated by the mutating-method middleware like every other non-GET.
        //
        // Description is DERIVED from the frontmatter rather than accepted as its own field: two
        // places to say what a style is for is two places to disagree, and the one the operator
        // reads in the list would not be the one Claude Code obeys.
        app.MapPost("/api/output-styles", async (OutputStyleCreateRequest? body, IServiceScopeFactory scopeFactory, CancellationToken ct) =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
            if (db is null)
                return Results.Problem("Database is not configured on this orchestrator");

            var name = body?.Name?.Trim() ?? "";
            var error = OutputStyleValidator.Validate(name, body?.Body);
            if (error is not null)
                return Results.BadRequest(new { error });

            if (await db.OutputStyles.AnyAsync(s => s.Name == name, ct))
                return Results.Conflict(new { error = $"Output style '{name}' already exists" });

            var style = new OutputStyle
            {
                Name        = name,
                Body        = body!.Body!,
                Description = OutputStyleRenderer.ReadDescription(body.Body!),
            };
            db.OutputStyles.Add(style);

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // The check above is not atomic with the insert, so two concurrent creates of the
                // same name race: one wins, the other trips the primary key. That is the same
                // condition the check reports, and a 500 would send the operator looking for a
                // broken orchestrator instead of a name already taken.
                //
                // Re-queried rather than matched on a provider error code: the answer is then the
                // same on MySQL and on SQLite, and — the part that matters — a write that failed
                // for any OTHER reason finds no row and is rethrown rather than being reported as
                // a name collision that never happened.
                db.ChangeTracker.Clear();
                if (!await db.OutputStyles.AsNoTracking().AnyAsync(s => s.Name == name, ct))
                    throw;

                return Results.Conflict(new { error = $"Output style '{name}' already exists" });
            }

            return Results.Created($"/api/output-styles/{name}", new
            {
                message = $"Output style '{name}' created",
                style.Name,
                style.Description,
            });
        });

        // Replace a style's body. The name is NOT editable — it is the value agents.OutputStyle
        // holds, and renaming the row orphans every agent pointing at it. Rename is create +
        // reassign + delete.
        app.MapPut("/api/output-styles/{name}", async (string name, OutputStyleUpdateRequest? body, IServiceScopeFactory scopeFactory, CancellationToken ct) =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
            if (db is null)
                return Results.Problem("Database is not configured on this orchestrator");

            var style = await db.OutputStyles.FirstOrDefaultAsync(s => s.Name == name, ct);
            if (style is null)
                return Results.NotFound(new { error = $"Output style '{name}' not found" });

            var error = OutputStyleValidator.ValidateBody(style.Name, body?.Body);
            if (error is not null)
                return Results.BadRequest(new { error });

            style.Body        = body!.Body!;
            style.Description = OutputStyleRenderer.ReadDescription(body.Body!);
            await db.SaveChangesAsync(ct);

            var usage = await OutputStyleUsage.ByStyleAsync(db, ct);
            var assigned = OutputStyleUsage.For(usage, style.Name);

            return Results.Ok(new
            {
                // The body reaches an agent at provision time and no sooner, so say so rather than
                // letting a green save imply a live edit took effect.
                message = assigned.Count == 0
                    ? $"Output style '{name}' updated. No agent is assigned to it."
                    : $"Output style '{name}' updated. Reprovision to apply: {string.Join(", ", assigned)}",
                style.Name,
                style.Description,
                Agents = assigned,
            });
        });

        // Delete a style, refusing while any agent still names it.
        //
        // A cascade would leave those agents writing an `outputStyle` into settings.json that
        // resolves to nothing, and Claude Code then reports the configured name in system/init
        // while running on the default — a silent degrade. Refuse and name the agents instead.
        app.MapDelete("/api/output-styles/{name}", async (string name, IServiceScopeFactory scopeFactory, CancellationToken ct) =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
            if (db is null)
                return Results.Problem("Database is not configured on this orchestrator");

            var style = await db.OutputStyles.FirstOrDefaultAsync(s => s.Name == name, ct);
            if (style is null)
                return Results.NotFound(new { error = $"Output style '{name}' not found" });

            var assigned = await OutputStyleUsage.AgentsUsingAsync(db, style.Name, ct);
            if (assigned.Count > 0)
            {
                return Results.Conflict(new
                {
                    error = $"Output style '{name}' is in use by {assigned.Count} agent(s): {string.Join(", ", assigned)}. "
                          + "Clear the style on those agents first.",
                    agents = assigned,
                });
            }

            db.OutputStyles.Remove(style);
            await db.SaveChangesAsync(ct);

            return Results.Ok(new { message = $"Output style '{name}' deleted" });
        });

        return app;
    }
}

/// <summary>Create payload. <c>Description</c> is derived from the body, never accepted.</summary>
public record OutputStyleCreateRequest(string? Name, string? Body);

/// <summary>Update payload. The name comes from the route and is not editable.</summary>
public record OutputStyleUpdateRequest(string? Body);
