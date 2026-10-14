namespace Fleet.Shared;

/// <summary>
/// The three agent harness names and PV1 (#386): an unknown provider string is refused everywhere
/// instead of falling through to Claude credentials and <c>ClaudeExecutor</c>.
/// </summary>
/// <remarks>
/// Ordinal and lowercase. An empty or null provider stays <see cref="Claude"/>, as it always has.
/// Every site that switches on a provider has an explicit case for each name below; its default
/// branch raises <see cref="DescribeFault"/>'s text instead of serving Claude.
/// </remarks>
public static class AgentProviders
{
    public const string Claude = "claude";
    public const string Codex = "codex";
    public const string Gemini = "gemini";

    public static IReadOnlyList<string> All { get; } = [Claude, Codex, Gemini];

    /// <summary>True for a known provider, and for null/empty (which means claude).</summary>
    public static bool IsKnown(string? provider) =>
        string.IsNullOrEmpty(provider) || All.Contains(provider, StringComparer.Ordinal);

    /// <summary>The effective provider: null/empty → claude, anything else verbatim.</summary>
    public static string Normalize(string? provider) =>
        string.IsNullOrEmpty(provider) ? Claude : provider;

    /// <summary>PV1: the fault for an unknown provider, or null for a known or empty one.</summary>
    public static string? DescribeFault(string? provider) =>
        IsKnown(provider) ? null : UnknownProviderText(provider!);

    public static string UnknownProviderText(string provider) =>
        $"Unknown provider '{provider}'; use claude, codex or gemini.";
}
