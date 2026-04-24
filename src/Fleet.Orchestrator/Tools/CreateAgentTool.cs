using System.ComponentModel;
using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

[McpServerToolType]
public sealed class CreateAgentTool(IServiceScopeFactory scopeFactory, IConfiguration config)
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
        [Description("LLM provider: claude (default) or codex (OpenAI via Codex SDK).")] string? provider = null)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        var name = agent_name.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(name))
            return "agent_name is required.";
        if (string.IsNullOrWhiteSpace(role))
            return "role is required.";
        if (string.IsNullOrWhiteSpace(model))
            return "model is required.";

        if (await db.Agents.AnyAsync(a => a.Name == name))
            return $"Agent '{name}' already exists in DB.";

        var resolvedContainer = string.IsNullOrWhiteSpace(container_name)
            ? $"fleet-{name}"
            : container_name.Trim();

        var resolvedDisplay = string.IsNullOrWhiteSpace(display_name)
            ? name
            : display_name.Trim();

        // Auto-allocate a unique host port for the agent's HTTP API.
        // The orchestrator (a native host process) proxies cancel requests via 127.0.0.1:{HostPort}.
        var maxPort = await db.Agents.MaxAsync(a => (int?)a.HostPort);
        var allocatedPort = Math.Max(maxPort ?? 8080, 8080) + 1;

        var agent = new Agent
        {
            Name          = name,
            DisplayName   = resolvedDisplay,
            Role          = role.Trim(),
            Model         = model.Trim(),
            ContainerName = resolvedContainer,
            MemoryLimitMb = memory_limit_mb ?? 4096,
            ShortName     = name,
            HostPort      = allocatedPort,
            Provider      = provider ?? "claude",
        };

        db.Agents.Add(agent);
        await db.SaveChangesAsync();

        // All agents need the default fleet network to reach RabbitMQ, fleet-memory, and other fleet services.
        // Network name is configurable via Provisioning:DefaultNetwork (defaults to fleet-net).
        var defaultNetwork = config["Provisioning:DefaultNetwork"] ?? "fleet-net";
        db.AgentNetworks.Add(new AgentNetwork
        {
            AgentId     = agent.Id,
            NetworkName = defaultNetwork,
        });

        // Every agent needs a Telegram bot token to start its transport (without one the
        // RabbitMQ consumers never get wired up). Seed TELEGRAM_NOTIFIER_BOT_TOKEN as the
        // default — callers who want a custom per-agent token (e.g. TELEGRAM_CTO_BOT_TOKEN)
        // can swap it in afterwards via manage_agent_env_refs.
        const string defaultBotToken = "TELEGRAM_NOTIFIER_BOT_TOKEN";
        db.AgentEnvRefs.Add(new AgentEnvRef
        {
            AgentId    = agent.Id,
            EnvKeyName = defaultBotToken,
        });
        await db.SaveChangesAsync();

        return $"Agent '{name}' created in DB (container: {resolvedContainer}, role: {role}, model: {model}, memory: {agent.MemoryLimitMb}MB, short_name: {name}, host_port: {allocatedPort}, networks: [{defaultNetwork}], env_refs: [{defaultBotToken}]). " +
               $"Configure it with manage_agent_mcp_endpoints, manage_agent_instructions, manage_agent_env_refs, etc., then call provision_agent to start the container.";
    }
}
