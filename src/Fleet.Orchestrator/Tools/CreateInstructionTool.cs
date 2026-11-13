using System.ComponentModel;
using Fleet.Orchestrator.Application;
using Fleet.Orchestrator.Services;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

/// <summary>The MCP adapter over <see cref="InstructionWriter.CreateAsync"/>, which <c>POST /api/instructions</c> calls too.</summary>
[McpServerToolType]
public sealed class CreateInstructionTool(InstructionWriter writer)
{
    [McpServerTool(Name = "create_instruction")]
    [Description("Create a new instruction record with an initial v1 version. Fails if an instruction with that name already exists.")]
    public async Task<string> CreateInstructionAsync(
        [Description("Instruction name (e.g. 'base', 'co-cto', 'developer')")] string instruction_name,
        [Description("Initial content for the instruction")] string content,
        [Description("Who is creating the instruction (e.g. agent name)")] string created_by,
        [Description("Optional reason / notes for this instruction")] string? reason = null)
    {
        var outcome = await writer.CreateAsync(instruction_name, content, created_by, reason, WriteCaller.Mcp);
        if (outcome.Fault is { } fault)
            return fault.Code == "instruction_exists"
                ? $"Instruction '{instruction_name}' already exists. Use update_instruction to add a new version."
                : fault.Message;

        return $"Instruction '{instruction_name}' created at v1." + PromptSizePolicy.RenderMcpLines(outcome.Value!.Size);
    }
}
