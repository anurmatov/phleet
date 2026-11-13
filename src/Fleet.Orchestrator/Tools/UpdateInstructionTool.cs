using System.ComponentModel;
using Fleet.Orchestrator.Application;
using Fleet.Orchestrator.Services;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

/// <summary>The MCP adapter over <see cref="InstructionWriter.AddVersionAsync"/>, which the REST versions route calls too.</summary>
[McpServerToolType]
public sealed class UpdateInstructionTool(InstructionWriter writer)
{
    [McpServerTool(Name = "update_instruction")]
    [Description("Create a new version of a named instruction. Auto-increments version number and prunes history beyond 20 versions.")]
    public async Task<string> UpdateInstructionAsync(
        [Description("Instruction name (e.g. 'base', 'co-cto', 'developer')")] string instruction_name,
        [Description("Full new content for the instruction")] string content,
        [Description("Reason for the update")] string reason,
        [Description("Who is making the update (e.g. agent name)")] string created_by)
    {
        var outcome = await writer.AddVersionAsync(instruction_name, content, created_by, reason, WriteCaller.Mcp);
        if (outcome.Fault is { } fault)
            return fault.Code == "instruction_not_found" ? $"Instruction '{instruction_name}' not found." : fault.Message;

        var written = outcome.Value!;
        return $"Instruction '{instruction_name}' updated to v{written.Version}." + PromptSizePolicy.RenderMcpLines(written.Size);
    }
}
