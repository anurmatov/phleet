namespace Fleet.Shared;

/// <summary>
/// Where an agent keeps the id of its provider session between container lifetimes, and the words
/// a reprovision uses to ask for a new one. One definition, because the agent writes the file and
/// the orchestrator removes it: two spellings of the path would make "fresh session" a no-op.
/// </summary>
public static class AgentSessionFile
{
    /// <summary>Path of the file, relative to the agent's work directory (its workspace root).</summary>
    public const string RelativePath = ".fleet/session.json";

    /// <summary>Reprovision keeps the session when the agent's rules allow it. The default.</summary>
    public const string Auto = "auto";

    /// <summary>Reprovision removes the file first, so the agent starts a new session.</summary>
    public const string Fresh = "fresh";

    /// <summary>True for a value a reprovision request may carry; null and empty mean <see cref="Auto"/>.</summary>
    public static bool IsValidChoice(string? value) =>
        string.IsNullOrEmpty(value) || value is Auto or Fresh;

    /// <summary>True when the request asks for a new session.</summary>
    public static bool IsFresh(string? value) => value == Fresh;
}
