using System.ComponentModel;
using Fleet.Orchestrator.Application;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

/// <summary>
/// The MCP adapter over <see cref="AgentConfigWriter"/>: it maps the tool's parameters to a patch and
/// renders the outcome. The rules are the writer's, shared with <c>PUT /api/agents/{name}/config</c>.
/// </summary>
[McpServerToolType]
public sealed class UpdateAgentConfigTool(AgentConfigWriter writer)
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
        [Description("Warmup timeout in seconds before the startup ping gives up (10-600, default 60). Slow local-model cold starts may need 180. Omit to keep current. Takes effect on the next reprovision.")] int? warmup_timeout_seconds = null,
        [Description("Short name for the agent (used in group chat). Omit to keep current.")] string? short_name = null,
        [Description("Show stats in status messages. Omit to keep current.")] bool? show_stats = null,
        [Description("Prefix all outgoing telegram messages with bold [ShortName] header for shared-bot visibility. Omit to keep current.")] bool? prefix_messages = null,
        [Description("Outbound Telegram formatting mode: 0=PlainText (legacy dumb-split, no parse_mode), 1=LegacyHtml (Markdown→HTML via TelegramFormatter, ParseMode.Html), 2=Rich (same syntax emitted as sendRichMessage blocks, fallback LegacyHtml→PlainText per message). Omit to keep current.")] byte? formatting_mode = null,
        [Description("Suppress intermediate tool-use progress messages in Telegram — only post the final response. Use for agents serving non-technical users (e.g. family assistant). Omit to keep current.")] bool? suppress_tool_messages = null,
        [Description("Telegram send-only mode: skip polling and message handling, only send messages. Use when multiple agents share a bot token. Omit to keep current.")] bool? telegram_send_only = null,
        [Description("Effort level. Claude cloud: low/medium/high/xhigh/max. Claude local (local_base_url set): off/low/medium/xhigh, or empty = model default sent as xhigh. Codex (cloud or local): none..xhigh. Pass empty string to clear. Omit to keep current.")] string? effort = null,
        [Description("JSON schema string for --json-schema flag (structured output). Pass empty string to clear. Omit to keep current.")] string? json_schema = null,
        [Description("JSON string for --agents flag (inline subagents). Pass empty string to clear. Omit to keep current.")] string? agents_json = null,
        [Description("Host port for the agent's HTTP API (used by orchestrator cancel proxy via 127.0.0.1:{host_port}). Pass 0 to clear. Omit to keep current.")] int? host_port = null,
        [Description("Enable Claude's built-in auto-memory. Set to false for agents using fleet-memory (disables CLAUDE_CODE_DISABLE_AUTO_MEMORY). Omit to keep current.")] bool? auto_memory_enabled = null,
        [Description("Journal this agent's human Telegram DMs and allowed groups in Comms. Requires FLEET_COMMS_JOURNAL_KEY and takes effect on reprovision. Omit to keep current.")] bool? journal_enabled = null,
        [Description("LLM provider: claude, codex or gemini. Any other value is refused. Omit to keep current. A local-model agent keeps its URL and context window across claude and codex; send the matching model and effort too (bare tag for claude, ollama/<tag> or lmstudio/<tag> for codex). Clear local_base_url before switching to gemini.")] string? provider = null,
        [Description("Codex sandbox mode (danger-full-access, workspace-write, read-only). Pass empty string to clear. Omit to keep current. Only applies to codex agents.")] string? codex_sandbox_mode = null,
        [Description("Enable access-request flow for unknown DMs (CanReceiveChatRequests). When true, unknown DMs are forwarded to the CTO agent (FLEET_CTO_AGENT) instead of being silently dropped. Omit to keep current.")] bool? can_receive_chat_requests = null,
        [Description("Message sent to requesting user when their access request is queued. Pass empty string to use the built-in default. Max 500 characters. Omit to keep current.")] string? request_received_message = null,
        [Description("Mount /var/run/docker.sock into the container (grants host-root; leave off unless agent manages containers). Omit to keep current.")] bool? mount_docker_sock = null,
        [Description("Name of an output_styles row this agent runs with — its chat tone and register. Pass empty string to clear (no style). Omit to keep current. Takes effect on the next reprovision.")] string? output_style = null,
        [Description("Local model (#382, #386), claude or codex agents: origin of the local inference server (e.g. Ollama), e.g. http://<server-address>:11434 — origin only, never …/v1; Fleet adds /v1/messages for claude and /v1 for codex. Set, the agent runs against that server; claude needs the server's bare model tag and effort off/low/medium/xhigh (empty = model default, sent as xhigh), codex needs model ollama/<tag> or lmstudio/<tag>. Stored canonical. Pass empty string to clear (back to the cloud). Omit to keep current. Takes effect on the next reprovision.")] string? local_base_url = null,
        [Description("Deprecated alias of local_base_url. If both are sent they must agree.")] string? anthropic_base_url = null,
        [Description("Local-model agents only (local_base_url set, claude or codex): the server's context size in tokens (4096-1048576), e.g. its num_ctx or --ctx. Claude gets it as CLAUDE_CODE_MAX_CONTEXT_TOKENS, codex as model_context_window. Ignored for cloud agents. Codex compaction is best-effort: it compacts at 90% of the window, a single message or tool output larger than the space left reaches the server unchecked, and the server's HTTP 400 overflow leaves the thread failing every turn until it is reset (/cancel all, then /reset, or restart the agent). Pass 0 to clear. Omit to keep current. Takes effect on the next reprovision.")] int? context_window = null,
        [Description("Claude and codex only: extra harness settings merged over the mode defaults, one per line (max 4096 chars, 50 lines; blank and # lines ignored). Allowlist only — anything else is refused, naming the line. Codex: key = value for web_search (\"disabled\"/\"cached\"/\"live\", local: \"disabled\" only), features.view_image (bool, local: false only), model_reasoning_summary (\"none\"/\"auto\"/\"concise\"/\"detailed\"), tool_output_token_limit (1000-100000), features.apps, features.remote_plugin, features.skill_mcp_dependency_install, check_for_update_on_startup, analytics.enabled (bool), history.persistence (\"none\"/\"save-all\"). Claude: --tools <names> (the one way to narrow built-in tools: keeps the kept set, the agent's AllowedTools built-ins and these names; without it the agent keeps every built-in), --disallowedTools WebSearch, --disable-slash-commands (no value, any mode: removes the skills list and every slash command, including /compact; pair it with a --tools line that leaves out Workflow, or the plan notes the larger Workflow description), !--exclude-dynamic-system-prompt-sections, MAX_MCP_OUTPUT_TOKENS=<1000-100000>, CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=0|1. Pass empty string to clear. Omit to keep current. Takes effect on the next reprovision.")] string? harness_extra = null)
    {
        var outcome = await writer.UpdateAsync(agent_name, new AgentConfigPatch
        {
            Model = model,
            MemoryLimitMb = memory_limit_mb,
            IsEnabled = is_enabled,
            Tools = SplitList(tools),
            Projects = SplitList(projects),
            Image = image,
            PermissionMode = permission_mode,
            MaxTurns = max_turns,
            WorkDir = work_dir,
            ProactiveIntervalMinutes = proactive_interval_minutes,
            GroupListenMode = group_listen_mode,
            GroupDebounceSeconds = group_debounce_seconds,
            WarmupTimeoutSeconds = warmup_timeout_seconds,
            ShortName = short_name,
            ShowStats = show_stats,
            PrefixMessages = prefix_messages,
            FormattingMode = formatting_mode,
            SuppressToolMessages = suppress_tool_messages,
            TelegramSendOnly = telegram_send_only,
            Effort = effort,
            JsonSchema = json_schema,
            AgentsJson = agents_json,
            HostPort = host_port,
            AutoMemoryEnabled = auto_memory_enabled,
            JournalEnabled = journal_enabled,
            Provider = provider,
            CodexSandboxMode = codex_sandbox_mode,
            CanReceiveChatRequests = can_receive_chat_requests,
            RequestReceivedMessage = request_received_message,
            MountDockerSock = mount_docker_sock,
            OutputStyle = output_style,
            LocalBaseUrl = local_base_url,
            LocalBaseUrlAlias = anthropic_base_url,
            ContextWindow = context_window,
            HarnessExtra = harness_extra,
        }, WriteCaller.Mcp);

        if (outcome.Fault is { } fault)
            return fault.Code switch
            {
                "agent_not_found" => $"Agent '{agent_name}' not found in DB.",
                "journal_not_configured" => System.Text.Json.JsonSerializer.Serialize(new { error = fault.Code, detail = fault.Message }),
                "local_model_invalid" => $"Invalid local model configuration: {fault.Message}",
                "harness_extra_invalid" => $"Invalid harness_extra: {fault.Message}",
                "output_style_unknown" => $"{fault.Message} Known styles: "
                    + (fault.Detail is List<string> { Count: > 0 } known ? string.Join(", ", known) : "(none)"),
                _ => fault.Message,
            };
        if (outcome.NotDelivered is { } notDelivered)
            return notDelivered;

        var changes = outcome.Value!.Changes;
        if (changes.Count == 0)
            return $"No changes specified for agent '{agent_name}'.";

        // Settings are written into the container when it is provisioned. A restart re-runs the same
        // container with the old ones, so it is not what applies a saved change.
        return $"Agent '{agent_name}' updated:\n{string.Concat(changes.Select(c => c + Environment.NewLine))}Note: reprovision the agent for provision-time changes to take effect; a restart does not apply them.";
    }

    /// <summary>A comma-separated list as the tool takes it; null stays "not sent".</summary>
    private static List<string>? SplitList(string? csv) =>
        csv?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
