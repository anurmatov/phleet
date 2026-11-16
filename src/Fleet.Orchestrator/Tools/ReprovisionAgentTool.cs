using System.ComponentModel;
using Fleet.Orchestrator.Services;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

[McpServerToolType]
public sealed class ReprovisionAgentTool(ContainerProvisioningService provisioning)
{
    [McpServerTool(Name = "reprovision_agent")]
    [Description(
        "Stops, removes, and re-creates the Docker container for an agent from its current DB config. " +
        "Use this after updating agent config (model, tools, memory). Workspace volume data is preserved. " +
        "The agent resumes its provider session when its provider and instructions are unchanged; " +
        "pass session=fresh to make it start a new one, which forgets the conversation.")]
    public async Task<string> ReprovisionAsync(
        [Description("Agent short name (e.g. my-agent)")] string agent_name,
        [Description("'auto' (default): resume the session when the agent can. 'fresh': remove the agent's session file so it starts a new session. Changed instructions start a new session either way.")] string? session = null)
    {
        var result = await provisioning.ReprovisionAsync(agent_name, session: session);
        return result.Success
            ? $"✓ {result.Message}"
            : $"✗ reprovision_agent failed: {result.Message}";
    }
}
