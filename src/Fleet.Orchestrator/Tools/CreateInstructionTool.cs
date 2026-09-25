using System.ComponentModel;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

[McpServerToolType]
public sealed class CreateInstructionTool(IServiceScopeFactory scopeFactory, PromptSizePolicy sizePolicy)
{
    [McpServerTool(Name = "create_instruction")]
    [Description("Create a new instruction record with an initial v1 version. Fails if an instruction with that name already exists.")]
    public async Task<string> CreateInstructionAsync(
        [Description("Instruction name (e.g. 'base', 'co-cto', 'developer')")] string instruction_name,
        [Description("Initial content for the instruction")] string content,
        [Description("Who is creating the instruction (e.g. agent name)")] string created_by,
        [Description("Optional reason / notes for this instruction")] string? reason = null)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        var exists = await db.Instructions.AnyAsync(i => i.Name == instruction_name);
        if (exists)
            return $"Instruction '{instruction_name}' already exists. Use update_instruction to add a new version.";

        var instruction = new Instruction
        {
            Name           = instruction_name,
            CurrentVersion = 1,
        };

        db.Instructions.Add(instruction);
        await db.SaveChangesAsync();

        db.InstructionVersions.Add(new InstructionVersion
        {
            InstructionId = instruction.Id,
            VersionNumber = 1,
            Content       = content,
            CreatedAt     = DateTime.UtcNow,
            CreatedBy     = created_by,
            Reason        = reason ?? "Initial creation",
        });

        await db.SaveChangesAsync();

        var size = sizePolicy.Evaluate(PromptSizeKind.Instruction, instruction_name, previousContent: null, content);
        sizePolicy.LogWrite(size, "mcp", instruction_name);
        return $"Instruction '{instruction_name}' created at v1." + PromptSizePolicy.RenderMcpLines(size);
    }
}
