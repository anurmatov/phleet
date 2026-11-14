using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Endpoints;

/// <summary>The REST surface over the repository list: list, create, update, toggle-active and delete.</summary>
public static class RepositoryEndpoints
{
    public static IEndpointRouteBuilder MapRepositoryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/repositories", async (bool? includeInactive, IServiceScopeFactory scopeFactory) =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
            if (db is null) return Results.Problem("Database is not configured on this orchestrator");

            var query = db.Repositories.AsQueryable();
            if (includeInactive != true) query = query.Where(r => r.IsActive);

            var repos = await query.OrderBy(r => r.Name).AsNoTracking().ToListAsync();
            return Results.Ok(repos.Select(r => new { r.Name, r.FullName, r.IsActive, r.CreatedAt, r.UpdatedAt }));
        });

        app.MapPost("/api/repositories", async (RepositoryRequest req, IServiceScopeFactory scopeFactory) =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
            if (db is null) return Results.Problem("Database is not configured");
            if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "Name is required" });
            if (string.IsNullOrWhiteSpace(req.FullName)) return Results.BadRequest(new { error = "FullName is required" });

            var repo = new Repository
            {
                Name = req.Name.Trim(),
                FullName = req.FullName.Trim(),
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            db.Repositories.Add(repo);
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException ex) { return Results.BadRequest(new { error = ex.InnerException?.Message ?? ex.Message }); }
            return Results.Created($"/api/repositories/{repo.Name}", new { repo.Name, repo.FullName, repo.IsActive });
        });

        app.MapPut("/api/repositories/{name}", async (string name, RepositoryRequest req, IServiceScopeFactory scopeFactory) =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
            if (db is null) return Results.Problem("Database is not configured");

            var repo = await db.Repositories.FirstOrDefaultAsync(r => r.Name == name);
            if (repo is null) return Results.NotFound(new { error = $"Repository '{name}' not found" });

            if (!string.IsNullOrWhiteSpace(req.FullName)) repo.FullName = req.FullName.Trim();
            if (!string.IsNullOrWhiteSpace(req.Name) && req.Name != name) repo.Name = req.Name.Trim();
            repo.UpdatedAt = DateTime.UtcNow;

            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException ex) { return Results.BadRequest(new { error = ex.InnerException?.Message ?? ex.Message }); }
            return Results.Ok(new { repo.Name, repo.FullName, repo.IsActive, repo.UpdatedAt });
        });

        app.MapPost("/api/repositories/{name}/toggle-active", async (string name, ToggleActiveRequest req, IServiceScopeFactory scopeFactory) =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
            if (db is null) return Results.Problem("Database is not configured");

            var repo = await db.Repositories.FirstOrDefaultAsync(r => r.Name == name);
            if (repo is null) return Results.NotFound(new { error = $"Repository '{name}' not found" });

            repo.IsActive = req.IsActive;
            repo.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(new { repo.Name, repo.IsActive, repo.UpdatedAt });
        });

        app.MapDelete("/api/repositories/{name}", async (string name, IServiceScopeFactory scopeFactory) =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
            if (db is null) return Results.Problem("Database is not configured");

            var repo = await db.Repositories.FirstOrDefaultAsync(r => r.Name == name);
            if (repo is null) return Results.NotFound(new { error = $"Repository '{name}' not found" });

            db.Repositories.Remove(repo);
            await db.SaveChangesAsync();
            return Results.Ok(new { deleted = name });
        });

        return app;
    }
}

internal sealed record RepositoryRequest(string? Name, string? FullName);
