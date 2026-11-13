using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Helpers;
using Fleet.Orchestrator.Services;
using Fleet.Shared;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Application;

/// <summary>Who is asking for a write. The one place a rule may differ by caller.</summary>
public enum WriteAuthority
{
    /// <summary>The operator: the dashboard or a caller holding the operator token (REST).</summary>
    Operator,
    /// <summary>An agent calling an MCP tool.</summary>
    Agent,
}

/// <summary>The caller of a write: its wording and audit tag, and what it is allowed to change.</summary>
public readonly record struct WriteCaller(WriteSurface Surface, WriteAuthority Authority)
{
    /// <summary>A REST request, which the operator token gates.</summary>
    public static WriteCaller Rest => new(WriteSurface.Rest, WriteAuthority.Operator);

    /// <summary>An MCP tool call made by an agent.</summary>
    public static WriteCaller Mcp => new(WriteSurface.Mcp, WriteAuthority.Agent);

    /// <summary>The tag written to audit log lines.</summary>
    public string Tag => Surface == WriteSurface.Rest ? "rest" : "mcp";

    internal string Name(string rest, string mcp) => Surface == WriteSurface.Rest ? rest : mcp;
}

public sealed record McpEndpointSpec(string McpName, string Url, string TransportType);

public sealed record InstructionAssignment(string InstructionName, int LoadOrder);

/// <summary>
/// A partial update of one agent. A null member is "not sent" and keeps the stored value; a related
/// list, when sent, replaces the stored list.
/// </summary>
public sealed record AgentConfigPatch
{
    public string? Model { get; init; }
    public int? MemoryLimitMb { get; init; }
    public bool? IsEnabled { get; init; }
    /// <summary>"" clears (the default image).</summary>
    public string? Image { get; init; }
    public string? PermissionMode { get; init; }
    public int? MaxTurns { get; init; }
    public string? WorkDir { get; init; }
    public int? ProactiveIntervalMinutes { get; init; }
    public string? GroupListenMode { get; init; }
    public int? GroupDebounceSeconds { get; init; }
    public int? WarmupTimeoutSeconds { get; init; }
    public string? ShortName { get; init; }
    public bool? ShowStats { get; init; }
    public bool? PrefixMessages { get; init; }
    public byte? FormattingMode { get; init; }
    public bool? SuppressToolMessages { get; init; }
    public bool? TelegramSendOnly { get; init; }
    /// <summary>"" clears.</summary>
    public string? Effort { get; init; }
    /// <summary>"" clears.</summary>
    public string? JsonSchema { get; init; }
    /// <summary>"" clears.</summary>
    public string? AgentsJson { get; init; }
    /// <summary>0 clears.</summary>
    public int? HostPort { get; init; }
    public bool? AutoMemoryEnabled { get; init; }
    public bool? JournalEnabled { get; init; }
    /// <summary>Operator only (<see cref="WriteAuthority.Operator"/>): an agent may not grant itself cross-chat reads.</summary>
    public bool? JournalCrossChatEnabled { get; init; }
    public bool? ChatEnabled { get; init; }
    public string? Provider { get; init; }
    /// <summary>"" clears.</summary>
    public string? CodexSandboxMode { get; init; }
    public bool? CanReceiveChatRequests { get; init; }
    /// <summary>"" clears (the built-in default).</summary>
    public string? RequestReceivedMessage { get; init; }
    public bool? MountDockerSock { get; init; }
    /// <summary>"" clears.</summary>
    public string? OutputStyle { get; init; }
    /// <summary>"" clears (back to the cloud).</summary>
    public string? LocalBaseUrl { get; init; }
    /// <summary>The deprecated alias of <see cref="LocalBaseUrl"/>; if both are sent they must agree (A1).</summary>
    public string? LocalBaseUrlAlias { get; init; }
    /// <summary>0 clears.</summary>
    public int? ContextWindow { get; init; }
    /// <summary>"" clears.</summary>
    public string? HarnessExtra { get; init; }

    public IReadOnlyList<string>? Tools { get; init; }
    public IReadOnlyList<string>? Projects { get; init; }
    public IReadOnlyList<McpEndpointSpec>? McpEndpoints { get; init; }
    public IReadOnlyList<string>? Networks { get; init; }
    /// <summary>Operator only when it attaches a bound secret; see <see cref="AgentConfigWriter.AddEnvRefAsync"/>.</summary>
    public IReadOnlyList<string>? EnvRefs { get; init; }
    public IReadOnlyList<long>? TelegramUsers { get; init; }
    public IReadOnlyList<long>? TelegramGroups { get; init; }
    public IReadOnlyList<InstructionAssignment>? Instructions { get; init; }
}

