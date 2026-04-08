using System.ComponentModel;
using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

[McpServerToolType]
public sealed class UpdateInstructionTool(IServiceScopeFactory scopeFactory)
{
    private const int MaxVersions = 20;

    [McpServerTool(Name = "update_instruction")]
    [Description("Create a new version of a named instruction. Auto-increments version number and prunes history beyond 20 versions.")]
    public async Task<string> UpdateInstructionAsync(
        [Description("Instruction name (e.g. 'base', 'co-cto', 'developer')")] string instruction_name,
        [Description("Full new content for the instruction")] string content,
        [Description("Reason for the update")] string reason,
        [Description("Who is making the update (e.g. agent name)")] string created_by)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        var instruction = await db.Instructions
            .Include(i => i.Versions.OrderBy(v => v.VersionNumber))
            .FirstOrDefaultAsync(i => i.Name == instruction_name);

        if (instruction is null)
            return $"Instruction '{instruction_name}' not found.";

        var newVersionNumber = instruction.CurrentVersion + 1;

        instruction.Versions.Add(new InstructionVersion
        {
            InstructionId = instruction.Id,
            VersionNumber = newVersionNumber,
            Content       = content,
            CreatedAt     = DateTime.UtcNow,
            CreatedBy     = created_by,
            Reason        = reason,
        });

        instruction.CurrentVersion = newVersionNumber;

        // Prune oldest versions beyond the cap
        var excess = instruction.Versions.Count - MaxVersions;
        if (excess > 0)
        {
            var toDelete = instruction.Versions
                .OrderBy(v => v.VersionNumber)
                .Take(excess)
                .ToList();
            db.InstructionVersions.RemoveRange(toDelete);
        }

        await db.SaveChangesAsync();

        return $"Instruction '{instruction_name}' updated to v{newVersionNumber}.";
    }
}
