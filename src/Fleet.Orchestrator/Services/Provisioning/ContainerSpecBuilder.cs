using Fleet.Orchestrator.Configuration;
using Fleet.Orchestrator.Data;
using Fleet.Shared;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// What an agent's container is created with: image, memory, environment, binds and networks.
/// Decides nothing about files, Docker or the database. Its only reads are <c>File.Exists</c>
/// checks for the credential files a bind would mount.
/// </summary>
internal static class ContainerSpecBuilder
{
    // Actual Docker network names as reported by the Docker API.
    // fleet-net is declared with an explicit name so it keeps its name (not prefixed by Docker Compose).
    internal const string FleetNetwork = ProvisioningOptions.DefaultNetworkName;

    /// <summary>The whole spec for one agent, from values already read from configuration and <c>.env</c>.</summary>
    /// <param name="defaultImage">The image of an agent whose row names none (<see cref="ProvisioningOptions.AgentImage"/>).</param>
    internal static ContainerSpec Build(
        Agent agent, Dictionary<string, string> envValues, IReadOnlyCollection<SecretBinding> bindings, SecretPlan secrets,
        string baseDir, string whisperServiceUrl, string kokoroServiceUrl, string defaultImage)
    {
        var env      = BuildEnv(agent, envValues, whisperServiceUrl, kokoroServiceUrl, bindings, secrets);
        var binds    = BuildBinds(agent, baseDir);
        var networks = BuildNetworks(agent);
        var memBytes = (long)agent.MemoryLimitMb * 1024 * 1024;
        var image    = agent.Image ?? defaultImage;

        // Bound secrets: the CA the agent trusts, and its place on the internal secrets network.
        if (secrets.Bind is { } caBind)
            binds.Add(caBind);
        if (secrets.SshBind is { } sshBind)
            binds.Add(sshBind);
        if (secrets.Address is not null && !networks.Contains(SecretProxyNetwork.NetworkName, StringComparer.Ordinal))
            networks.Add(SecretProxyNetwork.NetworkName);

        return new ContainerSpec(image, memBytes, env, binds, networks)
        {
            ExtraHosts = [.. secrets.ExtraHosts],
            SecretNetworkAddress = secrets.Address,
        };
    }

