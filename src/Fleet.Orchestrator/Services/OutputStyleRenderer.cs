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
    public static string ForPrompt(OutputStyle style) => BodyAfterFrontmatter(style.Body);

    /// <summary>
    /// What is left of <paramref name="body"/> once the frontmatter is removed, trimmed — the
    /// style's actual instructions. Returns the whole body when there is no frontmatter.
    /// </summary>
    /// <remarks>
    /// Read-only, and the same text <see cref="ForPrompt"/> inlines. The write path asks for it to
    /// refuse a body that is frontmatter and nothing else; nothing here rewrites the body.
    /// </remarks>
    public static string BodyAfterFrontmatter(string body) => StripFrontmatter(body).Trim();

    /// <summary>True when <paramref name="body"/> opens with a closed YAML frontmatter block.</summary>
    public static bool HasFrontmatter(string body) => TryStripFrontmatter(body, out _);

    /// <summary>
    /// The <c>description:</c> line from the frontmatter, or null when absent. Operator-facing;
    /// nothing reads it at runtime.
    /// </summary>
    public static string? ReadDescription(string body) => ReadFrontmatterValue(body, "description");

    /// <summary>
    /// The value of one frontmatter key (<c>name</c>, <c>description</c>,
    /// <c>keep-coding-instructions</c>), or null when the key is absent or its value is empty.
    /// </summary>
    /// <remarks>
    /// This is the only frontmatter reader in the codebase. The write path validates against it
    /// rather than parsing the file format a second time — two parsers would be two answers to
    /// what a style says, and the one the operator sees would not be the one Claude Code obeys.
    /// </remarks>
    public static string? ReadFrontmatterValue(string body, string key)
    {
        var prefix = key + ":";
        foreach (var line in FrontmatterLines(body))
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            var value = trimmed[prefix.Length..].Trim().Trim('"', '\'');
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

    private static string StripFrontmatter(string body) =>
        TryStripFrontmatter(body, out var remainder) ? remainder : body;

    /// <summary>
    /// Splits a closed frontmatter block off the front of <paramref name="body"/>. False — and
    /// <paramref name="remainder"/> set to the whole body — when there is no opening delimiter or
    /// the block is never closed, so an unterminated block cannot swallow the style.
    /// </summary>
    private static bool TryStripFrontmatter(string body, out string remainder)
    {
        remainder = body;
        if (!StartsWithDelimiter(body, out var afterOpen)) return false;

        // The closing delimiter must be a line of its own, so a body containing "---" as a
        // horizontal rule mid-sentence cannot truncate the style.
        var rest = body[afterOpen..];
        var index = 0;
        while (index < rest.Length)
        {
            var lineEnd = rest.IndexOf('\n', index);
            var line = (lineEnd < 0 ? rest[index..] : rest[index..lineEnd]).TrimEnd('\r');

            if (line == Delimiter)
            {
                remainder = lineEnd < 0 ? "" : rest[(lineEnd + 1)..];
                return true;
            }

            if (lineEnd < 0) break;
            index = lineEnd + 1;
        }

        return false;
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
