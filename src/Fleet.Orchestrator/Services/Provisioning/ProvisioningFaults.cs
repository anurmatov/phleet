using Fleet.Orchestrator.Data;
using Fleet.Shared;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// The faults and warnings that can be read off an agent row alone: what stops a provision before
/// Docker is called, and what a preview reports beside its diff.
/// </summary>
internal static class ProvisioningFaults
{
    internal static IEnumerable<string> SendWarnings(Agent agent)
    {
        if (JournalGrants.HasSendGrant(agent) && !JournalGrants.SendEffective(agent))
            yield return "journal_send_unavailable:journal_capture_off";
        if (agent.JournalCrossChatEnabled && !JournalGrants.CrossChatEffective(agent))
            yield return "journal_cross_chat_unavailable:" + (JournalGrants.HasSendGrant(agent) ? "journal_capture_off" : "send_grant_missing");
    }
    internal static string? FilesWarning(Agent agent) => AgentConfigFiles.HasFilesGrant(agent) && (!agent.JournalEnabled || !AgentConfigFiles.HasTelegramBot(agent))
        ? "journal_files_unavailable:journal_capture_off" : null;

    /// <summary>
    /// The local-model fault that must stop provisioning (#340 D1 point 2, #382, #386), or null:
    /// PV1, L1, L2, V4–V8, C1, C2, or a credential mount into <c>/root/.claude*</c>
    /// that would hand a Claude local agent the credential the bind skip above withholds.
    /// </summary>
    internal static string? DescribeLocalModelFault(Agent agent)
    {
        // PV1 (#386): an unknown provider is refused before anything else reads it.
        if (AgentProviders.DescribeFault(agent.Provider) is { } providerFault)
            return providerFault;

        if (LocalModel.DescribeConfigFault(
                agent.Provider, agent.LocalBaseUrl, agent.Model, agent.Effort, agent.ContextWindow)
            is { } fault)
        {
            return fault;
        }

        // #349 V8: off exists only in local mode; a local → cloud switch must not ship it upstream.
        if (ClaudeLocalModel.DescribeLocalOnlyEffortFault(agent.Provider, agent.LocalBaseUrl, agent.Effort)
            is { } effortFault)
        {
            return effortFault;
        }

        if (!ClaudeLocalModel.IsEnabled(agent.Provider, agent.LocalBaseUrl))
            return null;

        // "/root/.claude" also covers /root/.claude-host and /root/.claude.json.
        var claudeMount = agent.CredentialMounts.FirstOrDefault(
            m => m.MountPath.StartsWith("/root/.claude", StringComparison.Ordinal));
        return claudeMount is null
            ? null
            : $"Claude local model mode forbids a credential mount into /root/.claude*, but one targets "
            + $"{claudeMount.MountPath}. Remove it, then reprovision.";
    }

    /// <summary>
    /// The faults that stop provisioning before Docker is called, and a reprovision before the old
    /// container is removed: PV1 for every agent. Null
    /// when there is none.
    /// </summary>
    /// <remarks>
    /// Claude and codex local faults keep their #340/#382 path: <see cref="AgentConfigFiles.GenerateAppsettingsJson"/>
    /// refuses them when the files are rendered, which a reprovision does before it removes the
    /// container.
    /// </remarks>
    internal static string? DescribeProviderPreflightFault(Agent agent) =>
        AgentProviders.DescribeFault(agent.Provider);

    /// <summary>
    /// P1 (#382): a codex agent whose model is <c>ollama/…</c> or <c>lmstudio/…</c> and has no local
    /// server URL must reach its server through the legacy <c>CODEX_OSS_BASE_URL</c> Env Ref, with a
    /// non-blank value in <c>.env</c>. Returns the fault, or null.
    /// </summary>
    /// <remarks>
    /// Without this the Env Ref loop injects the literal <c>&lt;secret&gt;</c> for a missing key, and a
    /// missing or unreadable <c>.env</c> reads as empty, so the agent would only fail at startup.
    /// </remarks>
    internal static string? DescribeCodexServerUrlFault(Agent agent, IReadOnlyDictionary<string, string> envValues)
    {
        if (!string.Equals(agent.Provider, "codex", StringComparison.Ordinal)
            || !string.IsNullOrEmpty(agent.LocalBaseUrl)
            || CodexLocalModelProviders.Split(agent.Model).Provider is null)
        {
            return null;
        }

        if (!agent.EnvRefs.Any(e => e.EnvKeyName == CodexOssBaseUrlEnvRef))
            return $"Codex local model '{agent.Model}' has no server URL: set the local server URL, "
                 + $"or the legacy {CodexOssBaseUrlEnvRef} Env Ref.";

        return envValues.TryGetValue(CodexOssBaseUrlEnvRef, out var value) && !string.IsNullOrWhiteSpace(value)
            ? null
            : $"Codex local model '{agent.Model}' has no server URL: its {CodexOssBaseUrlEnvRef} Env Ref has "
            + "no non-blank value in the orchestrator's .env. Set the local server URL instead, or fix .env.";
    }

    internal const string CodexOssBaseUrlEnvRef = "CODEX_OSS_BASE_URL";
}
