using Fleet.Orchestrator.Data;
using Fleet.Shared;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// Renders, in memory, every file a provision writes under the agent's <c>.generated/</c>
/// directory: it loads what the files need from the database and calls the generators in
/// <see cref="AgentConfigFiles"/>. Writes nothing; <see cref="AgentFileWriter"/> does that.
/// </summary>
internal sealed class AgentFileRenderer(
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    JournalTokenService journalTokens,
    ILogger logger)
{
    private readonly ProvisioningPolicy _policy = new(config, journalTokens);

    /// <summary>
    /// Renders every file a provision writes under the agent's <c>.generated/</c> directory, in
    /// memory. Writing them is <see cref="AgentFileWriter.WriteConfigFilesAsync"/> and
    /// <see cref="AgentFileWriter.WriteInstructionFilesAsync"/>, which decide nothing.
    /// </summary>
    /// <exception cref="InvalidOperationException">A generator refused; see <see cref="AgentConfigFiles.GenerateAppsettingsJson"/>.</exception>
    internal GeneratedFiles RenderGeneratedFiles(
        Agent agent,
        string ctoAgentName,
        OutputStyle? style,
        ProjectContextPlan projectContexts,
        IReadOnlyDictionary<string, int>? instructionVersionOverrides)
    {
        var enabledTools  = agent.Tools.Where(t => t.IsEnabled).Select(t => t.ToolName).OrderBy(t => t).ToList();
        var mcpEndpoints  = agent.McpEndpoints.Select(e => e.McpName).OrderBy(e => e).ToList();

        logger.LogInformation(
            "Generating config for '{Agent}': {ToolCount} tools [{Tools}], {McpCount} MCP endpoints [{Mcp}], permissionMode={Mode}",
            agent.Name,
            enabledTools.Count, string.Join(", ", enabledTools),
            mcpEndpoints.Count, string.Join(", ", mcpEndpoints),
            agent.PermissionMode);

        var fleetMemoryMcpUrl = AgentConfigFiles.NormalizeFleetMemoryMcpUrl(config["FleetMemory:McpUrl"]);
        var journal = _policy.BuildJournalProvisioning(agent);
        if (ProvisioningFaults.FilesWarning(agent) is { } filesWarning) logger.LogWarning("{warning}", filesWarning);
        foreach (var warning in ProvisioningFaults.SendWarnings(agent)) logger.LogWarning("{warning}", warning);
        if (agent.JournalEnabled && journal.IngestToken is null)
            logger.LogInformation(
                "Journal enabled for '{Agent}', but it has no Telegram bot token ref; omitting Journal config",
                agent.Name);

        var chat = ChatAgents.BuildProvisioning(agent, journalTokens, config["Chat:SouthUrl"], _policy.PrimaryHumanUserIdOrNull());
        var appsettings = AgentConfigFiles.GenerateAppsettingsJson(agent, ctoAgentName, style, journal, _policy.PrimaryHumanRawValue(), chat);
        var mcpJson     = AgentConfigFiles.GenerateMcpJson(agent, fleetMemoryMcpUrl, journal.ReadToken, journal.FilesEnabled || journal.SendEnabled);
        var settings    = AgentConfigFiles.GenerateSettingsJson(agent, ctoAgentName, style);

        // #460: the same plan the effective-settings view serves. Written for codex agents only,
        // before the container is created, so the read-only bind always finds a file.
        string? codexRootToml = null;
        if (HarnessExtra.IsSupportedProvider(agent.Provider))
        {
            var plan = HarnessDefaults.Build(AgentConfigFiles.HarnessInputFor(agent));
            if (plan.ExtraState == HarnessDefaults.ExtraIgnored)
                logger.LogWarning("harnessExtra ignored for '{Agent}', mode defaults apply: {Reason}", agent.Name, plan.ExtraReason);
            foreach (var note in plan.Notes)
                logger.LogWarning("harnessExtra for '{Agent}': {Note}", agent.Name, note);
            codexRootToml = plan.CodexRootToml;
        }

        return new GeneratedFiles(
            appsettings, mcpJson, settings, codexRootToml, style,
            RenderInstructionFiles(agent, instructionVersionOverrides), projectContexts);
    }

    internal async Task<OutputStyle?> ResolveOutputStyleAsync(Agent agent)
    {
        if (string.IsNullOrWhiteSpace(agent.OutputStyle)) return null;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        return await db.OutputStyles.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Name == agent.OutputStyle);
    }

    /// <summary>
    /// The <c>roles/</c> files for the agent's instructions, from DB instruction content:
    /// <c>_base/system.md</c> and <c>{name}/system.md</c>. Null when the agent has no instructions.
    /// </summary>
    private List<RenderedInstruction>? RenderInstructionFiles(
        Agent agent,
        IReadOnlyDictionary<string, int>? versionOverrides = null)
    {
        var instructions = AgentConfigFiles.OrderedInstructions(agent);

        if (instructions.Count == 0)
        {
            logger.LogWarning("No instructions configured for '{Agent}' — skipping instruction file generation", agent.Name);
            return null;
        }

        var rendered = new List<RenderedInstruction>(instructions.Count);
        foreach (var agentInstruction in instructions)
        {
            var instruction = agentInstruction.Instruction;

            // Use caller-supplied version override if provided, else fall back to CurrentVersion
            InstructionVersion? version;
            if (versionOverrides is not null
                && versionOverrides.TryGetValue(instruction.Name, out var overrideVersionNumber))
            {
                version = instruction.Versions.FirstOrDefault(v => v.VersionNumber == overrideVersionNumber);
                if (version is null)
                {
                    logger.LogWarning(
                        "Requested version {V} for instruction '{Name}' not found — falling back to current",
                        overrideVersionNumber, instruction.Name);
                }
            }
            else
            {
                version = null;
            }

            version ??= instruction.Versions
                .FirstOrDefault(v => v.VersionNumber == instruction.CurrentVersion)
                ?? instruction.Versions.OrderByDescending(v => v.VersionNumber).FirstOrDefault();

            if (version is null)
            {
                logger.LogWarning(
                    "No version content found for instruction '{Name}' (agent '{Agent}') — skipping",
                    instruction.Name, agent.Name);
                continue;
            }

            // "base" → _base/system.md, anything else → {name}/system.md
            rendered.Add(new RenderedInstruction(AgentConfigFiles.InstructionDirectoryName(instruction.Name), version.Content));
        }

        return rendered;
    }

    // ── project contexts ───────────────────────────────────────────────────────

    /// <summary>
    /// Loads the agent's assigned project contexts. Names are matched in C#
    /// (<see cref="StringComparer.OrdinalIgnoreCase"/>) on loaded rows, never by database collation.
    /// </summary>
    internal async Task<ProjectContextPlan> ResolveProjectContextsAsync(Agent agent)
    {
        if (agent.Projects.Count == 0)
            return ProjectContextPlan.Empty;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        var headers = await db.ProjectContexts.AsNoTracking().Select(p => new { p.Id, p.Name }).ToListAsync();
        var ids = agent.Projects
            .Select(ap => ProjectNameMatch.MatchByName(headers, ap.ProjectName, h => h.Name)?.Id)
            .OfType<int>()
            .Distinct()
            .ToList();

        var contexts = ids.Count == 0
            ? []
            : await db.ProjectContexts
                .Include(p => p.Versions)
                .Where(p => ids.Contains(p.Id))
                .AsNoTracking()
                .ToListAsync();

        return BuildProjectContextPlan(agent, contexts, logger);
    }

    /// <summary>
    /// Every assignment renders the project's canonical context — the version row
    /// <c>CurrentVersion</c> points at, else the latest — or a stub when the context or its versions
    /// are missing.
    /// </summary>
    internal static ProjectContextPlan BuildProjectContextPlan(
        Agent agent, IReadOnlyCollection<ProjectContext> contexts, ILogger logger)
    {
        var assignments = new List<ProjectContextAssignment>();

        foreach (var agentProject in agent.Projects)
        {
            var projectName = agentProject.ProjectName;
            var ctx = ProjectNameMatch.MatchByName(contexts, projectName, c => c.Name);
            var version = ctx?.Versions.FirstOrDefault(v => v.VersionNumber == ctx.CurrentVersion)
                       ?? ctx?.Versions.OrderByDescending(v => v.VersionNumber).FirstOrDefault();

            assignments.Add(Assignment(agent, projectName, ctx, version, logger));
        }

        return new ProjectContextPlan(assignments);
    }

    private static ProjectContextAssignment Assignment(
        Agent agent, string projectName, ProjectContext? ctx, ProjectContextVersion? version, ILogger logger)
    {
        if (ctx is null)
        {
            logger.LogWarning(
                "Project context '{Project}' not found in DB for agent '{Agent}' — generating empty stub",
                projectName, agent.Name);
            return new ProjectContextAssignment(projectName,
                $"# {projectName}\n\n(No content — project context not yet seeded in DB)\n");
        }

        if (version is null)
        {
            logger.LogWarning(
                "Project context '{Project}' has no versions for agent '{Agent}' — generating empty stub",
                projectName, agent.Name);
            return new ProjectContextAssignment(projectName,
                $"# {projectName}\n\n(No content — no versions found)\n");
        }

        return new ProjectContextAssignment(projectName, version.Content);
    }
}
