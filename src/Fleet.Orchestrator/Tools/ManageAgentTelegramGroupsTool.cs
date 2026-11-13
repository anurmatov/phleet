using System.ComponentModel;
using Fleet.Orchestrator.Application;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

/// <summary>The MCP adapter over <see cref="AgentAllowlist"/>, which the REST telegram-groups routes call too.</summary>
[McpServerToolType]
public sealed class ManageAgentTelegramGroupsTool(AgentAllowlist allowlist)
{
    [McpServerTool(Name = "manage_agent_telegram_groups")]
    [Description("Add or remove an allowed Telegram group ID for an agent. Only listed group IDs will be monitored by the agent.")]
    public async Task<string> ManageAgentTelegramGroupsAsync(
        [Description("Agent name (e.g. fleet-cto, fleet-dev)")] string agent_name,
        [Description("Action: 'add' or 'remove'")] string action,
        [Description("Telegram group ID (numeric, typically negative)")] long group_id)
    {
        action = action.Trim().ToLowerInvariant();
        if (action is not ("add" or "remove"))
            return $"Unknown action '{action}'. Use 'add' or 'remove'.";

        var outcome = action == "add"
            ? await allowlist.AddGroupAsync(agent_name, group_id)
            : await allowlist.RemoveGroupAsync(agent_name, group_id);

        if (outcome.Fault is { } fault)
            return fault.Code == "agent_not_found" ? $"Agent '{agent_name}' not found in DB." : fault.Message;
        if (outcome.NotDelivered is { } notDelivered)
            return notDelivered;

        return outcome.Value switch
        {
            AllowlistChange.Added => $"Added Telegram group {group_id} to agent '{agent_name}'.",
            AllowlistChange.AlreadyPresent => $"Telegram group {group_id} already allowed for agent '{agent_name}'.",
            AllowlistChange.Removed => $"Removed Telegram group {group_id} from agent '{agent_name}'.",
            _ => $"Telegram group {group_id} not found for agent '{agent_name}'.",
        };
    }
}