/// <summary>A saved config update: one line per field that changed, in the tool's field names.</summary>
public sealed record AgentConfigUpdated(string AgentName, IReadOnlyList<string> Changes);

/// <summary>What an env-ref change did.</summary>
public enum EnvRefChange
{
    Added,
    AlreadyPresent,
    Removed,
    NotPresent,
}

/// <summary>
/// The one implementation of "change an agent's stored configuration". <c>PUT
/// /api/agents/{name}/config</c>, <c>update_agent_config</c> and <c>manage_agent_env_refs</c> are
/// adapters over it: they parse their input into an <see cref="AgentConfigPatch"/> and render the
/// outcome, and decide nothing.
/// </summary>
/// <remarks>
/// Every rule is checked against the agent's RESULTING state and a refusal saves nothing. The write
/// is one <c>SaveChanges</c>, so the agent row and its related rows commit together or not at all.
/// What follows the commit (the memory-ACL sync and the live Telegram allowlist broadcast) is
/// reported through <see cref="WriteOutcome{T}.NotDelivered"/> when it could not be delivered.
/// </remarks>
public sealed class AgentConfigWriter(
    IServiceScopeFactory scopes,
    IAclChangeNotifier aclNotifier,
    AgentConfigPublisherService publisher,
    ITelegramSetup telegram,
    ILogger<AgentConfigWriter> logger)
{
    private static readonly string[] ClaudeCloudEfforts = ["low", "medium", "high", "xhigh", "max"];
    private static readonly string[] CodexEfforts = ["none", "minimal", "low", "medium", "high", "xhigh"];

    public async Task<WriteOutcome<AgentConfigUpdated>> UpdateAsync(
        string agentName, AgentConfigPatch patch, WriteCaller caller, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
        if (db is null) return WriteFaults.NoDatabase;

        var agent = await db.Agents
            .Include(a => a.Tools)
            .Include(a => a.Projects)
            .Include(a => a.McpEndpoints)
            .Include(a => a.Networks)
            .Include(a => a.EnvRefs)
            .Include(a => a.TelegramUsers)
            .Include(a => a.TelegramGroups)
            .Include(a => a.Instructions)
            .AsSplitQuery()
            .FirstOrDefaultAsync(a => a.Name == agentName, ct);

        if (agent is null)
            return WriteFaults.AgentNotFound(agentName);

        // PV1 (#386): the resulting provider, before any field is applied. An empty provider stays claude.
        if (AgentProviders.DescribeFault(patch.Provider ?? agent.Provider) is { } providerFault)
            return WriteFaults.Invalid("provider_invalid", providerFault);

        // #382 A1: resolved first, so the effort check below sees the resulting local mode.
        var (baseUrlSent, baseUrl, baseUrlError) = AgentPatchHelpers.ResolveLocalBaseUrl(
            patch.LocalBaseUrl, patch.LocalBaseUrlAlias,
            caller.Name("localBaseUrl", "local_base_url"), caller.Name("anthropicBaseUrl", "anthropic_base_url"));
        if (baseUrlError is not null)
            return WriteFaults.Invalid("local_base_url_invalid", baseUrlError);

        // Only enabling needs a key. A saved true must remain editable and clearable.
        if (patch.JournalEnabled == true && !agent.JournalEnabled &&
            scope.ServiceProvider.GetRequiredService<JournalTokenService>().DescribeKeyFault() is { } journalFault)
            return WriteFaults.Invalid("journal_not_configured", journalFault);

        // The cross-chat switch is the operator's (docs/comms-journal.md): no agent grants it to itself.
        if (patch.JournalCrossChatEnabled is not null && caller.Authority != WriteAuthority.Operator)
            return WriteFaults.Invalid("operator_only",
                "The journal cross-chat switch can only be changed by the operator (dashboard or REST). Nothing was changed.");

        var changes = new List<string>();
        var oldCrossChat = agent.JournalCrossChatEnabled;

        // ── Scalars ──────────────────────────────────────────────────────────────────────────────
        if (patch.Model is not null && patch.Model != agent.Model)
        {
            changes.Add($"- model: {agent.Model} → {patch.Model}");
            agent.Model = patch.Model;
        }
        if (patch.MemoryLimitMb is { } memory && memory != agent.MemoryLimitMb)
        {
            changes.Add($"- memory_limit_mb: {agent.MemoryLimitMb} → {memory}");
            agent.MemoryLimitMb = memory;
        }
        if (patch.IsEnabled is { } enabled && enabled != agent.IsEnabled)
        {
            changes.Add($"- is_enabled: {agent.IsEnabled} → {enabled}");
            agent.IsEnabled = enabled;
        }
        if (patch.Image is not null && patch.Image != (agent.Image ?? ""))
        {
            changes.Add($"- image: {agent.Image ?? "(default)"} → {(patch.Image == "" ? "(default)" : patch.Image)}");
            agent.Image = patch.Image == "" ? null : patch.Image;
        }
        if (patch.PermissionMode is not null)
        {
            // Refused whoever asks: fleet containers run as root, and the CLI rejects the flag there.
            if (string.Equals(patch.PermissionMode, "bypassPermissions", StringComparison.OrdinalIgnoreCase))
                return WriteFaults.Invalid("permission_mode_unsupported",
                    "PermissionMode 'bypassPermissions' is not supported — fleet containers run as root and Claude CLI " +
                    "rejects --dangerously-skip-permissions for root processes. Use 'acceptEdits' with an explicit tools list.");
            if (patch.PermissionMode != agent.PermissionMode)
            {
                changes.Add($"- permission_mode: {agent.PermissionMode} → {patch.PermissionMode}");
                agent.PermissionMode = patch.PermissionMode;
            }
        }
        if (patch.MaxTurns is { } maxTurns && maxTurns != agent.MaxTurns)
        {
            changes.Add($"- max_turns: {agent.MaxTurns} → {maxTurns}");
            agent.MaxTurns = maxTurns;
        }
        if (patch.WorkDir is not null && patch.WorkDir != agent.WorkDir)
        {
            changes.Add($"- work_dir: {agent.WorkDir} → {patch.WorkDir}");
            agent.WorkDir = patch.WorkDir;
        }
        if (patch.ProactiveIntervalMinutes is { } proactive && proactive != agent.ProactiveIntervalMinutes)
        {
            changes.Add($"- proactive_interval_minutes: {agent.ProactiveIntervalMinutes} → {proactive}");
            agent.ProactiveIntervalMinutes = proactive;
        }
        if (patch.GroupListenMode is not null && patch.GroupListenMode != agent.GroupListenMode)
        {
            changes.Add($"- group_listen_mode: {agent.GroupListenMode} → {patch.GroupListenMode}");
            agent.GroupListenMode = patch.GroupListenMode;
        }
        if (patch.GroupDebounceSeconds is { } debounce && debounce != agent.GroupDebounceSeconds)
        {
            changes.Add($"- group_debounce_seconds: {agent.GroupDebounceSeconds} → {debounce}");
            agent.GroupDebounceSeconds = debounce;
        }
        if (patch.WarmupTimeoutSeconds is { } warmup)
        {
            // Rejected, never clamped (#357 MUST NOT): a hidden correction makes live configuration unauditable.
            if (WarmupTimeout.DescribeFault(warmup) is { } warmupFault)
                return WriteFaults.Invalid("warmup_timeout_invalid", warmupFault);
            if (warmup != agent.WarmupTimeoutSeconds)
            {
                changes.Add($"- warmup_timeout_seconds: {agent.WarmupTimeoutSeconds} → {warmup}");
                agent.WarmupTimeoutSeconds = warmup;
            }
        }
        if (patch.ShortName is not null)
        {
            // Blank falls back to the agent name: the short name is the agent's routing key.
            var shortName = string.IsNullOrWhiteSpace(patch.ShortName) ? agent.Name : patch.ShortName.Trim();
            if (shortName != agent.ShortName)
            {
                changes.Add($"- short_name: {agent.ShortName} → {shortName}");
                agent.ShortName = shortName;
            }
        }
        if (patch.ShowStats is { } showStats && showStats != agent.ShowStats)
        {
            changes.Add($"- show_stats: {agent.ShowStats} → {showStats}");
            agent.ShowStats = showStats;
        }
        if (patch.PrefixMessages is { } prefix && prefix != agent.PrefixMessages)
        {
            changes.Add($"- prefix_messages: {agent.PrefixMessages} → {prefix}");
            agent.PrefixMessages = prefix;
        }
        if (patch.FormattingMode is { } formatting)
        {
            if (formatting > 2)
                return WriteFaults.Invalid("formatting_mode_invalid",
                    $"Invalid {caller.Name("formattingMode", "formatting_mode")} {formatting}. Valid values: 0 (PlainText), 1 (LegacyHtml), 2 (Rich).");
            if (formatting != (byte)agent.FormattingMode)
            {
                changes.Add($"- formatting_mode: {(byte)agent.FormattingMode} → {formatting}");
                agent.FormattingMode = (FormattingMode)formatting;
            }
        }
        if (patch.SuppressToolMessages is { } suppress && suppress != agent.SuppressToolMessages)
        {
            changes.Add($"- suppress_tool_messages: {agent.SuppressToolMessages} → {suppress}");
            agent.SuppressToolMessages = suppress;
        }
        if (patch.TelegramSendOnly is { } sendOnly && sendOnly != agent.TelegramSendOnly)
        {
            changes.Add($"- telegram_send_only: {agent.TelegramSendOnly} → {sendOnly}");
            agent.TelegramSendOnly = sendOnly;
        }
        if (patch.Effort is not null && patch.Effort != (agent.Effort ?? ""))
        {
            // Checked when it changes, so re-sending a stored value never blocks an unrelated save.
            // #349: the cloud vocabulary check is skipped when the RESULTING agent is in local mode
            // (resolved provider + resolved base URL): the shared V7 rule at the finalize gate
            // decides instead, so `off` is not rejected by the cloud list first.
            var resultingProvider = patch.Provider ?? agent.Provider ?? "claude";
            var resultingBaseUrl = baseUrlSent ? baseUrl : agent.LocalBaseUrl;
            if (patch.Effort != "" && !ClaudeLocalModel.IsEnabled(resultingProvider, resultingBaseUrl)
                && DescribeCloudEffortFault(resultingProvider, patch.Effort) is { } effortFault)
                return WriteFaults.Invalid("effort_invalid", effortFault);

            changes.Add($"- effort: {agent.Effort ?? "(none)"} → {(patch.Effort == "" ? "(none)" : patch.Effort)}");
            agent.Effort = patch.Effort == "" ? null : patch.Effort;
        }
        if (patch.JsonSchema is not null && patch.JsonSchema != (agent.JsonSchema ?? ""))
        {
            changes.Add($"- json_schema: {(agent.JsonSchema is null ? "(none)" : "(set)")} → {(patch.JsonSchema == "" ? "(none)" : "(set)")}");
            agent.JsonSchema = patch.JsonSchema == "" ? null : patch.JsonSchema;
        }
        if (patch.AgentsJson is not null && patch.AgentsJson != (agent.AgentsJson ?? ""))
        {
            changes.Add($"- agents_json: {(agent.AgentsJson is null ? "(none)" : "(set)")} → {(patch.AgentsJson == "" ? "(none)" : "(set)")}");
            agent.AgentsJson = patch.AgentsJson == "" ? null : patch.AgentsJson;
        }
        if (patch.HostPort is { } hostPort && hostPort != (agent.HostPort ?? 0))
        {
            changes.Add($"- host_port: {agent.HostPort?.ToString() ?? "(none)"} → {(hostPort == 0 ? "(none)" : hostPort.ToString())}");
            agent.HostPort = hostPort == 0 ? null : hostPort;
        }
        if (patch.AutoMemoryEnabled is { } autoMemory && autoMemory != agent.AutoMemoryEnabled)
        {
            changes.Add($"- auto_memory_enabled: {agent.AutoMemoryEnabled} → {autoMemory}");
            agent.AutoMemoryEnabled = autoMemory;
        }
        if (patch.JournalEnabled is { } journal && journal != agent.JournalEnabled)
        {
            changes.Add($"- journal_enabled: {agent.JournalEnabled} → {journal}");
            agent.JournalEnabled = journal;
        }
        if (patch.JournalCrossChatEnabled is { } crossChat && crossChat != agent.JournalCrossChatEnabled)
        {
            changes.Add($"- journal_cross_chat_enabled: {agent.JournalCrossChatEnabled} → {crossChat}");
            agent.JournalCrossChatEnabled = crossChat;
        }
        // Chat (#530) is a plain switch, on by default: saving it never fails. Whether it applies is
        // decided at provisioning (ChatAgents.IsEffective).
        if (patch.ChatEnabled is { } chat && chat != agent.ChatEnabled)
        {
            changes.Add($"- chat_enabled: {agent.ChatEnabled} → {chat}");
            agent.ChatEnabled = chat;
        }
        if (patch.Provider is not null && patch.Provider != agent.Provider)
        {
            changes.Add($"- provider: {agent.Provider} → {patch.Provider}");
            agent.Provider = patch.Provider;
        }
        if (patch.CodexSandboxMode is not null)
        {
            if (patch.CodexSandboxMode != "" && !AgentPatchHelpers.ValidCodexSandboxModes.Contains(patch.CodexSandboxMode))
                return WriteFaults.Invalid("codex_sandbox_mode_invalid",
                    $"Invalid {caller.Name("CodexSandboxMode", "codex_sandbox_mode")} '{patch.CodexSandboxMode}'. " +
                    $"Valid values: {string.Join(", ", AgentPatchHelpers.ValidCodexSandboxModes)}.");
            if (patch.CodexSandboxMode != (agent.CodexSandboxMode ?? ""))
            {
                changes.Add($"- codex_sandbox_mode: {agent.CodexSandboxMode ?? "(none)"} → {(patch.CodexSandboxMode == "" ? "(none)" : patch.CodexSandboxMode)}");
                agent.CodexSandboxMode = patch.CodexSandboxMode == "" ? null : patch.CodexSandboxMode;
            }
        }
        if (patch.CanReceiveChatRequests is { } chatRequests && chatRequests != agent.CanReceiveChatRequests)
        {
            changes.Add($"- can_receive_chat_requests: {agent.CanReceiveChatRequests} → {chatRequests}");
            agent.CanReceiveChatRequests = chatRequests;
        }
        if (patch.RequestReceivedMessage is not null)
        {
            if (patch.RequestReceivedMessage.Length > 500)
                return WriteFaults.Invalid("request_received_message_too_long", caller.Name(
                    $"RequestReceivedMessage exceeds 500-character limit ({patch.RequestReceivedMessage.Length} chars).",
                    $"request_received_message exceeds the 500-character limit ({patch.RequestReceivedMessage.Length} chars)."));
            if (patch.RequestReceivedMessage != (agent.RequestReceivedMessage ?? ""))
            {
                changes.Add($"- request_received_message: {(agent.RequestReceivedMessage is null ? "(default)" : "(set)")} → {(patch.RequestReceivedMessage == "" ? "(default)" : "(set)")}");
                agent.RequestReceivedMessage = patch.RequestReceivedMessage == "" ? null : patch.RequestReceivedMessage;
            }
        }
        if (patch.MountDockerSock is { } dockerSock && dockerSock != agent.MountDockerSock)
        {
            changes.Add($"- mount_docker_sock: {agent.MountDockerSock} → {dockerSock}");
            agent.MountDockerSock = dockerSock;
        }
        if (patch.OutputStyle is not null)
        {
            // Empty string clears. A name is checked against output_styles HERE rather than at
            // provision time: an unresolvable name makes provisioning refuse outright, and finding
            // that out at the next reprovision is finding out far from the edit that caused it.
            var styleName = patch.OutputStyle.Trim();
            if (styleName.Length > 0 && !await db.OutputStyles.AnyAsync(s => s.Name == styleName, ct))
            {
                var known = await db.OutputStyles.Select(s => s.Name).OrderBy(n => n).ToListAsync(ct);
                return WriteFaults.Invalid("output_style_unknown", $"Output style '{styleName}' does not exist.", known);
            }
            if (styleName != (agent.OutputStyle ?? ""))
            {
                changes.Add($"- output_style: {agent.OutputStyle ?? "(none)"} → {(styleName.Length == 0 ? "(none)" : styleName)}");
                agent.OutputStyle = styleName.Length == 0 ? null : styleName;
            }
        }
        if (patch.ContextWindow is { } window)
        {
            // #367: 0 clears; anything else must be in range — rejected, never clamped.
            if (window != 0 && ContextWindow.DescribeFault(window) is { } windowFault)
                return WriteFaults.Invalid("context_window_invalid", windowFault);
            if (window != (agent.ContextWindow ?? 0))
            {
                changes.Add($"- context_window: {agent.ContextWindow?.ToString() ?? "(none)"} → {(window == 0 ? "(none)" : window.ToString())}");
                agent.ContextWindow = window == 0 ? null : window;
            }
        }
        // #460: "" clears; any other text is stored verbatim and checked below against the resulting
        // provider and mode.
        if (patch.HarnessExtra is not null && patch.HarnessExtra != (agent.HarnessExtra ?? ""))
        {
            changes.Add($"- harness_extra: {(agent.HarnessExtra is null ? "(none)" : "(set)")} → {(patch.HarnessExtra == "" ? "(none)" : "(set)")}");
            agent.HarnessExtra = patch.HarnessExtra == "" ? null : patch.HarnessExtra;
        }

        // ── Related lists: replaced when sent ────────────────────────────────────────────────────
        if (patch.Tools is not null)
        {
            // A tool that stays keeps its enabled flag: replacing the list must not switch a
            // disabled tool back on.
            var wasEnabled = agent.Tools.ToDictionary(t => t.ToolName, t => t.IsEnabled, StringComparer.OrdinalIgnoreCase);
            var names = Clean(patch.Tools);
            db.AgentTools.RemoveRange(agent.Tools);
            agent.Tools = names
                .Select(t => new AgentTool { AgentId = agent.Id, ToolName = t, IsEnabled = wasEnabled.GetValueOrDefault(t, true) })
                .ToList();
            changes.Add($"- tools replaced ({names.Count} tools)");
        }

        if (patch.Projects is not null)
        {
            var names = Clean(patch.Projects);
            db.AgentProjects.RemoveRange(agent.Projects);
            agent.Projects = names.Select(p => new AgentProject { AgentId = agent.Id, ProjectName = p }).ToList();
            changes.Add($"- projects replaced ({names.Count} projects)");
        }

        if (patch.McpEndpoints is not null)
        {
            db.AgentMcpEndpoints.RemoveRange(agent.McpEndpoints);
            agent.McpEndpoints = patch.McpEndpoints
                .DistinctBy(e => e.McpName, StringComparer.OrdinalIgnoreCase)
                .Select(e => new AgentMcpEndpoint { AgentId = agent.Id, McpName = e.McpName, Url = e.Url, TransportType = e.TransportType })
                .ToList();
            changes.Add($"- mcp_endpoints replaced ({agent.McpEndpoints.Count} endpoints)");
        }

        if (patch.Networks is not null)
        {
            db.AgentNetworks.RemoveRange(agent.Networks);
            agent.Networks = patch.Networks
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(n => new AgentNetwork { AgentId = agent.Id, NetworkName = n })
                .ToList();
            changes.Add($"- networks replaced ({agent.Networks.Count} networks)");
        }

        if (patch.EnvRefs is not null)
        {
            var wanted = patch.EnvRefs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var alreadyHeld = new HashSet<string>(agent.EnvRefs.Select(r => r.EnvKeyName), StringComparer.Ordinal);
            foreach (var key in wanted.Where(k => !alreadyHeld.Contains(k)))
                if (await DescribeEnvRefAttachFaultAsync(db, key, caller.Authority, ct) is { } attachFault)
                    return attachFault;

            // A key that stays keeps its placeholder hash: the running container still holds that
            // placeholder, and a save that merely repeats the list (the dashboard always sends it)
            // must not revoke the agent's bound secrets.
            var held = agent.EnvRefs
                .Where(r => r.PlaceholderSha256 is not null)
                .GroupBy(r => r.EnvKeyName, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().PlaceholderSha256, StringComparer.Ordinal);
            db.AgentEnvRefs.RemoveRange(agent.EnvRefs);
            agent.EnvRefs = wanted
                .Select(r => new AgentEnvRef { AgentId = agent.Id, EnvKeyName = r, PlaceholderSha256 = held.GetValueOrDefault(r) })
                .ToList();
            changes.Add($"- env_refs replaced ({agent.EnvRefs.Count} keys)");
        }

        // Captured before the lists change, so the diff can be told to the running agent.
        var oldUserIds = new HashSet<long>(agent.TelegramUsers.Select(u => u.UserId));
        var oldGroupIds = new HashSet<long>(agent.TelegramGroups.Select(g => g.GroupId));

        if (patch.TelegramUsers is not null)
        {
            // The owner is always on the list: a save cannot lock the operator out.
            var users = new HashSet<long>(patch.TelegramUsers);
            if (telegram.OwnerUserId is { } ownerId) users.Add(ownerId);
            db.AgentTelegramUsers.RemoveRange(agent.TelegramUsers);
            agent.TelegramUsers = users.Select(u => new AgentTelegramUser { AgentId = agent.Id, UserId = u }).ToList();
            changes.Add($"- telegram_users replaced ({users.Count} users)");
        }

        if (patch.TelegramGroups is not null)
        {
            db.AgentTelegramGroups.RemoveRange(agent.TelegramGroups);
            agent.TelegramGroups = patch.TelegramGroups
                .Distinct()
                .Select(g => new AgentTelegramGroup { AgentId = agent.Id, GroupId = g })
                .ToList();
            changes.Add($"- telegram_groups replaced ({agent.TelegramGroups.Count} groups)");
        }

        if (patch.Instructions is not null)
        {
            var names = patch.Instructions.Select(i => i.InstructionName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var instructions = await db.Instructions.Where(i => names.Contains(i.Name)).ToListAsync(ct);
            var missing = names
                .Where(n => instructions.All(i => !i.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (missing.Count > 0)
                return WriteFaults.Invalid("instruction_not_found", $"Instruction(s) not found: {string.Join(", ", missing)}");

            db.AgentInstructions.RemoveRange(agent.Instructions);
            agent.Instructions = patch.Instructions
                .DistinctBy(i => i.InstructionName, StringComparer.OrdinalIgnoreCase)
                .Select(i => new AgentInstruction
                {
                    AgentId = agent.Id,
                    InstructionId = instructions.First(x => x.Name.Equals(i.InstructionName, StringComparison.OrdinalIgnoreCase)).Id,
                    LoadOrder = i.LoadOrder,
                })
                .ToList();
            changes.Add($"- instructions replaced ({agent.Instructions.Count} instructions)");
        }

        var previousBaseUrl = agent.LocalBaseUrl;
        if (baseUrlSent) agent.LocalBaseUrl = baseUrl;

        // #340, #382, #386: the agent's resulting state (PV1, then the local-model rules), after every
        // field in the request is applied. Returning here skips SaveChanges, so nothing is persisted.
        if (AgentPatchHelpers.FinalizeLocalModel(agent) is { } localModelFault)
            return WriteFaults.Invalid("local_model_invalid", localModelFault);
        // #460 Part 3b: the allowlist, against the final provider and mode; nothing is saved on a fault.
        if (AgentPatchHelpers.DescribeHarnessExtraFault(agent) is { } harnessExtraFault)
            return WriteFaults.Invalid("harness_extra_invalid", harnessExtraFault.Message, harnessExtraFault);

        if (agent.LocalBaseUrl != previousBaseUrl)
            changes.Add($"- local_base_url: {previousBaseUrl ?? "(none)"} → {agent.LocalBaseUrl ?? "(none)"}");

        await db.SaveChangesAsync(ct);

        if (oldCrossChat != agent.JournalCrossChatEnabled)
            logger.LogInformation("Agent config changed: agent={name} field=journal_cross_chat_enabled old={old} new={new} via={via}",
                agent.Name, oldCrossChat, agent.JournalCrossChatEnabled, caller.Tag);

        // Project assignment IS the memory-ACL grant. Sync after the save so the hook sees the
        // committed assignment list, and only when the caller actually touched projects.
        if (patch.Projects is not null)
            await AgentProjectAccessSync.SyncAndBroadcastAsync(
                db, aclNotifier, agent.Name, agent.Projects.Select(p => p.ProjectName), ct);

        // Tell the running agent what changed on its allowlist, so it applies without a reprovision.
        var newUserIds = new HashSet<long>(agent.TelegramUsers.Select(u => u.UserId));
        var newGroupIds = new HashSet<long>(agent.TelegramGroups.Select(g => g.GroupId));
        var addedUsers = newUserIds.Except(oldUserIds).Select(id => new AddedUserInfo { UserId = id }).ToList();
        var removedUsers = oldUserIds.Except(newUserIds).ToList();
        var addedGroups = newGroupIds.Except(oldGroupIds).ToList();
        var removedGroups = oldGroupIds.Except(newGroupIds).ToList();
        string? notDelivered = null;
        if (addedUsers.Count > 0 || removedUsers.Count > 0 || addedGroups.Count > 0 || removedGroups.Count > 0)
        {
            var delivered = await publisher.PublishAllowlistUpdateAsync(agent.ShortName,
                addedUsers: addedUsers, removedUserIds: removedUsers,
                addedGroupIds: addedGroups, removedGroupIds: removedGroups, ct: ct);
            if (!delivered)
                notDelivered = LiveDelivery.AllowlistNotDelivered("the config, with its Telegram allowlist change,", agentName);
        }

        return WriteOutcome<AgentConfigUpdated>.Ok(new AgentConfigUpdated(agent.Name, changes), notDelivered);
    }

    /// <summary>
    /// Attaches one environment key reference to an agent. A bound secret is granted by the
    /// operator; an agent may attach it only when its binding says agents may
    /// (<c>AgentAttachable</c>), and only under the binding's exact name.
    /// </summary>
    public async Task<WriteOutcome<EnvRefChange>> AddEnvRefAsync(
        string agentName, string envKeyName, WriteCaller caller, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
        if (db is null) return WriteFaults.NoDatabase;
        var agent = await db.Agents.Include(a => a.EnvRefs).FirstOrDefaultAsync(a => a.Name == agentName, ct);
        if (agent is null)
            return WriteFaults.AgentNotFound(agentName);

        if (agent.EnvRefs.Any(r => r.EnvKeyName.Equals(envKeyName, StringComparison.OrdinalIgnoreCase)))
            return WriteOutcome<EnvRefChange>.Ok(EnvRefChange.AlreadyPresent);

        if (await DescribeEnvRefAttachFaultAsync(db, envKeyName, caller.Authority, ct) is { } attachFault)
            return attachFault;

        agent.EnvRefs.Add(new AgentEnvRef { AgentId = agent.Id, EnvKeyName = envKeyName });
        try { await db.SaveChangesAsync(ct); }
        catch (SecretStatePublicationException ex)
        {
            return WriteOutcome<EnvRefChange>.Ok(EnvRefChange.Added,
                $"Env ref '{envKeyName}' was saved for agent '{agentName}', but NOT published: {ex.Message}");
        }
        return WriteOutcome<EnvRefChange>.Ok(EnvRefChange.Added);
    }

    /// <summary>Removes one environment key reference. Any caller may give a key up.</summary>
    public async Task<WriteOutcome<EnvRefChange>> RemoveEnvRefAsync(
        string agentName, string envKeyName, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
        if (db is null) return WriteFaults.NoDatabase;
        var agent = await db.Agents.Include(a => a.EnvRefs).FirstOrDefaultAsync(a => a.Name == agentName, ct);
        if (agent is null)
            return WriteFaults.AgentNotFound(agentName);

        var existing = agent.EnvRefs.FirstOrDefault(r => r.EnvKeyName.Equals(envKeyName, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
            return WriteOutcome<EnvRefChange>.Ok(EnvRefChange.NotPresent);

        db.AgentEnvRefs.Remove(existing);
        try { await db.SaveChangesAsync(ct); }
        catch (SecretStatePublicationException ex)
        {
            // Never "Removed": the grant is gone from the database but was not revoked at the forwarder.
            return WriteOutcome<EnvRefChange>.Ok(EnvRefChange.Removed,
                $"Env ref '{envKeyName}' was deleted for agent '{agentName}', but the revocation was NOT published: {ex.Message}");
        }
        return WriteOutcome<EnvRefChange>.Ok(EnvRefChange.Removed);
    }

    /// <summary>
    /// The one rule for attaching a key that has a secret binding. The operator may always attach
    /// it. An agent may only when the binding is <c>AgentAttachable</c>, and the name is compared
    /// ignoring case so a differently-cased name cannot slip past the binding.
    /// </summary>
    private static async Task<WriteFault?> DescribeEnvRefAttachFaultAsync(
        OrchestratorDbContext db, string envKeyName, WriteAuthority authority, CancellationToken ct)
    {
        if (authority == WriteAuthority.Operator) return null;

        var binding = (await db.SecretBindings.AsNoTracking().ToListAsync(ct))
            .FirstOrDefault(b => b.EnvKeyName.Equals(envKeyName, StringComparison.OrdinalIgnoreCase));
        if (binding is null || (binding.AgentAttachable && binding.EnvKeyName == envKeyName)) return null;

        return WriteFaults.Invalid("bound_secret_operator_only",
            $"Env ref '{envKeyName}' is a bound secret that only the operator can attach "
            + "(dashboard, or PUT /api/agents/{name}/config). Nothing was changed.");
    }

    /// <summary>
    /// PV1 (#386): the cloud effort vocabulary, one case per provider; an empty provider is claude.
    /// The default branch is unreachable after the provider check and refuses rather than passing
    /// an unknown provider's effort through.
    /// </summary>
    private static string? DescribeCloudEffortFault(string provider, string effort) =>
        AgentProviders.Normalize(provider) switch
        {
            // "off" falls through to the finalize gate so the V8 fault is the one the caller sees,
            // not the generic cloud list text (#349).
            AgentProviders.Claude => effort != "off" && !ClaudeCloudEfforts.Contains(effort)
                ? $"Invalid effort '{effort}' for claude. Valid values: low, medium, high, xhigh, max."
                : null,
            AgentProviders.Codex => !CodexEfforts.Contains(effort)
                ? $"Invalid effort '{effort}' for codex. Valid values: none, minimal, low, medium, high, xhigh."
                : null,
            AgentProviders.Gemini => "Effort is not supported on gemini agents.",
            _ => AgentProviders.UnknownProviderText(provider),
        };

    private static List<string> Clean(IEnumerable<string> names) =>
        names.Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>What the writers need to know about the operator's Telegram setup.</summary>
/// <remarks>
/// Narrow on purpose: <see cref="SetupService"/> implements it, and a test supplies two values
/// without a .env file, Docker or a provisioning service.
/// </remarks>
public interface ITelegramSetup
{
    /// <summary>A usable bot token is configured. Without one an agent is headless.</summary>
    bool IsConfigured { get; }

    /// <summary>The operator's own Telegram user id, when known. It is never removed from an allowlist.</summary>
    long? OwnerUserId { get; }
}
