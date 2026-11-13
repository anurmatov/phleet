using System.Text.Json;
using Fleet.Orchestrator.Data;
using Fleet.Shared;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// The lifecycle of an agent's container: preview, provision, deprovision and reprovision, one at
/// a time per agent. It holds the per-agent lock, talks to Docker and records secret grants.
/// </summary>
/// <remarks>
/// What the container gets is decided elsewhere, and none of it needs Docker:
/// <see cref="ContainerSpecBuilder"/> (image, env, binds, networks), <see cref="AgentConfigFiles"/>
/// (the generated files' contents), <see cref="ProvisioningFaults"/> and
/// <see cref="ProvisioningPolicy"/> (what refuses a provision), <see cref="AgentFileRenderer"/> and
/// <see cref="AgentFileWriter"/> (rendering the files, then writing them), and
/// <see cref="ContainerSpecDiff"/> (a preview's comparison with the running container).
/// <see cref="SecretGrantPlanner.Plan"/> stays the only place that decides how a secret is delivered.
/// </remarks>
public sealed class ContainerProvisioningService(
    IServiceScopeFactory scopeFactory,
    DockerService docker,
    IConfiguration config,
    JournalTokenService journalTokens,
    ILogger<ContainerProvisioningService> logger,
    SecretProxySettings? secretSettings = null,
    SecretProxyStateWriter? secretState = null)
{
    private readonly SecretProxySettings _secretSettings = secretSettings ?? SecretProxySettings.Disabled;
    private readonly ProvisioningPolicy _policy = new(config, journalTokens);
    private readonly AgentFileRenderer _renderer = new(scopeFactory, config, journalTokens, logger);
    private readonly AgentFileWriter _writer = new(logger);

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

        // A preview mints nothing: a bound key shows a fixed marker where a provision puts a placeholder.
        var bindings = await db.SecretBindings.AsNoTracking().ToListAsync(ct);
        var (secretPlan, secretFault) = SecretGrantPlanner.Plan(
            agent, bindings, _secretSettings, secretState?.CaPresent ?? false, () => SecretPlaceholders.PreviewValue);

        var desired = BuildDesiredSpec(agent, envValues, bindings, secretPlan ?? SecretPlan.None);
        var (actual, resolvedContainerName) = await InspectActualAsync(agent.ContainerName, ct);
        var diffs   = actual is null
            ? ["container not found — not running"]
            : ContainerSpecDiff.ComputeDiff(desired, actual);
        if (secretFault is not null) diffs = [.. diffs, secretFault];

        if (ProvisioningFaults.FilesWarning(agent) is { } warning) diffs = [.. diffs, warning];
        diffs = [.. diffs, .. ProvisioningFaults.SendWarnings(agent)];

        logger.LogInformation(
            "Provision preview for {Agent} ({Container}): {DiffCount} diff(s)",
            agentName, agent.ContainerName, diffs.Count);

        return new ProvisionPreview(agentName, agent.ContainerName, resolvedContainerName, desired, actual, diffs);
    }

    // ── spec generation ────────────────────────────────────────────────────────

    private ContainerSpec BuildDesiredSpec(
        Agent agent, Dictionary<string, string> envValues, IReadOnlyCollection<SecretBinding> bindings, SecretPlan secrets)
    {
        var whisperServiceUrl = config["Provisioning:WhisperServiceUrl"] ?? "";
        var kokoroServiceUrl  = config["Provisioning:KokoroServiceUrl"]  ?? "";
        var baseDir = config["Provisioning:BaseDir"]
            ?? throw new InvalidOperationException("Provisioning:BaseDir is required but not configured.");

        return ContainerSpecBuilder.Build(
            agent, envValues, bindings, secrets, baseDir, whisperServiceUrl, kokoroServiceUrl, config["Provisioning:AgentImage"]);
    }

    // ── live provisioning ──────────────────────────────────────────────────────

    /// <summary>
    /// One provision, deprovision or reprovision per agent at a time; different agents do not wait
    /// for each other. Two that interleave on one agent can leave the stored placeholder hashes
    /// describing a container that is not the one running.
    /// </summary>
    private readonly KeyedAsyncLock _agentOperations = new();

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
        using var held = await _agentOperations.AcquireAsync(agentName, ct);

        var (prepared, refusal) = await PrepareAsync(
            agentName, imageOverride, instructionVersionOverrides, containerMustBeAbsent: true, ct);
        return prepared is null ? refusal! : await CreateAsync(prepared, ct);
    }

    /// <summary>
    /// Everything a provision decides, with every generated file rendered in memory: nothing is
    /// written and nothing is created. Returns the refusal instead when the agent cannot be
    /// provisioned as configured.
    /// </summary>
    /// <remarks>
    /// A reprovision calls this while the old container is still running, and creates the new one
    /// from the value returned here, so what was checked is what is created. Whatever can refuse
    /// for a reason found in the agent's configuration belongs here, not in <see cref="CreateAsync"/>.
    /// </remarks>
    private async Task<(PreparedProvision? Prepared, ProvisionResult? Refusal)> PrepareAsync(
        string agentName,
        string? imageOverride,
        IReadOnlyDictionary<string, int>? instructionVersionOverrides,
        bool containerMustBeAbsent,
        CancellationToken ct)
    {
        (PreparedProvision?, ProvisionResult?) Refuse(string message) =>
            (null, ProvisionResult.Fail(agentName, message));

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
            return (null, ProvisionResult.Fail(agentName, "agent not found in DB") with { Failure = ProvisionFailure.AgentNotFound });

        if (_policy.DescribePrimaryHumanFault() is { } priorityFault)
            return Refuse(priorityFault);
        // #386: PV1, before Docker is called or any file is generated.
        if (ProvisioningFaults.DescribeProviderPreflightFault(agent) is { } providerFault)
            return Refuse(providerFault);
        if (_policy.DescribeJournalProvisioningFault(agent) is { } journalFault)
            return Refuse(journalFault);

        // Bound secrets: decided before Docker is called or any file is generated. A fault here means
        // the agent would otherwise start without a way to use a secret it is meant to hold.
        var secretBindings = await db.SecretBindings.AsNoTracking().ToListAsync(ct);
        var (secretPlan, secretFault) = SecretGrantPlanner.Plan(
            agent, secretBindings, _secretSettings, secretState?.CaPresent ?? false, SecretPlaceholders.Create);
        if (secretPlan is null)
            return Refuse(secretFault!);
        if (secretPlan.HasGrants && secretState is null)
            return Refuse("secret_proxy_state_unavailable: the forwarder state cannot be written from this host");

        // Guard: bypassPermissions is rejected — fleet containers run as root and Claude CLI
        // refuses --dangerously-skip-permissions for root processes, which crashes the agent immediately.
        // Use acceptEdits + an explicit AllowedTools list instead.
        if (string.Equals(agent.PermissionMode, "bypassPermissions", StringComparison.OrdinalIgnoreCase))
            return Refuse(
                "PermissionMode 'bypassPermissions' is not supported in fleet containers (processes run as root). " +
                "Use 'acceptEdits' with an explicit AllowedTools list instead.");

        // Guard: container must not already exist. A reprovision skips it: the container it is
        // about to replace is still there, and stays there if anything below refuses.
        if (containerMustBeAbsent)
        {
            var existing = await docker.InspectContainerAsync(agent.ContainerName, ct);
            if (existing is not null)
                return Refuse(
                    $"container '{agent.ContainerName}' already exists — deprovision first or use reprovision_agent");
        }

        var envFile   = config["Provisioning:EnvFilePath"] ?? "/app/deploy/.env";
        var baseDir   = config["Provisioning:BaseDir"]
            ?? throw new InvalidOperationException("Provisioning:BaseDir is required but not configured.");
        var envValues = LoadEnvFile(envFile);
        var ctoAgentName = CtoAgentSetting.Resolve(envValues, config);

        if (ProvisioningFaults.DescribeCodexServerUrlFault(agent, envValues) is { } serverUrlFault)
            return Refuse(serverUrlFault);

        var projectContexts = await _renderer.ResolveProjectContextsAsync(agent);
        var style = await _renderer.ResolveOutputStyleAsync(agent);

        GeneratedFiles files;
        try
        {
            files = _renderer.RenderGeneratedFiles(agent, ctoAgentName ?? "", style, projectContexts, instructionVersionOverrides);
        }
        catch (InvalidOperationException ex)
        {
            // The generators refuse by throwing: a style with no row, a local-model row edited into
            // an invalid state (#340 D1 point 2). Nothing is written or created yet, so this is a
            // refusal with the named fault, and a reprovision keeps the old container.
            return Refuse(ex.Message);
        }

        if (LocalModel.IsEnabled(agent.Provider, agent.LocalBaseUrl))
        {
            logger.LogInformation(
                "Local model for '{Agent}': harness {Provider}, server {Origin}, context window {ContextWindow}",
                agentName, agent.Provider, agent.LocalBaseUrl, (object?)agent.ContextWindow ?? "unset");
            if (LocalModel.IsCodexEnabled(agent.Provider, agent.LocalBaseUrl)
                && agent.EnvRefs.Any(e => e.EnvKeyName == ProvisioningFaults.CodexOssBaseUrlEnvRef))
            {
                logger.LogWarning(
                    "Local model for '{Agent}': {EnvRef} Env Ref superseded by the local server URL; remove the Env Ref",
                    agentName, ProvisioningFaults.CodexOssBaseUrlEnvRef);
            }
        }
        else if (agent.ContextWindow is not null)
        {
            logger.LogInformation(
                "ContextWindow {ContextWindow} ignored for '{Agent}': it applies only to a claude or codex agent with a local server URL",
                agent.ContextWindow, agentName);
        }

        var spec      = BuildDesiredSpec(agent, envValues, secretBindings, secretPlan);

        // Apply image override if provided (e.g. CI uses a PR-specific image tag)
        if (!string.IsNullOrEmpty(imageOverride))
            spec = spec with { Image = imageOverride };

        // Expand relative paths in binds for direct Docker API calls
        var expandedBinds = spec.Binds.Select(b => ContainerSpecBuilder.ExpandBindPath(b, baseDir)).ToList();

        if (spec.Networks.Count == 0)
            return Refuse("no networks configured for agent");

        return (new PreparedProvision(agent, spec, expandedBinds, secretPlan, baseDir, files), null);
    }

    /// <summary>
    /// Writes the rendered files, tells the forwarder about the new placeholders, then creates and
    /// starts the container. The container named in <paramref name="prepared"/> must not exist.
    /// </summary>
    private async Task<ProvisionResult> CreateAsync(PreparedProvision prepared, CancellationToken ct)
    {
        var (agent, spec, expandedBinds, secretPlan, baseDir, files) = prepared;
        var agentName = agent.Name;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        await _writer.WriteConfigFilesAsync(agent, baseDir, files);
        await _writer.WriteInstructionFilesAsync(agent, baseDir, files);
        await _writer.WriteProjectContextFilesAsync(agent, baseDir, files.ProjectContexts);

        var primaryNetwork = spec.Networks[0];

        logger.LogInformation(
            "Provisioning '{Agent}' container='{Container}' image='{Image}'",
            agentName, agent.ContainerName, spec.Image);

        var logMaxSize = config["Provisioning:ContainerLogMaxSize"];
        var logMaxFile = config["Provisioning:ContainerLogMaxFile"];

        // The forwarder must know the new placeholders before the container that holds them starts,
        // and must forget the previous container's: record their hashes, then rewrite its state.
        if (await RecordSecretGrantsAsync(db, agent.Id, secretPlan, ct) is { } recordFault)
            return ProvisionResult.Fail(agentName, recordFault);

        // The agent's signing-socket directory must exist before Docker mounts it; fleet-secrets
        // puts the socket in it once it has read the state written just above.
        if (secretPlan.SshBind is not null && SecretProxySsh.AgentDirectoryName(agent.Name) is { } sshDirectory)
        {
            try
            {
                Directory.CreateDirectory(Path.Combine(baseDir, SecretProxySsh.Directory, sshDirectory));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ProvisionResult.Fail(agentName,
                    $"secret_ssh_directory_unavailable: {SecretProxySsh.Directory}/{sshDirectory} could not be created under the fleet base directory");
            }
        }

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
            logMaxFile,
            spec.ExtraHosts);

        if (containerId is null)
            return ProvisionResult.Fail(agentName, "Docker API failed to create container — check orchestrator logs");

        // Connect to additional networks
        var extraNetworks = spec.Networks.Skip(1).ToList();
        foreach (var network in extraNetworks)
        {
            if (network == SecretProxyNetwork.NetworkName && spec.SecretNetworkAddress is { } secretAddress)
            {
                // Not optional: without its fixed address here the agent cannot use any bound secret.
                if (await docker.ConnectToNetworkAsync(network, containerId, ct, secretAddress))
                    continue;
                await docker.RemoveContainerAsync(agent.ContainerName, ct);
                var cleanupFault = await RecordSecretGrantsAsync(db, agent.Id, SecretPlan.None, ct);
                return ProvisionResult.Fail(agentName,
                    $"secret_proxy_network_unavailable: could not attach '{agent.ContainerName}' to {network} at {secretAddress}. "
                    + "Check that fleet-secrets is running, that the network exists with the configured subnet, "
                    + "and that no other container holds that address."
                    + (cleanupFault is null ? "" : $" Also: {cleanupFault}"));
            }

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
        using var held = await _agentOperations.AcquireAsync(agentName, ct);
        return await RemoveAsync(agentName, ct);
    }

    /// <summary><see cref="DeprovisionAsync"/> for a caller that already holds the agent's lock.</summary>
    private async Task<ProvisionResult> RemoveAsync(string agentName, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        var agent = await db.Agents
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Name == agentName, ct);

        if (agent is null)
            return ProvisionResult.Fail(agentName, "agent not found in DB") with { Failure = ProvisionFailure.AgentNotFound };

        var existing = await docker.InspectContainerAsync(agent.ContainerName, ct);
        if (existing is null)
            return ProvisionResult.Fail(agentName,
                $"container '{agent.ContainerName}' not found — nothing to deprovision") with { Failure = ProvisionFailure.ContainerNotFound };

        logger.LogInformation("Deprovisioning '{Agent}' (container='{Container}')", agentName, agent.ContainerName);

        // Stop first (graceful), then remove with force
        var stopped = await docker.StopContainerAsync(agent.ContainerName);
        if (!stopped)
            logger.LogWarning("Stop returned false for '{Container}' — will still attempt remove", agent.ContainerName);

        var removed = await docker.RemoveContainerAsync(agent.ContainerName, ct);
        if (!removed)
            return ProvisionResult.Fail(agentName,
                $"failed to remove container '{agent.ContainerName}' — check orchestrator logs");

        // The container is gone, and its placeholders with it: the forwarder forgets them now. If it
        // could not be told, that is not a clean deprovision: say so rather than report success while
        // the old grants may still be honoured.
        if (await RecordSecretGrantsAsync(db, agent.Id, SecretPlan.None, ct) is { } revokeFault)
            return ProvisionResult.Fail(agentName,
                $"container '{agent.ContainerName}' stopped and removed, but its secret grants were not revoked at fleet-secrets: {revokeFault}");

        return ProvisionResult.Ok(agentName, $"container '{agent.ContainerName}' stopped and removed");
    }

    /// <summary>
    /// The fault a reprovision of this agent would hit in its bound secrets, or null. Reads only,
    /// apart from one refresh of the forwarder state: it runs before the old container is removed,
    /// so a refusal leaves a working agent working.
    /// </summary>
    private async Task<string?> DescribeSecretPlanFaultAsync(string agentName, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var agent = await db.Agents.Include(a => a.EnvRefs).AsNoTracking().FirstOrDefaultAsync(a => a.Name == agentName, ct);
        if (agent is null)
            return null; // ProvisionAsync reports the missing agent.

        var bindings = await db.SecretBindings.AsNoTracking().ToListAsync(ct);
        // Nothing is minted here: only the faults matter.
        var (plan, fault) = SecretGrantPlanner.Plan(
            agent, bindings, _secretSettings, secretState?.CaPresent ?? false, () => SecretPlaceholders.PreviewValue);
        if (plan is null)
            return fault;

        // The swap publishes twice: the old placeholders are revoked, then the new ones granted. If
        // the state cannot be written, either step would fail after the container is already gone.
        var publishes = plan.HasGrants || agent.EnvRefs.Any(r => r.PlaceholderSha256 is not null);
        if (!publishes)
            return null;
        if (secretState is null)
            return "secret_proxy_state_unavailable: the forwarder state cannot be written from this host";
        return await secretState.WriteAsync(ct, failClosed: false) is { } stateFault
            ? $"{stateFault}: the fleet-secrets state cannot be written, so this agent's placeholders could not be "
              + "revoked or replaced. Its container was left running. Check the secret-proxy/state directory under the fleet base directory."
            : null;
    }

    /// <summary>
    /// Stores the SHA-256 of each placeholder in <paramref name="plan"/> on the agent's Env Refs,
    /// clears every other one, and rewrites the forwarder state. Returns the fault, or null. With no
    /// stored hash and no grant it touches nothing, so an agent without bound secrets costs nothing.
    /// </summary>
    private async Task<string?> RecordSecretGrantsAsync(
        OrchestratorDbContext db, int agentId, SecretPlan plan, CancellationToken ct)
    {
        var references = await db.AgentEnvRefs.Where(r => r.AgentId == agentId).ToListAsync(ct);
        var changed = false;
        foreach (var reference in references)
        {
            var hash = plan.Grants.FirstOrDefault(g => g.EnvKeyName == reference.EnvKeyName) is { } grant
                ? SecretPlaceholders.Hash(grant.Placeholder)
                : null;
            if (reference.PlaceholderSha256 == hash)
                continue;
            reference.PlaceholderSha256 = hash;
            changed = true;
        }

        if (!changed)
            return null;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (SecretStatePublicationException ex)
        {
            // The hashes are saved but fleet-secrets was not told (the save interceptor's write
            // failed and the old state was withdrawn). Not a success, whichever way the change went.
            return ex.Message;
        }

        if (secretState is null)
            return null;
        return await secretState.WriteAsync(ct) is { } fault ? new SecretStatePublicationException(fault).Message : null;
    }

    /// <summary>
    /// Deprovisions then re-provisions the container. Used when config has changed.
    /// An optional imageOverride overrides the DB-configured image (used by CI for PR-specific images).
    /// </summary>
    /// <remarks>
    /// The new container is decided and rendered in full before the old one is touched, so a
    /// refusal for anything in the agent's configuration leaves the old container running.
    /// </remarks>
    public async Task<ProvisionResult> ReprovisionAsync(
        string agentName,
        string? imageOverride = null,
        IReadOnlyDictionary<string, int>? instructionVersionOverrides = null,
        CancellationToken ct = default)
    {
        using var held = await _agentOperations.AcquireAsync(agentName, ct);

        if (_policy.DescribePrimaryHumanFault() is { } priorityFault)
            return ProvisionResult.Fail(agentName, priorityFault);

        // #386: PV1 likewise keep the old container.
        if (await DescribeProviderPreflightFaultAsync(agentName, ct) is { } providerFault)
            return ProvisionResult.Fail(agentName, providerFault);

        // Journal faults must be found before the old container is removed. A missing signing key or
        // an unsupported provider therefore leaves the old, working container untouched.
        if (await DescribeJournalProvisioningFaultAsync(agentName, ct) is { } journalFault)
            return ProvisionResult.Fail(agentName, journalFault);

        // P1 (#382) likewise: a codex local agent with no reachable server keeps its old container.
        if (await DescribeCodexServerUrlFaultAsync(agentName, ct) is { } serverUrlFault)
            return ProvisionResult.Fail(agentName, serverUrlFault);

        // Bound secrets likewise. Everything a provision would refuse for (forwarder off or
        // misconfigured, no CA, no address, conflicting names, a state that cannot be written) is
        // found here, while the old container is still running.
        if (await DescribeSecretPlanFaultAsync(agentName, ct) is { } secretFault)
            return ProvisionResult.Fail(agentName, secretFault);

        // And everything else: the permission mode, a style with no row, a local-model fault. The
        // new container is created from exactly what is rendered here.
        var (prepared, refusal) = await PrepareAsync(
            agentName, imageOverride, instructionVersionOverrides, containerMustBeAbsent: false, ct);
        if (prepared is null)
            return refusal!;

        var deprovision = await RemoveAsync(agentName, ct);
        if (!deprovision.Success)
        {
            // If the container simply doesn't exist, skip deprovision and go straight to provision.
            // This handles manually-removed containers and first-time provision after config-only updates.
            if (deprovision.Failure == ProvisionFailure.ContainerNotFound)
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

        var provision = await CreateAsync(prepared, ct);
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
            allNetworks = [ContainerSpecBuilder.FleetNetwork];
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
            return (ContainerSpecDiff.FromInspect(doc.RootElement), resolvedName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse Docker inspect response for {Container}", resolvedName);
            return (null, resolvedName);
        }
    }

    private async Task<string?> DescribeJournalProvisioningFaultAsync(
        string agentName, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var agent = await db.Agents
            .Include(a => a.McpEndpoints)
            .Include(a => a.EnvRefs)
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Name == agentName, ct);
        return agent is null ? null : _policy.DescribeJournalProvisioningFault(agent);
    }

    private async Task<string?> DescribeProviderPreflightFaultAsync(string agentName, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var agent = await db.Agents
            .Include(a => a.CredentialMounts)
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Name == agentName, ct);
        return agent is null ? null : ProvisioningFaults.DescribeProviderPreflightFault(agent);
    }

    private async Task<string?> DescribeCodexServerUrlFaultAsync(string agentName, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var agent = await db.Agents
            .Include(a => a.EnvRefs)
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Name == agentName, ct);
        return agent is null
            ? null
            : ProvisioningFaults.DescribeCodexServerUrlFault(agent, LoadEnvFile(config["Provisioning:EnvFilePath"] ?? "/app/deploy/.env"));
    }

    /// <summary>
    /// The deployment's primary human on Telegram, or null when it is blank or not a valid id.
    /// For startup, which must not fail on a value provisioning already refuses by name.
    /// </summary>
    public long? PrimaryHumanUserIdOrNull() => _policy.PrimaryHumanUserIdOrNull();

    /// <inheritdoc cref="ProvisioningPolicy.MediaEndpointIsConfigured"/>
    internal bool MediaEndpointIsConfigured() => _policy.MediaEndpointIsConfigured();

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

    // ── The same members under their earlier names ─────────────────────────────
    //
    // Each of these moved to the class named on its line. They stay here, as one-line forwards, for
    // the endpoints, tools and tests that reach them through this type.

    internal const string JournalMcpServerName = AgentConfigFiles.JournalMcpServerName;
    internal const string JournalFilesServerName = AgentConfigFiles.JournalFilesServerName;
    internal const string JournalFilesGrant = AgentConfigFiles.JournalFilesGrant;
    internal static IReadOnlyDictionary<string, bool> McpHeaderSupport => AgentConfigFiles.McpHeaderSupport;

    /// <summary>The deployment's <c>.env</c>, by the one rule in <see cref="EnvFile"/>. Never throws.</summary>
    internal static Dictionary<string, string> LoadEnvFile(string path) => EnvFile.Read(path);

    /// <inheritdoc cref="ContainerSpecBuilder.BuildEnv"/>
    internal static List<string> BuildEnv(
        Agent agent, Dictionary<string, string> envValues, string whisperServiceUrl, string kokoroServiceUrl,
        IReadOnlyCollection<SecretBinding> bindings, SecretPlan secrets) =>
        ContainerSpecBuilder.BuildEnv(agent, envValues, whisperServiceUrl, kokoroServiceUrl, bindings, secrets);

    internal static List<string> BuildBinds(Agent agent, string baseDir) => ContainerSpecBuilder.BuildBinds(agent, baseDir);
    internal static string CodexRootTomlBind(string containerName) => ContainerSpecBuilder.CodexRootTomlBind(containerName);
    internal static bool IsHostedProvider(Agent agent) => ContainerSpecBuilder.IsHostedProvider(agent);

    public static List<string> ComputeDiff(ContainerSpec desired, ContainerSpec actual) => ContainerSpecDiff.ComputeDiff(desired, actual);
    internal static string FormatBytes(long bytes) => ContainerSpecDiff.FormatBytes(bytes);
    internal static string ParseEnvKey(string envEntry) => ContainerSpecDiff.ParseEnvKey(envEntry);

    internal static string? DescribeLocalModelFault(Agent agent) => ProvisioningFaults.DescribeLocalModelFault(agent);
    internal static string? DescribeProviderPreflightFault(Agent agent) => ProvisioningFaults.DescribeProviderPreflightFault(agent);
    internal static string? DescribeCodexServerUrlFault(Agent agent, IReadOnlyDictionary<string, string> envValues) =>
        ProvisioningFaults.DescribeCodexServerUrlFault(agent, envValues);

    internal static string GenerateAppsettingsJson(
        Agent agent, string ctoAgentName, OutputStyle? style = null,
        JournalProvisioning? journal = null, string? primaryHumanUserId = null, ChatProvisioning? chat = null) =>
        AgentConfigFiles.GenerateAppsettingsJson(agent, ctoAgentName, style, journal, primaryHumanUserId, chat);

    internal static string GenerateMcpJson(
        Agent agent, string fleetMemoryMcpUrl, string? journalReadToken = null, bool filesEnabled = false) =>
        AgentConfigFiles.GenerateMcpJson(agent, fleetMemoryMcpUrl, journalReadToken, filesEnabled);

    internal static string GenerateSettingsJson(Agent agent, string ctoAgentName, OutputStyle? style = null) =>
        AgentConfigFiles.GenerateSettingsJson(agent, ctoAgentName, style);

    internal static List<string> AllowedToolsFor(Agent agent, string ctoAgentName) => AgentConfigFiles.AllowedToolsFor(agent, ctoAgentName);
    internal static HarnessInput HarnessInputFor(Agent agent) => AgentConfigFiles.HarnessInputFor(agent);
    internal static bool HasStyleFile(Agent agent) => AgentConfigFiles.HasStyleFile(agent);
    internal static List<AgentInstruction> OrderedInstructions(Agent agent) => AgentConfigFiles.OrderedInstructions(agent);
    internal static string InstructionDirectoryName(string instructionName) => AgentConfigFiles.InstructionDirectoryName(instructionName);
    internal static string WithAgentParam(string url, string agentName) => AgentConfigFiles.WithAgentParam(url, agentName);
    internal static string WithAgentTelegramParams(string url, string agentName, byte formattingMode) =>
        AgentConfigFiles.WithAgentTelegramParams(url, agentName, formattingMode);
    internal static string NormalizeFleetMemoryMcpUrl(string? rawUrl) => AgentConfigFiles.NormalizeFleetMemoryMcpUrl(rawUrl);
    internal static string WithoutQuery(string url) => AgentConfigFiles.WithoutQuery(url);
    internal static long? ParsePrimaryHumanUserId(string? value) => AgentConfigFiles.ParsePrimaryHumanUserId(value);
    internal static string ReplyLookupState(Agent agent, JournalProvisioning? journal) => AgentConfigFiles.ReplyLookupState(agent, journal);
    internal static bool SupportsMcpHeaders(string? provider) => AgentConfigFiles.SupportsMcpHeaders(provider);

    internal static ProjectContextPlan BuildProjectContextPlan(
        Agent agent, IReadOnlyCollection<ProjectContext> contexts, ILogger logger) =>
        AgentFileRenderer.BuildProjectContextPlan(agent, contexts, logger);
}
