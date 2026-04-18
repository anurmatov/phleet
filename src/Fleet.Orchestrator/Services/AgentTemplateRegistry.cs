namespace Fleet.Orchestrator.Services;

public record AgentTemplateSummary(
    string Name,
    string DisplayName,
    string Description,
    string DefaultModel,
    int ToolCount,
    int McpCount);

public record AgentTemplateMcpEntry(string McpName, string Url, string TransportType);

public record AgentTemplateToolEntry(string ToolName, bool IsEnabled);

public record AgentTemplateInstructionEntry(string Name, int LoadOrder);

public record AgentTemplateConfig(
    string Model,
    string Role,
    string Provider,
    int MemoryLimitMb,
    string PermissionMode,
    int MaxTurns,
    string WorkDir,
    int ProactiveIntervalMinutes,
    string GroupListenMode,
    int GroupDebounceSeconds,
    bool ShowStats,
    bool PrefixMessages,
    bool SuppressToolMessages,
    bool TelegramSendOnly,
    bool AutoMemoryEnabled,
    IReadOnlyList<AgentTemplateToolEntry> Tools,
    IReadOnlyList<string> Projects,
    IReadOnlyList<AgentTemplateMcpEntry> McpEndpoints,
    IReadOnlyList<string> Networks,
    IReadOnlyList<string> EnvRefs,
    IReadOnlyList<AgentTemplateInstructionEntry> Instructions);

public record AgentTemplateEntry(
    string Name,
    string DisplayName,
    string Description,
    AgentTemplateConfig Config);

public static class AgentTemplateRegistry
{
    private static readonly IReadOnlyList<AgentTemplateMcpEntry> CoreMcps =
    [
        new("fleet-memory",    "http://fleet-memory:3100",           "http"),
        new("fleet-telegram",  "http://fleet-telegram:3800",         "http"),
    ];

    private static readonly IReadOnlyList<AgentTemplateMcpEntry> FullMcps =
    [
        new("fleet-memory",      "http://fleet-memory:3100",              "http"),
        new("fleet-playwright",  "http://fleet-playwright:3200/mcp",      "http"),
        new("fleet-temporal",    "http://fleet-temporal-bridge:3001",     "http"),
        new("fleet-orchestrator","http://fleet-orchestrator:3600/mcp",    "http"),
        new("fleet-telegram",    "http://fleet-telegram:3800",            "http"),
    ];

    private static IReadOnlyList<AgentTemplateToolEntry> T(params string[] names) =>
        names.Select(n => new AgentTemplateToolEntry(n, true)).ToList();

