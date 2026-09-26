namespace Fleet.Shared;

/// <summary>
/// Context window of a local-model Claude agent's server, in tokens (#367): the bounds and the
/// validation shared by every orchestrator write path (PUT and MCP <c>update_agent_config</c>).
/// </summary>
/// <remarks>
/// Claude CLI does not know the window of a model name it does not recognise, so it can compact
/// later than a local server's fixed context and the server rejects the turn. Provisioning passes
/// the value as <see cref="EnvVar"/>, and only for a claude agent in local mode: Anthropic models
/// have known windows that an override could shrink. There is no default on purpose — the right
/// value is the server's own configuration, and a guessed one fails silently. Invalid values are
/// rejected, never clamped. On the write paths 0 clears the value.
/// </remarks>
public static class ContextWindow
{
    public const int MinTokens = 4_096;
    public const int MaxTokens = 1_048_576;

    /// <summary>What Claude CLI reads as the real window of an unrecognised model.</summary>
    public const string EnvVar = "CLAUDE_CODE_MAX_CONTEXT_TOKENS";

    /// <summary>Returns the fault for a proposed value, or null when it is in range.</summary>
    public static string? DescribeFault(int tokens)
        => tokens is < MinTokens or > MaxTokens
            ? $"Context window {tokens} is outside the valid range {MinTokens}..{MaxTokens} tokens (0 clears it)."
            : null;
}
