using System.ComponentModel;
using System.Text;
using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

[McpServerToolType]
public sealed class UpdateAgentConfigTool(IServiceScopeFactory scopeFactory)
{
    [McpServerTool(Name = "update_agent_config")]
    [Description("Update agent configuration in the DB. All fields are optional — only provided fields are changed. Does NOT restart the agent; restart separately after updating.")]
    public async Task<string> UpdateAgentConfigAsync(
        [Description("Agent name (e.g. fleet-cto, fleet-dev)")] string agent_name,
        [Description("New model name (e.g. claude-opus-4-7, claude-sonnet-4-6). Omit to keep current.")] string? model = null,
        [Description("New memory limit in MB. Omit to keep current.")] int? memory_limit_mb = null,
        [Description("Set enabled/disabled. Omit to keep current.")] bool? is_enabled = null,
        [Description("Replace the full tool list. Pass comma-separated tool names. Omit to keep current.")] string? tools = null,
        [Description("Replace the full project list. Pass comma-separated project names. Omit to keep current.")] string? projects = null,
        [Description("Docker image override (e.g. my-org/my-image:tag). Omit to keep current.")] string? image = null,
        [Description("Permission mode for claude (e.g. acceptEdits, default). Omit to keep current.")] string? permission_mode = null,
        [Description("Max turns per claude invocation. Omit to keep current.")] int? max_turns = null,
        [Description("Working directory inside container. Omit to keep current.")] string? work_dir = null,
        [Description("Proactive message interval in minutes (0=disabled). Omit to keep current.")] int? proactive_interval_minutes = null,
        [Description("Group listen mode (mention, all, none). Omit to keep current.")] string? group_listen_mode = null,
        [Description("Group debounce in seconds before processing group messages. Omit to keep current.")] int? group_debounce_seconds = null,
        [Description("Short name for the agent (used in group chat). Omit to keep current.")] string? short_name = null,
        [Description("Show stats in status messages. Omit to keep current.")] bool? show_stats = null,
        [Description("Prefix all outgoing telegram messages with bold [ShortName] header for shared-bot visibility. Omit to keep current.")] bool? prefix_messages = null,
        [Description("Suppress intermediate tool-use progress messages in Telegram — only post the final response. Use for agents serving non-technical users (e.g. family assistant). Omit to keep current.")] bool? suppress_tool_messages = null,
        [Description("Telegram send-only mode: skip polling and message handling, only send messages. Use when multiple agents share a bot token. Omit to keep current.")] bool? telegram_send_only = null,
        [Description("Claude effort level (low/medium/high/max). Pass empty string to clear. Omit to keep current.")] string? effort = null,
        [Description("JSON schema string for --json-schema flag (structured output). Pass empty string to clear. Omit to keep current.")] string? json_schema = null,
        [Description("JSON string for --agents flag (inline subagents). Pass empty string to clear. Omit to keep current.")] string? agents_json = null,
        [Description("Host port for the agent's HTTP API (used by orchestrator cancel proxy via 127.0.0.1:{host_port}). Pass 0 to clear. Omit to keep current.")] int? host_port = null,
        [Description("Enable Claude's built-in auto-memory. Set to false for agents using fleet-memory (disables CLAUDE_CODE_DISABLE_AUTO_MEMORY). Omit to keep current.")] bool? auto_memory_enabled = null,
        [Description("LLM provider: claude or codex. Omit to keep current.")] string? provider = null,
        [Description("Codex sandbox mode (danger-full-access, workspace-write, read-only). Pass empty string to clear. Omit to keep current. Only applies to codex agents.")] string? codex_sandbox_mode = null)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        var agent = await db.Agents
            .Include(a => a.Tools)
            .Include(a => a.Projects)
            .FirstOrDefaultAsync(a => a.Name == agent_name);

        if (agent is null)
            return $"Agent '{agent_name}' not found in DB.";

        var changes = new StringBuilder();

        if (model is not null && model != agent.Model)
        {
            changes.AppendLine($"- model: {agent.Model} → {model}");
            agent.Model = model;
        }

        if (memory_limit_mb is not null && memory_limit_mb != agent.MemoryLimitMb)
        {
            changes.AppendLine($"- memory_limit_mb: {agent.MemoryLimitMb} → {memory_limit_mb}");
            agent.MemoryLimitMb = memory_limit_mb.Value;
        }

        if (is_enabled is not null && is_enabled != agent.IsEnabled)
        {
            changes.AppendLine($"- is_enabled: {agent.IsEnabled} → {is_enabled}");
            agent.IsEnabled = is_enabled.Value;
        }

        if (image is not null && image != (agent.Image ?? ""))
        {
            changes.AppendLine($"- image: {agent.Image ?? "(default)"} → {(image == "" ? "(default)" : image)}");
            agent.Image = image == "" ? null : image;
        }

        if (permission_mode is not null && permission_mode != agent.PermissionMode)
        {
            changes.AppendLine($"- permission_mode: {agent.PermissionMode} → {permission_mode}");
            agent.PermissionMode = permission_mode;
        }

        if (max_turns is not null && max_turns != agent.MaxTurns)
        {
            changes.AppendLine($"- max_turns: {agent.MaxTurns} → {max_turns}");
            agent.MaxTurns = max_turns.Value;
        }

        if (work_dir is not null && work_dir != agent.WorkDir)
        {
            changes.AppendLine($"- work_dir: {agent.WorkDir} → {work_dir}");
            agent.WorkDir = work_dir;
        }

