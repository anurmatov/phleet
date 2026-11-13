using System.ComponentModel;
using Fleet.Orchestrator.Application;
using Fleet.Orchestrator.Services;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

/// <summary>The MCP adapter over <see cref="InstructionWriter.RollbackAsync"/>, which the REST rollback route calls too.</summary>
[McpServerToolType]
public sealed class RollbackInstructionTool(InstructionWriter writer)
{
    [McpServerTool(Name = "rollback_instruction")]
    [Description("Roll back an instruction to a prior version by creating a new version with the content copied from the target version. Non-destructive — full history is preserved.")]
    public async Task<string> RollbackInstructionAsync(
        [Description("Instruction name (e.g. 'base', 'co-cto', 'developer')")] string instruction_name,
        [Description("Version number to roll back to")] int target_version)
    {
        var outcome = await writer.RollbackAsync(instruction_name, target_version, WriteCaller.Mcp);
        if (outcome.Fault is { } fault)
            return fault.Code switch
            {
                "instruction_not_found" => $"Instruction '{instruction_name}' not found.",
                "instruction_version_not_found" => $"Version {target_version} does not exist for instruction '{instruction_name}'.",
                _ => fault.Message,
            };

        var written = outcome.Value!;
        return $"Instruction '{instruction_name}' rolled back to v{target_version} content — saved as v{written.Version}."
             + PromptSizePolicy.RenderMcpLines(written.Size);
    }
}
