using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fleet.Shared;
using Microsoft.Extensions.Logging;

namespace Fleet.Agent.Services;

/// <summary>What a provider process start does about the session: resume one, or begin a new one.</summary>
/// <param name="ResumeId">The session to resume, or null for a new session.</param>
/// <param name="Reason">Why the session is new (<c>no file</c>, <c>instructions changed</c>, …). Empty when resuming.</param>
/// <param name="ReplacedId">
/// The session of this same provider that the file named and that is not being resumed, so the
/// new session replaces it. Null when the file named none, or one of another provider.
/// </param>
internal sealed record SessionStartDecision(string? ResumeId, string Reason, string? ReplacedId = null);

/// <summary>
/// <c>.fleet/session.json</c> under the agent's work directory
/// (<see cref="AgentSessionFile.RelativePath"/>): the provider session a Claude or Codex agent
/// goes back to after its container is replaced.
/// </summary>
/// <remarks>
/// <para>It records the provider, the session (Claude) or thread (Codex) id, where the model runs,
/// and a SHA-256 of the exact instructions the session was started with. A start resumes the
/// session only when all three equal what this start will use. The instructions are part of the
/// rule and not an optimisation: a resumed Claude session ignores a new system prompt, and a
/// resumed Codex thread keeps the old instructions next to the new ones.</para>
/// <para>Where the model runs is compared for the same kind of reason: a conversation recorded
/// against one backend (the provider's own cloud, a local server, a hosted provider) carries items
/// another backend can refuse, and that would fail on every turn after a resume that itself
/// succeeded.</para>
/// <para>The instructions themselves are never stored or logged, only their hash.</para>
/// </remarks>
internal sealed class ProviderSessionFile(string workDir, string provider, string runsOn, ILogger logger)
{
    /// <summary><see cref="RunsOn"/> for a model on the provider's own cloud.</summary>
    internal const string Cloud = "cloud";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _provider = provider;
    private readonly string _runsOn = runsOn;
    private readonly ILogger _logger = logger;

    /// <summary>What the file is known to hold, so an unchanged session is not written again.</summary>
    private Content? _known;

    /// <summary>Absolute path of the file.</summary>
    internal string FilePath { get; } = Path.Combine(workDir, AgentSessionFile.RelativePath);

    /// <summary>The hash a session is bound to: SHA-256 of the instructions' UTF-8 bytes, lower-case hex.</summary>
    internal static string HashInstructions(string instructions) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(instructions)));

    /// <summary>
    /// Reads the file and decides this start. Never throws: a file that cannot be read or does not
    /// hold a session is a new session.
    /// </summary>
    internal SessionStartDecision Decide(string instructionsHash)
    {
        _known = null;

        if (!File.Exists(FilePath))
            return new SessionStartDecision(null, "no file");

        Content? content;
        try
        {
            content = JsonSerializer.Deserialize<Content>(File.ReadAllText(FilePath), Json);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Session file {Path} could not be read ({Error})", FilePath, ex.GetType().Name);
            return new SessionStartDecision(null, "unreadable file");
        }

        if (content is null || string.IsNullOrWhiteSpace(content.SessionId) || string.IsNullOrWhiteSpace(content.Provider))
            return new SessionStartDecision(null, "unreadable file");

        if (!string.Equals(content.Provider, _provider, StringComparison.Ordinal))
            return new SessionStartDecision(null, "other provider");

        _known = content;
        if (!string.Equals(content.RunsOn ?? Cloud, _runsOn, StringComparison.Ordinal))
            return new SessionStartDecision(null, "model runs elsewhere", content.SessionId);

        if (!string.Equals(content.InstructionsSha256, instructionsHash, StringComparison.OrdinalIgnoreCase))
            return new SessionStartDecision(null, "instructions changed", content.SessionId);

        return new SessionStartDecision(content.SessionId, "");
    }

    /// <summary>
    /// Records the session, atomically: the content goes to a temporary file beside the target,
    /// which then replaces it by rename. Never throws; a failed write is logged and the next start
    /// is a new session.
    /// </summary>
    internal void Save(string sessionId, string instructionsHash)
    {
        var content = new Content(_provider, sessionId, instructionsHash, _runsOn);
        if (content == _known)
            return;

        var tmpPath = FilePath + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(tmpPath, JsonSerializer.Serialize(content, Json), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tmpPath, FilePath, overwrite: true);
            _known = content;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _known = null;
            _logger.LogWarning(
                "Session file {Path} could not be written ({Error}); the next start begins a new session",
                FilePath, ex.GetType().Name);
            try { File.Delete(tmpPath); }
            catch { /* the warning above already says the write failed */ }
        }
    }

    /// <summary>
    /// Removes the file, so the next start is a new session. Never throws. Returns false only when
    /// a file is still there afterwards.
    /// </summary>
    internal bool Remove()
    {
        _known = null;
        try
        {
            File.Delete(FilePath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Session file {Path} could not be removed ({Error})", FilePath, ex.GetType().Name);
            return !File.Exists(FilePath);
        }
    }

    private sealed record Content(
        [property: JsonPropertyName("provider")] string Provider,
        [property: JsonPropertyName("sessionId")] string SessionId,
        [property: JsonPropertyName("instructionsSha256")] string InstructionsSha256,
        [property: JsonPropertyName("runsOn")] string? RunsOn);
}
