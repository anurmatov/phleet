using System.ComponentModel;
using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

[McpServerToolType]
public sealed class ManageAgentInstructionsTool(IServiceScopeFactory scopeFactory)
{
    [McpServerTool(Name = "manage_agent_instructions")]
    [Description("Add, update load order, or remove an instruction assignment for an agent. Use action='add' to assign an instruction, action='update' to change load order, action='remove' to unassign. Instructions are loaded in ascending load_order when the agent starts.")]
    public async Task<string> ManageAgentInstructionsAsync(
        [Description("Agent name (e.g. fleet-cto, fleet-dev)")] string agent_name,
        [Description("Action: 'add', 'update', or 'remove'")] string action,
        [Description("Instruction name (e.g. base, co-cto, developer)")] string instruction_name,
        [Description("Load order (ascending). Required for add; optional for update.")] int? load_order = null)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        var agent = await db.Agents
            .Include(a => a.Instructions)
                .ThenInclude(ai => ai.Instruction)
            .FirstOrDefaultAsync(a => a.Name == agent_name);

        if (agent is null)
            return $"Agent '{agent_name}' not found in DB.";

        var instruction = await db.Instructions
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.Name == instruction_name);

        if (instruction is null && action != "remove")
            return $"Instruction '{instruction_name}' not found.";

        action = action.Trim().ToLowerInvariant();

        switch (action)
        {
            case "add":
            {
                if (load_order is null)
                    return "load_order is required for action='add'.";

                if (agent.Instructions.Any(ai => ai.Instruction.Name.Equals(instruction_name, StringComparison.OrdinalIgnoreCase)))
                    return $"Instruction '{instruction_name}' is already assigned to agent '{agent_name}'. Use action='update' to change load order.";

                db.AgentInstructions.Add(new AgentInstruction
                {
                    AgentId       = agent.Id,
                    InstructionId = instruction!.Id,
                    LoadOrder     = load_order.Value,
                });
                await db.SaveChangesAsync();
                return $"Assigned instruction '{instruction_name}' to agent '{agent_name}' at load order {load_order}.";
            }

            case "update":
            {
                var existing = agent.Instructions.FirstOrDefault(ai => ai.Instruction.Name.Equals(instruction_name, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                    return $"Instruction '{instruction_name}' is not assigned to agent '{agent_name}'. Use action='add' to assign it.";

                if (load_order is null)
                    return "load_order is required for action='update'.";

                if (load_order == existing.LoadOrder)
                    return $"No changes — instruction '{instruction_name}' already has load order {load_order}.";

                var old = existing.LoadOrder;
                existing.LoadOrder = load_order.Value;
                await db.SaveChangesAsync();
                return $"Updated instruction '{instruction_name}' on agent '{agent_name}': load_order {old} → {load_order}.";
            }

            case "remove":
            {
                var existing = agent.Instructions.FirstOrDefault(ai => ai.Instruction.Name.Equals(instruction_name, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                    return $"Instruction '{instruction_name}' is not assigned to agent '{agent_name}'.";

                db.AgentInstructions.Remove(existing);
                await db.SaveChangesAsync();
                return $"Removed instruction '{instruction_name}' from agent '{agent_name}'.";
            }

            default:
                return $"Unknown action '{action}'. Use 'add', 'update', or 'remove'.";
        }
    }
}
