using Fleet.Orchestrator.Data;
using Fleet.Shared;

namespace Fleet.Orchestrator.Helpers;

internal static class AgentPatchHelpers
{
    /// <summary>
    /// Validates an agent's Claude local-model state after every field in the request is applied,
    /// then stores <c>AnthropicBaseUrl</c> in canonical form (#340 D1 point 1). Returns the fault,
    /// or null. On a fault the caller returns an error without saving, so nothing is persisted.
    /// </summary>
    /// <remarks>
    /// Shared by <c>update_agent_config</c> and <c>PUT /api/agents/{name}/config</c> so the two write
    /// paths cannot disagree. Runs on every write: a request that only sets <c>effort</c> on a local
    /// agent is still refused (V7). Agents without the field are never affected.
    /// </remarks>
    internal static string? FinalizeClaudeLocalModel(Agent agent)
    {
        if (ClaudeLocalModel.DescribeConfigFault(agent.Provider, agent.AnthropicBaseUrl, agent.Model, agent.Effort)
            is { } fault)
        {
            return fault;
        }

        // #349 V8: off exists only in local mode; a local → cloud switch must not ship it upstream.
        if (ClaudeLocalModel.DescribeLocalOnlyEffortFault(agent.Provider, agent.AnthropicBaseUrl, agent.Effort)
            is { } effortFault)
        {
            return effortFault;
        }

        if (ClaudeLocalModel.IsEnabled(agent.Provider, agent.AnthropicBaseUrl))
            agent.AnthropicBaseUrl = ClaudeLocalModel.CanonicalizeBaseUrl(agent.AnthropicBaseUrl!);

        return null;
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
