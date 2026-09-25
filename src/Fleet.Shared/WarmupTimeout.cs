namespace Fleet.Shared;

/// <summary>
/// Per-agent executor warmup timeout (#357): bounds, default and validation shared by every
/// orchestrator write path (REST create, PUT and MCP <c>update_agent_config</c>) and by the
/// agent's options default.
/// </summary>
/// <remarks>
/// The persisted <c>Agent.WarmupTimeoutSeconds</c> value is the authoritative runtime source;
/// these constants exist only to validate writes and to seed the default, never as a second
/// timeout the runtime could prefer. Invalid values are rejected, never clamped — a silent
/// correction would make live configuration unauditable.
/// </remarks>
public static class WarmupTimeout
{
    /// <summary>
    /// Cloud agents should not wait longer than before; a slower default would slow every
    /// startup for a local-model-only need. Local agents opt up per agent.
    /// </summary>
    public const int DefaultSeconds = 60;

    public const int MinSeconds = 10;
    public const int MaxSeconds = 600;

    /// <summary>Returns the fault for a proposed value, or null when it is in range.</summary>
    public static string? DescribeFault(int seconds)
        => seconds is < MinSeconds or > MaxSeconds
            ? $"Warmup timeout {seconds}s is outside the valid range {MinSeconds}..{MaxSeconds}s."
            : null;
}
