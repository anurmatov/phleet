using System.Text.Json;
using Fleet.Orchestrator.Data;
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

    // Roles whose containers mount /var/run/docker.sock (co-cto, devops, developer)
    private static readonly HashSet<string> DockerSockRoles =
        new(["co-cto", "devops", "developer"], StringComparer.OrdinalIgnoreCase);

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
        var containerName = agent.ContainerName;
        var role = agent.Role;

        var baseDir = config["Provisioning:BaseDir"]
            ?? throw new InvalidOperationException("Provisioning:BaseDir is required but not configured.");

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

        // docker.sock is only mounted for roles that need Docker CLI access
        if (DockerSockRoles.Contains(role))
            binds.Add("/var/run/docker.sock:/var/run/docker.sock");

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
            // Mount codex credentials for seeding new codex containers (entrypoint.sh reads this)
            var codexTokenStorePath = Path.Combine(baseDir, ".codex-credentials.json");
            if (File.Exists(codexTokenStorePath))
                binds.Add("./.codex-credentials.json:/root/.codex-host/auth.json:ro");
        }
        else
        {
            // Mount orchestrator-stored Claude credentials for seeding new containers (entrypoint.sh reads this)
            var tokenStorePath = Path.Combine(baseDir, ".claude-credentials.json");
            if (File.Exists(tokenStorePath))
                binds.Add("./.claude-credentials.json:/root/.claude-host/.credentials.json:ro");
        }

        // Credential file mounts (from Files section in Credentials view)
        foreach (var mount in agent.CredentialMounts)
        {
            if (File.Exists(mount.CredentialFile.FilePath))
                binds.Add($"{mount.CredentialFile.FilePath}:{mount.MountPath}:{mount.Mode}");
        }

        return binds;
    }

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

        await GenerateConfigFilesAsync(agent, baseDir, ctoAgentName ?? "");
        await GenerateInstructionFilesAsync(agent, baseDir, instructionVersionOverrides);
        await GenerateProjectContextFilesAsync(agent, baseDir);

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

        var containerId = await docker.CreateContainerAsync(
            agent.ContainerName,
            spec.Image,
            spec.MemoryBytes,
            spec.Env,
            expandedBinds,
            primaryNetwork,
            GetEffectiveHostPort(agent),
            ct);

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
        return ProvisionResult.Ok(agentName,
            $"container '{agent.ContainerName}' created and started (id={containerId[..12]}, networks=[{networksMsg}])");
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
    private async Task GenerateConfigFilesAsync(Agent agent, string baseDir, string ctoAgentName)
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

        var fleetMemoryMcpUrl = config["FleetMemory:McpUrl"] ?? "http://fleet-memory:3100/mcp";
        await File.WriteAllTextAsync(Path.Combine(generatedDir, "appsettings.json"), GenerateAppsettingsJson(agent));
        await File.WriteAllTextAsync(Path.Combine(generatedDir, ".mcp.json"),        GenerateMcpJson(agent, fleetMemoryMcpUrl));
        await File.WriteAllTextAsync(Path.Combine(generatedDir, "settings.json"),    GenerateSettingsJson(agent, ctoAgentName));

        logger.LogInformation(
            "Generated config files for '{Agent}' in {Dir}",
            agent.Name, generatedDir);
    }

    /// <summary>
    /// Generates roles/_base/system.md and roles/{role}/system.md from DB instruction content
    /// into the agent's .generated/roles/ workspace directory before the container is started.
    /// </summary>
    private async Task GenerateInstructionFilesAsync(
        Agent agent,
        string baseDir,
        IReadOnlyDictionary<string, int>? versionOverrides = null)
    {
        var rolesDir = Path.Combine(baseDir, "workspaces", agent.ContainerName, ".generated", "roles");

        var instructions = agent.Instructions
            .OrderBy(ai => ai.LoadOrder)
            .ToList();

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
            var subDir = instruction.Name == "base" ? "_base" : instruction.Name;
            var dir    = Path.Combine(rolesDir, subDir);
            Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(Path.Combine(dir, "system.md"), version.Content);
        }

        logger.LogInformation(
            "Generated instruction files for '{Agent}' in {Dir} ({Count} instruction(s))",
            agent.Name, rolesDir, instructions.Count);
    }

    /// <summary>
    /// Generates projects/{name}/context.md files from DB content into the agent's
    /// .generated/projects/ workspace directory before the container is started.
    /// </summary>
    private async Task GenerateProjectContextFilesAsync(Agent agent, string baseDir)
    {
        var projectsDir = Path.Combine(baseDir, "workspaces", agent.ContainerName, ".generated", "projects");
        Directory.CreateDirectory(projectsDir);

        if (agent.Projects.Count == 0)
        {
            logger.LogInformation("No projects configured for '{Agent}' — skipping project context file generation", agent.Name);
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        foreach (var agentProject in agent.Projects)
        {
            var projectName = agentProject.ProjectName;

            var ctx = await db.ProjectContexts
                .Include(p => p.Versions)
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Name == projectName);

            if (ctx is null)
            {
                logger.LogWarning(
                    "Project context '{Project}' not found in DB for agent '{Agent}' — generating empty stub",
                    projectName, agent.Name);

                var stubDir = Path.Combine(projectsDir, projectName);
                Directory.CreateDirectory(stubDir);
                await File.WriteAllTextAsync(Path.Combine(stubDir, "context.md"),
                    $"# {projectName}\n\n(No content — project context not yet seeded in DB)\n");
                continue;
            }

            var version = ctx.Versions.FirstOrDefault(v => v.VersionNumber == ctx.CurrentVersion)
                       ?? ctx.Versions.OrderByDescending(v => v.VersionNumber).FirstOrDefault();

            if (version is null)
            {
                logger.LogWarning(
                    "Project context '{Project}' has no versions for agent '{Agent}' — generating empty stub",
                    projectName, agent.Name);

                var stubDir = Path.Combine(projectsDir, projectName);
                Directory.CreateDirectory(stubDir);
                await File.WriteAllTextAsync(Path.Combine(stubDir, "context.md"),
                    $"# {projectName}\n\n(No content — no versions found)\n");
                continue;
            }

            var dir = Path.Combine(projectsDir, projectName);
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "context.md"), version.Content);
        }

        logger.LogInformation(
            "Generated project context files for '{Agent}' in {Dir} ({Count} project(s))",
            agent.Name, projectsDir, agent.Projects.Count);
    }

    private static string GenerateAppsettingsJson(Agent agent)
    {
        var tools = agent.Tools.Where(t => t.IsEnabled).OrderBy(t => t.ToolName).Select(t => t.ToolName).ToList();
        var projects   = agent.Projects.Select(p => p.ProjectName).ToList();

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
                agent.SuppressToolMessages,
                agent.Effort,
                agent.JsonSchema,
                agent.AgentsJson,
                agent.CodexSandboxMode,
            },
            Telegram = new
            {
                AllowedUserIds  = agent.TelegramUsers.Select(u => u.UserId).ToList(),
                AllowedGroupIds = agent.TelegramGroups.Select(g => g.GroupId).ToList(),
                SendOnly = agent.TelegramSendOnly,
            },
        };

        return JsonSerializer.Serialize(obj, IndentedJson);
    }

    private static string GenerateMcpJson(Agent agent, string fleetMemoryMcpUrl)
    {
        var mcpServers = agent.McpEndpoints
            .OrderBy(e => e.McpName)
            .ToDictionary(
                e => e.McpName,
                e =>
                {
                    // Append ?agent={name} to fleet-telegram, fleet-memory, and fleet-temporal URLs
                    // so each server can identify the calling agent without relying on the LLM to pass it.
                    var url = (e.McpName == "fleet-telegram" || e.McpName == "fleet-memory" || e.McpName == "fleet-temporal")
                        ? $"{e.Url.TrimEnd('/')}?agent={agent.Name}"
                        : e.Url;
                    return (object)new { type = e.TransportType, url };
                });

        // Every agent gets fleet-memory access for memory_get, even if not in DB.
        // This is the provisioning-time enforcement point for mandatory read access.
        if (!mcpServers.ContainsKey("fleet-memory"))
        {
            var url = $"{fleetMemoryMcpUrl.TrimEnd('/')}?agent={agent.Name}";
            mcpServers["fleet-memory"] = new { type = "sse", url };
        }

        return JsonSerializer.Serialize(new { mcpServers }, IndentedJson);
    }

    private static string GenerateSettingsJson(Agent agent, string ctoAgentName)
    {
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

        allow.Sort(StringComparer.OrdinalIgnoreCase);

        return JsonSerializer.Serialize(new { permissions = new { allow } }, IndentedJson);
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
