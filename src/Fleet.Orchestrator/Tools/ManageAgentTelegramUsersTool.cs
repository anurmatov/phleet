using System.ComponentModel;
using Fleet.Orchestrator.Application;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

/// <summary>The MCP adapter over <see cref="AgentAllowlist"/>, which the REST telegram-users routes call too.</summary>
[McpServerToolType]
public sealed class ManageAgentTelegramUsersTool(AgentAllowlist allowlist)
{
    [McpServerTool(Name = "manage_agent_telegram_users")]
    [Description("Add or remove an allowed Telegram user ID for an agent. Only listed user IDs can send DMs to the agent's bot.")]
    public async Task<string> ManageAgentTelegramUsersAsync(
        [Description("Agent name (e.g. fleet-cto, fleet-dev)")] string agent_name,
        [Description("Action: 'add' or 'remove'")] string action,
        [Description("Telegram user ID (numeric)")] long user_id,
        [Description("Telegram @username (without @). Optional — used in the welcome DM when a newly-approved user is greeted.")] string? username = null,
        [Description("User's first name from Telegram. Optional — used in welcome DM when username is not set.")] string? first_name = null)
    {
        action = action.Trim().ToLowerInvariant();
        if (action is not ("add" or "remove"))
            return $"Unknown action '{action}'. Use 'add' or 'remove'.";

        var outcome = action == "add"
            ? await allowlist.AddUserAsync(agent_name, user_id, username, first_name)
            : await allowlist.RemoveUserAsync(agent_name, user_id);

        if (outcome.Fault is { } fault)
            return fault.Code == "agent_not_found" ? $"Agent '{agent_name}' not found in DB." : fault.Message;
        if (outcome.NotDelivered is { } notDelivered)
            return notDelivered;

        return outcome.Value switch
        {
            AllowlistChange.Added => $"Added Telegram user {user_id} to agent '{agent_name}'.",
            AllowlistChange.AlreadyPresent => $"Telegram user {user_id} already allowed for agent '{agent_name}'.",
            AllowlistChange.Removed => $"Removed Telegram user {user_id} from agent '{agent_name}'.",
            _ => $"Telegram user {user_id} not found for agent '{agent_name}'.",
        };
    }
}
