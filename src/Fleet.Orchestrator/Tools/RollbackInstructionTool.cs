using System.ComponentModel;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

[McpServerToolType]
public sealed class RollbackInstructionTool(IServiceScopeFactory scopeFactory, PromptSizePolicy sizePolicy)
{
    private const int MaxVersions = 20;

    [McpServerTool(Name = "rollback_instruction")]
    [Description("Roll back an instruction to a prior version by creating a new version with the content copied from the target version. Non-destructive — full history is preserved.")]
    public async Task<string> RollbackInstructionAsync(
        [Description("Instruction name (e.g. 'base', 'co-cto', 'developer')")] string instruction_name,
        [Description("Version number to roll back to")] int target_version)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        var instruction = await db.Instructions
            .Include(i => i.Versions.OrderBy(v => v.VersionNumber))
            .FirstOrDefaultAsync(i => i.Name == instruction_name);

        if (instruction is null)
            return $"Instruction '{instruction_name}' not found.";

        var target = instruction.Versions.FirstOrDefault(v => v.VersionNumber == target_version);
        if (target is null)
            return $"Version {target_version} does not exist for instruction '{instruction_name}'.";

        var previousContent = instruction.Versions.FirstOrDefault(v => v.VersionNumber == instruction.CurrentVersion)?.Content;

        var newVersionNumber = instruction.CurrentVersion + 1;

        instruction.Versions.Add(new InstructionVersion
        {
            InstructionId = instruction.Id,
            VersionNumber = newVersionNumber,
            Content       = target.Content,
            CreatedAt     = DateTime.UtcNow,
            CreatedBy     = "rollback",
            Reason        = $"rollback to version {target_version}",
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

        var size = sizePolicy.Evaluate(PromptSizeKind.Instruction, instruction_name, previousContent, target.Content);
        sizePolicy.LogWrite(size, "mcp", instruction_name);
        return $"Instruction '{instruction_name}' rolled back to v{target_version} content — saved as v{newVersionNumber}."
             + PromptSizePolicy.RenderMcpLines(size);
    }
}
