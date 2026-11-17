using System.Globalization;

namespace Fleet.Memory.Services;

/// <summary>
/// How memories are cut for the index and what a search leaves out. Read once at startup from the
/// raw configuration strings, like <see cref="MemorySizeGuidance"/>: an empty value is the default,
/// and a value that is not a number or is out of range is the default with one Warning, never a
/// service that cannot start.
///
/// <see cref="SectionChars"/>, <see cref="MaxSections"/> and <see cref="EmbedderId"/> are part of
/// what a memory's index entry is made from, so changing one of them re-indexes every memory at
/// the next start.
/// </summary>
public sealed record MemoryIndexSettings
{
    public const string SectionCharsKey = "Search:SectionChars";
    public const string MaxSectionsKey = "Search:MaxSections";
    public const string MinMeaningScoreKey = "Search:MinMeaningScore";

    public const int DefaultSectionChars = 1200;
    public const int DefaultMaxSections = 200;

    /// <summary>The longest section, in characters. A memory no longer than this is one section.</summary>
    public int SectionChars { get; init; } = DefaultSectionChars;

    /// <summary>The most sections of one memory that are indexed.</summary>
    public int MaxSections { get; init; } = DefaultMaxSections;

    /// <summary>
    /// A hit found by meaning alone is dropped when its similarity is below this. 0 (the default)
    /// drops nothing: the right value depends on the embedding model.
    /// </summary>
    public float MinMeaningScore { get; init; }

    /// <summary>Which model made the vectors: the provider, its model and the dimensions.</summary>
    public string EmbedderId { get; init; } = "";

    public static MemoryIndexSettings FromConfiguration(IConfiguration configuration, ILogger logger)
    {
        var provider = configuration["Embedding:Provider"] ?? "onnx";
        var model = provider.Equals("ollama", StringComparison.OrdinalIgnoreCase)
            ? configuration["Embedding:Ollama:Model"] ?? "nomic-embed-text"
            : Path.GetFileName(configuration["Embedding:Onnx:ModelPath"] ?? "all-MiniLM-L6-v2.onnx");

        return new MemoryIndexSettings
        {
            SectionChars = ReadInt(configuration, SectionCharsKey, DefaultSectionChars, 200, 8000, logger),
            MaxSections = ReadInt(configuration, MaxSectionsKey, DefaultMaxSections, 1, 2000, logger),
            MinMeaningScore = ReadScore(configuration, MinMeaningScoreKey, logger),
            EmbedderId = $"{provider.ToLowerInvariant()}/{model}/{configuration["Embedding:Dimensions"] ?? "384"}",
        };
    }

    private static int ReadInt(IConfiguration configuration, string key, int fallback, int min, int max, ILogger logger)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (int.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max)
            return value;

        logger.LogWarning("{Key} must be a whole number from {Min} to {Max}; using the default {Default}", key, min, max, fallback);
        return fallback;
    }

    private static float ReadScore(IConfiguration configuration, string key, ILogger logger)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        if (float.TryParse(raw.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) && value is >= 0 and <= 1)
            return value;

        logger.LogWarning("{Key} must be a number from 0 to 1; using the default 0 (off)", key);
        return 0;
    }
}
