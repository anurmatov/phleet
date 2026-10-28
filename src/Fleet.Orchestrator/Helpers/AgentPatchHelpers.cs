using Fleet.Orchestrator.Data;
using Fleet.Shared;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Helpers;

internal static class AgentPatchHelpers
{
    /// <summary>
    /// Validates an agent's provider and local-model state after every field in the request is
    /// applied (#340, #382, #386), then stores <c>LocalBaseUrl</c> in canonical form. Returns the
    /// fault, or null. On a fault the caller returns an error without saving, so nothing is persisted.
    /// </summary>
    /// <remarks>
    /// Shared by <c>update_agent_config</c> and <c>PUT /api/agents/{name}/config</c> so the two write
    /// paths cannot disagree. Runs on every write: a request that only sets <c>effort</c> on a local
    /// agent is still refused (V7). Agents without the field are never affected.
    /// <c>Model</c> and <c>Effort</c> are never rewritten here: on a provider switch the caller sends
    /// the matching values, and V6 or C1 rejects a mismatch.
    /// </remarks>
    internal static string? FinalizeLocalModel(Agent agent)
    {
        // PV1 (#386): the resulting provider, whichever field or earlier write put it there.
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

        if (LocalModel.IsEnabled(agent.Provider, agent.LocalBaseUrl))
            agent.LocalBaseUrl = LocalModel.CanonicalizeBaseUrl(agent.LocalBaseUrl!);

        return null;
    }

    /// <summary>
    /// Validates the agent's resulting <c>HarnessExtra</c> against the allowlist for its resulting
    /// provider and mode (#460 Part 3b). Returns the fault, or null; on a fault the caller returns an
    /// error without saving. Run after <see cref="FinalizeLocalModel"/>, so the mode is final.
    /// </summary>
    /// <remarks>
    /// Shared by <c>update_agent_config</c>, <c>PUT /api/agents/{name}/config</c> and both create
    /// paths. Like <see cref="FinalizeLocalModel"/> it checks the resulting state on every write, so a
    /// provider or mode switch that would leave a stored value outside the allowlist (gemini, or a
    /// local agent with <c>web_search = "live"</c>) is refused until the value is changed or cleared.
    /// </remarks>
    internal static HarnessExtraFault? DescribeHarnessExtraFault(Agent agent) =>
        HarnessExtra.Parse(agent.HarnessExtra, agent.Provider,
            LocalModel.IsEnabled(agent.Provider, agent.LocalBaseUrl)).Fault;

    /// <summary>
    /// The local server URL and context window a create request carries (#386): validated by the same
    /// rules as the PUT, before the row is written, and stored in canonical form. Returns the fault,
    /// or null.
    /// </summary>
    /// <remarks>
    /// A create without either field is unchanged; with one, it is checked exactly as an
    /// update would check it. <c>null</c>, <c>""</c> and a context of 0 mean not set.
    /// </remarks>
    internal static string? ApplyLocalModelOnCreate(Agent agent, string? localBaseUrl, int? contextWindow)
    {
        var url = string.IsNullOrEmpty(localBaseUrl) ? null : localBaseUrl;
        var window = contextWindow is null or 0 ? null : contextWindow;
        if (url is null && window is null)
        {
            // No local fields: only the effort rules the PUT applies to a cloud agent (V8).
            return ClaudeLocalModel.DescribeLocalOnlyEffortFault(agent.Provider, null, agent.Effort);
        }

        if (window is int tokens && ContextWindow.DescribeFault(tokens) is { } windowFault)
            return windowFault;

        agent.LocalBaseUrl = url;
        agent.ContextWindow = window;
        return FinalizeLocalModel(agent);
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

    /// <summary>
    /// The 400 text for a create or config body that does not deserialize (#455): malformed JSON,
    /// or a field of the wrong JSON type. Shared
    /// by <c>POST /api/agents</c> and <c>PUT /api/agents/{name}/config</c>; nothing is written.
    /// </summary>
    internal static string DescribeBodyFault(System.Text.Json.JsonException ex) =>
        $"Invalid request body at '{ex.Path ?? "$"}': malformed JSON or a value of the wrong type (booleans take true or false).";

    /// <summary>The 400 body for a refused <c>harnessExtra</c>: the field, the line and the reason.</summary>
    internal static object HarnessExtraBadRequest(HarnessExtraFault fault) =>
        new { error = fault.Message, code = fault.Code, field = "harnessExtra", line = fault.Line };

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

    internal sealed record RuntimeSelectionFault(int Status, string Code);
    internal static async Task<RuntimeSelectionFault?> ApplyRuntimeSelectionAsync(
        OrchestratorDbContext db, Agent agent, string? kind, int? version, string? auth, string? keyRef)
    {
        var runtime = kind ?? agent.RuntimeKind;
        var policy = version == 0 ? null : version ?? agent.SandboxPolicyVersionId;
        var mode = auth == "" ? null : auth ?? agent.NativeAuthMode;
        var reference = keyRef == "" ? null : keyRef ?? agent.ProviderApiKeyEnvRef;
        if (runtime is not ("docker" or "openshell")) return new(400, "invalid_runtime_kind");
        if (mode is not (null or "api_key")) return new(400, "unsupported_native_auth_mode");
        if (reference is not null && !System.Text.RegularExpressions.Regex.IsMatch(reference, @"^[A-Za-z_][A-Za-z0-9_]{0,127}\z"))
            return new(400, "invalid_provider_key_reference");
        if (policy is not null && !await db.SandboxPolicyVersions.AnyAsync(v => v.Id == policy))
            return new(404, "sandbox_policy_version_not_found");
        if (runtime == "openshell" && (agent.Provider != "codex" || agent.LocalBaseUrl is not null || policy is null || mode != "api_key" || reference is null || agent.MountDockerSock))
            return new(400, "openshell_requires_codex_api_key_policy_and_no_socket");
        agent.RuntimeKind = runtime;
        agent.SandboxPolicyVersionId = policy;
        agent.NativeAuthMode = mode;
        agent.ProviderApiKeyEnvRef = reference;
        return null;
    }

    internal static bool RuntimePending(Agent agent) => agent.RuntimeKind != agent.AppliedRuntimeKind
        || agent.SandboxPolicyVersionId != agent.AppliedSandboxPolicyVersionId;
    internal static string? RuntimeApplyFault(Agent agent) => agent.RuntimeKind != "docker"
        || agent.AppliedRuntimeKind != "docker" || RuntimePending(agent) ? "managed_runtime_unavailable" : null;
}
