using Fleet.Orchestrator.Configuration;
using Fleet.Orchestrator.Data;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// What the deployment's own settings decide for an agent: who the primary human is, and what the
/// journal gives the agent (its tokens, its switches, and the faults that refuse it).
/// </summary>
internal sealed class ProvisioningPolicy(IConfiguration config, JournalTokenService journalTokens)
{
    internal string? PrimaryHumanRawValue() =>
        EnvFile.Read(ProvisioningOptions.From(config).EnvFilePath).GetValueOrDefault("FLEET_PRIMARY_HUMAN_USER_ID");

    /// <summary>
    /// The deployment's primary human on Telegram, or null when it is blank or not a valid id.
    /// For startup, which must not fail on a value provisioning already refuses by name.
    /// </summary>
    public long? PrimaryHumanUserIdOrNull()
    {
        try { return AgentConfigFiles.ParsePrimaryHumanUserId(PrimaryHumanRawValue()); }
        catch (InvalidOperationException) { return null; }
    }

    internal string? DescribePrimaryHumanFault()
    {
        try { AgentConfigFiles.ParsePrimaryHumanUserId(PrimaryHumanRawValue()); return null; }
        catch (InvalidOperationException) { return "primary_human_invalid"; }
    }

    internal string? DescribeJournalProvisioningFault(Agent agent)
    {
        if (agent.McpEndpoints.Any(e => string.Equals(e.McpName, AgentConfigFiles.JournalFilesServerName, StringComparison.OrdinalIgnoreCase)))
            return "journal_files_endpoint_reserved";
        var hasJournalEndpoint = agent.McpEndpoints.Any(e =>
            string.Equals(e.McpName, AgentConfigFiles.JournalMcpServerName, StringComparison.OrdinalIgnoreCase));
        if (!agent.JournalEnabled && !hasJournalEndpoint) return null;

        var keyFault = journalTokens.DescribeKeyFault();
        if (keyFault is not null) return keyFault;
        if (hasJournalEndpoint && !AgentConfigFiles.SupportsMcpHeaders(agent.Provider))
            return "journal_headers_unsupported";
        return null;
    }

    internal JournalProvisioning BuildJournalProvisioning(Agent agent)
    {
        if (DescribeJournalProvisioningFault(agent) is { } fault)
            throw new InvalidOperationException(fault);

        var hasBotToken = agent.EnvRefs.Any(e =>
            e.EnvKeyName.StartsWith("TELEGRAM_", StringComparison.OrdinalIgnoreCase)
            && e.EnvKeyName.EndsWith("_BOT_TOKEN", StringComparison.OrdinalIgnoreCase));
        var hasJournalEndpoint = agent.McpEndpoints.Any(e =>
            string.Equals(e.McpName, AgentConfigFiles.JournalMcpServerName, StringComparison.OrdinalIgnoreCase));

        var filesEnabled = AgentConfigFiles.HasFilesGrant(agent) && agent.JournalEnabled && hasBotToken;
        var sendEnabled = JournalGrants.SendEffective(agent);
        return new JournalProvisioning(
            agent.JournalEnabled && hasBotToken
                ? journalTokens.Mint(JournalTokenService.PurposeIngest, agent.Name)
                : null,
            agent.JournalEnabled ? journalTokens.ExcludedChatIds() : [],
            (hasJournalEndpoint || filesEnabled || sendEnabled)
                ? journalTokens.Mint(JournalTokenService.PurposeRead, agent.Name)
                : null,
            // ⚠️ Derived, never configured per agent. `Journal:MediaEnabled` is the agent's opt-in
            //    for archived media, and nothing else on the agent side can know whether a bucket
            //    exists. Without this key the flag is unreachable from a provisioned deployment:
            //    the agent journals every attachment as `not_archived(media_disabled)` forever and
            //    the media plane never runs, while Comms happily accepts uploads.
            //
            //    The gate is the ENDPOINT ALONE, which is the same key the compose file uses to
            //    enable media (`Comms__Media__Endpoint=${FLEET_COMMS_MEDIA_ENDPOINT:-}`) and the
            //    same key `upgrade.sh` uses to decide whether to start the `comms-media` profile.
            //    There is therefore exactly one switch, and no second condition that can be unset
            //    while the bucket is running. An earlier version of this also required
            //    `FLEET_COMMS_MEDIA_BUCKET`, which `setup.sh` never writes — compose defaults it to
            //    `comms-journal` — so a fresh install that said yes to media provisioned every
            //    agent with the flag OFF. That is the bug this line exists to avoid, not a
            //    theoretical one.
            //
            //    The bucket name and the scoped credential pair are deliberately NOT part of the
            //    gate: an agent must not read a secret to decide a boolean, and a half-provisioned
            //    bucket is Comms' startup failure to raise, not the agent's to guess at.
            MediaEnabled: MediaEndpointIsConfigured(), FilesEnabled: filesEnabled,
            SendEnabled: sendEnabled,
            CrossChatToken: JournalGrants.CrossChatEffective(agent)
                ? journalTokens.Mint(JournalTokenService.PurposeReadCrossChat, agent.Name) : null);
    }

    /// <summary>
    /// Whether the deployment's media switch is on, read from the same <c>.env</c> the orchestrator
    /// reads the Codex server URL from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read from the provisioning env file the same way <c>DescribeCodexServerUrlFault</c> reads the
    /// Codex URL — these values are deployment-scoped, and the orchestrator's own process environment
    /// is not where the Comms media settings live. <c>EnvFile.Read</c> keeps <c>KEY=</c> as an empty
    /// value and skips a commented <c>#KEY=…</c>, so both "declined media" (<c>setup.sh</c> writes a
    /// blank endpoint) and "never asked" (the line is commented out in <c>.env.example</c>) read as
    /// off.
    /// </para>
    /// <para>
    /// The endpoint alone, with no <c>FLEET_COMMS_MEDIA_BUCKET</c> condition: see the caller for why
    /// that second condition was the bug.
    /// </para>
    /// </remarks>
    internal bool MediaEndpointIsConfigured()
    {
        var env = EnvFile.Read(ProvisioningOptions.From(config).EnvFilePath);
        return HasMeaningfulValue(env, "FLEET_COMMS_MEDIA_ENDPOINT");
    }

    private static bool HasMeaningfulValue(IReadOnlyDictionary<string, string> env, string key) =>
        env.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value);
}
