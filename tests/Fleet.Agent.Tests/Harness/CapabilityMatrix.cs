using System.Text.RegularExpressions;

namespace Fleet.Agent.Tests.Harness;

/// <summary>The four-tier honesty taxonomy (D6). Order of declaration is not significant.</summary>
internal enum Verdict
{
    /// <summary>The provider emits a distinguishable frame and the client receives a correct, non-lossy event.</summary>
    Supported,

    /// <summary>The client-visible event is produced without a confirming provider frame.</summary>
    Inferred,

    /// <summary>The provider emits nothing the runtime can map, or has no such capability.</summary>
    Unsupported,

    /// <summary>The client receives the event, but the payload carries content the boundary excludes.</summary>
    Leaky,
}

/// <summary>One parsed row of the committed capability matrix (D6).</summary>
internal sealed record CapabilityRow(
    string Provider,
    string Scenario,
    string ProviderFrames,
    string AgentProgress,
    string ClientEvents,
    Verdict Verdict,
    string Evidence,
    string Note)
{
    public string Key => $"{Provider}/{Scenario}";
}

/// <summary>
/// Parses <c>docs/spikes/real-adapter-capability-matrix.md</c> per the fixed D6 grammar.
///
/// <para>The document cannot drift from the code: every scenario asserts its observation against
/// the row parsed here, and <c>ProviderCapabilityMatrixTests</c> asserts row set ↔ asserted set in
/// BOTH directions.</para>
/// </summary>
internal static partial class CapabilityMatrix
{
    /// <summary>The literal that means "no L3 capture has been run for this row".</summary>
    public const string FixtureOnly = "fixture-only";

    /// <summary>
    /// A <c>verified</c> evidence cell names the L3 capture date and the exact pinned CLI, in the
    /// cell itself, so a reader never has to correlate a row against a separate run log.
    /// </summary>
    [GeneratedRegex(@"^verified@\d{4}-\d{2}-\d{2}/[a-z0-9.\-]+$")]
    public static partial Regex VerifiedEvidencePattern { get; }

    /// <summary>Provider table headings, matched exactly.</summary>
    public const string Claude = "claude";
    public const string Codex = "codex";
    public const string Gemini = "gemini";

    public static readonly IReadOnlyList<string> Providers = [Claude, Codex, Gemini];

    private static readonly string[] ExpectedHeader =
        ["scenario", "provider frames", "agent progress", "client events", "verdict", "evidence", "note"];

    /// <summary>Repo-relative path of the committed matrix.</summary>
    public const string DocumentPath = "docs/spikes/real-adapter-capability-matrix.md";

    /// <summary>Parse the committed document from the repository root.</summary>
    public static IReadOnlyList<CapabilityRow> Parse() =>
        Parse(File.ReadAllText(RepoPaths.Resolve(DocumentPath)));

    /// <summary>
    /// Parse matrix text. Provider scope comes from a <c>## &lt;provider&gt;</c> heading; every
    /// pipe table beneath it contributes rows, so the per-provider table and the cross-cutting
    /// gaps table share one grammar and one parser.
    /// </summary>
    public static IReadOnlyList<CapabilityRow> Parse(string markdown)
    {
        var rows = new List<CapabilityRow>();
        string? provider = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rawLine in markdown.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                var heading = line[3..].Trim();
                provider = Providers.Contains(heading, StringComparer.Ordinal) ? heading : null;
                continue;
            }

            if (provider is null || !line.StartsWith('|'))
                continue;

            var cells = SplitRow(line);
            if (cells.Count != ExpectedHeader.Length)
            {
                throw new FormatException(
                    $"Matrix row under '{provider}' has {cells.Count} cells, expected {ExpectedHeader.Length}: {line}");
            }

            // Header and separator rows.
            if (cells.SequenceEqual(ExpectedHeader, StringComparer.Ordinal))
                continue;
            if (cells.All(c => c.Length > 0 && c.All(ch => ch is '-' or ':')))
                continue;

            var row = new CapabilityRow(
                Provider: provider,
                Scenario: cells[0],
                ProviderFrames: cells[1],
                AgentProgress: cells[2],
                ClientEvents: cells[3],
                Verdict: ParseVerdict(cells[4], provider, cells[0]),
                Evidence: cells[5],
                Note: cells[6]);

            if (!seen.Add(row.Key))
                throw new FormatException($"Duplicate matrix row {row.Key}.");

            rows.Add(row);
        }

        if (rows.Count == 0)
            throw new FormatException("The capability matrix parsed to zero rows.");

        return rows;
    }

    private static List<string> SplitRow(string line) =>
        line.Trim('|').Split('|').Select(c => c.Trim()).ToList();

    private static Verdict ParseVerdict(string cell, string provider, string scenario) => cell switch
    {
        "supported" => Verdict.Supported,
        "inferred" => Verdict.Inferred,
        "unsupported" => Verdict.Unsupported,
        "leaky" => Verdict.Leaky,
        _ => throw new FormatException(
            $"Row {provider}/{scenario} has verdict '{cell}'; expected one of supported/inferred/unsupported/leaky."),
    };

    /// <summary>Look up a row, failing with the missing key rather than a null reference.</summary>
    public static CapabilityRow Row(this IReadOnlyList<CapabilityRow> rows, string provider, string scenario) =>
        rows.FirstOrDefault(r => r.Provider == provider && r.Scenario == scenario)
        ?? throw new InvalidOperationException(
            $"The capability matrix has no row for {provider}/{scenario}. A harness assertion without a row is a "
            + "documentation gap, not a test bug — add the row to " + DocumentPath + ".");
}

/// <summary>
/// Resolves repository-relative paths from the test output directory, so every path in the suite
/// is stated once and a rename fails loudly instead of silently sweeping nothing.
/// </summary>
internal static class RepoPaths
{
    private static readonly Lazy<string> RootPath = new(FindRoot);

    /// <summary>The repository root, found by walking up from the test output directory.</summary>
    public static string Root => RootPath.Value;

    public static string Resolve(string repoRelativePath)
    {
        var full = Path.Combine(Root, repoRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(full) && !Directory.Exists(full))
            throw new FileNotFoundException($"Repository path '{repoRelativePath}' does not exist at '{full}'.", full);
        return full;
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Fleet.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate the repository root (no Fleet.sln above '{AppContext.BaseDirectory}').");
    }
}