        if (proactive_interval_minutes is not null && proactive_interval_minutes != agent.ProactiveIntervalMinutes)
        {
            changes.AppendLine($"- proactive_interval_minutes: {agent.ProactiveIntervalMinutes} → {proactive_interval_minutes}");
            agent.ProactiveIntervalMinutes = proactive_interval_minutes.Value;
        }

        if (group_listen_mode is not null && group_listen_mode != agent.GroupListenMode)
        {
            changes.AppendLine($"- group_listen_mode: {agent.GroupListenMode} → {group_listen_mode}");
            agent.GroupListenMode = group_listen_mode;
        }

        if (group_debounce_seconds is not null && group_debounce_seconds != agent.GroupDebounceSeconds)
        {
            changes.AppendLine($"- group_debounce_seconds: {agent.GroupDebounceSeconds} → {group_debounce_seconds}");
            agent.GroupDebounceSeconds = group_debounce_seconds.Value;
        }

        if (short_name is not null && short_name != agent.ShortName)
        {
            changes.AppendLine($"- short_name: {agent.ShortName} → {short_name}");
            agent.ShortName = short_name;
        }

        if (show_stats is not null && show_stats != agent.ShowStats)
        {
            changes.AppendLine($"- show_stats: {agent.ShowStats} → {show_stats}");
            agent.ShowStats = show_stats.Value;
        }

        if (prefix_messages is not null && prefix_messages != agent.PrefixMessages)
        {
            changes.AppendLine($"- prefix_messages: {agent.PrefixMessages} → {prefix_messages}");
            agent.PrefixMessages = prefix_messages.Value;
        }

        if (suppress_tool_messages is not null && suppress_tool_messages != agent.SuppressToolMessages)
        {
            changes.AppendLine($"- suppress_tool_messages: {agent.SuppressToolMessages} → {suppress_tool_messages}");
            agent.SuppressToolMessages = suppress_tool_messages.Value;
        }

        if (telegram_send_only is not null && telegram_send_only != agent.TelegramSendOnly)
        {
            changes.AppendLine($"- telegram_send_only: {agent.TelegramSendOnly} → {telegram_send_only}");
            agent.TelegramSendOnly = telegram_send_only.Value;
        }

        if (effort is not null && effort != (agent.Effort ?? ""))
        {
            changes.AppendLine($"- effort: {agent.Effort ?? "(none)"} → {(effort == "" ? "(none)" : effort)}");
            agent.Effort = effort == "" ? null : effort;
        }

        if (json_schema is not null && json_schema != (agent.JsonSchema ?? ""))
        {
            changes.AppendLine($"- json_schema: {(agent.JsonSchema is null ? "(none)" : "(set)")} → {(json_schema == "" ? "(none)" : "(set)")}");
            agent.JsonSchema = json_schema == "" ? null : json_schema;
        }

        if (agents_json is not null && agents_json != (agent.AgentsJson ?? ""))
        {
            changes.AppendLine($"- agents_json: {(agent.AgentsJson is null ? "(none)" : "(set)")} → {(agents_json == "" ? "(none)" : "(set)")}");
            agent.AgentsJson = agents_json == "" ? null : agents_json;
        }

        if (host_port is not null && host_port != (agent.HostPort ?? 0))
        {
            changes.AppendLine($"- host_port: {agent.HostPort?.ToString() ?? "(none)"} → {(host_port == 0 ? "(none)" : host_port.ToString())}");
            agent.HostPort = host_port == 0 ? null : host_port;
        }

        if (auto_memory_enabled is not null && auto_memory_enabled != agent.AutoMemoryEnabled)
        {
            changes.AppendLine($"- auto_memory_enabled: {agent.AutoMemoryEnabled} → {auto_memory_enabled}");
            agent.AutoMemoryEnabled = auto_memory_enabled.Value;
        }

        if (provider is not null && provider != agent.Provider)
        {
            changes.AppendLine($"- provider: {agent.Provider} → {provider}");
            agent.Provider = provider;
        }

        if (codex_sandbox_mode is not null && codex_sandbox_mode != (agent.CodexSandboxMode ?? ""))
        {
            var validModes = new[] { "read-only", "workspace-write", "danger-full-access" };
            if (codex_sandbox_mode != "" && !Array.Exists(validModes, m => m == codex_sandbox_mode))
                return $"Invalid codex_sandbox_mode '{codex_sandbox_mode}'. Valid values: read-only, workspace-write, danger-full-access.";
            changes.AppendLine($"- codex_sandbox_mode: {agent.CodexSandboxMode ?? "(none)"} → {(codex_sandbox_mode == "" ? "(none)" : codex_sandbox_mode)}");
            agent.CodexSandboxMode = codex_sandbox_mode == "" ? null : codex_sandbox_mode;
        }

        if (tools is not null)
        {
            var newTools = tools
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            db.AgentTools.RemoveRange(agent.Tools);
            agent.Tools = newTools.Select(t => new AgentTool { AgentId = agent.Id, ToolName = t, IsEnabled = true }).ToList();
            changes.AppendLine($"- tools replaced ({newTools.Count} tools)");
        }

        if (projects is not null)
        {
            var newProjects = projects
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            db.AgentProjects.RemoveRange(agent.Projects);
            agent.Projects = newProjects.Select(p => new AgentProject { AgentId = agent.Id, ProjectName = p }).ToList();
            changes.AppendLine($"- projects replaced ({newProjects.Count} projects)");
        }

        if (changes.Length == 0)
            return $"No changes specified for agent '{agent_name}'.";

        await db.SaveChangesAsync();

        return $"Agent '{agent_name}' updated:\n{changes}Note: restart the agent container for changes to take effect.";
    }
}
