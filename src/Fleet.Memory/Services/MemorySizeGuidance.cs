using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using Fleet.Memory.Models;

namespace Fleet.Memory.Services;

/// <summary>
/// The size of one memory write, returned as <c>size</c> on <c>PUT /internal/memory/{id}</c>
/// (snake_case, like the rest of the fleet-memory API). <see cref="Bytes"/> is the exact embedding
/// input: <c>{title}\n\n{content}</c> as parsed back from disk.
/// </summary>
public sealed record MemorySizeReport(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("previous_bytes")] int? PreviousBytes,
    [property: JsonPropertyName("bytes")] int Bytes,
    [property: JsonPropertyName("limit_bytes")] int? LimitBytes,
    [property: JsonPropertyName("limit_key")] string LimitKey,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("warning")] string? Warning);

/// <summary>
/// Embedding-input size guidance for memories (#346), off by default. fleet-memory embeds each
/// memory as one input with no chunking, and re-embeds every file on every start; text past the
/// model's input window may be cut off or rejected. The platform does not know the operator's
/// tokenizer, so there is no default limit and nothing here talks about tokens.
/// </summary>
/// <remarks>
/// <para>
/// Advisory only: a memory over the guidance is still saved and still indexed. The limit is read
/// once from the raw <see cref="IConfiguration"/> string — deliberately NOT a property of
/// <c>EmbeddingOptions</c>, which <c>Program.cs</c> binds with <c>Get&lt;EmbeddingOptions&gt;()</c>,
/// where a typo would become a crash loop. A change needs a recreate.
/// </para>
/// <para>
/// No line here ever carries a memory title or content: ids, byte counts and key names only.
/// </para>
/// </remarks>
public sealed class MemorySizeGuidance
{
    public const string Key = "Embedding:InputGuidanceBytes";
    public const int DefaultLimitBytes = 0;
    public const string Unit = "utf8Bytes";
    public const string Kind = "memory";

    public const string StatusUnder = "under";
    public const string StatusCrossed = "crossed";
    public const string StatusStillOver = "stillOver";
    public const string StatusDisabled = "disabled";

    /// <summary>At most this many per-memory lines after a full index; the rest are counted.</summary>
    public const int MaxListedOverGuidance = 20;

    private const string WarningTail =
        "The whole memory is embedded as one input and re-embedded on every fleet-memory restart; " +
        "text past the embedding model's input window may be cut off or rejected, leaving the memory " +
        "partly or wholly unsearchable. Consider splitting it by topic and linking the parts by id.";

    private readonly ILogger _logger;

    /// <summary>Guidance in bytes; <c>0</c> means off.</summary>
    public int LimitBytes { get; }

    public bool Enabled => LimitBytes > 0;

    public MemorySizeGuidance(int limitBytes, ILogger logger)
    {
        LimitBytes = limitBytes;
        _logger = logger;
    }

    /// <summary>
    /// An integer ≥ 0 is taken as is (<c>0</c> = off). Unset, empty or whitespace is the default with
    /// nothing logged. Anything else — not an integer, negative, outside <c>int</c> — is the default
    /// plus exactly one Warning naming the key.
    /// </summary>
    public static MemorySizeGuidance FromConfiguration(IConfiguration configuration, ILogger logger)
    {
        var raw = configuration[Key];
        if (string.IsNullOrWhiteSpace(raw))
            return new MemorySizeGuidance(DefaultLimitBytes, logger);

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0)
            return new MemorySizeGuidance(value, logger);

