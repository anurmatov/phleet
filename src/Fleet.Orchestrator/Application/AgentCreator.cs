using System.Text.Json;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Helpers;
using Fleet.Orchestrator.Services;
using Fleet.Shared;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Application;

/// <summary>A new agent. Only <see cref="Name"/>, <see cref="Role"/> and <see cref="Model"/> are required.</summary>
public sealed record NewAgent
{
    public string? Name { get; init; }
    public string? Role { get; init; }
    public string? Model { get; init; }
    public string? DisplayName { get; init; }
    public string? ContainerName { get; init; }
    public int? MemoryLimitMb { get; init; }
    public bool? IsEnabled { get; init; }
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
    public string? Effort { get; init; }
    public string? JsonSchema { get; init; }
    public string? AgentsJson { get; init; }
    public bool? AutoMemoryEnabled { get; init; }
    public string? Provider { get; init; }
    public string? CodexSandboxMode { get; init; }
    public bool? MountDockerSock { get; init; }
    public string? LocalBaseUrl { get; init; }
    public int? ContextWindow { get; init; }
    public string? HarnessExtra { get; init; }
    public IReadOnlyList<string>? Tools { get; init; }
    public IReadOnlyList<string>? Projects { get; init; }
    public IReadOnlyList<McpEndpointSpec>? McpEndpoints { get; init; }
    public IReadOnlyList<string>? Networks { get; init; }
    public IReadOnlyList<string>? EnvRefs { get; init; }
    public IReadOnlyList<long>? TelegramUsers { get; init; }
    public IReadOnlyList<long>? TelegramGroups { get; init; }

    /// <summary>Create the container too. The dashboard does; <c>create_agent</c> leaves it to <c>provision_agent</c>.</summary>
    public bool Provision { get; init; }
}

/// <summary>A stored new agent, and what happened when its container was asked for.</summary>
public sealed record AgentCreated(
    string Name,
    string Role,
    string Model,
    string ContainerName,
    int MemoryLimitMb,
    int HostPort,
    IReadOnlyList<string> Networks,
    IReadOnlyList<string> EnvRefs,
    bool ProvisionRequested,
    string? ProvisionFailure);

