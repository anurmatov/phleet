using System.Globalization;
using System.Text;

namespace Fleet.Orchestrator.Services;

/// <summary>Which resident-prompt row a size report is about.</summary>
public enum PromptSizeKind
{
    Instruction,
    ProjectContext,
}

/// <summary>
/// The size of one instruction or project-context write, returned as <c>size</c> on the REST write
/// endpoints (camelCase, like the rest of the orchestrator API). Computed once, after the write commits.
/// </summary>
public sealed record PromptSizeReport(
    string Kind,
    string Unit,
    int? PreviousBytes,
    int Bytes,
    int? LimitBytes,
    string LimitKey,
    string Status,
    string? Warning);

/// <summary>
/// Advisory size limits for role instructions and project contexts (#346). Both are inlined into
/// the system prompt of every assigned agent on every turn, so a write over the soft limit is saved
/// and reported, never refused.
/// </summary>
/// <remarks>
/// <para>
/// The measure is UTF-8 bytes (<see cref="Encoding.UTF8"/>), never <c>string.Length</c>: UTF-16
/// units undercount non-Latin text, which tokenizes worse. Bytes are not tokens and nothing here
/// claims they are.
/// </para>
/// <para>
/// Limits are read once from the raw <see cref="IConfiguration"/> string with <c>int.TryParse</c> —
/// never through the options binder — so a typo falls back to the default with one Warning instead
/// of failing startup. A change needs a recreate. Size never changes a seeding, migration or
/// provisioning outcome: nothing outside the write surfaces calls this.
/// </para>
/// </remarks>
public sealed class PromptSizePolicy
{
    public const string InstructionKey = "PromptSizeWarnings:InstructionBytes";
    public const string ProjectContextKey = "PromptSizeWarnings:ProjectContextBytes";
    public const int DefaultLimitBytes = 10_000;
    public const string Unit = "utf8Bytes";

    public const string StatusUnder = "under";
    public const string StatusCrossed = "crossed";
    public const string StatusStillOver = "stillOver";
    public const string StatusDisabled = "disabled";

    private const string WarningTail =
        "Every assigned agent loads this text on every turn; move reference detail into memories and link them by id.";

    private readonly ILogger _logger;

    /// <summary>Instruction soft limit in bytes; <c>0</c> means disabled.</summary>
    public int InstructionLimitBytes { get; }

    /// <summary>Project-context soft limit in bytes; <c>0</c> means disabled.</summary>
    public int ProjectContextLimitBytes { get; }

    public PromptSizePolicy(int instructionLimitBytes, int projectContextLimitBytes, ILogger logger)
    {
        InstructionLimitBytes = instructionLimitBytes;
        ProjectContextLimitBytes = projectContextLimitBytes;
        _logger = logger;
    }

    /// <summary>Parses both keys independently, logging one Warning per invalid value.</summary>
    public static PromptSizePolicy FromConfiguration(IConfiguration configuration, ILogger logger) =>
        new(ParseLimit(configuration, InstructionKey, logger),
            ParseLimit(configuration, ProjectContextKey, logger),
            logger);

