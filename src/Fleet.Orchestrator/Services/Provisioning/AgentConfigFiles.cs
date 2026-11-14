using System.Text.Json;
using Fleet.Orchestrator.Data;
using Fleet.Shared;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// The contents of the files a provision writes for an agent: <c>appsettings.json</c>,
/// <c>.mcp.json</c> and <c>settings.json</c>, with the rules they share. Pure: an agent row in, text
/// out, so every one of them is tested without Docker or a database.
/// </summary>
internal static class AgentConfigFiles
{
    internal const string JournalMcpServerName = "fleet-comms-journal";
    internal const string JournalFilesServerName = "fleet-journal-files";
    internal const string JournalFilesGrant = "mcp__fleet-journal-files__fetch_attachment";
    internal static bool HasFilesGrant(Agent agent) => agent.Tools.Any(t => t.IsEnabled && string.Equals(t.ToolName, JournalFilesGrant, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Header support is a property of the pinned provider CLI, not something provisioning probes
    /// at runtime. Flip a false value only with a pinned-CLI tools/list transcript in the PR.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, bool> McpHeaderSupport =
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude"] = true,
            ["codex"] = true,
            ["gemini"] = false,
        };

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    /// <summary>
    /// Whether this agent resolves its style as a Claude Code style FILE, as opposed to having the
    /// same text inlined into its prompt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Output styles are a Claude Code feature: only a claude agent has anything that reads the
    /// file or the <c>settings.json</c> key. Codex and gemini carry the identical text in
    /// <c>Agent.OutputStyleBody</c> instead — see <see cref="GenerateAppsettingsJson"/>. The split
    /// is here, in one predicate, so no path can give a provider the file and forget the prompt.
    /// </para>
    /// <para>
    /// A claude agent in local-model mode takes the inlined path too (#365). Claude Code
    /// re-asserts an active style by appending a system-role message every turn, and a local
    /// server that folds every system message into the top block then changes the prompt prefix
    /// on every turn, so its KV cache never matches and each message re-prefills the whole prompt.
    /// </para>
    /// </remarks>
    internal static bool HasStyleFile(Agent agent) =>
        !string.IsNullOrWhiteSpace(agent.OutputStyle) &&
        string.Equals(agent.Provider, "claude", StringComparison.OrdinalIgnoreCase) &&
        !ClaudeLocalModel.IsEnabled(agent.Provider, agent.LocalBaseUrl);

    /// <summary>
    /// The agent's assigned instructions in load order, with the tiebreak applied.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>One definition, used by both generators.</b> The files under <c>.generated/roles/</c>
    /// and the <c>InstructionOrder</c> list in <c>appsettings.json</c> describe the same set, and
    /// the agent inlines the files in the order that list gives. Two sort expressions would be two
    /// answers to one question, and the disagreement would surface as an instruction that is on
    /// disk and never assembled — which is the defect #309 exists to close.
    /// </para>
    /// <para>
    /// <c>LoadOrder</c> is not unique, so it is not a total order on its own. The name is the
    /// tiebreak, ordinal, so two instructions sharing a load order have a defined position rather
    /// than whatever the database happened to return.
    /// </para>
    /// </remarks>
    internal static List<AgentInstruction> OrderedInstructions(Agent agent) =>
        agent.Instructions
            .OrderBy(ai => ai.LoadOrder)
            .ThenBy(ai => ai.Instruction.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The <c>roles/</c> subdirectory an instruction is written to.
    /// </summary>
    /// <remarks>
    /// The <c>base</c> → <c>_base</c> rename lives here and nowhere else. The agent is handed the
    /// resulting directory names rather than the instruction names, so it never has to re-implement
    /// this mapping — a second copy of it is a second thing that can drift.
    /// </remarks>
    internal static string InstructionDirectoryName(string instructionName) =>
        instructionName == "base" ? "_base" : instructionName;

    /// <summary>The generated <c>Agent.AllowedTools</c> list, which the agent reads as its own.</summary>
    internal static List<string> AllowedToolsFor(Agent agent, string ctoAgentName)
    {
        var tools = agent.Tools.Where(t => t.IsEnabled).OrderBy(t => t.ToolName)
            .Select(t => JournalGrants.IsSendGrant(t.ToolName) ? JournalGrants.SendGrant : string.Equals(t.ToolName, JournalFilesGrant, StringComparison.OrdinalIgnoreCase) ? JournalFilesGrant : t.ToolName).ToList();

        // Codex derives config.toml enabled_tools from AllowedTools (entrypoint.sh). Auto-grant
        // the same baseline tools that GenerateSettingsJson grants for claude/gemini, but only
        // for codex: other providers do not read AllowedTools this way.
        if (string.Equals(agent.Provider, "codex", StringComparison.OrdinalIgnoreCase))
        {
            if (!tools.Contains("mcp__fleet-memory__memory_get", StringComparer.OrdinalIgnoreCase))
                tools.Add("mcp__fleet-memory__memory_get");

            if (!string.IsNullOrWhiteSpace(ctoAgentName) &&
                !string.Equals(agent.Name, ctoAgentName, StringComparison.OrdinalIgnoreCase) &&
                !tools.Contains("mcp__fleet-temporal__notify_cto", StringComparer.OrdinalIgnoreCase))
            {
                tools.Add("mcp__fleet-temporal__notify_cto");
            }

            tools.Sort(StringComparer.OrdinalIgnoreCase);
        }

        return tools;
    }

    /// <summary>
    /// The <see cref="HarnessDefaults.Build"/> input for a claude or codex agent (#460): the values the
    /// agent itself reads from its generated config. Provisioning and the effective-settings view both
    /// call this, so the view and the generated <c>codex-root.toml</c> come from one plan.
    /// </summary>
    /// <remarks>
    /// <c>AllowedTools</c> is the list the agent ends up with: the generated list bound onto the agent's
    /// own six-tool default (<see cref="HarnessDefaults.AgentDefaultAllowedTools"/>), which the binder
    /// appends to rather than replaces. It is built without the CTO name: the plan reads only built-in
    /// names, and the CTO auto-grant is an <c>mcp__</c> entry.
    /// </remarks>
    internal static HarnessInput HarnessInputFor(Agent agent)
    {
        var local = LocalModel.IsEnabled(agent.Provider, agent.LocalBaseUrl);
        return new HarnessInput
        {
            Provider = agent.Provider,
            Local = local,
            ContextWindow = local ? agent.ContextWindow : null,
            AllowedTools = [.. HarnessDefaults.AgentDefaultAllowedTools, .. AllowedToolsFor(agent, ctoAgentName: "")],
            HarnessExtra = agent.HarnessExtra,
            LocalBaseUrl = local ? LocalModel.CanonicalizeBaseUrl(agent.LocalBaseUrl!) : null,
            Model = agent.Model,
            Effort = agent.Effort,
        };
    }

    internal static string GenerateAppsettingsJson(
        Agent agent,
        string ctoAgentName,
        OutputStyle? style = null,
        JournalProvisioning? journal = null, string? primaryHumanUserId = null,
        ChatProvisioning? chat = null)
    {
        var primaryId = ParsePrimaryHumanUserId(primaryHumanUserId);
        if (!string.IsNullOrWhiteSpace(agent.OutputStyle) && style is null)
            throw new InvalidOperationException(
                $"Agent '{agent.Name}' names output style '{agent.OutputStyle}', which has no row in " +
                "output_styles — refusing to generate config that drops the style silently.");

        // #340 D1 point 2: a row edited by hand into an invalid state stops here, and provisioning
        // refuses with the named fault instead of starting the agent misconfigured. A reprovision
        // renders before it removes anything, so the old container keeps running.
        if (ProvisioningFaults.DescribeLocalModelFault(agent) is { } localFault)
            throw new InvalidOperationException($"Agent '{agent.Name}' cannot be provisioned: {localFault}");

        var tools = AllowedToolsFor(agent, ctoAgentName);
        var projects   = agent.Projects.Select(p => p.ProjectName).ToList();
        var hosted     = HostedModelProviders.TryResolve(agent.Provider, agent.Model, out var hostedProvider, out _);

        var obj = new
        {
            Agent = new
            {
                agent.Name,
                agent.ContainerName,
                agent.Role,
                agent.Model,
                agent.Provider,
                Projects     = projects,
                AllowedTools = tools,
                agent.PermissionMode,
                agent.MaxTurns,
                agent.WorkDir,
                agent.ProactiveIntervalMinutes,
                agent.GroupListenMode,
                agent.GroupDebounceSeconds,
                agent.WarmupTimeoutSeconds,
                agent.ShortName,
                agent.ShowStats,
                agent.PrefixMessages,
                agent.FormattingMode,
                agent.SuppressToolMessages,
                agent.Effort,
                agent.JsonSchema,
                agent.AgentsJson,
                agent.CodexSandboxMode,

                // #335 D5. Computed from the shared registry so entrypoint.sh can decide the key
                // handoff without a prefix list of its own, and the agent can check parity (D8).
                HostedProvider       = hosted,
                HostedProviderKeyEnv = hosted ? hostedProvider.KeyEnvVar : null,

                // #340. Always canonical, so the agent's startup gate can treat any other form as
                // version skew or a hand edit. null for every agent not in local mode.
                AnthropicBaseUrl = ClaudeLocalModel.IsEnabled(agent.Provider, agent.LocalBaseUrl)
                    ? ClaudeLocalModel.CanonicalizeBaseUrl(agent.LocalBaseUrl!)
                    : null,

                // #309. Every assigned instruction, as roles/ directory names, in load order.
                //
                // Before this the agent read two fixed paths — roles/_base and roles/{Role} — so
                // every other assigned instruction was written to disk and never read, with no
                // error and no log line. The agent cannot derive this list itself: a directory
                // listing has no load order, and it cannot distinguish an assigned instruction
                // from a stale directory left by one that was unassigned.
                InstructionOrder = OrderedInstructions(agent)
                    .Select(ai => InstructionDirectoryName(ai.Instruction.Name))
                    .ToList(),
            },
            Telegram = new
            {
                AllowedUserIds  = agent.TelegramUsers.Select(u => u.UserId).ToList(),
                AllowedGroupIds = agent.TelegramGroups.Select(g => g.GroupId).ToList(),
                SendOnly = agent.TelegramSendOnly,
                CanReceiveChatRequests = agent.CanReceiveChatRequests,
                RequestReceivedMessage = agent.RequestReceivedMessage,
            },
        };

        var json = JsonSerializer.Serialize(obj, IndentedJson);

        // Inlined for codex, gemini and local-model claude (#365). Cloud claude reads the style file
        // instead, and putting the text in both places would assert the same rules twice.
        //
        // Added by editing the serialized document rather than by a nullable property on the
        // anonymous type above, because a null property still SERIALIZES — and an agent with no
        // style must produce the same bytes it produced before this existed.
        var codexLocal = LocalModel.IsCodexEnabled(agent.Provider, agent.LocalBaseUrl);
        if ((style is null || HasStyleFile(agent))
            && journal?.IngestToken is null
            && journal?.ReadToken is null
            && !codexLocal
            && !HasTelegramBot(agent)
            && agent.HarnessExtra is null
            && chat is null)
            return json;

        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        if (HasTelegramBot(agent))
        {
            node["Agent"]!.AsObject()["ReplyLookup"] = ReplyLookupState(agent, journal);
            if (primaryId is { } primary) node["Telegram"]!.AsObject()["PrimaryHumanUserId"] = primary;
        }
        if (style is not null && !HasStyleFile(agent))
            node["Agent"]!.AsObject()["OutputStyleBody"] = OutputStyleRenderer.ForPrompt(style);
        if (journal?.ReadToken is not null && SupportsMcpHeaders(agent.Provider)
            && agent.McpEndpoints.Any(e => string.Equals(e.McpName, JournalMcpServerName, StringComparison.OrdinalIgnoreCase)))
            node["Agent"]!.AsObject()["McpHeaderSupport"] = true;

        // #382: codex local mode only, so every other agent keeps its bytes. The agent sets the URL
        // as CODEX_OSS_BASE_URL on the codex child and sends the window as model_context_window.
        if (codexLocal)
        {
            var agentNode = node["Agent"]!.AsObject();
            agentNode["CodexOssBaseUrl"] = LocalModel.CodexOssBaseUrl(LocalModel.CanonicalizeBaseUrl(agent.LocalBaseUrl!));
            if (agent.ContextWindow is int window)
                agentNode["ContextWindow"] = window;
        }

        // #460: written only when set, so every agent without the field keeps its bytes and an older
        // agent image ignores the unknown key.
        if (agent.HarnessExtra is not null)
            node["Agent"]!.AsObject()["HarnessExtra"] = agent.HarnessExtra;
        if (journal?.IngestToken is not null)
        {
            node["Journal"] = new System.Text.Json.Nodes.JsonObject
            {
                ["IngestToken"] = journal.IngestToken,
                ["ExcludedChatIds"] = string.Join(',', journal.ExcludedChatIds),
            };

            // Written only when true, so an agent in a deployment with no bucket produces the exact
            // bytes it produced before media existed. Absent means false on the agent side, which is
            // the safe default and keeps this a one-write rollback.
            if (journal.FilesEnabled || journal.SendEnabled)
            {
                node["Journal"]!.AsObject()["ReadToken"] = journal.ReadToken;
                if (journal.FilesEnabled) node["Journal"]!.AsObject()["FilesEnabled"] = true;
                if (journal.SendEnabled) node["Journal"]!.AsObject()["SendEnabled"] = true;
            }
            if (journal.CrossChatToken is not null)
            {
                node["Journal"]!.AsObject()["CrossChatEnabled"] = true;
                node["Journal"]!.AsObject()["CrossChatToken"] = journal.CrossChatToken;
            }
            if (journal.MediaEnabled)
                node["Journal"]!.AsObject()["MediaEnabled"] = true;
        }

        // #530. Written only for an agent chat applies to (ChatAgents.IsEffective), so every other
        // agent — chat off, or no chat service on this deployment — keeps its bytes. The
        // section's presence is what turns the agent's chat path on; it names no agent and no
        // broker, because the agent already holds both.
        if (chat is not null)
        {
            var conversations = new System.Text.Json.Nodes.JsonObject
            {
                ["SouthBaseUrl"] = chat.SouthBaseUrl,
                ["SouthBearerToken"] = chat.SouthToken,
            };
            if (chat.PrimaryHumanPrincipalId is not null)
                conversations["PrimaryHumanPrincipalId"] = chat.PrimaryHumanPrincipalId;
            node["Conversations"] = conversations;
        }
        return node.ToJsonString(IndentedJson);
    }

    /// <summary>
    /// Appends ?agent={agentName} to a fleet-internal MCP URL, stripping any pre-existing
    /// query string first. Prevents double-appending if the URL was stored in the DB
    /// with an old ?agent= already attached (e.g. after a manual DB edit or re-provision).
    /// Note: ALL existing query params are dropped, not just ?agent=. Fleet-internal MCP
    /// URLs never carry other query params, so this is intentional and safe.
    /// </summary>
    internal static string WithAgentParam(string url, string agentName)
    {
        var trimmed = url.TrimEnd('/');
        var questionIdx = trimmed.IndexOf('?');
        var basePath = (questionIdx >= 0 ? trimmed[..questionIdx] : trimmed).TrimEnd('/');
        return $"{basePath}?agent={agentName}";
    }

    internal static string WithAgentTelegramParams(string url, string agentName, byte formattingMode)
    {
        var trimmed = url.TrimEnd('/');
        var questionIdx = trimmed.IndexOf('?');
        var basePath = (questionIdx >= 0 ? trimmed[..questionIdx] : trimmed).TrimEnd('/');
        return $"{basePath}?agent={agentName}&formatting_mode={formattingMode}";
    }

    /// <summary>
    /// Normalizes the FleetMemory:McpUrl config value for use in the auto-inject path.
    /// Strips a pure trailing /mcp segment (case-insensitive) and any leftover trailing slash.
    /// Falls back to the hardcoded default when the value is empty, null, or not a valid URI.
    /// Must only be applied to the auto-inject fallback URL, never to explicit DB rows.
    /// </summary>
    internal static string NormalizeFleetMemoryMcpUrl(string? rawUrl)
    {
        const string DefaultUrl = "http://fleet-memory:3100";

        if (string.IsNullOrWhiteSpace(rawUrl) || !Uri.TryCreate(rawUrl, UriKind.Absolute, out _))
            return DefaultUrl;

        // Strip query string; work on path only.
        var withoutQuery = rawUrl.Split('?')[0].TrimEnd('/');

        // Strip a pure trailing /mcp (case-insensitive). A segment like /mcp/v1 is left intact.
        if (withoutQuery.EndsWith("/mcp", StringComparison.OrdinalIgnoreCase))
            withoutQuery = withoutQuery[..^4];

        return withoutQuery.TrimEnd('/');
    }

    internal static string GenerateMcpJson(
        Agent agent,
        string fleetMemoryMcpUrl,
        string? journalReadToken = null, bool filesEnabled = false)
    {
        if (agent.McpEndpoints.Any(e => string.Equals(e.McpName, JournalFilesServerName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("journal_files_endpoint_reserved");
        var mcpServers = agent.McpEndpoints
            .OrderBy(e => e.McpName)
            .ToDictionary(
                e => e.McpName,
                e =>
                {
                    if (string.Equals(e.McpName, JournalMcpServerName, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!SupportsMcpHeaders(agent.Provider))
                            throw new InvalidOperationException("journal_headers_unsupported");
                        if (string.IsNullOrWhiteSpace(journalReadToken))
                            throw new InvalidOperationException("journal_key_missing");

                        return (object)new
                        {
                            type = e.TransportType,
                            url = WithoutQuery(e.Url),
                            headers = new Dictionary<string, string>
                            {
                                ["Authorization"] = $"Bearer {journalReadToken}",
                            },
                        };
                    }

                    // Append ?agent={name} to fleet-telegram, fleet-memory, and fleet-temporal URLs
                    // so each server can identify the calling agent without relying on the LLM to pass it.
                    // WithAgentParam strips any existing query string before appending to prevent
                    // double-appending when the URL was stored in the DB with ?agent= already.
                    // fleet-telegram also gets ?formatting_mode={n} so send tools can honour the agent's
                    // FormattingMode without an extra orchestrator round-trip on every send.
                    var url = e.McpName == "fleet-telegram"
                        ? WithAgentTelegramParams(e.Url, agent.Name, (byte)agent.FormattingMode)
                        : (e.McpName == "fleet-memory" || e.McpName == "fleet-temporal")
                            ? WithAgentParam(e.Url, agent.Name)
                            : e.Url;
                    return (object)new { type = e.TransportType, url };
                });

        // Every agent gets fleet-memory access for memory_get, even if not in DB.
        // This is the provisioning-time enforcement point for mandatory read access.
        if (!mcpServers.ContainsKey("fleet-memory"))
        {
            var url = WithAgentParam(fleetMemoryMcpUrl, agent.Name);
            mcpServers["fleet-memory"] = new { type = "http", url };
        }

        if (filesEnabled && (HasFilesGrant(agent) || JournalGrants.HasSendGrant(agent)) && agent.JournalEnabled && HasTelegramBot(agent)
            && !string.IsNullOrWhiteSpace(journalReadToken))
            mcpServers[JournalFilesServerName] = new { type = "http", url = "http://127.0.0.1:8091/journal-files/v1/mcp" };
        return JsonSerializer.Serialize(new { mcpServers }, IndentedJson);
    }

    internal static long? ParsePrimaryHumanUserId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!long.TryParse(value.Trim(), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var id) || id <= 0)
            throw new InvalidOperationException("primary_human_invalid");
        return id;
    }

    internal static bool HasTelegramBot(Agent agent) => agent.EnvRefs.Any(e =>
        e.EnvKeyName.StartsWith("TELEGRAM_", StringComparison.OrdinalIgnoreCase)
        && e.EnvKeyName.EndsWith("_BOT_TOKEN", StringComparison.OrdinalIgnoreCase));

    internal static string ReplyLookupState(Agent agent, JournalProvisioning? journal)
    {
        if (!agent.JournalEnabled || journal?.IngestToken is null) return "journal_capture_off";
        if (!SupportsMcpHeaders(agent.Provider)) return "provider_headers_unsupported";
        if (!agent.McpEndpoints.Any(e => string.Equals(e.McpName, JournalMcpServerName, StringComparison.OrdinalIgnoreCase))
            || !agent.Tools.Any(t => t.IsEnabled && string.Equals(t.ToolName, "mcp__fleet-comms-journal__get_message", StringComparison.OrdinalIgnoreCase)))
            return "tool_not_granted";
        return "available";
    }

    internal static bool SupportsMcpHeaders(string? provider) =>
        provider is not null && McpHeaderSupport.TryGetValue(provider, out var supported) && supported;

    internal static string WithoutQuery(string url)
    {
        var trimmed = url.TrimEnd('/');
        var question = trimmed.IndexOf('?');
        return (question >= 0 ? trimmed[..question] : trimmed).TrimEnd('/');
    }

    /// <summary>
    /// The user-level <c>~/.claude/settings.json</c>. Carries <c>outputStyle</c> only for an agent
    /// that has one; a styleless agent's file is byte-identical to what it was before styles
    /// existed, which is what makes the column a per-agent rollout switch and a one-write rollback.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The agent names a style with no row, so no file was generated for it. Refusing here is the
    /// point: naming an unresolvable style would leave the agent silently on <c>default</c> with
    /// nothing to show for it, which is indistinguishable from the style not working.
    /// </exception>
    internal static string GenerateSettingsJson(Agent agent, string ctoAgentName, OutputStyle? style = null)
    {
        if (!string.IsNullOrWhiteSpace(agent.OutputStyle) && style is null)
            throw new InvalidOperationException(
                $"Agent '{agent.Name}' names output style '{agent.OutputStyle}', which has no row in " +
                "output_styles — refusing to write settings.json with an unresolvable reference.");

        var allow = agent.Tools
            .Where(t => t.IsEnabled)
            .OrderBy(t => t.ToolName)
            .Select(t => JournalGrants.IsSendGrant(t.ToolName) ? JournalGrants.SendGrant : string.Equals(t.ToolName, JournalFilesGrant, StringComparison.OrdinalIgnoreCase) ? JournalFilesGrant : t.ToolName)
            .ToList();

        // Every agent gets memory_get — provisioning-time enforcement of mandatory read access.
        // Use the fully-qualified MCP name so Claude CLI matches the permission without prompting.
        if (!allow.Contains("mcp__fleet-memory__memory_get", StringComparer.OrdinalIgnoreCase))
            allow.Add("mcp__fleet-memory__memory_get");

        // Auto-grant notify_cto to every agent except the CTO agent itself.
        // If FLEET_CTO_AGENT is unset (empty ctoAgentName), skip the grant entirely — fail-safe
        // over fail-open: a misconfigured CTO name must not silently create a self-loop.
        // Use the fully-qualified MCP name so Claude CLI matches the permission without prompting.
        if (!string.IsNullOrWhiteSpace(ctoAgentName) &&
            !string.Equals(agent.Name, ctoAgentName, StringComparison.OrdinalIgnoreCase) &&
            !allow.Contains("mcp__fleet-temporal__notify_cto", StringComparer.OrdinalIgnoreCase))
        {
            allow.Add("mcp__fleet-temporal__notify_cto");
        }

        allow.Sort(StringComparer.OrdinalIgnoreCase);

        // Two shapes rather than one with a null: the key must be ABSENT, not null, for an agent
        // with no style — Claude Code would resolve a null to nothing useful and the byte-identity
        // guarantee would be gone either way.
        return HasStyleFile(agent) && style is not null
            ? JsonSerializer.Serialize(new { permissions = new { allow }, outputStyle = style.Name }, IndentedJson)
            : JsonSerializer.Serialize(new { permissions = new { allow } }, IndentedJson);
    }
}
