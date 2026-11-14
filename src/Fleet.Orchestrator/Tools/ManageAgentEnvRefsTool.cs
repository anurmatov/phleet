using System.ComponentModel;
using Fleet.Orchestrator.Application;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

/// <summary>
/// The MCP adapter for one env-ref change. The bound-secret rule is <see cref="AgentConfigWriter"/>'s:
/// an agent (<see cref="WriteCaller.Mcp"/>) may attach a bound key only when its binding allows it.
/// </summary>
[McpServerToolType]
public sealed class ManageAgentEnvRefsTool(AgentConfigWriter writer)
{
    [McpServerTool(Name = "manage_agent_env_refs")]
    [Description("Add or remove an environment variable key reference for an agent. Env refs are secret key names read from the .env file at provision time (no actual values stored).")]
    public async Task<string> ManageAgentEnvRefsAsync(
        [Description("Agent name (e.g. fleet-cto, fleet-dev)")] string agent_name,
        [Description("Action: 'add' or 'remove'")] string action,
        [Description("Environment variable key name (e.g. TELEGRAM_BOT_TOKEN)")] string env_key_name)
    {
        action = action.Trim().ToLowerInvariant();
        if (action is not ("add" or "remove"))
            return $"Unknown action '{action}'. Use 'add' or 'remove'.";

        var outcome = action == "add"
            ? await writer.AddEnvRefAsync(agent_name, env_key_name, WriteCaller.Mcp)
            : await writer.RemoveEnvRefAsync(agent_name, env_key_name);

        if (outcome.Fault is { } fault)
            return fault.Code == "agent_not_found" ? $"Agent '{agent_name}' not found in DB." : fault.Message;
        if (outcome.NotDelivered is { } notPublished)
            return notPublished;

        return outcome.Value switch
        {
            EnvRefChange.Added => $"Added env ref '{env_key_name}' to agent '{agent_name}'.",
            EnvRefChange.AlreadyPresent => $"Env ref '{env_key_name}' already exists for agent '{agent_name}'.",
            EnvRefChange.Removed => $"Removed env ref '{env_key_name}' from agent '{agent_name}'.",
            _ => $"Env ref '{env_key_name}' not found for agent '{agent_name}'.",
        };
    }
}
