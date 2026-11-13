using Fleet.Orchestrator.Application;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Endpoints;

/// <summary>
/// The <c>/api/instructions</c> surface: list, detail, new version, rollback, create and
/// toggle-active.
/// </summary>
/// <remarks>
/// Lifted out of <c>Program.cs</c> (#346) so the endpoint tests map the SAME handlers a live
/// orchestrator maps. The four writes are adapters over <see cref="InstructionWriter"/>, which the
/// instruction tools call too; the three that store content report its <c>size</c>
/// (<see cref="PromptSizePolicy"/>), computed after the write commits. Writes are gated by the
/// method-based bearer middleware (<see cref="OrchestratorAuth"/>).
/// </remarks>
public static class InstructionEndpoints
{
    public static IEndpointRouteBuilder MapInstructionEndpoints(this IEndpointRouteBuilder app)
    {
        // REST: list all instructions with version summary
        app.MapGet("/api/instructions", async (IServiceScopeFactory scopeFactory) =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
            if (db is null)
                return Results.Problem("Database is not configured on this orchestrator");

            var instructions = await db.Instructions
                .Include(i => i.Versions)
                .Include(i => i.AgentInstructions)
                    .ThenInclude(ai => ai.Agent)
                .AsSplitQuery()
                .AsNoTracking()
                .OrderBy(i => i.Name)
                .ToListAsync();

            return Results.Ok(instructions.Select(i => new
            {
                i.Name,
                i.CurrentVersion,
                i.IsActive,
                TotalVersions = i.Versions.Count,
                Agents = i.AgentInstructions.Select(ai => ai.Agent.Name).OrderBy(n => n),
                // #346: UTF-8 bytes of the current version, for the dashboard's Size column.
                CurrentBytes = i.Versions.FirstOrDefault(v => v.VersionNumber == i.CurrentVersion)?.Content is { } content
                    ? PromptSizePolicy.Utf8Bytes(content)
                    : (int?)null,
            }));
        });

        // REST: get full instruction with all versions and content
        app.MapGet("/api/instructions/{name}", async (string name, IServiceScopeFactory scopeFactory) =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
            if (db is null)
                return Results.Problem("Database is not configured on this orchestrator");

            var instruction = await db.Instructions
                .Include(i => i.Versions.OrderByDescending(v => v.VersionNumber))
                .AsNoTracking()
                .FirstOrDefaultAsync(i => i.Name == name);

            if (instruction is null)
                return Results.NotFound(new { error = $"Instruction '{name}' not found" });

            return Results.Ok(new
            {
                instruction.Name,
                instruction.CurrentVersion,
                Versions = instruction.Versions.Select(v => new
                {
                    v.VersionNumber,
                    v.Content,
                    CreatedAt = v.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                    v.CreatedBy,
                    v.Reason,
                }),
            });
        });

        // REST: create new version of an instruction
        app.MapPost("/api/instructions/{name}/versions", async (string name, HttpRequest request, InstructionWriter writer) =>
        {
            var body = await request.ReadFromJsonAsync<InstructionUpdateRequest>();
            if (body is null)
                return Results.BadRequest(new { error = "content is required" });

            return WriteHttp.Answer(
                await writer.AddVersionAsync(name, body.Content, body.CreatedBy ?? "dashboard", body.Reason, WriteCaller.Rest),
                written => Results.Ok(new { message = $"Instruction '{name}' updated to v{written.Version}", version = written.Version, size = written.Size }));
        });

        // REST: rollback instruction to a prior version (creates new version with old content)
        app.MapPost("/api/instructions/{name}/rollback/{targetVersion:int}", async (string name, int targetVersion, InstructionWriter writer) =>
            WriteHttp.Answer(
                await writer.RollbackAsync(name, targetVersion, WriteCaller.Rest),
                written => Results.Ok(new { message = $"Rolled back '{name}' to v{targetVersion} content — saved as v{written.Version}", version = written.Version, size = written.Size })));

        // REST: create a new instruction with initial v1 content
        app.MapPost("/api/instructions", async (HttpRequest request, InstructionWriter writer) =>
        {
            var body = await request.ReadFromJsonAsync<InstructionCreateRequest>();
            if (body is null)
                return Results.BadRequest(new { error = "name and content are required" });

            return WriteHttp.Answer(
                await writer.CreateAsync(body.Name, body.Content, body.CreatedBy ?? "dashboard", body.Reason, WriteCaller.Rest),
                written => Results.Ok(new { message = $"Instruction '{written.Name}' created at v1", size = written.Size }));
        });

        // REST: toggle active/inactive on an instruction
        app.MapPost("/api/instructions/{name}/toggle-active", async (string name, ToggleActiveRequest req, InstructionWriter writer) =>
            WriteHttp.Answer(await writer.SetActiveAsync(name, req.IsActive), isActive => Results.Ok(new { Name = name, IsActive = isActive })));

        return app;
    }
}
