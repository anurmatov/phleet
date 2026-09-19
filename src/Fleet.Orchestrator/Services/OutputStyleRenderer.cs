using Fleet.Orchestrator.Data;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// Renders one <see cref="OutputStyle"/> row for whichever provider is asking.
/// </summary>
/// <remarks>
/// <para>
/// Output styles are a Claude Code feature. A claude agent gets the row written to disk as a style
/// file and named in <c>settings.json</c>; codex and gemini have no such mechanism, so they get the
/// same text inlined into their system prompt instead. Both renderings come from this one row, and
/// this is the only place that knows the file format — a second parser would be a second answer to
/// what the style says.
/// </para>
/// <para>
/// <b>Frontmatter is stripped before inlining and never before writing the file.</b> Claude Code
/// reads <c>name</c>, <c>description</c> and <c>keep-coding-instructions</c> out of it, so the file
/// must keep it verbatim; a prompt has no use for it and would spend context on YAML the model is
/// meant to obey rather than read.
/// </para>
/// </remarks>
public static class OutputStyleRenderer
{
    private const string Delimiter = "---";

    /// <summary>The bytes written to <c>.generated/output-styles/{name}.md</c> — the row, verbatim.</summary>
    public static string ForStyleFile(OutputStyle style) => style.Body;

    /// <summary>
    /// The text inlined into a non-claude agent's system prompt: the body with its YAML
    /// frontmatter removed, trimmed. Returns the whole body when there is no frontmatter.
    /// </summary>
    public static string ForPrompt(OutputStyle style) => StripFrontmatter(style.Body).Trim();

    /// <summary>
    /// The <c>description:</c> line from the frontmatter, or null when absent. Operator-facing;
    /// nothing reads it at runtime.
    /// </summary>
    public static string? ReadDescription(string body)
    {
        foreach (var line in FrontmatterLines(body))
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("description:", StringComparison.OrdinalIgnoreCase)) continue;

            var value = trimmed["description:".Length..].Trim().Trim('"', '\'');
            return value.Length == 0 ? null : value;
        }

        return null;
    }

    private static IEnumerable<string> FrontmatterLines(string body)
    {
        if (!StartsWithDelimiter(body, out var afterOpen)) yield break;

        foreach (var line in body[afterOpen..].Split('\n'))
        {
            if (line.TrimEnd('\r') == Delimiter) yield break;
            yield return line;
        }
    }

    private static string StripFrontmatter(string body)
    {
        if (!StartsWithDelimiter(body, out var afterOpen)) return body;

        // The closing delimiter must be a line of its own, so a body containing "---" as a
        // horizontal rule mid-sentence cannot truncate the style.
        var rest = body[afterOpen..];
        var index = 0;
        while (index < rest.Length)
        {
            var lineEnd = rest.IndexOf('\n', index);
            var line = (lineEnd < 0 ? rest[index..] : rest[index..lineEnd]).TrimEnd('\r');

            if (line == Delimiter)
                return lineEnd < 0 ? "" : rest[(lineEnd + 1)..];

            if (lineEnd < 0) break;
            index = lineEnd + 1;
        }

        // Unterminated frontmatter — return the body untouched rather than swallowing all of it.
        return body;
    }

    private static bool StartsWithDelimiter(string body, out int afterOpen)
    {
        afterOpen = 0;
        if (!body.StartsWith(Delimiter, StringComparison.Ordinal)) return false;

        var firstNewline = body.IndexOf('\n');
        if (firstNewline < 0) return false;
        if (body[..firstNewline].TrimEnd('\r') != Delimiter) return false;

        afterOpen = firstNewline + 1;
        return true;
    }
}
