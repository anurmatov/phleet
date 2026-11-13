using System.ComponentModel;
using Fleet.Orchestrator.Application;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

/// <summary>
/// The MCP adapter over <see cref="AgentCreator"/>, which <c>POST /api/agents</c> calls too. It never
/// asks for the container: <c>provision_agent</c> does that once the agent is configured.
/// </summary>
[McpServerToolType]
public sealed class CreateAgentTool(AgentCreator creator)
{
    [McpServerTool(Name = "create_agent")]
    [Description("Create a new agent DB record with minimal required fields. Does NOT provision a container — use provision_agent after configuring the agent fully. Use manage_agent_mcp_endpoints, manage_agent_instructions, manage_agent_env_refs, etc. to configure, then provision_agent to start the container.")]
    public async Task<string> CreateAgentAsync(
        [Description("Agent short name — lowercase, no spaces (e.g. my-agent). Used as the DB key and part of the container name.")] string agent_name,
        [Description("Role for the agent (e.g. developer, cto, ops). Must match a role file.")] string role,
        [Description("Model to use (e.g. claude-sonnet-4-6, claude-opus-4-7).")] string model,
        [Description("Display name shown in the dashboard (e.g. 'Fleet Dev'). Defaults to agent_name if omitted.")] string? display_name = null,
        [Description("Memory limit in MB. Defaults to 4096.")] int? memory_limit_mb = null,
        [Description("Docker container name override (e.g. fleet-my-agent). Defaults to 'fleet-{agent_name}'.")] string? container_name = null,
        [Description("LLM provider: claude (default), codex or gemini. Any other value is refused.")] string? provider = null,
        [Description("When true, /var/run/docker.sock is bind-mounted into the container. Grants host Docker access — only set for agents that manage containers (e.g. devops, developer, co-cto). Defaults to false.")] bool? mount_docker_sock = null,
        [Description("Local model server origin (scheme://host[:port], no path), e.g. http://inference-host:8000. Optional local mode for claude or codex. Checked like update_agent_config before anything is written.")] string? local_base_url = null,
        [Description("The local server's context size in tokens (its --ctx). Used only for local models.")] int? context_window = null,
        [Description("Claude and codex only: extra harness settings merged over the mode defaults, one per line; checked against the same allowlist as update_agent_config before anything is written. Omit for none.")] string? harness_extra = null)
    {
        var outcome = await creator.CreateAsync(new NewAgent
        {
            Name = agent_name,
            Role = role,
            Model = model,
            DisplayName = display_name,
            MemoryLimitMb = memory_limit_mb,
            ContainerName = container_name,
            Provider = provider,
            MountDockerSock = mount_docker_sock,
            LocalBaseUrl = local_base_url,
            ContextWindow = context_window,
            HarnessExtra = harness_extra,
            Provision = false,
        }, WriteCaller.Mcp);

        if (outcome.Fault is { } fault)
            return fault.Code == "harness_extra_invalid" ? $"Invalid harness_extra: {fault.Message}" : fault.Message;

        var created = outcome.Value!;
        return $"Agent '{created.Name}' created in DB (container: {created.ContainerName}, role: {created.Role}, model: {created.Model}, " +
               $"memory: {created.MemoryLimitMb}MB, short_name: {created.Name}, host_port: {created.HostPort}, " +
               $"networks: [{string.Join(", ", created.Networks)}], env_refs: [{string.Join(", ", created.EnvRefs)}]). " +
               $"Configure it with manage_agent_mcp_endpoints, manage_agent_instructions, manage_agent_env_refs, etc., then call provision_agent to start the container.";
    }
}
