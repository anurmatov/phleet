using System.Text.Json;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Helpers;
using Fleet.Orchestrator.Services;
using Fleet.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fleet.Orchestrator.Endpoints;

/// <summary>
/// The agent-create REST surface: <c>POST /api/agents</c>.
/// </summary>
/// <remarks>
/// Lifted verbatim from <c>Program.cs</c> (#386) so the endpoint tests can run the production
/// handler over real HTTP and prove PV1 refuses an unknown provider before a row is written — the
/// same pattern as <see cref="AgentConfigEndpoints"/> (#357). It also takes the local server URL and
/// context window, checked like the PUT before anything is written.
/// </remarks>
public static class AgentCreateEndpoints
{
    public static WebApplication MapAgentCreateEndpoints(this WebApplication app)
    {
// REST: create a new agent (insert into DB + provision container)
app.MapPost("/api/agents", async (HttpRequest request, IServiceScopeFactory scopeFactory, ContainerProvisioningService provisioning, AgentRegistry registry, SetupService setupService, ConfigService configService, TemporalClientRegistry temporal, ILogger<Program> logger) =>
{
    using var scope = scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
    if (db is null)
        return Results.Problem("Database is not configured on this orchestrator");

    CreateAgentRequest? body;
    try { body = await request.ReadFromJsonAsync<CreateAgentRequest>(); }

    catch (JsonException ex) { return Results.BadRequest(new { error = AgentPatchHelpers.DescribeBodyFault(ex) }); }
    if (body is null || string.IsNullOrWhiteSpace(body.Name))
        return Results.BadRequest(new { error = "name is required" });
    if (string.IsNullOrWhiteSpace(body.Model))
        return Results.BadRequest(new { error = "model is required" });
    if (string.IsNullOrWhiteSpace(body.Role))
        return Results.BadRequest(new { error = "role is required" });
    // PV1 (#386): refused before anything is written. Omitted or "" stays claude.
    if (AgentProviders.DescribeFault(body.Provider) is { } providerFault)
        return Results.BadRequest(new { error = providerFault });
    // The local server URL and context window are checked like the PUT before anything is written.
    var localModel = new Agent
    {
        Name = "", DisplayName = "", Role = "", ContainerName = "", // a validation probe, never saved
        Provider = body.Provider ?? "claude",
        Model = body.Model.Trim(),
        Effort = string.IsNullOrEmpty(body.Effort) ? null : body.Effort,
        HarnessExtra = string.IsNullOrEmpty(body.HarnessExtra) ? null : body.HarnessExtra,
    };
    if (AgentPatchHelpers.ApplyLocalModelOnCreate(localModel, body.LocalBaseUrl, body.ContextWindow) is { } localModelFault)
        return Results.BadRequest(new { error = localModelFault });
    // #460: the same allowlist as the PUT, against the new agent's provider and mode.
    if (AgentPatchHelpers.DescribeHarnessExtraFault(localModel) is { } harnessExtraFault)
        return Results.BadRequest(AgentPatchHelpers.HarnessExtraBadRequest(harnessExtraFault));

    // Telegram is a hard prerequisite — agents without a valid bot token are headless.
    var setupStatus = setupService.GetStatus();
    if (!setupStatus.Telegram.Configured)
        return Results.Conflict(new
        {
            error = "telegram_not_configured",
            message = "Cannot provision agent: Telegram bot token is not configured. Configure Telegram first using the setup banner."
        });

    // First agent must be a co-cto — specialists are provisioned by it on demand.
    var agentCount = await db.Agents.CountAsync();
    if (agentCount == 0 && !string.Equals(body.Role.Trim(), "co-cto", StringComparison.OrdinalIgnoreCase))
        return Results.Conflict(new
        {
            error = "cto_required_first",
            message = "Your first agent must be a co-cto."
        });

    var name = body.Name.Trim().ToLowerInvariant();

    if (await db.Agents.AnyAsync(a => a.Name == name))
        return Results.Conflict(new { error = $"Agent '{name}' already exists" });

    if (body.WarmupTimeoutSeconds is not null && WarmupTimeout.DescribeFault(body.WarmupTimeoutSeconds.Value) is { } warmupFault)
        return Results.BadRequest(new { error = warmupFault });

    var containerName = string.IsNullOrWhiteSpace(body.ContainerName) ? $"fleet-{name}" : body.ContainerName.Trim();
    var displayName   = string.IsNullOrWhiteSpace(body.DisplayName)   ? name               : body.DisplayName.Trim();

    var agent = new Agent
    {
        Name          = name,
        DisplayName   = displayName,
        Role          = body.Role.Trim(),
        Model         = body.Model.Trim(),
        ContainerName = containerName,
        MemoryLimitMb = body.MemoryLimitMb ?? 4096,
        IsEnabled     = body.IsEnabled ?? true,
        Image         = string.IsNullOrEmpty(body.Image) ? null : body.Image,
        PermissionMode            = body.PermissionMode            ?? "acceptEdits",
        MaxTurns                  = body.MaxTurns                  ?? 50,
        WorkDir                   = body.WorkDir                   ?? "/workspace",
        ProactiveIntervalMinutes  = body.ProactiveIntervalMinutes  ?? 0,
        GroupListenMode           = body.GroupListenMode           ?? "mention",
        GroupDebounceSeconds      = body.GroupDebounceSeconds      ?? 15,
        WarmupTimeoutSeconds      = body.WarmupTimeoutSeconds      ?? WarmupTimeout.DefaultSeconds,
        ShortName                 = string.IsNullOrWhiteSpace(body.ShortName) ? name : body.ShortName.Trim(),
        ShowStats                 = body.ShowStats                 ?? true,
        PrefixMessages            = body.PrefixMessages            ?? false,
        FormattingMode            = (Fleet.Shared.FormattingMode)(body.FormattingMode ?? 0),
        SuppressToolMessages      = body.SuppressToolMessages      ?? false,
        TelegramSendOnly          = body.TelegramSendOnly          ?? false,
        Effort                    = string.IsNullOrEmpty(body.Effort) ? null : body.Effort,
        JsonSchema                = string.IsNullOrEmpty(body.JsonSchema) ? null : body.JsonSchema,
        AgentsJson                = string.IsNullOrEmpty(body.AgentsJson) ? null : body.AgentsJson,
        AutoMemoryEnabled         = body.AutoMemoryEnabled ?? true,
        Provider                  = body.Provider          ?? "claude",
        CodexSandboxMode          = string.IsNullOrEmpty(body.CodexSandboxMode) ? null : body.CodexSandboxMode,
        MountDockerSock           = body.MountDockerSock   ?? false,

        HarnessExtra              = localModel.HarnessExtra,
    };
    agent.LocalBaseUrl = localModel.LocalBaseUrl;
    agent.ContextWindow = localModel.ContextWindow;

    db.Agents.Add(agent);
    await db.SaveChangesAsync();

    if (body.Tools is not null)
        foreach (var t in body.Tools.Distinct(StringComparer.OrdinalIgnoreCase))
            db.AgentTools.Add(new AgentTool { AgentId = agent.Id, ToolName = t, IsEnabled = true });

    if (body.Projects is not null)
        foreach (var p in body.Projects.Distinct(StringComparer.OrdinalIgnoreCase))
            db.AgentProjects.Add(new AgentProject { AgentId = agent.Id, ProjectName = p });

    if (body.McpEndpoints is not null)
        foreach (var e in body.McpEndpoints.DistinctBy(x => x.McpName, StringComparer.OrdinalIgnoreCase))
            db.AgentMcpEndpoints.Add(new AgentMcpEndpoint { AgentId = agent.Id, McpName = e.McpName, Url = e.Url, TransportType = e.TransportType });

    if (body.Networks is not null)
        foreach (var n in body.Networks.Distinct(StringComparer.OrdinalIgnoreCase))
            db.AgentNetworks.Add(new AgentNetwork { AgentId = agent.Id, NetworkName = n });

    var envRefSet = new HashSet<string>(
        body.EnvRefs?.Distinct(StringComparer.OrdinalIgnoreCase) ?? [],
        StringComparer.OrdinalIgnoreCase);
    // Every agent needs some Telegram bot token ref to start its transport (without one the
    // RabbitMQ consumers never get wired up). If the caller didn't pass any *_BOT_TOKEN-shaped
    // key, seed TELEGRAM_NOTIFIER_BOT_TOKEN so the agent boots with a working default.
    var hasBotTokenRef = envRefSet.Any(k =>
        k.StartsWith("TELEGRAM_", StringComparison.OrdinalIgnoreCase) &&
        k.EndsWith("_BOT_TOKEN", StringComparison.OrdinalIgnoreCase));
    if (!hasBotTokenRef)
        envRefSet.Add("TELEGRAM_NOTIFIER_BOT_TOKEN");
    foreach (var r in envRefSet)
        db.AgentEnvRefs.Add(new AgentEnvRef { AgentId = agent.Id, EnvKeyName = r });

    // Build the final Telegram user set: explicit payload + installer's ID (deduped)
    var telegramUserSet = new HashSet<long>(body.TelegramUsers?.Distinct() ?? []);
    var ownerTgId = setupService.GetTelegramUserId();
    if (ownerTgId.HasValue) telegramUserSet.Add(ownerTgId.Value);
    foreach (var u in telegramUserSet)
        db.AgentTelegramUsers.Add(new AgentTelegramUser { AgentId = agent.Id, UserId = u });

    if (body.TelegramGroups is not null)
        foreach (var g in body.TelegramGroups.Distinct())
            db.AgentTelegramGroups.Add(new AgentTelegramGroup { AgentId = agent.Id, GroupId = g });

    await db.SaveChangesAsync();

    // A new agent's projects are assignments too — grant them now so its first memory read works
    // without a second manual step.
    if (body.Projects is not null)
    {
        await AgentProjectAccessSync.SyncAndBroadcastAsync(
            db, configService, agent.Name, body.Projects);
    }

    // Immediately register the new agent in the in-memory registry so it's visible in GET /api/agents
    // and list_agents even before provisioning or first heartbeat.
    registry.PreloadFromDbConfig(agent);

    // When the first co-cto agent is created, write FLEET_CTO_AGENT to .env and broadcast
    // config.changed so peers (fleet-bridge, fleet-temporal-bridge) self-update their routing.
    // Only fires when no prior co-cto existed (agentCount == 0 ensures this is the first agent overall).
    if (string.Equals(agent.Role, "co-cto", StringComparison.OrdinalIgnoreCase)
        && !await db.Agents.AnyAsync(a => a.Role.ToLower() == "co-cto" && a.Id != agent.Id))
    {
        try
        {
            await configService.PutValuesAsync(new Dictionary<string, string>
            {
                ["FLEET_CTO_AGENT"] = name
            }, CancellationToken.None);
            logger.LogInformation("FLEET_CTO_AGENT set to '{Agent}' and config.changed broadcast", name);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not write FLEET_CTO_AGENT for new co-cto agent '{Agent}'", name);
        }

    }

    if (body.Provision != false)
    {
        var result = await provisioning.ProvisionAsync(name);
        if (!result.Success)
            return Results.Json(
                new
                {
                    message = $"Agent '{name}' created in DB but provisioning failed: {result.Message}",
                    agentName = name,
                },
                statusCode: 207);

        // Welcome DM — fire-and-forget workflow so there's no waiting for a result.
        // 15s delay gives the agent process time to start consuming RabbitMQ.
        if (string.Equals(agent.Role, "co-cto", StringComparison.OrdinalIgnoreCase))
        {
            var ceoUserId = setupService.GetTelegramUserId();
            if (WelcomeDmHelper.ShouldTrigger(agent, true, ceoUserId))
            {
                var welcomeInput = JsonSerializer.Serialize(new
                {
                    TargetAgent = name,
                    TaskDescription = WelcomeDmHelper.BuildWelcomeDirective(name, ceoUserId!.Value),
                    TimeoutMinutes = 1
                });
                await WelcomeDmHelper.TriggerAsync(
                    agent,
                    saveWelcomeSentAt: async () => await db.SaveChangesAsync(CancellationToken.None),
                    startWorkflow: async () =>
                    {
                        await Task.Delay(TimeSpan.FromSeconds(15));
                        await temporal.StartWorkflowAsync(
                            "FireAndForgetTaskWorkflow",
                            $"welcome-{agent.Id}",
                            "fleet",
                            "fleet",
                            welcomeInput);
                    },
                    logger);
            }
            else if (agent.WelcomeSentAt is null)
            {
                logger.LogWarning("Welcome DM skipped: CEO Telegram user ID not configured.");
            }
        }

        return Results.Created($"/api/agents/{name}", new { message = $"Agent '{name}' created and provisioned", agentName = name });
    }

    return Results.Created($"/api/agents/{name}", new { message = $"Agent '{name}' created (not provisioned — use POST /api/agents/{name}/reprovision when ready)", agentName = name });
});

        return app;
    }
}