    private static readonly IReadOnlyList<AgentTemplateToolEntry> CtoTools = T(
        "Read", "Glob", "Grep", "WebFetch", "WebSearch",
        "mcp__fleet-memory__memory_delete", "mcp__fleet-memory__memory_get",
        "mcp__fleet-memory__memory_list", "mcp__fleet-memory__memory_search",
        "mcp__fleet-memory__memory_stats", "mcp__fleet-memory__memory_store",
        "mcp__fleet-memory__memory_update",
        "mcp__fleet-playwright__browser_click", "mcp__fleet-playwright__browser_close",
        "mcp__fleet-playwright__browser_console_messages", "mcp__fleet-playwright__browser_evaluate",
        "mcp__fleet-playwright__browser_fill_form", "mcp__fleet-playwright__browser_hover",
        "mcp__fleet-playwright__browser_navigate", "mcp__fleet-playwright__browser_navigate_back",
        "mcp__fleet-playwright__browser_network_requests", "mcp__fleet-playwright__browser_press_key",
        "mcp__fleet-playwright__browser_resize", "mcp__fleet-playwright__browser_run_code",
        "mcp__fleet-playwright__browser_select_option", "mcp__fleet-playwright__browser_snapshot",
        "mcp__fleet-playwright__browser_tabs", "mcp__fleet-playwright__browser_take_screenshot",
        "mcp__fleet-playwright__browser_type", "mcp__fleet-playwright__browser_wait_for",
        "mcp__fleet-temporal__request_memory_store",
        "mcp__fleet-temporal__temporal_cancel_workflow", "mcp__fleet-temporal__temporal_create_schedule",
        "mcp__fleet-temporal__temporal_delete_schedule", "mcp__fleet-temporal__temporal_describe_schedule",
        "mcp__fleet-temporal__temporal_get_workflow_result", "mcp__fleet-temporal__temporal_get_workflow_status",
        "mcp__fleet-temporal__temporal_list_schedules", "mcp__fleet-temporal__temporal_list_workflow_types",
        "mcp__fleet-temporal__temporal_list_workflows", "mcp__fleet-temporal__temporal_signal_workflow",
        "mcp__fleet-temporal__temporal_start_workflow", "mcp__fleet-temporal__temporal_terminate_workflow",
        "mcp__fleet-orchestrator__agent_logs", "mcp__fleet-orchestrator__create_agent",
        "mcp__fleet-orchestrator__create_instruction", "mcp__fleet-orchestrator__create_project_context",
        "mcp__fleet-orchestrator__create_workflow_definition", "mcp__fleet-orchestrator__deprovision_agent",
        "mcp__fleet-orchestrator__diff_instruction_versions", "mcp__fleet-orchestrator__ensure_networks_exist",
        "mcp__fleet-orchestrator__get_agent_config", "mcp__fleet-orchestrator__get_agent_history",
        "mcp__fleet-orchestrator__get_agent_status", "mcp__fleet-orchestrator__get_project_context",
        "mcp__fleet-orchestrator__get_uwe_reference", "mcp__fleet-orchestrator__get_workflow_definition",
        "mcp__fleet-orchestrator__list_agent_configs", "mcp__fleet-orchestrator__list_agents",
        "mcp__fleet-orchestrator__list_instruction_versions", "mcp__fleet-orchestrator__list_project_contexts",
        "mcp__fleet-orchestrator__list_repositories", "mcp__fleet-orchestrator__list_workflow_definitions",
        "mcp__fleet-orchestrator__manage_agent_env_refs", "mcp__fleet-orchestrator__manage_agent_instructions",
        "mcp__fleet-orchestrator__manage_agent_mcp_endpoints", "mcp__fleet-orchestrator__manage_agent_networks",
        "mcp__fleet-orchestrator__manage_agent_telegram_groups", "mcp__fleet-orchestrator__manage_agent_telegram_users",
        "mcp__fleet-orchestrator__manage_repository", "mcp__fleet-orchestrator__preview_agent_provision",
        "mcp__fleet-orchestrator__provision_agent", "mcp__fleet-orchestrator__reprovision_agent",
        "mcp__fleet-orchestrator__restart_agent", "mcp__fleet-orchestrator__restart_agent_with_version",
        "mcp__fleet-orchestrator__rollback_instruction", "mcp__fleet-orchestrator__rollback_project_context",
        "mcp__fleet-orchestrator__start_agent", "mcp__fleet-orchestrator__stop_agent",
        "mcp__fleet-orchestrator__system_health", "mcp__fleet-orchestrator__update_agent_config",
        "mcp__fleet-orchestrator__update_instruction", "mcp__fleet-orchestrator__update_project_context",
        "mcp__fleet-orchestrator__update_workflow_definition",
        "mcp__fleet-telegram__send_message", "mcp__fleet-telegram__get_chat_info"
    );

    private static readonly IReadOnlyList<AgentTemplateToolEntry> DevTools = T(
        "Read", "Glob", "Grep", "Edit", "Write", "Bash", "WebFetch", "WebSearch",
        "mcp__fleet-memory__memory_get", "mcp__fleet-memory__memory_list",
        "mcp__fleet-memory__memory_search", "mcp__fleet-memory__memory_stats",
        "mcp__fleet-temporal__request_memory_store",
        "mcp__fleet-telegram__send_message", "mcp__fleet-telegram__get_chat_info"
    );

    private static readonly IReadOnlyList<AgentTemplateToolEntry> OpsTools = T(
        "Read", "Glob", "Grep", "Bash", "WebFetch",
        "mcp__fleet-memory__memory_get", "mcp__fleet-memory__memory_list",
        "mcp__fleet-memory__memory_search", "mcp__fleet-memory__memory_stats",
        "mcp__fleet-temporal__request_memory_store",
        "mcp__fleet-telegram__send_message", "mcp__fleet-telegram__get_chat_info"
    );

    private static readonly IReadOnlyList<AgentTemplateToolEntry> PmTools = T(
        "Read", "Glob", "Grep", "WebFetch", "WebSearch",
        "mcp__fleet-memory__memory_get", "mcp__fleet-memory__memory_list",
        "mcp__fleet-memory__memory_search", "mcp__fleet-memory__memory_stats",
        "mcp__fleet-temporal__request_memory_store",
        "mcp__fleet-playwright__browser_navigate", "mcp__fleet-playwright__browser_snapshot",
        "mcp__fleet-playwright__browser_take_screenshot",
        "mcp__fleet-telegram__send_message", "mcp__fleet-telegram__get_chat_info"
    );

    private static readonly IReadOnlyList<string> CoreEnvRefs =
        ["TELEGRAM_CTO_BOT_TOKEN", "GITHUB_APP_ID", "GITHUB_APP_PEM"];

    private static readonly IReadOnlyList<string> CoreNetworks = ["fleet-net"];