    /// <summary>
    /// An integer ≥ 0 is taken as is (<c>0</c> = off). Unset, empty or whitespace is the default with
    /// nothing logged. Anything else — not an integer, negative, outside <c>int</c> — is the default
    /// plus exactly one Warning naming the key.
    /// </summary>
    internal static int ParseLimit(IConfiguration configuration, string key, ILogger logger)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
            return DefaultLimitBytes;

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0)
            return value;

        logger.LogWarning("Invalid value for {Key}: expected an integer >= 0 (0 disables); using the default {Default} bytes",
            key, Format(DefaultLimitBytes));
        return DefaultLimitBytes;
    }

    /// <summary><c>PromptSizeWarnings: instruction=10,000 projectContext=disabled</c>.</summary>
    public string StartupLine =>
        $"PromptSizeWarnings: instruction={FormatLimit(InstructionLimitBytes)} projectContext={FormatLimit(ProjectContextLimitBytes)}";

    public void LogStartup() => _logger.LogInformation("{StartupLine}", StartupLine);

    public int LimitFor(PromptSizeKind kind) =>
        kind == PromptSizeKind.Instruction ? InstructionLimitBytes : ProjectContextLimitBytes;

    public static string KeyFor(PromptSizeKind kind) =>
        kind == PromptSizeKind.Instruction ? InstructionKey : ProjectContextKey;

    public static int Utf8Bytes(string text) => Encoding.UTF8.GetByteCount(text);

    /// <summary>
    /// The report for one write. <paramref name="previousContent"/> is the content of the version row
    /// <c>CurrentVersion</c> pointed at before the write, or null on create or when that row is missing.
    /// </summary>
    public PromptSizeReport Evaluate(PromptSizeKind kind, string name, string? previousContent, string content)
    {
        var limit = LimitFor(kind);
        var key = KeyFor(kind);
        int? previous = previousContent is null ? null : Utf8Bytes(previousContent);
        var bytes = Utf8Bytes(content);
        var kindName = kind == PromptSizeKind.Instruction ? "instruction" : "projectContext";

        if (limit == 0)
            return new PromptSizeReport(kindName, Unit, previous, bytes, null, key, StatusDisabled, null);

        var label = kind == PromptSizeKind.Instruction ? "Instruction" : "Project context";
        var wasOver = previous is { } p && p > limit;

        if (bytes <= limit)
            return new PromptSizeReport(kindName, Unit, previous, bytes, limit, key, StatusUnder, null);

        var warning = wasOver
            ? $"{label} '{name}' is still over the {Format(limit)}-byte soft limit ({key}): {Format(previous!.Value)} → {Format(bytes)} UTF-8 bytes. Saved anyway. {WarningTail}"
            : $"{label} '{name}' is {Format(bytes)} UTF-8 bytes, {Format(bytes - limit)} over the {Format(limit)}-byte soft limit ({key}). Saved anyway. {WarningTail}";

        return new PromptSizeReport(kindName, Unit, previous, bytes, limit, key,
            wasOver ? StatusStillOver : StatusCrossed, warning);
    }

    /// <summary>
    /// The lines an MCP write appends to its unchanged success output: always <c>\nSize: …</c>, plus
    /// <c>\nSize warning: …</c> for <c>crossed</c> / <c>stillOver</c>.
    /// </summary>
    public static string RenderMcpLines(PromptSizeReport report)
    {
        var sb = new StringBuilder("\nSize: ");
        if (report.PreviousBytes is { } previous)
            sb.Append(Format(previous)).Append(" → ");
        sb.Append(Format(report.Bytes)).Append(" UTF-8 bytes; ");
        sb.Append(report.Kind == "instruction" ? "instruction" : "project-context").Append(" soft limit ");
        sb.Append(report.LimitBytes is { } limit
            ? $"{Format(limit)} ({report.LimitKey})."
            : $"disabled ({report.LimitKey}=0).");

        if (report.Warning is not null)
            sb.Append("\nSize warning: ").Append(report.Warning);

        return sb.ToString();
    }

    /// <summary>One Information line per <c>crossed</c> / <c>stillOver</c> write; names only, never content.</summary>
    public void LogWrite(PromptSizeReport report, string surface, string name)
    {
        if (report.Status is not (StatusCrossed or StatusStillOver))
            return;

        _logger.LogInformation(
            "PromptSize {Status}: surface={Surface} kind={Kind} name={Name} bytes={Bytes} previousBytes={PreviousBytes} limitBytes={LimitBytes}",
            report.Status, surface, report.Kind, name,
            Format(report.Bytes),
            report.PreviousBytes is { } p ? Format(p) : "none",
            report.LimitBytes is { } l ? Format(l) : "disabled");
    }

    /// <summary><c>N0</c> with the invariant culture (<c>10,234</c>), pre-rendered for every line.</summary>
    public static string Format(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string FormatLimit(int limit) => limit == 0 ? "disabled" : Format(limit);
}
