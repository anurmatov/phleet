using System.Text.Json;
using Fleet.Orchestrator.Data;
using Fleet.Shared;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// Shadow-mode container provisioning service.
/// Generates expected Docker container specs from DB config and diffs them against
/// the actual running container. No containers are created or modified.
/// </summary>
public sealed class ContainerProvisioningService(
    IServiceScopeFactory scopeFactory,
    DockerService docker,
    IConfiguration config,
    ILogger<ContainerProvisioningService> logger)
{
    private const string DefaultAgentImage = "fleet:agent";

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    // Actual Docker network names as reported by the Docker API.
    // fleet-net is declared with an explicit name so it keeps its name (not prefixed by Docker Compose).
    private const string FleetNetwork = "fleet-net";


    public async Task<ProvisionPreview> PreviewAsync(string agentName, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        var agent = await db.Agents
            .Include(a => a.Tools.Where(t => t.IsEnabled))
            .Include(a => a.Projects)
            .Include(a => a.McpEndpoints)
            .Include(a => a.EnvRefs)
            .Include(a => a.TelegramUsers)
            .Include(a => a.TelegramGroups)
            .Include(a => a.Networks)
            .Include(a => a.CredentialMounts).ThenInclude(m => m.CredentialFile)
            .AsSplitQuery()
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Name == agentName, ct);

        if (agent is null)
            return ProvisionPreview.NotFound(agentName);

        var envFile = config["Provisioning:EnvFilePath"] ?? "/app/deploy/.env";
        var envValues = LoadEnvFile(envFile);

        var desired = BuildDesiredSpec(agent, envValues);
        var (actual, resolvedContainerName) = await InspectActualAsync(agent.ContainerName, ct);
        var diffs   = actual is null
            ? ["container not found — not running"]
            : ComputeDiff(desired, actual);

        logger.LogInformation(
            "Provision preview for {Agent} ({Container}): {DiffCount} diff(s)",
            agentName, agent.ContainerName, diffs.Count);

        return new ProvisionPreview(agentName, agent.ContainerName, resolvedContainerName, desired, actual, diffs);
    }

    // ── spec generation ────────────────────────────────────────────────────────

    private ContainerSpec BuildDesiredSpec(Agent agent, Dictionary<string, string> envValues)
    {
        var containerName = agent.ContainerName;

        var whisperServiceUrl = config["Provisioning:WhisperServiceUrl"] ?? "";
        var kokoroServiceUrl  = config["Provisioning:KokoroServiceUrl"]  ?? "";
        var env      = BuildEnv(agent, envValues, whisperServiceUrl, kokoroServiceUrl);
        var binds    = BuildBinds(agent);
        var networks = BuildNetworks(agent);
        var memBytes = (long)agent.MemoryLimitMb * 1024 * 1024;
        var image    = agent.Image ?? config["Provisioning:AgentImage"] ?? DefaultAgentImage;

        return new ContainerSpec(image, memBytes, env, binds, networks);
    }

    private static List<string> BuildEnv(Agent agent, Dictionary<string, string> envValues, string whisperServiceUrl, string kokoroServiceUrl)
    {
        var env = new List<string>();

        // Secret refs from DB — resolve values from .env if available
        foreach (var envRef in agent.EnvRefs.OrderBy(e => e.EnvKeyName))
        {
            var value = envValues.TryGetValue(envRef.EnvKeyName, out var v) ? v : "<secret>";
            // Only *_BOT_TOKEN-shaped TELEGRAM_* keys map to the Telegram__BotToken ASP.NET config key.
            // Other TELEGRAM_* keys (e.g. TELEGRAM_USER_ID, TELEGRAM_GROUP_ID) pass through as-is
            // so they can be used as normal env vars without colliding with the bot token config slot.
            if (envRef.EnvKeyName.StartsWith("TELEGRAM_", StringComparison.OrdinalIgnoreCase) &&
                envRef.EnvKeyName.EndsWith("_BOT_TOKEN", StringComparison.OrdinalIgnoreCase))
                env.Add($"Telegram__BotToken={value}");
            else
                env.Add($"{envRef.EnvKeyName}={value}");
        }

        // Static env vars common to all agents
        env.Add("RabbitMq__Host=rabbitmq");

        // Use ShortName from DB if populated, fall back to role-derived name
        var gitName  = !string.IsNullOrEmpty(agent.ShortName) ? agent.ShortName : DeriveGitName(agent.Role);
        var gitEmail = $"{agent.ContainerName}@users.noreply.github.com";
        env.Add($"GIT_USER_NAME={gitName}");
        env.Add($"GIT_USER_EMAIL={gitEmail}");

        // GitHub App PEM — inject for all agents when present in .env (base64-encoded)
        if (envValues.TryGetValue("GITHUB_APP_PEM", out var githubAppPem))
            env.Add($"GITHUB_APP_PEM={githubAppPem}");

        // Claude auto-memory — disable when agent uses fleet-memory instead
        if (!agent.AutoMemoryEnabled)
            env.Add("CLAUDE_CODE_DISABLE_AUTO_MEMORY=1");

        // Whisper (speech-to-text) — all agents, URL from cluster config
        if (!string.IsNullOrWhiteSpace(whisperServiceUrl))
            env.Add($"Whisper__ServiceUrl={whisperServiceUrl}");

        // Kokoro TTS (text-to-speech) — all agents, URL from cluster config
        if (!string.IsNullOrWhiteSpace(kokoroServiceUrl))
            env.Add($"Tts__ServiceUrl={kokoroServiceUrl}");

        return env;
    }

    private List<string> BuildBinds(Agent agent)
    {
        var baseDir = config["Provisioning:BaseDir"]
            ?? throw new InvalidOperationException("Provisioning:BaseDir is required but not configured.");
        return BuildBinds(agent, baseDir);
    }

    internal static List<string> BuildBinds(Agent agent, string baseDir)
    {
        var containerName = agent.ContainerName;

        var binds = new List<string>
        {
            $"./workspaces/{containerName}:/workspace",
            $"./workspaces/{containerName}/.generated/projects:/app/projects:ro",
            $"./workspaces/{containerName}/.generated/appsettings.json:/app/appsettings.json:ro",
            $"./workspaces/{containerName}/claude:/root/.claude",
            $"./workspaces/{containerName}/.generated/settings.json:/root/.claude/settings.json:ro",
            $"./workspaces/{containerName}/codex:/root/.codex",
            $"./workspaces/{containerName}/.generated/.mcp.json:/workspace/.mcp.json:ro",
            $"./workspaces/{containerName}/.generated/roles:/app/roles:ro",
        };

        // docker.sock is only mounted when the agent's MountDockerSock flag is on
        if (agent.MountDockerSock)
            binds.Add("/var/run/docker.sock:/var/run/docker.sock");

        // Output styles resolve as a PROJECT-level directory beside the user-level settings.json
        // above — that is the combination provisioning produces and the one that was verified on
        // the pinned CLI. Claude only: the other providers get the same text in their prompt and
        // have nothing that would read this. Absent for a styleless agent, so its container config
        // is unchanged.
        if (HasStyleFile(agent))
            binds.Add($"./workspaces/{containerName}/.generated/output-styles:/workspace/.claude/output-styles:ro");

        if (agent.Provider == "gemini")
        {
            // Mount gemini OAuth credentials writable — the CLI's google-auth-library refreshes
            // the file in-place on token expiry, so the mount must be read-write. The refreshed
            // token propagates back to the host, preventing stale-token failures on container restart.
            var geminiTokenStorePath = Path.Combine(baseDir, ".gemini-credentials.json");
            if (File.Exists(geminiTokenStorePath))
                binds.Add("./.gemini-credentials.json:/root/.gemini/oauth_creds.json:rw");
        }
        else if (agent.Provider == "codex")
        {
            // A hosted-provider agent must hold no OpenAI credential (#335 D6): its model is served
            // by the vendor through the loopback adapter, and the credential would be one more
            // secret in a container whose model runs a root shell.
            if (IsHostedProvider(agent))
                return AppendCredentialMounts(agent, binds);

            // Mount codex credentials for seeding new codex containers (entrypoint.sh reads this)
            var codexTokenStorePath = Path.Combine(baseDir, ".codex-credentials.json");
            if (File.Exists(codexTokenStorePath))
                binds.Add("./.codex-credentials.json:/root/.codex-host/auth.json:ro");
        }
        else
        {
            // A local-model agent must hold no Claude credential (#340 D3): its model is served by a
            // local Anthropic-compatible server, which must never receive a real OAuth token.
            if (ClaudeLocalModel.IsEnabled(agent.Provider, agent.AnthropicBaseUrl))
                return AppendCredentialMounts(agent, binds);

            // Mount orchestrator-stored Claude credentials for seeding new containers (entrypoint.sh reads this)
            var tokenStorePath = Path.Combine(baseDir, ".claude-credentials.json");
            if (File.Exists(tokenStorePath))
                binds.Add("./.claude-credentials.json:/root/.claude-host/.credentials.json:ro");
        }

        return AppendCredentialMounts(agent, binds);
    }

    private static List<string> AppendCredentialMounts(Agent agent, List<string> binds)
    {
        // Credential file mounts (from Files section in Credentials view)
        foreach (var mount in agent.CredentialMounts)
        {
            if (File.Exists(mount.CredentialFile.FilePath))
                binds.Add($"{mount.CredentialFile.FilePath}:{mount.MountPath}:{mount.Mode}");
        }

        return binds;
    }

    /// <summary>
    /// The Claude local-model fault that must stop provisioning (#340 D1 point 2), or null: V1–V7,
    /// or a local agent carrying a credential mount into <c>/root/.claude*</c>, which would hand
    /// it the Claude credential the bind skip above withholds.
    /// </summary>
    internal static string? DescribeClaudeLocalModelFault(Agent agent)
    {
        if (ClaudeLocalModel.DescribeConfigFault(agent.Provider, agent.AnthropicBaseUrl, agent.Model, agent.Effort)
            is { } fault)
        {
            return fault;
        }

        if (!ClaudeLocalModel.IsEnabled(agent.Provider, agent.AnthropicBaseUrl))
            return null;

        // "/root/.claude" also covers /root/.claude-host and /root/.claude.json.
        var claudeMount = agent.CredentialMounts.FirstOrDefault(
            m => m.MountPath.StartsWith("/root/.claude", StringComparison.Ordinal));
        return claudeMount is null
            ? null
            : $"Claude local model mode forbids a credential mount into /root/.claude*, but one targets "
            + $"{claudeMount.MountPath}. Remove it, then reprovision.";
    }

    /// <summary>True when this agent's model routes to a hosted provider (#335 D1).</summary>
    internal static bool IsHostedProvider(Agent agent) =>
        HostedModelProviders.TryResolve(agent.Provider, agent.Model, out _, out _);

    private static List<string> BuildNetworks(Agent agent)
    {
        // Use DB-stored network list if available; otherwise default to the fleet network only
        if (agent.Networks.Count > 0)
            return agent.Networks.Select(n => n.NetworkName).ToList();

        return [FleetNetwork];
    }

    // ── live provisioning ──────────────────────────────────────────────────────

    /// <summary>
    /// Creates and starts a container from DB config.
    /// Fails if a container with that name already exists.
    /// </summary>
    public async Task<ProvisionResult> ProvisionAsync(
        string agentName,
        string? imageOverride = null,
        IReadOnlyDictionary<string, int>? instructionVersionOverrides = null,
        CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        var agent = await db.Agents
            .Include(a => a.Tools)                    // load all; in-memory filter applied during generation
            .Include(a => a.Projects)
            .Include(a => a.McpEndpoints)
            .Include(a => a.EnvRefs)
            .Include(a => a.TelegramUsers)
            .Include(a => a.TelegramGroups)
            .Include(a => a.Networks)
            .Include(a => a.Instructions)
                .ThenInclude(ai => ai.Instruction)
                    .ThenInclude(i => i.Versions)
            .Include(a => a.CredentialMounts).ThenInclude(m => m.CredentialFile)
            .AsSplitQuery()
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Name == agentName, ct);

        if (agent is null)
            return ProvisionResult.Fail(agentName, "agent not found in DB");

        // Guard: bypassPermissions is rejected — fleet containers run as root and Claude CLI
        // refuses --dangerously-skip-permissions for root processes, which crashes the agent immediately.
        // Use acceptEdits + an explicit AllowedTools list instead.
        if (string.Equals(agent.PermissionMode, "bypassPermissions", StringComparison.OrdinalIgnoreCase))
            return ProvisionResult.Fail(agentName,
                "PermissionMode 'bypassPermissions' is not supported in fleet containers (processes run as root). " +
                "Use 'acceptEdits' with an explicit AllowedTools list instead.");

        // Guard: container must not already exist
        var existing = await docker.InspectContainerAsync(agent.ContainerName, ct);
        if (existing is not null)
            return ProvisionResult.Fail(agentName,
                $"container '{agent.ContainerName}' already exists — deprovision first or use reprovision_agent");

        var envFile   = config["Provisioning:EnvFilePath"] ?? "/app/deploy/.env";
        var baseDir   = config["Provisioning:BaseDir"]
            ?? throw new InvalidOperationException("Provisioning:BaseDir is required but not configured.");
        var envValues = LoadEnvFile(envFile);
        envValues.TryGetValue("FLEET_CTO_AGENT", out var ctoAgentName);

        // #347: every assignment's EFFECTIVE mode is decided once, here, and everything below keys
        // on it. A card assignment whose context or card row is missing throws before any file is
        // written, so the agent is not started on a silent fallback.
        var projectContexts = await ResolveProjectContextsAsync(agent);

        await GenerateConfigFilesAsync(agent, baseDir, ctoAgentName ?? "", projectContexts);
        await GenerateInstructionFilesAsync(agent, baseDir, instructionVersionOverrides);
        await GenerateProjectContextFilesAsync(agent, baseDir, projectContexts);

        var spec      = BuildDesiredSpec(agent, envValues);

        // Apply image override if provided (e.g. CI uses a PR-specific image tag)
        if (!string.IsNullOrEmpty(imageOverride))
            spec = spec with { Image = imageOverride };

        // Expand relative paths in binds for direct Docker API calls
        var expandedBinds = spec.Binds.Select(b => ExpandBindPath(b, baseDir)).ToList();

        if (spec.Networks.Count == 0)
            return ProvisionResult.Fail(agentName, "no networks configured for agent");

        var primaryNetwork = spec.Networks[0];

        logger.LogInformation(
            "Provisioning '{Agent}' container='{Container}' image='{Image}'",
            agentName, agent.ContainerName, spec.Image);

        var logMaxSize = config["Provisioning:ContainerLogMaxSize"];
        var logMaxFile = config["Provisioning:ContainerLogMaxFile"];

        var containerId = await docker.CreateContainerAsync(
            agent.ContainerName,
            spec.Image,
            spec.MemoryBytes,
            spec.Env,
            expandedBinds,
            primaryNetwork,
            GetEffectiveHostPort(agent),
            ct,
            logMaxSize,
            logMaxFile);

        if (containerId is null)
            return ProvisionResult.Fail(agentName, "Docker API failed to create container — check orchestrator logs");

        // Connect to additional networks
        var extraNetworks = spec.Networks.Skip(1).ToList();
        foreach (var network in extraNetworks)
        {
            var ok = await docker.ConnectToNetworkAsync(network, containerId, ct);
            if (!ok)
                logger.LogWarning(
                    "Failed to connect '{Container}' to network '{Network}' — continuing",
                    agent.ContainerName, network);
        }

        // Start the container
        var started = await docker.StartContainerAsync(agent.ContainerName);
        if (!started)
            return ProvisionResult.Fail(agentName,
                $"container created (id={containerId[..12]}) but failed to start — check Docker logs");

        var networksMsg = string.Join(", ", spec.Networks);
        var notes = projectContexts.Notes.Count > 0 ? " — " + string.Join("; ", projectContexts.Notes) : "";
        return ProvisionResult.Ok(agentName,
            $"container '{agent.ContainerName}' created and started (id={containerId[..12]}, networks=[{networksMsg}]){notes}");
    }

    /// <summary>
    /// Stops and removes the container for the given agent.
    /// Fails if the container is not found.
    /// </summary>
    public async Task<ProvisionResult> DeprovisionAsync(string agentName, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        var agent = await db.Agents
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Name == agentName, ct);

        if (agent is null)
            return ProvisionResult.Fail(agentName, "agent not found in DB");

        var existing = await docker.InspectContainerAsync(agent.ContainerName, ct);
        if (existing is null)
            return ProvisionResult.Fail(agentName,
                $"container '{agent.ContainerName}' not found — nothing to deprovision");

        logger.LogInformation("Deprovisioning '{Agent}' (container='{Container}')", agentName, agent.ContainerName);

        // Stop first (graceful), then remove with force
        var stopped = await docker.StopContainerAsync(agent.ContainerName);
        if (!stopped)
            logger.LogWarning("Stop returned false for '{Container}' — will still attempt remove", agent.ContainerName);

        var removed = await docker.RemoveContainerAsync(agent.ContainerName, ct);
        if (!removed)
            return ProvisionResult.Fail(agentName,
                $"failed to remove container '{agent.ContainerName}' — check orchestrator logs");

        return ProvisionResult.Ok(agentName, $"container '{agent.ContainerName}' stopped and removed");
    }

    /// <summary>
    /// Deprovisions then re-provisions the container. Used when config has changed.
    /// An optional imageOverride overrides the DB-configured image (used by CI for PR-specific images).
    /// </summary>
    public async Task<ProvisionResult> ReprovisionAsync(
        string agentName,
        string? imageOverride = null,
        IReadOnlyDictionary<string, int>? instructionVersionOverrides = null,
        CancellationToken ct = default)
    {
        var deprovision = await DeprovisionAsync(agentName, ct);
        if (!deprovision.Success)
        {
            // If the container simply doesn't exist, skip deprovision and go straight to provision.
            // This handles manually-removed containers and first-time provision after config-only updates.
            if (deprovision.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning(
                    "Deprovision skipped for '{Agent}' — container not found, proceeding to provision",
                    agentName);
            }
            else
            {
                return ProvisionResult.Fail(agentName, $"deprovision failed: {deprovision.Message}");
            }
        }

        var provision = await ProvisionAsync(agentName, imageOverride, instructionVersionOverrides, ct);
        if (!provision.Success)
            return ProvisionResult.Fail(agentName,
                $"deprovision succeeded but provision failed: {provision.Message}");

        return ProvisionResult.Ok(agentName, $"reprovision complete — {provision.Message}");
    }

    /// <summary>
    /// Reprovisiones all DB-registered agents whose containers are currently running.
    /// Agents whose containers are not running are skipped.
    /// An optional imageOverride is applied to every agent (e.g. for bulk image upgrades).
    /// Returns per-agent results.
    /// </summary>
    public async Task<IReadOnlyList<ProvisionResult>> ReprovisionRunningAsync(
        string? imageOverride = null,
        CancellationToken ct = default)
    {
        // 1. Fetch all enabled agent names + container names from DB
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        var agents = await db.Agents
            .AsNoTracking()
            .Where(a => a.IsEnabled)
            .OrderByDescending(a => a.Id)
            .Select(a => new { a.Name, a.ContainerName })
            .ToListAsync(ct);

        // 2. Fetch set of currently running container names from Docker
        var runningNames = await docker.ListRunningContainerNamesAsync(ct);
        if (runningNames is null)
            return [ProvisionResult.Fail("(all)", "Docker API unavailable — cannot list running containers")];

        var results = new List<ProvisionResult>();

        foreach (var agent in agents)
        {
            // Skip if the container is not currently running
            if (!runningNames.Contains(agent.ContainerName))
            {
                logger.LogInformation(
                    "Skipping '{Agent}' (container '{Container}' not running)",
                    agent.Name, agent.ContainerName);
                continue;
            }

            logger.LogInformation(
                "Reprovisioning running agent '{Agent}' (container='{Container}')",
                agent.Name, agent.ContainerName);

            var result = await ReprovisionAsync(agent.Name, imageOverride, null, ct);
            results.Add(result);
        }

        return results;
    }

    // ── path expansion ─────────────────────────────────────────────────────────

    /// <summary>
    /// Expands a bind string's host-side path: replaces ./ with baseDir.
    /// Format: "host:container" or "host:container:options"
    /// </summary>
    private static string ExpandBindPath(string bind, string baseDir)
    {
        var parts = bind.Split(':', 3);
        if (parts.Length < 2) return bind;

        var host = parts[0]
            .Replace("./", $"{baseDir.TrimEnd('/')}/", StringComparison.Ordinal);

        return parts.Length == 3
            ? $"{host}:{parts[1]}:{parts[2]}"
            : $"{host}:{parts[1]}";
    }

    /// <summary>
    /// Ensures that all distinct networks referenced in agent_networks exist as
    /// standalone Docker bridge networks. Creates any that are missing.
    /// Returns a summary of what was created vs already existed.
    /// </summary>
    public async Task<NetworkEnsureResult> EnsureNetworksExistAsync(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        var allNetworks = await db.AgentNetworks
            .Select(n => n.NetworkName)
            .Distinct()
            .ToListAsync(ct);

        if (allNetworks.Count == 0)
        {
            // Fallback: ensure the default fleet network exists even if DB is empty
            allNetworks = [FleetNetwork];
        }

        var created  = new List<string>();
        var existing = new List<string>();
        var failed   = new List<string>();

        foreach (var networkName in allNetworks)
        {
            var ok = await docker.CreateNetworkIfMissingAsync(networkName, ct);
            if (!ok)
            {
                failed.Add(networkName);
                continue;
            }
            // Distinguish created vs already-existed via a second check? We can't easily
            // tell from the API, so just report all as "ensured".
            existing.Add(networkName);
        }

        logger.LogInformation(
            "Network ensure complete: {Total} network(s) checked, {Failed} failed",
            allNetworks.Count, failed.Count);

        return new NetworkEnsureResult(existing, failed);
    }

    // ── Docker inspect ─────────────────────────────────────────────────────────

    /// <summary>
    /// Inspects the actual running container, trying the DB name first then the
    /// Docker Compose v2 name ({project}-{service}-1). Returns the spec and the
    /// resolved container name that was found.
    /// </summary>
    public async Task<(ContainerSpec? Spec, string ResolvedName)> InspectActualAsync(
        string containerName, CancellationToken ct = default)
    {
        // Try DB name first (used when orchestrator provisions directly)
        var json = await docker.InspectContainerAsync(containerName, ct);

        // Fallback to Docker Compose v2 naming: {project}-{service}-1
        var resolvedName = containerName;
        if (json is null)
        {
            var project = config["Provisioning:ComposeProject"] ?? "fleet";
            var composeName = $"{project}-{containerName}-1";
            json = await docker.InspectContainerAsync(composeName, ct);
            if (json is not null)
            {
                resolvedName = composeName;
                logger.LogDebug(
                    "Container '{DbName}' not found by DB name, resolved to compose name '{ComposeName}'",
                    containerName, composeName);
            }
        }

        if (json is null)
            return (null, resolvedName);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var image  = root.GetProperty("Config").GetProperty("Image").GetString() ?? "";
            var memory = root.GetProperty("HostConfig").GetProperty("Memory").GetInt64();

            var env = new List<string>();
            if (root.GetProperty("Config").TryGetProperty("Env", out var envArr))
                foreach (var e in envArr.EnumerateArray())
                    env.Add(e.GetString() ?? "");

            var binds = new List<string>();
            if (root.GetProperty("HostConfig").TryGetProperty("Binds", out var bindsEl)
                && bindsEl.ValueKind == JsonValueKind.Array)
                foreach (var b in bindsEl.EnumerateArray())
                    binds.Add(b.GetString() ?? "");

            var networks = new List<string>();
            if (root.TryGetProperty("NetworkSettings", out var ns)
                && ns.TryGetProperty("Networks", out var netsEl))
                foreach (var n in netsEl.EnumerateObject())
                    networks.Add(n.Name);

            return (new ContainerSpec(image, memory, env, binds, networks), resolvedName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse Docker inspect response for {Container}", resolvedName);
            return (null, resolvedName);
        }
    }

    // ── diff ───────────────────────────────────────────────────────────────────

    public static List<string> ComputeDiff(ContainerSpec desired, ContainerSpec actual)
    {
        var diffs = new List<string>();

        // Image
        if (!string.Equals(desired.Image, actual.Image, StringComparison.OrdinalIgnoreCase))
            diffs.Add($"image: desired '{desired.Image}' vs actual '{actual.Image}'");

        // Memory
        if (desired.MemoryBytes != actual.MemoryBytes)
            diffs.Add($"memory: desired {FormatBytes(desired.MemoryBytes)} vs actual {FormatBytes(actual.MemoryBytes)}");

        // Networks
        var desiredNets = new HashSet<string>(desired.Networks, StringComparer.OrdinalIgnoreCase);
        var actualNets  = new HashSet<string>(actual.Networks,  StringComparer.OrdinalIgnoreCase);
        foreach (var n in desiredNets.Except(actualNets))
            diffs.Add($"network missing: '{n}'");
        foreach (var n in actualNets.Except(desiredNets))
            diffs.Add($"network extra: '{n}'");

        // Binds — compare container-side paths only (host paths differ between environments)
        var desiredContainerPaths = desired.Binds
            .Select(ParseContainerPath)
            .Where(p => p is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;
        var actualContainerPaths = actual.Binds
            .Select(ParseContainerPath)
            .Where(p => p is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;

        foreach (var p in desiredContainerPaths.Except(actualContainerPaths))
            diffs.Add($"mount missing (container path): '{p}'");
        foreach (var p in actualContainerPaths.Except(desiredContainerPaths))
            diffs.Add($"mount extra (container path): '{p}'");

        // Env — compare keys only (values may differ due to secrets)
        // Exclude base-image env vars that are injected by Docker/the base image and are not
        // part of the agent's desired config.
        var baseImageEnvPrefixes = new[] { "DOTNET_", "ASPNETCORE_" };
        var baseImageEnvKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "PATH", "APP_UID", "HOME", "HOSTNAME", "TERM",
            "DOTNET_RUNNING_IN_CONTAINER", "DOTNET_VERSION",
        };
        bool IsBaseImageEnv(string key) =>
            baseImageEnvKeys.Contains(key) ||
            baseImageEnvPrefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal));

        var desiredEnvKeys = desired.Env.Select(ParseEnvKey).Where(k => !IsBaseImageEnv(k)).ToHashSet(StringComparer.Ordinal);
        var actualEnvKeys  = actual.Env.Select(ParseEnvKey).Where(k => !IsBaseImageEnv(k)).ToHashSet(StringComparer.Ordinal);
        foreach (var k in desiredEnvKeys.Except(actualEnvKeys))
            diffs.Add($"env missing: '{k}'");
        foreach (var k in actualEnvKeys.Except(desiredEnvKeys))
            diffs.Add($"env extra: '{k}'");

        return diffs;
    }

    // ── helpers (internal so PreviewAgentProvisionTool can reuse) ──────────────

    internal static string FormatBytes(long bytes) => bytes switch
    {
        0           => "unlimited / unset",
        >= 1 << 30  => $"{bytes / (1 << 30)}GB",
        >= 1 << 20  => $"{bytes / (1 << 20)}MB",
        _           => $"{bytes}B",
    };

    private static Dictionary<string, string> LoadEnvFile(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return result;
        try
        {
            foreach (var line in File.ReadAllLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith('#') || !trimmed.Contains('=')) continue;
                var idx = trimmed.IndexOf('=');
                var key = trimmed[..idx].Trim();
                var val = trimmed[(idx + 1)..].Trim().Trim('"');
                result[key] = val;
            }
        }
        catch (Exception) { /* non-fatal */ }
        return result;
    }

    private static string DeriveGitName(string role) => role switch
    {
        "co-cto"          => "Acto",
        "developer"       => "Adev",
        "devops"          => "Aops",
        "product-manager" => "Apm",
        _                 => role,
    };

    // ── config file generation ─────────────────────────────────────────────────

    /// <summary>
    /// Generates appsettings.json, .mcp.json, and settings.json into the agent's
    /// .generated/ workspace directory before the container is started.
    /// </summary>
    private async Task GenerateConfigFilesAsync(
        Agent agent, string baseDir, string ctoAgentName, ProjectContextPlan projectContexts)
    {
        var generatedDir = Path.Combine(baseDir, "workspaces", agent.ContainerName, ".generated");
        Directory.CreateDirectory(generatedDir);

        var enabledTools  = agent.Tools.Where(t => t.IsEnabled).Select(t => t.ToolName).OrderBy(t => t).ToList();
        var mcpEndpoints  = agent.McpEndpoints.Select(e => e.McpName).OrderBy(e => e).ToList();

        logger.LogInformation(
            "Generating config for '{Agent}': {ToolCount} tools [{Tools}], {McpCount} MCP endpoints [{Mcp}], permissionMode={Mode}",
            agent.Name,
            enabledTools.Count, string.Join(", ", enabledTools),
            mcpEndpoints.Count, string.Join(", ", mcpEndpoints),
            agent.PermissionMode);

        var fleetMemoryMcpUrl = NormalizeFleetMemoryMcpUrl(config["FleetMemory:McpUrl"]);

        // The style file is written BEFORE settings.json, so "settings.json names a style that
        // exists on disk" is a fact about the order rather than a hope. GenerateSettingsJson
        // refuses the reference outright when the row is missing.
        var style = await ResolveOutputStyleAsync(agent);
        await WriteOutputStyleFileAsync(agent, generatedDir, style);

        // #347 D6: the fallback endpoint and its one grant exist only for an agent with at least one
        // EFFECTIVE card assignment. Everyone else gets exactly the bytes they got before cards.
        var contextMcpUrl = projectContexts.HasEffectiveCard
            ? ResolveContextMcpUrl(config["Provisioning:ContextMcpUrl"])
            : null;

        await File.WriteAllTextAsync(Path.Combine(generatedDir, "appsettings.json"),
            GenerateAppsettingsJson(agent, ctoAgentName, style, projectContexts.Routing));
        await File.WriteAllTextAsync(Path.Combine(generatedDir, ".mcp.json"),
            GenerateMcpJson(agent, fleetMemoryMcpUrl, contextMcpUrl));
        await File.WriteAllTextAsync(Path.Combine(generatedDir, "settings.json"),
            GenerateSettingsJson(agent, ctoAgentName, style, grantContextFallback: projectContexts.HasEffectiveCard));

        logger.LogInformation(
            "Generated config files for '{Agent}' in {Dir}",
            agent.Name, generatedDir);
    }

    // ── output styles ──────────────────────────────────────────────────────────

    /// <summary>
    /// Whether this agent resolves its style as a Claude Code style FILE, as opposed to having the
    /// same text inlined into its prompt.
    /// </summary>
    /// <remarks>
    /// Output styles are a Claude Code feature: only a claude agent has anything that reads the
    /// file or the <c>settings.json</c> key. Codex and gemini carry the identical text in
    /// <c>Agent.OutputStyleBody</c> instead — see <see cref="GenerateAppsettingsJson"/>. The split
    /// is here, in one predicate, so no path can give a provider the file and forget the prompt.
    /// </remarks>
    internal static bool HasStyleFile(Agent agent) =>
        !string.IsNullOrWhiteSpace(agent.OutputStyle) &&
        string.Equals(agent.Provider, "claude", StringComparison.OrdinalIgnoreCase);

    private async Task<OutputStyle?> ResolveOutputStyleAsync(Agent agent)
    {
        if (string.IsNullOrWhiteSpace(agent.OutputStyle)) return null;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        return await db.OutputStyles.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Name == agent.OutputStyle);
    }

    private async Task WriteOutputStyleFileAsync(Agent agent, string generatedDir, OutputStyle? style)
    {
        var dir = Path.Combine(generatedDir, "output-styles");

        if (!HasStyleFile(agent) || style is null)
        {
            // A stale file from a previous provision would still be mounted and discoverable, so
            // clearing the style has to remove it rather than merely stop naming it.
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            return;
        }

        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(
            Path.Combine(dir, $"{style.Name}.md"), OutputStyleRenderer.ForStyleFile(style));

        logger.LogInformation(
            "Wrote output style '{Style}' for '{Agent}' to {Dir}", style.Name, agent.Name, dir);
    }

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

    /// <summary>
    /// Generates roles/_base/system.md and roles/{name}/system.md from DB instruction content
    /// into the agent's .generated/roles/ workspace directory before the container is started.
    /// </summary>
    private async Task GenerateInstructionFilesAsync(
        Agent agent,
        string baseDir,
        IReadOnlyDictionary<string, int>? versionOverrides = null)
    {
        var rolesDir = Path.Combine(baseDir, "workspaces", agent.ContainerName, ".generated", "roles");

        var instructions = OrderedInstructions(agent);

        if (instructions.Count == 0)
        {
            logger.LogWarning("No instructions configured for '{Agent}' — skipping instruction file generation", agent.Name);
            return;
        }

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
            var subDir = InstructionDirectoryName(instruction.Name);
            var dir    = Path.Combine(rolesDir, subDir);
            Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(Path.Combine(dir, "system.md"), version.Content);
        }

        logger.LogInformation(
            "Generated instruction files for '{Agent}' in {Dir} ({Count} instruction(s))",
            agent.Name, rolesDir, instructions.Count);
    }

    // ── project contexts (#347: per-assignment full/card) ──────────────────────

    /// <summary>The fallback MCP route's default URL, matching the example compose service name.</summary>
    internal const string DefaultContextMcpUrl = "http://fleet-orchestrator:3600/mcp/context";

    /// <summary>The MCP server name card agents reach the fallback route under.</summary>
    internal const string ContextMcpServerName = "fleet-context";

    /// <summary>The one tool grant the fallback route carries.</summary>
    internal const string ContextFallbackGrant = "mcp__fleet-context__get_project_context";

    /// <summary><c>Provisioning:ContextMcpUrl</c>, or the default when unset or not an absolute URL.</summary>
    internal static string ResolveContextMcpUrl(string? raw) =>
        !string.IsNullOrWhiteSpace(raw) && Uri.TryCreate(raw.Trim(), UriKind.Absolute, out _)
            ? raw.Trim()
            : DefaultContextMcpUrl;

    /// <summary>
    /// Loads the agent's assigned project contexts and decides each assignment's effective mode.
    /// Names are matched in C# (<see cref="StringComparer.OrdinalIgnoreCase"/>) on loaded rows, never
    /// by database collation.
    /// </summary>
    private async Task<ProjectContextPlan> ResolveProjectContextsAsync(Agent agent)
    {
        if (agent.Projects.Count == 0)
            return ProjectContextPlan.Empty;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        var headers = await db.ProjectContexts.AsNoTracking().Select(p => new { p.Id, p.Name }).ToListAsync();
        var ids = agent.Projects
            .Select(ap => ProjectContextAccess.MatchByName(headers, ap.ProjectName, h => h.Name)?.Id)
            .OfType<int>()
            .Distinct()
            .ToList();

        var contexts = ids.Count == 0
            ? []
            : await db.ProjectContexts
                .Include(p => p.Versions)
                .Include(p => p.CardVersions)
                .Include(p => p.Routes)
                .Where(p => ids.Contains(p.Id))
                .AsSplitQuery()
                .AsNoTracking()
                .ToListAsync();

        return BuildProjectContextPlan(agent, contexts, logger);
    }

    /// <summary>
    /// Decides every assignment's EFFECTIVE mode from loaded rows, once per provision.
    /// </summary>
    /// <remarks>
    /// <list type="table">
    /// <listheader><term>DB mode / card state</term><description>effective · <c>context.md</c> · <c>full.md</c></description></listheader>
    /// <item><term><c>full</c>, any</term><description>full · full content, or today's stub · none</description></item>
    /// <item><term><c>card</c>, no missing keeps</term><description>card · card + footer · full content (<c>Warning card_stale</c> when stale)</description></item>
    /// <item><term><c>card</c>, missing keeps</term><description>full · full content · none (<c>Warning card_fallback_full</c> + a result note)</description></item>
    /// <item><term><c>card</c>, no context row / no card</term><description>throws — the agent is not started</description></item>
    /// </list>
    /// A card that lacks a keep marker of the current full context is never rendered: it would drop
    /// a marked rule from the agent's prompt. Falling back to full keeps the rule resident, and says so.
    /// </remarks>
    internal static ProjectContextPlan BuildProjectContextPlan(
        Agent agent, IReadOnlyCollection<ProjectContext> contexts, ILogger logger)
    {
        var assignments = new List<ProjectContextAssignment>();
        var notes = new List<string>();
        var matched = new List<(string ProjectName, ProjectContext Context)>();

        foreach (var agentProject in agent.Projects)
        {
            var projectName = agentProject.ProjectName;
            var ctx = ProjectContextAccess.MatchByName(contexts, projectName, c => c.Name);
            if (ctx is not null)
                matched.Add((projectName, ctx));

            var version = ctx?.Versions.FirstOrDefault(v => v.VersionNumber == ctx.CurrentVersion)
                       ?? ctx?.Versions.OrderByDescending(v => v.VersionNumber).FirstOrDefault();

            if (!string.Equals(agentProject.ContextMode, ProjectContextMode.Card, StringComparison.Ordinal))
            {
                assignments.Add(FullAssignment(agent, projectName, agentProject.ContextMode, ctx, version, logger));
                continue;
            }

            // Card mode. Every inconsistency below is only reachable by hand-editing the DB (the
            // write paths refuse card mode without a card), and falling back silently would hide it.
            if (ctx is null)
                throw CardProvisionFault(agent, projectName, "no project context with that name exists");
            if (ctx.CurrentCardVersion is not { } cardVersion)
                throw CardProvisionFault(agent, projectName, "the project has no card (CurrentCardVersion is NULL)");
            var card = ctx.CardVersions.FirstOrDefault(c => c.VersionNumber == cardVersion)
                ?? throw CardProvisionFault(agent, projectName, $"card version {cardVersion} has no row");
            if (version is null)
                throw CardProvisionFault(agent, projectName, "the project context has no full versions");

            var state = ProjectCardService.Evaluate(
                card.VersionNumber, card.BasedOnFullVersion, card.Content, version.VersionNumber, version.Content);
            var effectiveCard = state.MissingKeeps.Count == 0;

            logger.LogInformation(
                "ProjectContext assignment agent={Agent} project={Project} mode={Mode} effective={Effective} " +
                "card={Card} basedOn={BasedOn} full={Full} stale={Stale} missingKeeps={MissingKeeps} invalidKeeps={InvalidKeeps}",
                agent.Name, projectName, ProjectContextMode.Card,
                effectiveCard ? ProjectContextMode.Card : ProjectContextMode.Full,
                state.CurrentVersion, state.BasedOnFullVersion, version.VersionNumber,
                state.Stale ? "true" : "false",
                state.MissingKeeps.Count == 0 ? "-" : string.Join(",", state.MissingKeeps),
                state.InvalidKeeps.Count);

            if (!effectiveCard)
            {
                var slugs = string.Join(", ", state.MissingKeeps);
                logger.LogWarning(
                    "ProjectContext card_fallback_full agent={Agent} project={Project} card={Card} missingKeeps={MissingKeeps} " +
                    "— the card lacks keep marker(s) of full v{Full}; rendering the full context",
                    agent.Name, projectName, state.CurrentVersion, string.Join(",", state.MissingKeeps), version.VersionNumber);
                notes.Add(
                    $"project '{projectName}' rendered in full: its card v{state.CurrentVersion} is missing keep marker(s) {slugs}");
                assignments.Add(new ProjectContextAssignment(
                    projectName, ProjectContextMode.Card, EffectiveCard: false, version.Content, FullMd: null, version.VersionNumber));
                continue;
            }

            if (state.Stale)
            {
                logger.LogWarning(
                    "ProjectContext card_stale agent={Agent} project={Project} card={Card} basedOn={BasedOn} full={Full}",
                    agent.Name, projectName, state.CurrentVersion, state.BasedOnFullVersion, version.VersionNumber);
            }

            assignments.Add(new ProjectContextAssignment(
                projectName,
                ProjectContextMode.Card,
                EffectiveCard: true,
                ProjectCardService.RenderResidentCard(
                    projectName, card.Content, card.VersionNumber, card.BasedOnFullVersion, version.VersionNumber),
                FullMd: version.Content,
                version.VersionNumber));
        }

        // Routes cover EVERY assigned project, in any effective mode: precedence is evaluated over
        // all of them, and a level whose matches are all full must still win (and attach nothing).
        var routes = assignments.Any(a => a.EffectiveCard)
            ? matched
                .SelectMany(m => m.Context.Routes.Select(r => new ProjectContextRouteEntry(r.SignalKind, r.SignalValue, m.ProjectName)))
                .Distinct()
                .OrderBy(r => r.Kind, StringComparer.Ordinal)
                .ThenBy(r => r.Value, StringComparer.Ordinal)
                .ThenBy(r => r.Project, StringComparer.Ordinal)
                .ToList()
            : [];

        return new ProjectContextPlan(assignments, routes, notes);
    }

    /// <summary>A full-mode assignment: exactly what provisioning wrote before cards existed.</summary>
    private static ProjectContextAssignment FullAssignment(
        Agent agent, string projectName, string mode, ProjectContext? ctx, ProjectContextVersion? version, ILogger logger)
    {
        if (ctx is null)
        {
            logger.LogWarning(
                "Project context '{Project}' not found in DB for agent '{Agent}' — generating empty stub",
                projectName, agent.Name);
            return new ProjectContextAssignment(projectName, mode, EffectiveCard: false,
                $"# {projectName}\n\n(No content — project context not yet seeded in DB)\n", FullMd: null, FullVersion: null);
        }

        if (version is null)
        {
            logger.LogWarning(
                "Project context '{Project}' has no versions for agent '{Agent}' — generating empty stub",
                projectName, agent.Name);
            return new ProjectContextAssignment(projectName, mode, EffectiveCard: false,
                $"# {projectName}\n\n(No content — no versions found)\n", FullMd: null, FullVersion: null);
        }

        return new ProjectContextAssignment(projectName, mode, EffectiveCard: false, version.Content, FullMd: null, version.VersionNumber);
    }

    private static InvalidOperationException CardProvisionFault(Agent agent, string projectName, string reason) =>
        new($"Agent '{agent.Name}' cannot be provisioned: project '{projectName}' is assigned in card mode, but " +
            $"{reason}. Flip the assignment to full or repair the project context, then reprovision.");

    /// <summary>
    /// Writes <c>projects/{name}/context.md</c> for every assignment and <c>projects/{name}/full.md</c>
    /// for effective card assignments into the agent's <c>.generated/projects/</c> directory before the
    /// container is started.
    /// </summary>
    private async Task GenerateProjectContextFilesAsync(Agent agent, string baseDir, ProjectContextPlan plan)
    {
        var projectsDir = Path.Combine(baseDir, "workspaces", agent.ContainerName, ".generated", "projects");
        Directory.CreateDirectory(projectsDir);

        // A full.md left by an earlier card provision would still be mounted, and the agent attaches
        // it for any project in cardProjects — so every full.md that is not an effective card
        // assignment's is removed, not merely left unwritten.
        var cardNames = plan.Assignments.Where(a => a.EffectiveCard).Select(a => a.ProjectName).ToHashSet(StringComparer.Ordinal);
        foreach (var dir in Directory.EnumerateDirectories(projectsDir))
        {
            var fullMd = Path.Combine(dir, "full.md");
            if (!cardNames.Contains(Path.GetFileName(dir)) && File.Exists(fullMd))
            {
                File.Delete(fullMd);
                logger.LogInformation(
                    "Removed stale full.md for project '{Project}' of '{Agent}'", Path.GetFileName(dir), agent.Name);
            }
        }

        if (agent.Projects.Count == 0)
        {
            logger.LogInformation("No projects configured for '{Agent}' — skipping project context file generation", agent.Name);
            return;
        }

        foreach (var assignment in plan.Assignments)
        {
            var dir = Path.Combine(projectsDir, assignment.ProjectName);
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "context.md"), assignment.ContextMd);
            if (assignment.FullMd is not null)
                await File.WriteAllTextAsync(Path.Combine(dir, "full.md"), assignment.FullMd);
        }

        logger.LogInformation(
            "Generated project context files for '{Agent}' in {Dir} ({Count} project(s))",
            agent.Name, projectsDir, agent.Projects.Count);
    }

    /// <param name="routing">
    /// #347: the <c>Agent.ProjectContextRouting</c> block, non-null only for an agent with at least
    /// one effective card assignment — see <see cref="ProjectContextPlan.Routing"/>.
    /// </param>
    internal static string GenerateAppsettingsJson(
        Agent agent, string ctoAgentName, OutputStyle? style = null, ProjectContextRouting? routing = null)
    {
        if (!string.IsNullOrWhiteSpace(agent.OutputStyle) && style is null)
            throw new InvalidOperationException(
                $"Agent '{agent.Name}' names output style '{agent.OutputStyle}', which has no row in " +
                "output_styles — refusing to generate config that drops the style silently.");

        // #340 D1 point 2: a row edited by hand into an invalid state stops here, and reprovision
        // leaves the agent down with the named fault instead of starting it misconfigured.
        if (DescribeClaudeLocalModelFault(agent) is { } localFault)
            throw new InvalidOperationException($"Agent '{agent.Name}' cannot be provisioned: {localFault}");

        var tools = agent.Tools.Where(t => t.IsEnabled).OrderBy(t => t.ToolName).Select(t => t.ToolName).ToList();

        // Codex derives config.toml enabled_tools from AllowedTools (entrypoint.sh).
        // Auto-grant the same baseline tools that GenerateSettingsJson grants for claude/gemini,
        // but gate strictly to codex — other providers don't read AllowedTools this way.
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

            // #347: the fallback tool, for an agent with an effective card assignment only.
            if (routing is not null && !tools.Contains(ContextFallbackGrant, StringComparer.OrdinalIgnoreCase))
                tools.Add(ContextFallbackGrant);

            tools.Sort(StringComparer.OrdinalIgnoreCase);
        }

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
                AnthropicBaseUrl = ClaudeLocalModel.IsEnabled(agent.Provider, agent.AnthropicBaseUrl)
                    ? ClaudeLocalModel.CanonicalizeBaseUrl(agent.AnthropicBaseUrl!)
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

        // Inlined for codex and gemini only. Claude reads the style file instead, and putting the
        // text in both places would assert the same rules twice with nothing to gain.
        //
        // Added by editing the serialized document rather than by a nullable property on the
        // anonymous type above, because a null property still SERIALIZES — and an agent with no
        // style must produce the same bytes it produced before this existed.
        //
        // #347's ProjectContextRouting block follows the same rule, for the same reason: an agent
        // with no effective card assignment is byte-identical to before cards existed.
        var inlineStyle = style is not null && !HasStyleFile(agent);
        if (!inlineStyle && routing is null) return json;

        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        var agentNode = node["Agent"]!.AsObject();
        if (inlineStyle)
            agentNode["OutputStyleBody"] = OutputStyleRenderer.ForPrompt(style!);
        if (routing is not null)
            agentNode["ProjectContextRouting"] = routing.ToJsonNode();
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

    /// <param name="contextMcpUrl">
    /// #347: the fallback route's base URL, non-null only for an agent with at least one effective
    /// card assignment. It adds <c>fleet-context</c> (an explicit DB row of that name wins, but still
    /// gets <c>?agent=</c>). The admin <c>/mcp</c> is never injected here.
    /// </param>
    internal static string GenerateMcpJson(Agent agent, string fleetMemoryMcpUrl, string? contextMcpUrl = null)
    {
        var mcpServers = agent.McpEndpoints
            .OrderBy(e => e.McpName)
            .ToDictionary(
                e => e.McpName,
                e =>
                {
                    // Append ?agent={name} to fleet-telegram, fleet-memory, and fleet-temporal URLs
                    // so each server can identify the calling agent without relying on the LLM to pass it.
                    // WithAgentParam strips any existing query string before appending to prevent
                    // double-appending when the URL was stored in the DB with ?agent= already.
                    // fleet-telegram also gets ?formatting_mode={n} so send tools can honour the agent's
                    // FormattingMode without an extra orchestrator round-trip on every send.
                    // fleet-context too, but only when the fallback is due: the route 403s any
                    // session without an agent, and a zero-card agent's row is left byte-identical.
                    var url = e.McpName == "fleet-telegram"
                        ? WithAgentTelegramParams(e.Url, agent.Name, (byte)agent.FormattingMode)
                        : (e.McpName == "fleet-memory" || e.McpName == "fleet-temporal" ||
                           (contextMcpUrl is not null && e.McpName == ContextMcpServerName))
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

        // #347 D6: the card agents' fallback — one read-only tool on sessions bound to this agent.
        if (contextMcpUrl is not null && !mcpServers.ContainsKey(ContextMcpServerName))
        {
            var url = WithAgentParam(contextMcpUrl, agent.Name);
            mcpServers[ContextMcpServerName] = new { type = "http", url };
        }

        return JsonSerializer.Serialize(new { mcpServers }, IndentedJson);
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
    /// <param name="grantContextFallback">
    /// #347: true only for an agent with at least one effective card assignment, which gets
    /// <c>mcp__fleet-context__get_project_context</c>.
    /// </param>
    internal static string GenerateSettingsJson(
        Agent agent, string ctoAgentName, OutputStyle? style = null, bool grantContextFallback = false)
    {
        if (!string.IsNullOrWhiteSpace(agent.OutputStyle) && style is null)
            throw new InvalidOperationException(
                $"Agent '{agent.Name}' names output style '{agent.OutputStyle}', which has no row in " +
                "output_styles — refusing to write settings.json with an unresolvable reference.");

        var allow = agent.Tools
            .Where(t => t.IsEnabled)
            .OrderBy(t => t.ToolName)
            .Select(t => t.ToolName)
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

        if (grantContextFallback && !allow.Contains(ContextFallbackGrant, StringComparer.OrdinalIgnoreCase))
            allow.Add(ContextFallbackGrant);

        allow.Sort(StringComparer.OrdinalIgnoreCase);

        // Two shapes rather than one with a null: the key must be ABSENT, not null, for an agent
        // with no style — Claude Code would resolve a null to nothing useful and the byte-identity
        // guarantee would be gone either way.
        return HasStyleFile(agent) && style is not null
            ? JsonSerializer.Serialize(new { permissions = new { allow }, outputStyle = style.Name }, IndentedJson)
            : JsonSerializer.Serialize(new { permissions = new { allow } }, IndentedJson);
    }

    private static string? ParseContainerPath(string bind)
    {
        // bind format: "host:container" or "host:container:options"
        var parts = bind.Split(':', 3);
        return parts.Length >= 2 ? parts[1] : null;
    }

    internal static string ParseEnvKey(string envEntry)
    {
        var idx = envEntry.IndexOf('=');
        return idx >= 0 ? envEntry[..idx] : envEntry;
    }

    /// <summary>
    /// Returns the effective HostPort for an agent.
    /// Uses the DB-stored value if set; otherwise computes deterministically as 8080 + agent.Id.
    /// </summary>
    public static int GetEffectiveHostPort(Agent agent) => agent.HostPort ?? (8080 + agent.Id);

    /// <summary>
    /// Returns the base URL for proxying HTTP requests to an agent.
    /// Prefers container-name routing on the Docker network when a container name is available.
    /// Falls back to host-port routing for agents without a container name (e.g. not yet provisioned).
    /// </summary>
    public string GetAgentBaseUrl(Agent agent)
    {
        if (!string.IsNullOrWhiteSpace(agent.ContainerName))
            return $"http://{agent.ContainerName}:8080";

        var hostPort = GetEffectiveHostPort(agent);
        logger.LogWarning(
            "Agent {Name} has no container name — falling back to host port {Port}. " +
            "This path will fail in containerized context; agent must be provisioned with a container name.",
            agent.Name, hostPort);
        return $"http://127.0.0.1:{hostPort}";
    }
}

public record ContainerSpec(
    string Image,
    long MemoryBytes,
    List<string> Env,
    List<string> Binds,
    List<string> Networks);

public record ProvisionPreview(
    string AgentName,
    string ContainerName,
    /// <summary>The actual container name that was found (may differ from ContainerName if compose naming was used).</summary>
    string ResolvedContainerName,
    ContainerSpec Desired,
    ContainerSpec? Actual,
    List<string> Diffs)
{
    public static ProvisionPreview NotFound(string agentName) =>
        new(agentName, "", "", new ContainerSpec("", 0, [], [], []), null, [$"agent '{agentName}' not found in DB"]);
}

public record NetworkEnsureResult(
    List<string> Ensured,
    List<string> Failed)
{
    public bool AllOk => Failed.Count == 0;
}

public record ProvisionResult(string AgentName, bool Success, string Message)
{
    public static ProvisionResult Ok(string agentName, string message)   => new(agentName, true,  message);
    public static ProvisionResult Fail(string agentName, string message) => new(agentName, false, message);
}

/// <summary>One assignment's project context, resolved once per provision (#347).</summary>
/// <param name="ProjectName">The assignment's name — the <c>projects/</c> directory the agent reads.</param>
/// <param name="Mode">The DB mode, <see cref="ProjectContextMode"/>.</param>
/// <param name="EffectiveCard">The card is resident. False for DB <c>full</c> and for a card that fell back.</param>
/// <param name="ContextMd">The <c>context.md</c> body.</param>
/// <param name="FullMd">The <c>full.md</c> body — the current full content — for effective card only.</param>
/// <param name="FullVersion">The full version rendered, or null for a stub.</param>
internal sealed record ProjectContextAssignment(
    string ProjectName, string Mode, bool EffectiveCard, string ContextMd, string? FullMd, int? FullVersion);

/// <summary>One route in <c>Agent.ProjectContextRouting.routes</c>.</summary>
internal sealed record ProjectContextRouteEntry(string Kind, string Value, string Project);

/// <summary>Every assignment's effective mode, plus the routes and the notes for the provision result.</summary>
internal sealed record ProjectContextPlan(
    IReadOnlyList<ProjectContextAssignment> Assignments,
    IReadOnlyList<ProjectContextRouteEntry> Routes,
    IReadOnlyList<string> Notes)
{
    public static readonly ProjectContextPlan Empty = new([], [], []);

    public bool HasEffectiveCard => Assignments.Any(a => a.EffectiveCard);

    /// <summary>The <c>Agent.ProjectContextRouting</c> block, or null with no effective card assignment.</summary>
    public ProjectContextRouting? Routing
    {
        get
        {
            var cards = Assignments.Where(a => a.EffectiveCard).ToList();
            if (cards.Count == 0) return null;

            var cardProjects = cards.Select(a => a.ProjectName)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p, StringComparer.Ordinal)
                .ToList();
            var fullVersions = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var a in cards)
                fullVersions[a.ProjectName] = a.FullVersion!.Value;

            return new ProjectContextRouting(cardProjects, fullVersions, Routes);
        }
    }
}