    /// <param name="bindings">Every secret binding. A bound key is never written here, whatever <paramref name="secrets"/> holds.</param>
    /// <param name="secrets">The agent's placeholder plan, or <see cref="SecretPlan.None"/>.</param>
    internal static List<string> BuildEnv(
        Agent agent, Dictionary<string, string> envValues, string whisperServiceUrl, string kokoroServiceUrl,
        IReadOnlyCollection<SecretBinding> bindings, SecretPlan secrets)
    {
        var env = new List<string>();

        // Secret refs from DB — resolve values from .env if available
        foreach (var envRef in agent.EnvRefs.OrderBy(e => e.EnvKeyName))
        {
            // A bound secret never enters the container. Its placeholder is added below, from the plan.
            if (SecretGrantPlanner.IsBound(bindings, envRef.EnvKeyName))
                continue;

            var value = envValues.TryGetValue(envRef.EnvKeyName, out var v) ? v : "<secret>";
            // Only *_BOT_TOKEN-shaped TELEGRAM_* keys map to the Telegram__BotToken ASP.NET config key.
            // Other TELEGRAM_* keys (e.g. TELEGRAM_USER_ID, TELEGRAM_GROUP_ID) pass through as-is
            // so they can be used as normal env vars without colliding with the bot token config slot.
            if (JournalGrants.IsBotRef(envRef.EnvKeyName))
            {
                if (envRef.EnvKeyName == JournalGrants.BotRef(agent))
                    env.Add($"Telegram__BotToken={JournalGrants.ResolveBotToken(agent, envValues) ?? "<secret>"}");
            }
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

        // GitHub App PEM — inject for all agents when present in .env (base64-encoded), unless the key
        // is a bound secret: then no agent holds it, and those granted it hold a placeholder instead.
        if (envValues.TryGetValue("GITHUB_APP_PEM", out var githubAppPem)
            && !SecretGrantPlanner.IsBound(bindings, "GITHUB_APP_PEM"))
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

        // #367: a local server's context size, so Claude CLI compacts before the server rejects the
        // turn. Local-mode claude agents only: Anthropic models have known windows an override
        // could shrink. The claude child inherits it; ClaudeExecutor strips only its own list.
        if (agent.ContextWindow is int window && ClaudeLocalModel.IsEnabled(agent.Provider, agent.LocalBaseUrl))
            env.Add($"{ContextWindow.EnvVar}={window}");

        env.AddRange(secrets.Env);

        return env;
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
        // the pinned CLI. Cloud claude only: the other providers and local-model claude get the
        // same text in their prompt (see AgentConfigFiles.HasStyleFile). Absent for a styleless agent, so its
        // container config is unchanged.
        if (AgentConfigFiles.HasStyleFile(agent))
            binds.Add($"./workspaces/{containerName}/.generated/output-styles:/workspace/.claude/output-styles:ro");

        // PV1 (#386): one explicit case per provider. Only an empty provider means claude; any
        // other string is refused rather than handed the Claude credential.
        switch (AgentProviders.Normalize(agent.Provider))
        {
            case AgentProviders.Gemini:
            {
                // Mount gemini OAuth credentials writable — the CLI's google-auth-library refreshes
                // the file in-place on token expiry, so the mount must be read-write. The refreshed
                // token propagates back to the host, preventing stale-token failures on container restart.
                var geminiTokenStorePath = Path.Combine(baseDir, ".gemini-credentials.json");
                if (File.Exists(geminiTokenStorePath))
                    binds.Add("./.gemini-credentials.json:/root/.gemini/oauth_creds.json:rw");
                break;
            }
            case AgentProviders.Codex:
            {
                // #460: the generated root keys, written by GenerateConfigFilesAsync before the
                // container is created (a missing host file would mount as an empty directory).
                // Codex agents only; entrypoint.sh reads the same container path.
                binds.Add(CodexRootTomlBind(containerName));

                // A hosted-provider agent must hold no OpenAI credential (#335 D6): its model is served
                // by the vendor through the loopback adapter, and the credential would be one more
                // secret in a container whose model runs a root shell.
                if (IsHostedProvider(agent))
                    return AppendCredentialMounts(agent, binds);

                // Mount codex credentials for seeding new codex containers (entrypoint.sh reads this)
                var codexTokenStorePath = Path.Combine(baseDir, ".codex-credentials.json");
                if (File.Exists(codexTokenStorePath))
                    binds.Add("./.codex-credentials.json:/root/.codex-host/auth.json:ro");
                break;
            }
            case AgentProviders.Claude:
            {
                // A local-model agent must hold no Claude credential (#340 D3): its model is served by a
                // local Anthropic-compatible server, which must never receive a real OAuth token.
                if (ClaudeLocalModel.IsEnabled(agent.Provider, agent.LocalBaseUrl))
                    return AppendCredentialMounts(agent, binds);

                // Mount orchestrator-stored Claude credentials for seeding new containers (entrypoint.sh reads this)
                var tokenStorePath = Path.Combine(baseDir, ".claude-credentials.json");
                if (File.Exists(tokenStorePath))
                    binds.Add("./.claude-credentials.json:/root/.claude-host/.credentials.json:ro");
                break;
            }
            default:
                throw new InvalidOperationException(AgentProviders.UnknownProviderText(agent.Provider));
        }

        return AppendCredentialMounts(agent, binds);
    }

    /// <summary>The read-only bind of <c>.generated/codex-root.toml</c> (#460), codex agents only.</summary>
    internal static string CodexRootTomlBind(string containerName) =>
        $"./workspaces/{containerName}/.generated/{HarnessDefaults.CodexRootTomlFileName}:{HarnessDefaults.CodexRootTomlContainerPath}:ro";

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

    /// <summary>
    /// Expands a bind string's host-side path: replaces ./ with baseDir.
    /// Format: "host:container" or "host:container:options"
    /// </summary>
    internal static string ExpandBindPath(string bind, string baseDir)
    {
        var parts = bind.Split(':', 3);
        if (parts.Length < 2) return bind;

        var host = parts[0]
            .Replace("./", $"{baseDir.TrimEnd('/')}/", StringComparison.Ordinal);

        return parts.Length == 3
            ? $"{host}:{parts[1]}:{parts[2]}"
            : $"{host}:{parts[1]}";
    }

    private static string DeriveGitName(string role) => role switch
    {
        "co-cto"          => "Acto",
        "developer"       => "Adev",
        "devops"          => "Aops",
        "product-manager" => "Apm",
        _                 => role,
    };
}