    private static readonly Dictionary<string, AgentTemplateEntry> _templates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cto"] = new AgentTemplateEntry(
            Name: "cto",
            DisplayName: "CTO Agent",
            Description: "Orchestration and oversight — manages agents, workflows, memory, and the fleet stack",
            Config: new AgentTemplateConfig(
                Model: "claude-opus-4-6",
                Role: "co-cto",
                Provider: "claude",
                MemoryLimitMb: 4096,
                PermissionMode: "acceptEdits",
                MaxTurns: 50,
                WorkDir: "/workspace",
                ProactiveIntervalMinutes: 60,
                GroupListenMode: "all",
                GroupDebounceSeconds: 15,
                ShowStats: false,
                PrefixMessages: false,
                SuppressToolMessages: false,
                TelegramSendOnly: false,
                AutoMemoryEnabled: true,
                Tools: CtoTools,
                Projects: [],
                McpEndpoints: FullMcps,
                Networks: CoreNetworks,
                EnvRefs: CoreEnvRefs,
                Instructions: [new("base", 1), new("co-cto", 2)])),

        ["dev"] = new AgentTemplateEntry(
            Name: "dev",
            DisplayName: "Developer Agent",
            Description: "Implements features, fixes bugs, writes tests, opens PRs",
            Config: new AgentTemplateConfig(
                Model: "claude-sonnet-4-6",
                Role: "developer",
                Provider: "claude",
                MemoryLimitMb: 2048,
                PermissionMode: "acceptEdits",
                MaxTurns: 50,
                WorkDir: "/workspace",
                ProactiveIntervalMinutes: 0,
                GroupListenMode: "mention",
                GroupDebounceSeconds: 15,
                ShowStats: false,
                PrefixMessages: false,
                SuppressToolMessages: false,
                TelegramSendOnly: false,
                AutoMemoryEnabled: true,
                Tools: DevTools,
                Projects: [],
                McpEndpoints: CoreMcps,
                Networks: CoreNetworks,
                EnvRefs: CoreEnvRefs,
                Instructions: [new("base", 1)])),

        ["ops"] = new AgentTemplateEntry(
            Name: "ops",
            DisplayName: "Ops Agent",
            Description: "Monitors infra, deploys services, manages servers and CI/CD",
            Config: new AgentTemplateConfig(
                Model: "claude-sonnet-4-6",
                Role: "devops",
                Provider: "claude",
                MemoryLimitMb: 2048,
                PermissionMode: "acceptEdits",
                MaxTurns: 50,
                WorkDir: "/workspace",
                ProactiveIntervalMinutes: 0,
                GroupListenMode: "mention",
                GroupDebounceSeconds: 15,
                ShowStats: false,
                PrefixMessages: false,
                SuppressToolMessages: false,
                TelegramSendOnly: false,
                AutoMemoryEnabled: true,
                Tools: OpsTools,
                Projects: [],
                McpEndpoints: CoreMcps,
                Networks: CoreNetworks,
                EnvRefs: CoreEnvRefs,
                Instructions: [new("base", 1)])),

        ["pm"] = new AgentTemplateEntry(
            Name: "pm",
            DisplayName: "PM Agent",
            Description: "Product management — specs, issue creation, user research, and stakeholder comms",
            Config: new AgentTemplateConfig(
                Model: "claude-sonnet-4-6",
                Role: "product-manager",
                Provider: "claude",
                MemoryLimitMb: 2048,
                PermissionMode: "acceptEdits",
                MaxTurns: 50,
                WorkDir: "/workspace",
                ProactiveIntervalMinutes: 0,
                GroupListenMode: "mention",
                GroupDebounceSeconds: 15,
                ShowStats: false,
                PrefixMessages: false,
                SuppressToolMessages: false,
                TelegramSendOnly: false,
                AutoMemoryEnabled: true,
                Tools: PmTools,
                Projects: [],
                McpEndpoints: [
                    new("fleet-memory",     "http://fleet-memory:3100",           "http"),
                    new("fleet-playwright", "http://fleet-playwright:3200/mcp",   "http"),
                    new("fleet-telegram",   "http://fleet-telegram:3800",         "http"),
                ],
                Networks: CoreNetworks,
                EnvRefs: CoreEnvRefs,
                Instructions: [new("base", 1)])),
    };

    public static IReadOnlyList<AgentTemplateSummary> GetAll() =>
        _templates.Values
            .Select(e => new AgentTemplateSummary(
                e.Name,
                e.DisplayName,
                e.Description,
                e.Config.Model,
                e.Config.Tools.Count,
                e.Config.McpEndpoints.Count))
            .ToList();

    public static AgentTemplateEntry? TryGet(string name) =>
        _templates.TryGetValue(name, out var entry) ? entry : null;
}