/// <summary>
/// <c>Agent.ProjectContextRouting</c> in the generated <c>appsettings.json</c> (#347), bound by the
/// agent's <c>AgentOptions.ProjectContextRouting</c>. Property names are camelCase, as pinned.
/// </summary>
/// <param name="CardProjects">Effective card assignment names, ordinal-ignore-case sorted.</param>
/// <param name="FullVersions">The current full version of each card project.</param>
/// <param name="Routes">Routes of every assigned project, any effective mode; sorted kind, value, project.</param>
internal sealed record ProjectContextRouting(
    IReadOnlyList<string> CardProjects,
    IReadOnlyDictionary<string, int> FullVersions,
    IReadOnlyList<ProjectContextRouteEntry> Routes)
{
    public System.Text.Json.Nodes.JsonObject ToJsonNode()
    {
        var fullVersions = new System.Text.Json.Nodes.JsonObject();
        foreach (var p in CardProjects)
            fullVersions[p] = FullVersions[p];

        var routes = new System.Text.Json.Nodes.JsonArray();
        foreach (var r in Routes)
            routes.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["kind"] = r.Kind,
                ["value"] = r.Value,
                ["project"] = r.Project,
            });

        var cardProjects = new System.Text.Json.Nodes.JsonArray();
        foreach (var p in CardProjects)
            cardProjects.Add(p);

        return new System.Text.Json.Nodes.JsonObject
        {
            ["cardProjects"] = cardProjects,
            ["fullVersions"] = fullVersions,
            ["routes"] = routes,
        };
    }
}
