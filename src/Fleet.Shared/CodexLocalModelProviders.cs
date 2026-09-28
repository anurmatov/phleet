namespace Fleet.Shared;

/// <summary>
/// The codex built-in <c>modelProvider</c> ids that point at a local OpenAI-compatible inference
/// server. Both are reserved inside codex, so only these two spellings are accepted — an invented id
/// would be rejected by the app-server at <c>thread/start</c>.
/// </summary>
/// <remarks>
/// Shared so <see cref="ClaudeLocalModel"/> can refuse a claude local model tag that names the codex
/// path, reading the same list <c>CodexExecutor</c> routes on (#340 V6), and so the orchestrator's
/// codex local-model check (#382 C1) splits a model exactly as the executor does.
/// </remarks>
public static class CodexLocalModelProviders
{
    public static IReadOnlyList<string> Ids { get; } = ["ollama", "lmstudio"];

    /// <summary>
    /// Splits an <c>ollama/…</c> or <c>lmstudio/…</c> model string into the codex
    /// <c>modelProvider</c> id and the bare model id it names.
    /// </summary>
    /// <remarks>
    /// Split at the first <c>/</c>; local only when that slash is neither the first nor the last
    /// character and the prefix is one of <see cref="Ids"/>, case-insensitively. The tag keeps any
    /// later slashes. Any other string comes back unchanged with a null provider.
    /// </remarks>
    public static (string? Provider, string Model) Split(string model)
    {
        var slash = model.IndexOf('/');
        if (slash <= 0 || slash == model.Length - 1)
            return (null, model);

        var prefix = model[..slash];
        var provider = Ids.FirstOrDefault(p => string.Equals(p, prefix, StringComparison.OrdinalIgnoreCase));

        return provider is null ? (null, model) : (provider, model[(slash + 1)..]);
    }
}