/// <summary>
/// The one implementation of "create an agent". <c>POST /api/agents</c> and <c>create_agent</c> are
/// adapters over it.
/// </summary>
/// <remarks>
/// Every refusal happens before anything is written. The agent row and its related rows are saved
/// in one <c>SaveChanges</c>: there is no window in which an agent exists without its network, its
/// bot-token reference or its owner on the allowlist.
/// </remarks>
public sealed class AgentCreator(
    IServiceScopeFactory scopes,
    ITelegramSetup telegram,
    IAclChangeNotifier aclNotifier,
    IConfigWriter configWriter,
    AgentRegistry registry,
    ContainerProvisioningService provisioning,
    TemporalClientRegistry temporal,
    IConfiguration configuration,
    AgentHostPorts hostPorts,
    ILogger<AgentCreator> logger)
{
    /// <summary>The bot-token reference an agent gets when the caller names none; without one its transport never starts.</summary>
    public const string DefaultBotTokenRef = "TELEGRAM_NOTIFIER_BOT_TOKEN";

    public async Task<WriteOutcome<AgentCreated>> CreateAsync(
        NewAgent request, WriteCaller caller, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
        if (db is null) return WriteFaults.NoDatabase;

        if (string.IsNullOrWhiteSpace(request.Name))
            return WriteFaults.Invalid("name_required", caller.Name("name is required", "agent_name is required."));
        if (string.IsNullOrWhiteSpace(request.Model))
            return WriteFaults.Invalid("model_required", caller.Name("model is required", "model is required."));
        if (string.IsNullOrWhiteSpace(request.Role))
            return WriteFaults.Invalid("role_required", caller.Name("role is required", "role is required."));

        // PV1 (#386): refused before anything is written. Omitted or "" stays claude.
        if (AgentProviders.DescribeFault(request.Provider) is { } providerFault)
            return WriteFaults.Invalid("provider_invalid", providerFault);

        var name = request.Name.Trim().ToLowerInvariant();
        var role = request.Role.Trim();
        var model = request.Model.Trim();

        // The local server URL and context window are checked like an update, before the row is written.
        var agent = new Agent
        {
            Name = name,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? name : request.DisplayName.Trim(),
            Role = role,
            Model = model,
            ContainerName = string.IsNullOrWhiteSpace(request.ContainerName) ? $"fleet-{name}" : request.ContainerName.Trim(),
            MemoryLimitMb = request.MemoryLimitMb ?? 4096,
            IsEnabled = request.IsEnabled ?? true,
            Image = string.IsNullOrEmpty(request.Image) ? null : request.Image,
            PermissionMode = request.PermissionMode ?? "acceptEdits",
            MaxTurns = request.MaxTurns ?? 50,
            WorkDir = request.WorkDir ?? "/workspace",
            ProactiveIntervalMinutes = request.ProactiveIntervalMinutes ?? 0,
            GroupListenMode = request.GroupListenMode ?? "mention",
            GroupDebounceSeconds = request.GroupDebounceSeconds ?? 15,
            WarmupTimeoutSeconds = request.WarmupTimeoutSeconds ?? WarmupTimeout.DefaultSeconds,
            ShortName = string.IsNullOrWhiteSpace(request.ShortName) ? name : request.ShortName.Trim(),
            ShowStats = request.ShowStats ?? true,
            PrefixMessages = request.PrefixMessages ?? false,
            FormattingMode = (FormattingMode)(request.FormattingMode ?? 0),
            SuppressToolMessages = request.SuppressToolMessages ?? false,
            TelegramSendOnly = request.TelegramSendOnly ?? false,
            Effort = string.IsNullOrEmpty(request.Effort) ? null : request.Effort,
            JsonSchema = string.IsNullOrEmpty(request.JsonSchema) ? null : request.JsonSchema,
            AgentsJson = string.IsNullOrEmpty(request.AgentsJson) ? null : request.AgentsJson,
            AutoMemoryEnabled = request.AutoMemoryEnabled ?? true,
            Provider = request.Provider ?? "claude",
            CodexSandboxMode = string.IsNullOrEmpty(request.CodexSandboxMode) ? null : request.CodexSandboxMode,
            MountDockerSock = request.MountDockerSock ?? false,
            HarnessExtra = string.IsNullOrEmpty(request.HarnessExtra) ? null : request.HarnessExtra,
        };
        if (AgentPatchHelpers.ApplyLocalModelOnCreate(agent, request.LocalBaseUrl, request.ContextWindow) is { } localModelFault)
            return WriteFaults.Invalid("local_model_invalid", localModelFault);
        // #460: the same allowlist as an update, against the new agent's provider and mode.
        if (AgentPatchHelpers.DescribeHarnessExtraFault(agent) is { } harnessExtraFault)
            return WriteFaults.Invalid("harness_extra_invalid", harnessExtraFault.Message, harnessExtraFault);

        // Telegram is a hard prerequisite — agents without a valid bot token are headless.
        if (!telegram.IsConfigured)
            return WriteFaults.Conflict("telegram_not_configured",
                "Cannot provision agent: Telegram bot token is not configured. Configure Telegram first using the setup banner.");

        // From here to the commit below, one create at a time, and no other writer of a host port
        // (AgentHostPorts). Everything this create decides from the agents already stored (the
        // first-agent rule, the name, the port) is decided and saved before another create or a
        // host_port update reads them: two creates never take the same port, and a second create
        // of one name is refused here, not by the database.
        using var hostPortGate = await hostPorts.EnterAsync(ct);

        // First agent must be a co-cto — specialists are provisioned by it on demand.
        var isFirstAgent = !await db.Agents.AnyAsync(ct);
        var isCoCto = string.Equals(role, "co-cto", StringComparison.OrdinalIgnoreCase);
        if (isFirstAgent && !isCoCto)
            return WriteFaults.Conflict("cto_required_first", "Your first agent must be a co-cto.");

        if (await db.Agents.AnyAsync(a => a.Name == name, ct))
            return WriteFaults.Conflict("agent_exists",
                caller.Name($"Agent '{name}' already exists", $"Agent '{name}' already exists in DB."));

        if (request.WarmupTimeoutSeconds is { } warmup && WarmupTimeout.DescribeFault(warmup) is { } warmupFault)
            return WriteFaults.Invalid("warmup_timeout_invalid", warmupFault);

        // A host port no other agent uses. An agent without a stored port answers on 8080 + its id
        // (ContainerProvisioningService.GetEffectiveHostPort), so the highest port in use is taken
        // over both kinds, and the new agent's port is stored: it never depends on an id it does
        // not have yet. Read under the gate, which is held until this agent is committed.
        agent.HostPort = await AgentHostPorts.NextFreeAsync(db, ct);

        foreach (var tool in Clean(request.Tools))
            agent.Tools.Add(new AgentTool { ToolName = tool, IsEnabled = true });

        var projects = Clean(request.Projects);
        foreach (var project in projects)
            agent.Projects.Add(new AgentProject { ProjectName = project });

        foreach (var endpoint in (request.McpEndpoints ?? []).DistinctBy(e => e.McpName, StringComparer.OrdinalIgnoreCase))
            agent.McpEndpoints.Add(new AgentMcpEndpoint { McpName = endpoint.McpName, Url = endpoint.Url, TransportType = endpoint.TransportType });

        // Every agent needs the fleet network to reach RabbitMQ, fleet-memory and the other fleet
        // services, so an agent created without one gets the default (Provisioning:DefaultNetwork).
        var networks = Clean(request.Networks);
        if (networks.Count == 0)
            networks.Add(configuration["Provisioning:DefaultNetwork"] ?? "fleet-net");
        foreach (var network in networks)
            agent.Networks.Add(new AgentNetwork { NetworkName = network });

        // Every agent needs some Telegram bot token ref to start its transport (without one the
        // RabbitMQ consumers never get wired up). If the caller named no *_BOT_TOKEN-shaped key, the
        // default is seeded so the agent boots; a per-agent token can replace it afterwards.
        var envRefs = Clean(request.EnvRefs);
        if (!envRefs.Any(k => k.StartsWith("TELEGRAM_", StringComparison.OrdinalIgnoreCase)
                           && k.EndsWith("_BOT_TOKEN", StringComparison.OrdinalIgnoreCase)))
            envRefs.Add(DefaultBotTokenRef);
        foreach (var key in envRefs)
            agent.EnvRefs.Add(new AgentEnvRef { EnvKeyName = key });

        // The owner is on every allowlist from the start.
        var users = new HashSet<long>(request.TelegramUsers ?? []);
        if (telegram.OwnerUserId is { } ownerId) users.Add(ownerId);
        foreach (var user in users)
            agent.TelegramUsers.Add(new AgentTelegramUser { UserId = user });

        foreach (var group in (request.TelegramGroups ?? []).Distinct())
            agent.TelegramGroups.Add(new AgentTelegramGroup { GroupId = group });

        // A co-cto created while no other exists becomes the fleet's CTO agent.
        var isFirstCoCto = isCoCto && !await db.Agents.AnyAsync(a => a.Role.ToLower() == "co-cto", ct);

        db.Agents.Add(agent);
        await db.SaveChangesAsync(ct);
        // Committed: the port is taken. What follows (the memory grant, the registry, the container)
        // reads no port and can take long, so it runs outside the gate.
        hostPortGate.Dispose();

        // A new agent's projects are assignments too — grant them now so its first memory read works
        // without a second manual step.
        if (projects.Count > 0)
            await AgentProjectAccessSync.SyncAndBroadcastAsync(db, aclNotifier, agent.Name, projects, ct);

        // Visible in GET /api/agents and list_agents before provisioning or a first heartbeat.
        registry.PreloadFromDbConfig(agent);

        // FLEET_CTO_AGENT goes to .env and config.changed is broadcast, so peers (fleet-bridge,
        // fleet-temporal-bridge) route to the new CTO without a restart.
        if (isFirstCoCto)
        {
            try
            {
                await configWriter.PutValuesAsync(new Dictionary<string, string> { ["FLEET_CTO_AGENT"] = name }, CancellationToken.None);
                logger.LogInformation("FLEET_CTO_AGENT set to '{Agent}' and config.changed broadcast", name);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not write FLEET_CTO_AGENT for new co-cto agent '{Agent}'", name);
            }
        }

        string? provisionFailure = null;
        if (request.Provision)
        {
            var result = await provisioning.ProvisionAsync(name);
            if (!result.Success)
                provisionFailure = result.Message;
            else if (isCoCto)
                await SendWelcomeAsync(db, agent);
        }

        return WriteOutcome<AgentCreated>.Ok(new AgentCreated(
            name, role, model, agent.ContainerName, agent.MemoryLimitMb, agent.HostPort!.Value,
            networks, envRefs, request.Provision, provisionFailure));
    }

    /// <summary>
    /// The welcome DM: a fire-and-forget workflow, so nothing waits for its result. The 15 s delay
    /// gives the agent process time to start consuming RabbitMQ.
    /// </summary>
    private async Task SendWelcomeAsync(OrchestratorDbContext db, Agent agent)
    {
        var ownerId = telegram.OwnerUserId;
        if (!WelcomeDmHelper.ShouldTrigger(agent, true, ownerId))
        {
            if (agent.WelcomeSentAt is null)
                logger.LogWarning("Welcome DM skipped: CEO Telegram user ID not configured.");
            return;
        }

        var welcomeInput = JsonSerializer.Serialize(new
        {
            TargetAgent = agent.Name,
            TaskDescription = WelcomeDmHelper.BuildWelcomeDirective(agent.Name, ownerId!.Value),
            TimeoutMinutes = 1,
        });
        var workflowId = $"welcome-{agent.Id}";
        await WelcomeDmHelper.TriggerAsync(
            agent,
            saveWelcomeSentAt: async () => await db.SaveChangesAsync(CancellationToken.None),
            startWorkflow: async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(15));
                await temporal.StartWorkflowAsync("FireAndForgetTaskWorkflow", workflowId, "fleet", "fleet", welcomeInput);
            },
            logger);
    }

    private static List<string> Clean(IEnumerable<string>? names) =>
        (names ?? []).Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
