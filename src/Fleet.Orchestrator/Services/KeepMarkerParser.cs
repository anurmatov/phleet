using System.Text.RegularExpressions;

namespace Fleet.Orchestrator.Services;

/// <summary>Result of <see cref="KeepMarkerParser.Parse"/>.</summary>
/// <param name="Slugs">Valid marker slugs, de-duplicated ordinally, in first-occurrence order.</param>
/// <param name="Invalid">
/// Each candidate that is not the start of a valid marker, as a snippet of at most
/// <see cref="KeepMarkerParser.MaxSnippetLength"/> characters, in occurrence order.
/// </param>
public sealed record KeepMarkerScan(IReadOnlyList<string> Slugs, IReadOnlyList<string> Invalid)
{
    public bool HasInvalid => Invalid.Count > 0;
}

/// <summary>
/// The single definition of <c>keep</c> markers in project contexts and cards. Every consumer —
/// the card-write gate, full-write validation, <c>missingKeeps</c>, provisioning and the REST/MCP
/// displays — calls this parser, so they can never disagree on an edge case.
/// </summary>
/// <remarks>
/// <para>
/// The parser runs over raw text and is deliberately not markdown-aware: a marker inside a fenced
/// code block is a marker. To show one in prose without declaring it, write it so it fails the
/// candidate pattern, e.g. <c>&amp;lt;!-- keep:x --&amp;gt;</c>.
/// </para>
/// <list type="bullet">
/// <item><b>Candidate</b> — every match of <c>&lt;!--[ \t]*keep[ \t]*:</c>, case-insensitive.</item>
/// <item><b>Valid</b> — the candidate starts a whole-comment match of
/// <c>&lt;!--[ \t]*keep:([a-z0-9][a-z0-9-]{0,63})[ \t]*--&gt;</c> (case-sensitive, single line):
/// the comment holds the marker and nothing else.</item>
/// <item><b>Invalid</b> — any other candidate: uppercase or illegal slug, missing slug, extra
/// text, <c>keep :</c>, spanning lines, unterminated.</item>
/// </list>
/// </remarks>
public static class KeepMarkerParser
{
    public const int MaxSnippetLength = 80;

    private static readonly Regex Candidate = new(
        @"<!--[ \t]*keep[ \t]*:",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // \G anchors the match at the candidate's start; [ \t] and the slug class cannot cross a line.
    private static readonly Regex ValidAtCandidate = new(
        @"\G<!--[ \t]*keep:([a-z0-9][a-z0-9-]{0,63})[ \t]*-->",
        RegexOptions.CultureInvariant);

    public static KeepMarkerScan Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return new KeepMarkerScan([], []);

        var slugs = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var invalid = new List<string>();

        foreach (Match candidate in Candidate.Matches(text))
        {
            var valid = ValidAtCandidate.Match(text, candidate.Index);
            if (valid.Success)
            {
                var slug = valid.Groups[1].Value;
                if (seen.Add(slug))
                    slugs.Add(slug);
            }
            else
            {
                invalid.Add(Snippet(text, candidate.Index));
            }
        }

        return new KeepMarkerScan(slugs, invalid);
    }

    /// <summary>
    /// Valid slugs of <paramref name="fullContent"/> that do not appear as a valid marker in
    /// <paramref name="cardContent"/>, in the full context's first-occurrence order. Presence only:
    /// the text around a marker is never compared, and extra card slugs are allowed.
    /// </summary>
    public static IReadOnlyList<string> Missing(string? fullContent, string? cardContent)
    {
        var cardSlugs = new HashSet<string>(Parse(cardContent).Slugs, StringComparer.Ordinal);
        return Parse(fullContent).Slugs.Where(s => !cardSlugs.Contains(s)).ToList();
    }

    /// <summary>One-line summary of invalid candidates for an error message.</summary>
    public static string DescribeInvalid(IReadOnlyList<string> invalid) =>
        $"invalid keep marker(s): {string.Join(", ", invalid.Select(i => $"'{i}'"))}. " +
        "A keep marker must be a whole comment on one line: <!-- keep:slug --> " +
        "where slug matches [a-z0-9][a-z0-9-]{0,63}.";

    private static string Snippet(string text, int start)
    {
        var lineEnd = text.IndexOfAny(['\r', '\n'], start);
        var end = lineEnd < 0 ? text.Length : lineEnd;
        var close = text.IndexOf("-->", start, end - start, StringComparison.Ordinal);
        if (close >= 0)
            end = close + 3;
        var length = Math.Min(end - start, MaxSnippetLength);
        return text.Substring(start, length);
    }
}
