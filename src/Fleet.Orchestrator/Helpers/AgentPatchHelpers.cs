using Fleet.Orchestrator.Data;
using Fleet.Shared;

namespace Fleet.Orchestrator.Helpers;

internal static class AgentPatchHelpers
{
    /// <summary>
    /// Validates an agent's local-model state after every field in the request is applied (#340,
    /// #382), then stores <c>LocalBaseUrl</c> in canonical form. Returns the fault, or null. On a
    /// fault the caller returns an error without saving, so nothing is persisted.
    /// </summary>
    /// <remarks>
    /// Shared by <c>update_agent_config</c> and <c>PUT /api/agents/{name}/config</c> so the two write
    /// paths cannot disagree. Runs on every write: a request that only sets <c>effort</c> on a local
    /// agent is still refused (V7). Agents without the field are never affected. <c>Model</c> is
    /// never rewritten here: on a provider switch the caller sends the matching model, and V6 or
    /// C1 rejects a mismatch.
    /// </remarks>
    internal static string? FinalizeLocalModel(Agent agent)
    {
        if (LocalModel.DescribeConfigFault(agent.Provider, agent.LocalBaseUrl, agent.Model, agent.Effort)
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

        if (LocalModel.IsEnabled(agent.Provider, agent.LocalBaseUrl))
            agent.LocalBaseUrl = LocalModel.CanonicalizeBaseUrl(agent.LocalBaseUrl!);

        return null;
    }

    /// <summary>
    /// A1 (#382): resolves the local server URL field and its deprecated alias into one value.
    /// </summary>
    /// <remarks>
    /// <c>null</c> means not sent, <c>""</c> means clear, anything else means set. One field sent
    /// wins. Both sent: both <c>""</c> clears; one <c>""</c> is a conflict; two values are each
    /// checked with L2 (a fault names that field), then must be equal in canonical form. A stale
    /// dashboard that echoes the alias therefore never clears or changes the URL by accident.
    /// </remarks>
    /// <returns>
    /// <c>Sent</c> false when neither field was sent. Otherwise <c>Value</c> is the value to store
    /// (<c>null</c> clears), or <c>Error</c> names the field(s) at fault and nothing may be saved.
    /// </returns>
    internal static (bool Sent, string? Value, string? Error) ResolveLocalBaseUrl(
        string? value, string? alias, string valueName, string aliasName)
    {
        if (value is null && alias is null)
            return (false, null, null);
        if (alias is null)
            return (true, value == "" ? null : value, null);
        if (value is null)
            return (true, alias == "" ? null : alias, null);
        if (value == "" && alias == "")
            return (true, null, null);

        var conflict = $"{valueName} and {aliasName} disagree; send only {valueName} "
                     + $"({aliasName} is a deprecated alias of it).";
        if (value == "" || alias == "")
            return (true, null, conflict);

        if (LocalModel.DescribeBaseUrlFault(value) is { } valueFault)
            return (true, null, $"{valueName}: {valueFault}");
        if (LocalModel.DescribeBaseUrlFault(alias) is { } aliasFault)
            return (true, null, $"{aliasName}: {aliasFault}");

        var canonical = LocalModel.CanonicalizeBaseUrl(value);
        return canonical == LocalModel.CanonicalizeBaseUrl(alias)
            ? (true, canonical, null)
            : (true, null, conflict);
    }

    internal static readonly string[] ValidCodexSandboxModes =
        ["read-only", "workspace-write", "danger-full-access"];

    // Maps an incoming PATCH value to the stored CodexSandboxMode.
    // "" (empty string) → (null error, null value)  — clears the field
    // valid mode       → (null error, value)         — sets the field
    // invalid mode     → (error message, null)       — caller returns 400
    internal static (string? Error, string? Value) MapCodexSandboxMode(string input)
    {
        if (input == "")
            return (null, null);
        if (!Array.Exists(ValidCodexSandboxModes, m => m == input))
            return ($"Invalid CodexSandboxMode '{input}'. Valid values: {string.Join(", ", ValidCodexSandboxModes)}.", null);
        return (null, input);
    }
}
