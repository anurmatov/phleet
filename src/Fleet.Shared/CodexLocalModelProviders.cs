namespace Fleet.Shared;

/// <summary>
/// The codex built-in <c>modelProvider</c> ids that point at a local OpenAI-compatible inference
/// server. Both are reserved inside codex, so only these two spellings are accepted — an invented id
/// would be rejected by the app-server at <c>thread/start</c>.
/// </summary>
/// <remarks>
/// Shared so <see cref="ClaudeLocalModel"/> can refuse a claude local model tag that names the codex
/// path, reading the same list <c>CodexExecutor</c> routes on (#340 V6).
/// </remarks>
public static class CodexLocalModelProviders
{
    public static IReadOnlyList<string> Ids { get; } = ["ollama", "lmstudio"];
}