        logger.LogWarning("Invalid value for {Key}: expected an integer >= 0 (0 disables); embedding-input guidance stays disabled", Key);
        return new MemorySizeGuidance(DefaultLimitBytes, logger);
    }

    public static int Utf8Bytes(string text) => Encoding.UTF8.GetByteCount(text);

    /// <summary>
    /// <c>Embedding input guidance: 25,000 UTF-8 bytes (Embedding:InputGuidanceBytes); provider=ollama model=bge-m3</c>,
    /// or <c>disabled</c> in place of the number.
    /// </summary>
    public string StartupLine(string provider, string model) =>
        $"Embedding input guidance: {(Enabled ? $"{Format(LimitBytes)} UTF-8 bytes" : "disabled")} ({Key}); provider={provider} model={model}";

    public void LogStartup(string provider, string model) =>
        _logger.LogInformation("{StartupLine}", StartupLine(provider, model));

    /// <summary>
    /// The report for one write. <paramref name="previousText"/> is the embedding text before an
    /// update, or null on store.
    /// </summary>
    public MemorySizeReport Evaluate(string id, string? previousText, string text)
    {
        int? previous = previousText is null ? null : Utf8Bytes(previousText);
        var bytes = Utf8Bytes(text);

        if (!Enabled)
            return new MemorySizeReport(Kind, Unit, previous, bytes, null, Key, StatusDisabled, null);

        if (bytes <= LimitBytes)
            return new MemorySizeReport(Kind, Unit, previous, bytes, LimitBytes, Key, StatusUnder, null);

        var id8 = id.Length > 8 ? id[..8] : id;
        var wasOver = previous is { } p && p > LimitBytes;
        var warning = wasOver
            ? $"Memory {id8} embedding input is still over the {Format(LimitBytes)}-byte guidance ({Key}): {Format(previous!.Value)} → {Format(bytes)} UTF-8 bytes. Saved anyway. {WarningTail}"
            : $"Memory {id8} embedding input (title + content) is {Format(bytes)} UTF-8 bytes, {Format(bytes - LimitBytes)} over the {Format(LimitBytes)}-byte guidance ({Key}). Saved anyway. {WarningTail}";

        return new MemorySizeReport(Kind, Unit, previous, bytes, LimitBytes, Key,
            wasOver ? StatusStillOver : StatusCrossed, warning);
    }

    /// <summary>
    /// What <c>memory_store</c> / <c>memory_update</c> append to their unchanged output: nothing when
    /// the guidance is off; otherwise <c>\nSize: …</c> plus <c>\nSize warning: …</c> when over.
    /// </summary>
    public static string RenderMcpLines(MemorySizeReport report)
    {
        if (report.LimitBytes is not { } limit)
            return "";

        var sb = new StringBuilder("\nSize: ");
        if (report.PreviousBytes is { } previous)
            sb.Append(Format(previous)).Append(" → ");
        sb.Append(Format(report.Bytes)).Append(" UTF-8 bytes embedded; embedding-input guidance ")
          .Append(Format(limit)).Append(" (").Append(Key).Append(").");

        if (report.Warning is not null)
            sb.Append("\nSize warning: ").Append(report.Warning);

        return sb.ToString();
    }

    /// <summary>One Information line per <c>crossed</c> / <c>stillOver</c> write; id and sizes only.</summary>
    public void LogWrite(MemorySizeReport report, string surface, string op, string id)
    {
        if (report.Status is not (StatusCrossed or StatusStillOver))
            return;

        _logger.LogInformation(
            "MemorySize {Status}: surface={Surface} op={Op} id={Id} bytes={Bytes} previousBytes={PreviousBytes} limitBytes={LimitBytes}",
            report.Status, surface, op, id,
            Format(report.Bytes),
            report.PreviousBytes is { } p ? Format(p) : "none",
            report.LimitBytes is { } l ? Format(l) : "disabled");
    }

    /// <summary>
    /// The size check after a full index, from the documents the scan already parsed — no extra I/O
    /// and no embedding calls. <paramref name="documents"/> are every parsed memory; a file that failed
    /// to parse is in <paramref name="corrupt"/>, so <c>scanned = indexed + indexFailures + corrupt</c>.
    /// </summary>
    public FullScanSizeSummary SummarizeFullScan(int indexed, int indexFailures, int corrupt, IEnumerable<MemoryDocument> documents)
    {
        var over = Enabled
            ? documents
                .Select(d => (d.Id, Bytes: Utf8Bytes(MemoryService.EmbeddingText(d))))
                .Where(x => x.Bytes > LimitBytes)
                .OrderByDescending(x => x.Bytes)
                .ThenBy(x => x.Id, StringComparer.Ordinal)
                .ToList()
            : [];

        var scanned = indexed + indexFailures + corrupt;
        var level = indexFailures == 0 && corrupt == 0 && over.Count == 0 ? LogLevel.Information : LogLevel.Warning;
        var lines = new List<(LogLevel Level, string Message)>
        {
            (level,
             $"Full index size check: scanned={Format(scanned)} indexed={Format(indexed)} indexFailures={Format(indexFailures)} " +
             $"corrupt={Format(corrupt)} overGuidance={Format(over.Count)} limitBytes={(Enabled ? Format(LimitBytes) : "disabled")}"),
        };

        foreach (var (id, bytes) in over.Take(MaxListedOverGuidance))
            lines.Add((LogLevel.Warning, $"Memory over embedding-input guidance: id={id} bytes={Format(bytes)} limitBytes={Format(LimitBytes)}"));

        if (over.Count > MaxListedOverGuidance)
            lines.Add((LogLevel.Warning, $"…and {Format(over.Count - MaxListedOverGuidance)} more"));

        return new FullScanSizeSummary(scanned, indexed, indexFailures, corrupt, over.Count, lines);
    }

    /// <summary><c>N0</c> with the invariant culture (<c>10,234</c>), pre-rendered for every line.</summary>
    public static string Format(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
}

/// <summary>The counts behind the <c>Full index size check:</c> line, and the rendered lines to log in order.</summary>
public sealed record FullScanSizeSummary(
    int Scanned,
    int Indexed,
    int IndexFailures,
    int Corrupt,
    int OverGuidance,
    IReadOnlyList<(LogLevel Level, string Message)> Lines);
