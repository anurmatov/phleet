using System.ComponentModel;
using Fleet.Orchestrator.Application;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

/// <summary>
/// The MCP adapter over <see cref="AgentProjectAccessWriter"/>, which the REST project-access routes
/// call too. The wildcard actions are a grant and a revocation of <c>*</c>.
/// </summary>
[McpServerToolType]
public sealed class ManageAgentProjectAccessTool(AgentProjectAccessWriter access)
{
    [McpServerTool(Name = "manage_agent_project_access")]
    [Description("Manage memory project access for an agent. Actions: 'list' — show current projects; 'add' — grant access to a project; 'remove' — revoke access to a project; 'set_wildcard' — grant access to all projects (*); 'clear_wildcard' — remove wildcard, leaving only explicit project rows.")]
    public async Task<string> ManageAgentProjectAccessAsync(
        [Description("Agent name (e.g. acto, adev)")] string agent_name,
        [Description("Action: 'list', 'add', 'remove', 'set_wildcard', 'clear_wildcard'")] string action,
        [Description("Project name (required for 'add' and 'remove' actions)")] string? project = null)
    {
        if (string.IsNullOrWhiteSpace(agent_name))
            return "manage_agent_project_access: missing required parameter 'agent_name'.";

        action = action.Trim().ToLowerInvariant();
        var agentName = AgentProjectAccessWriter.NormalizeAgent(agent_name);
        var proj = AgentProjectAccessWriter.NormalizeProject(project);

        switch (action)
        {
            case "list":
            {
                var rows = await access.ListAsync(agentName);
                if (rows.Count == 0)
                    return $"Agent '{agentName}' has no memory project access entries.";

                // Provenance decides whether a row survives an unassignment, so show it: an
                // 'assignment' row disappears when the project is unassigned, a 'manual' one does not.
                var projects = string.Join(", ", rows.Select(row => $"{row.Project} ({row.Source})"));
                return rows.Any(row => row.Project == AgentProjectAccessWriter.Wildcard)
                    ? $"Agent '{agentName}' has wildcard access (*) — can read all memory projects. Rows: {projects}"
                    : $"Agent '{agentName}' project access: {projects}";
            }

            case "add":
            {
                if (proj.Length == 0)
                    return "manage_agent_project_access: 'add' action requires 'project' parameter.";

                var outcome = await access.GrantAsync(agentName, proj);
                if (outcome.Fault is { } fault) return fault.Message;
                if (outcome.NotDelivered is { } notDelivered) return notDelivered;
                return outcome.Value!.Changed
                    ? $"Added project '{proj}' to agent '{agentName}'. Current access: {string.Join(", ", outcome.Value.Projects)}"
                    : $"Agent '{agentName}' already has access to project '{proj}'.";
            }

            case "remove":
            {
                if (proj.Length == 0)
                    return "manage_agent_project_access: 'remove' action requires 'project' parameter.";

                var outcome = await access.RevokeAsync(agentName, proj);
                if (outcome.Fault is { Code: "project_access_not_found" })
                    return $"Agent '{agentName}' does not have access to project '{proj}'.";
                if (outcome.Fault is { } fault) return fault.Message;
                if (outcome.NotDelivered is { } notDelivered) return notDelivered;
                return $"Removed project '{proj}' from agent '{agentName}'. Remaining access: {Listed(outcome.Value!.Projects)}";
            }

            case "set_wildcard":
            {
                var outcome = await access.GrantAsync(agentName, AgentProjectAccessWriter.Wildcard);
                if (outcome.Fault is { } fault) return fault.Message;
                if (outcome.NotDelivered is { } notDelivered) return notDelivered;
                return $"Agent '{agentName}' now has wildcard access (*) — can read all memory projects.";
            }

            case "clear_wildcard":
            {
                var outcome = await access.RevokeAsync(agentName, AgentProjectAccessWriter.Wildcard);
                if (outcome.Fault is { Code: "project_access_not_found" })
                    return $"Agent '{agentName}' does not have a wildcard entry — nothing to clear.";
                if (outcome.Fault is { } fault) return fault.Message;
                if (outcome.NotDelivered is { } notDelivered) return notDelivered;
                return $"Cleared wildcard from agent '{agentName}'. Explicit project access: {Listed(outcome.Value!.Projects)}";
            }

            default:
                return $"Unknown action '{action}'. Valid actions: list, add, remove, set_wildcard, clear_wildcard.";
        }
    }

    private static string Listed(IReadOnlyList<string> projects) => projects.Count > 0 ? string.Join(", ", projects) : "(none)";
}
