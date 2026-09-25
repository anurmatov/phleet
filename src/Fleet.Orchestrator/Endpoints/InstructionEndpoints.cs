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
/// Lifted out of <c>Program.cs</c> verbatim (#346) — the output-style precedent — so the endpoint
/// tests map the SAME handlers a live orchestrator maps. The only addition is the <c>size</c> report
/// on the three writes (<see cref="PromptSizePolicy"/>), computed after the write commits; every
/// existing field, status code and error body is unchanged. Writes are gated by the method-based
/// bearer middleware (<see cref="OrchestratorAuth"/>).
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
        app.MapPost("/api/instructions/{name}/versions", async (string name, HttpRequest request, IServiceScopeFactory scopeFactory, [FromServices] PromptSizePolicy sizePolicy) =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
            if (db is null)
                return Results.Problem("Database is not configured on this orchestrator");

            var body = await request.ReadFromJsonAsync<InstructionUpdateRequest>();
            if (body is null || string.IsNullOrWhiteSpace(body.Content))
                return Results.BadRequest(new { error = "content is required" });

            const int MaxVersions = 20;

            var instruction = await db.Instructions
                .Include(i => i.Versions.OrderBy(v => v.VersionNumber))
                .FirstOrDefaultAsync(i => i.Name == name);

            if (instruction is null)
                return Results.NotFound(new { error = $"Instruction '{name}' not found" });

            // Captured before Versions.Add so the new row is never mistaken for the previous one.
            var previousContent = CurrentContent(instruction);

            var newVersion = instruction.CurrentVersion + 1;
            instruction.Versions.Add(new InstructionVersion
            {
                InstructionId = instruction.Id,
                VersionNumber = newVersion,
                Content       = body.Content,
                CreatedAt     = DateTime.UtcNow,
                CreatedBy     = body.CreatedBy ?? "dashboard",
                Reason        = body.Reason,
            });
            instruction.CurrentVersion = newVersion;

            var excess = instruction.Versions.Count - MaxVersions;
            if (excess > 0)
                db.InstructionVersions.RemoveRange(instruction.Versions.OrderBy(v => v.VersionNumber).Take(excess));

            await db.SaveChangesAsync();

            var size = sizePolicy.Evaluate(PromptSizeKind.Instruction, name, previousContent, body.Content);
            sizePolicy.LogWrite(size, "rest", name);
            return Results.Ok(new { message = $"Instruction '{name}' updated to v{newVersion}", version = newVersion, size });
        });

        // REST: rollback instruction to a prior version (creates new version with old content)
        app.MapPost("/api/instructions/{name}/rollback/{targetVersion:int}", async (string name, int targetVersion, IServiceScopeFactory scopeFactory, [FromServices] PromptSizePolicy sizePolicy) =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
            if (db is null)
                return Results.Problem("Database is not configured on this orchestrator");

            const int MaxVersions = 20;

            var instruction = await db.Instructions
                .Include(i => i.Versions.OrderBy(v => v.VersionNumber))
                .FirstOrDefaultAsync(i => i.Name == name);

            if (instruction is null)
                return Results.NotFound(new { error = $"Instruction '{name}' not found" });

            var target = instruction.Versions.FirstOrDefault(v => v.VersionNumber == targetVersion);
            if (target is null)
                return Results.NotFound(new { error = $"Version {targetVersion} not found" });

            var previousContent = CurrentContent(instruction);

            var newVersion = instruction.CurrentVersion + 1;
            instruction.Versions.Add(new InstructionVersion
            {
                InstructionId = instruction.Id,
                VersionNumber = newVersion,
                Content       = target.Content,
                CreatedAt     = DateTime.UtcNow,
                CreatedBy     = "rollback",
                Reason        = $"rollback to v{targetVersion}",
            });
            instruction.CurrentVersion = newVersion;

            var excess = instruction.Versions.Count - MaxVersions;
            if (excess > 0)
                db.InstructionVersions.RemoveRange(instruction.Versions.OrderBy(v => v.VersionNumber).Take(excess));

            await db.SaveChangesAsync();

            var size = sizePolicy.Evaluate(PromptSizeKind.Instruction, name, previousContent, target.Content);
            sizePolicy.LogWrite(size, "rest", name);
            return Results.Ok(new { message = $"Rolled back '{name}' to v{targetVersion} content — saved as v{newVersion}", version = newVersion, size });
        });

        // REST: create a new instruction with initial v1 content
        app.MapPost("/api/instructions", async (HttpRequest request, IServiceScopeFactory scopeFactory, [FromServices] PromptSizePolicy sizePolicy) =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
            if (db is null)
                return Results.Problem("Database is not configured on this orchestrator");

            var body = await request.ReadFromJsonAsync<InstructionCreateRequest>();
            if (body is null || string.IsNullOrWhiteSpace(body.Name) || string.IsNullOrWhiteSpace(body.Content))
                return Results.BadRequest(new { error = "name and content are required" });

            if (!System.Text.RegularExpressions.Regex.IsMatch(body.Name, @"^[a-zA-Z0-9_-]+$"))
                return Results.BadRequest(new { error = "name must contain only letters, digits, hyphens, or underscores" });

            var exists = await db.Instructions.AnyAsync(i => i.Name == body.Name);
            if (exists)
                return Results.Conflict(new { error = $"Instruction '{body.Name}' already exists" });

            var instr = new Instruction { Name = body.Name, CurrentVersion = 1 };
            db.Instructions.Add(instr);
            await db.SaveChangesAsync();

            db.InstructionVersions.Add(new InstructionVersion
            {
                InstructionId = instr.Id,
                VersionNumber = 1,
                Content       = body.Content,
                CreatedAt     = DateTime.UtcNow,
                CreatedBy     = body.CreatedBy ?? "dashboard",
                Reason        = body.Reason ?? "Initial creation",
            });
            await db.SaveChangesAsync();

            var size = sizePolicy.Evaluate(PromptSizeKind.Instruction, body.Name, previousContent: null, body.Content);
            sizePolicy.LogWrite(size, "rest", body.Name);
            return Results.Ok(new { message = $"Instruction '{body.Name}' created at v1", size });
        });

        // REST: toggle active/inactive on an instruction
        app.MapPost("/api/instructions/{name}/toggle-active", async (string name, ToggleActiveRequest req, IServiceScopeFactory scopeFactory) =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
            if (db is null) return Results.Problem("Database is not configured");
            var instr = await db.Instructions.FirstOrDefaultAsync(i => i.Name == name);
            if (instr is null) return Results.NotFound(new { error = $"Instruction '{name}' not found" });
            instr.IsActive = req.IsActive;
            await db.SaveChangesAsync();
            return Results.Ok(new { instr.Name, instr.IsActive });
        });

        return app;
    }

    /// <summary>
    /// Content of the version row <c>CurrentVersion</c> points at, or null when that row is missing
    /// (pruned or never written) — the size report then has no <c>previousBytes</c>.
    /// </summary>
    private static string? CurrentContent(Instruction instruction) =>
        instruction.Versions.FirstOrDefault(v => v.VersionNumber == instruction.CurrentVersion)?.Content;
}
