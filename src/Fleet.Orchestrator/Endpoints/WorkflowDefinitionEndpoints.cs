using Fleet.Orchestrator.Application;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Endpoints;

/// <summary>
/// The <c>/api/workflow-definitions</c> surface: list, detail, create, update and toggle-active.
/// </summary>
/// <remarks>
/// The three writes are adapters over <see cref="WorkflowDefinitionWriter"/>, which
/// <c>create_workflow_definition</c> and <c>update_workflow_definition</c> call too. Writes are
/// gated by the method-based bearer middleware (<see cref="OrchestratorAuth"/>).
/// </remarks>
public static class WorkflowDefinitionEndpoints
{
    public static IEndpointRouteBuilder MapWorkflowDefinitionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/workflow-definitions", async (bool? includeInactive, IServiceScopeFactory scopeFactory) =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
            if (db is null) return Results.Problem("Database is not configured");
            var query = db.WorkflowDefinitions.AsQueryable();
            if (includeInactive != true) query = query.Where(d => d.IsActive);
            var defs = await query
                .OrderBy(d => d.Name)
                .AsNoTracking()
                .Select(d => new { d.Name, d.Namespace, d.TaskQueue, d.Description, d.Version, d.IsActive, d.CreatedAt, d.UpdatedAt })
                .ToListAsync();
            return Results.Ok(defs);
        });

        app.MapGet("/api/workflow-definitions/{name}", async (string name, IServiceScopeFactory scopeFactory) =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
            if (db is null) return Results.Problem("Database is not configured");
            var def = await db.WorkflowDefinitions
                .Include(d => d.Versions)
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.Name == name);
            if (def is null) return Results.NotFound(new { error = $"Workflow definition '{name}' not found" });
            return Results.Ok(new
            {
                def.Name, def.Namespace, def.TaskQueue, def.Description,
                def.Version, def.IsActive, def.Definition,
                def.CreatedAt, def.UpdatedAt, def.CreatedBy,
                Versions = def.Versions.OrderByDescending(v => v.Version)
                    .Select(v => new { v.Version, v.Definition, v.Reason, v.CreatedAt, v.CreatedBy })
                    .ToList(),
            });
        });

        app.MapPost("/api/workflow-definitions", async (WorkflowDefinitionRequest req, WorkflowDefinitionWriter writer) =>
            WriteHttp.Answer(
                await writer.CreateAsync(new NewWorkflowDefinition(
                    req.Name, req.Namespace, req.TaskQueue, req.Definition, req.Description, req.CreatedBy)),
                def => Results.Created($"/api/workflow-definitions/{def.Name}", def)));

        app.MapPut("/api/workflow-definitions/{name}", async (string name, WorkflowDefinitionRequest req, WorkflowDefinitionWriter writer) =>
            WriteHttp.Answer(
                await writer.UpdateAsync(name, new WorkflowDefinitionPatch(
                    req.Definition, req.Description, req.Namespace, req.TaskQueue, req.Reason, req.CreatedBy)),
                Summary));

        app.MapPost("/api/workflow-definitions/{name}/toggle-active", async (string name, ToggleActiveRequest req, WorkflowDefinitionWriter writer) =>
            WriteHttp.Answer(await writer.SetActiveAsync(name, req.IsActive), Summary));

        return app;
    }

    private static IResult Summary(WorkflowDefinition def) => Results.Ok(new
    {
        def.Name, def.Namespace, def.TaskQueue, def.Description,
        def.Version, def.IsActive, def.UpdatedAt,
    });
}

internal sealed record WorkflowDefinitionRequest
{
    public string? Name { get; init; }
    public string? Namespace { get; init; }
    public string? TaskQueue { get; init; }
    public string? Definition { get; init; }
    public string? Description { get; init; }
    public string? Reason { get; init; }
    public string? CreatedBy { get; init; }
}
