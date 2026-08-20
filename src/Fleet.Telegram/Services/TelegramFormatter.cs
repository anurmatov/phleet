using System.Text;
using System.Text.RegularExpressions;

namespace Fleet.Telegram.Services;

/// <summary>
/// Converts a permissive Markdown-like subset to Telegram HTML, with automatic
/// escaping, format-aware splitting, and a plain-text fallback strip.
///
/// Recognized subset: **bold**, `inline code`, fenced code blocks, [label](url) links.
/// Anything unrecognized (including list syntax) is treated as literal escaped text.
///
/// Escape order is load-bearing: literal-text spans are escaped (&amp; &lt; &gt;) first;
/// recognized spans are then wrapped in Telegram HTML tags built from the already-escaped
/// inner content. Never escape after wrapping.
/// </summary>
public static class TelegramFormatter
{
    private const int TelegramMaxLength = 4096;

    // ─── Public API ──────────────────────────────────────────────────────────

    /// <summary>
    /// Converts <paramref name="text"/> to Telegram HTML chunks ready to send.
    /// Each chunk is ≤4096 UTF-16 code units and is independently valid HTML.
    /// Returns a single empty-string chunk for null/empty/whitespace input.
    /// </summary>
    public static List<string> FormatAndSplit(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [string.Empty];

        var html = ConvertToHtml(text);
        return SplitHtml(html);
    }

    /// <summary>
    /// Returns the text with all Markdown-like formatting stripped and HTML
    /// entities escaped — suitable for a plain-text (no parse_mode) send.
    /// </summary>
    public static string StripToPlain(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;
        return EscapeHtml(StripMarkdown(text));
    }

    /// <summary>
    /// Returns true when <paramref name="html"/> contains any formatting tokens
    /// (the caller can use this to decide whether to set parse_mode=Html).
    /// </summary>
    public static bool HasFormatting(string html) =>
        html.Contains('<') && (html.Contains("</b>") || html.Contains("</code>") ||
            html.Contains("</pre>") || html.Contains("</a>"));

    // ─── Conversion ──────────────────────────────────────────────────────────

    internal static string ConvertToHtml(string text)
    {
        var sb = new StringBuilder(text.Length + 64);
        int i = 0;
        int len = text.Length;

        while (i < len)
        {
            // Fenced code block: ```lang\n...\n```
            if (i + 2 < len && text[i] == '`' && text[i + 1] == '`' && text[i + 2] == '`')
            {
                int start = i + 3;
                // skip optional language tag on same line
                int langEnd = start;
                while (langEnd < len && text[langEnd] != '\n' && text[langEnd] != '`')
                    langEnd++;
                if (langEnd < len && text[langEnd] == '\n')
                    start = langEnd + 1;

                int closeIdx = IndexOfTripleBacktick(text, start);
                if (closeIdx >= 0)
                {
                    var content = EscapeHtml(text.Substring(start, closeIdx - start));
                    sb.Append("<pre>").Append(content).Append("</pre>");
                    i = closeIdx + 3;
                    // skip trailing newline
                    if (i < len && text[i] == '\n') i++;
                    continue;
                }
                // unmatched ``` — treat as literal
            }

            // Inline code: `...`
            if (text[i] == '`')
            {
                int closeIdx = text.IndexOf('`', i + 1);
                if (closeIdx > i)
                {
                    var content = EscapeHtml(text.Substring(i + 1, closeIdx - i - 1));
                    sb.Append("<code>").Append(content).Append("</code>");
                    i = closeIdx + 1;
                    continue;
                }
            }

            // Bold: **...**
            if (i + 1 < len && text[i] == '*' && text[i + 1] == '*')
            {
                int closeIdx = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (closeIdx > i)
                {
                    var content = EscapeHtml(text.Substring(i + 2, closeIdx - i - 2));
                    sb.Append("<b>").Append(content).Append("</b>");
                    i = closeIdx + 2;
                    continue;
                }
            }

            // Link: [label](url)
            if (text[i] == '[')
            {
                int labelClose = text.IndexOf(']', i + 1);
                if (labelClose > i && labelClose + 1 < len && text[labelClose + 1] == '(')
                {
                    int urlClose = text.IndexOf(')', labelClose + 2);
                    if (urlClose > labelClose)
                    {
                        var label = text.Substring(i + 1, labelClose - i - 1);
                        var url = text.Substring(labelClose + 2, urlClose - labelClose - 2).Trim();
                        if (IsValidUrl(url))
                        {
                            sb.Append("<a href=\"")
                              .Append(EscapeAttribute(url))
                              .Append("\">")
                              .Append(EscapeHtml(label))
                              .Append("</a>");
                            i = urlClose + 1;
                            continue;
                        }
                        // invalid URL — fall through as literal
                    }
                }
            }

            // Literal character — escape and emit
            char c = text[i];
            sb.Append(c switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                _ => c.ToString()
            });
            i++;
        }

        return sb.ToString();
    }

    // ─── Splitting ───────────────────────────────────────────────────────────

    internal static List<string> SplitHtml(string html)
    {
        if (html.Length <= TelegramMaxLength)
            return [html];

        var chunks = new List<string>();
        var remaining = html;

        while (remaining.Length > 0)
        {
            if (remaining.Length <= TelegramMaxLength)
            {
                chunks.Add(remaining);
                break;
            }

            // First pass: find split at full budget to learn which tag (if any) is open.
            int firstPass = FindSplitPoint(remaining, TelegramMaxLength);
            string openTag = GetOpenTagAt(remaining, firstPass);

            int closeTagLen = openTag.Length > 0 ? 3 + openTag.Length : 0; // "</" + name + ">"
            int reopenTagLen = openTag.Length > 0 ? 2 + openTag.Length : 0; // "<" + name + ">"

            // Second pass: tighten the budget so chunk + close tag ≤ 4096.
            int effectiveBudget = TelegramMaxLength - closeTagLen;
            int splitAt = closeTagLen > 0
                ? FindSplitPoint(remaining, effectiveBudget)
                : firstPass;

            // Re-check open tag at the tighter split point.
            openTag = GetOpenTagAt(remaining, splitAt);

            string chunk = openTag.Length > 0
                ? remaining[..splitAt] + "</" + openTag + ">"
                : remaining[..splitAt];

            chunks.Add(chunk);

            string tail = remaining[splitAt..];
            remaining = openTag.Length > 0
                ? "<" + openTag + ">" + tail
                : tail;
        }

        return chunks;
    }

    /// <summary>
    /// Finds the best split index at or before <paramref name="limit"/> characters.
    /// Prefers paragraph/line boundaries; backs off from tag/surrogate boundaries.
    /// </summary>
    private static int FindSplitPoint(string html, int limit)
    {
        // Ensure we never split a surrogate pair
        int candidate = limit;
        if (candidate < html.Length && char.IsLowSurrogate(html[candidate]))
            candidate--;

        // Try to find a line break within the last quarter of the budget
        int searchFrom = Math.Max(0, candidate - candidate / 4);
        int lastNewline = html.LastIndexOf('\n', candidate - 1, candidate - searchFrom);
        if (lastNewline > searchFrom)
        {
            // Check we're not inside a tag at this point
            if (!IsInsideTag(html, lastNewline + 1))
                return lastNewline + 1;
        }

        // Back off from any open tag
        while (candidate > 0 && IsInsideTag(html, candidate))
            candidate--;

        // Final surrogate check after backing off
        if (candidate < html.Length && char.IsLowSurrogate(html[candidate]) && candidate > 0)
            candidate--;

        return Math.Max(1, candidate);
    }

    /// <summary>Returns true when position <paramref name="pos"/> is inside an HTML tag.</summary>
    private static bool IsInsideTag(string html, int pos)
    {
        for (int i = pos - 1; i >= 0; i--)
        {
            if (html[i] == '>') return false;
            if (html[i] == '<') return true;
        }
        return false;
    }

    /// <summary>
    /// Returns the innermost open tag name at <paramref name="splitAt"/> that has not yet been closed,
    /// so the chunk can be closed and the next chunk reopened. Returns empty string if none.
    /// </summary>
    private static string GetOpenTagAt(string html, int splitAt)
    {
        var segment = html[..splitAt];
        var stack = new Stack<string>();

        int i = 0;
        while (i < segment.Length)
        {
            if (segment[i] != '<') { i++; continue; }

            int end = segment.IndexOf('>', i);
            if (end < 0) break;

            string tag = segment.Substring(i + 1, end - i - 1).Trim();
            if (tag.StartsWith('/'))
            {
                string name = tag[1..].Split(' ')[0].ToLowerInvariant();
                if (stack.Count > 0 && stack.Peek() == name)
                    stack.Pop();
            }
            else
            {
                string name = tag.Split(' ')[0].Split('>')[0].ToLowerInvariant();
                // Only track tags we emit: b, code, pre, a
                if (name is "b" or "code" or "pre" or "a")
                    stack.Push(name);
            }
            i = end + 1;
        }

        return stack.Count > 0 ? stack.Peek() : string.Empty;
    }

    // ─── Plain-text strip ────────────────────────────────────────────────────

    private static string StripMarkdown(string text)
    {
        // Remove fenced code blocks (keep content)
        text = Regex.Replace(text, @"```[^\n]*\n([\s\S]*?)```", "$1");
        // Remove inline code backticks
        text = Regex.Replace(text, @"`([^`]+)`", "$1");
        // Remove bold **
        text = Regex.Replace(text, @"\*\*(.+?)\*\*", "$1");
        // Convert [label](url) to label
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^\)]+\)", "$1");
        return text;
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static int IndexOfTripleBacktick(string text, int startFrom)
    {
        int idx = startFrom;
        while (idx < text.Length - 2)
        {
            idx = text.IndexOf('`', idx);
            if (idx < 0) return -1;
            if (idx + 2 < text.Length && text[idx + 1] == '`' && text[idx + 2] == '`')
                return idx;
            idx++;
        }
        return -1;
    }

    private static bool IsValidUrl(string url) =>
        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    internal static string EscapeHtml(string text)
    {
        if (text.Length == 0) return text;
        return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }

    private static string EscapeAttribute(string value) =>
        value.Replace("&", "&amp;").Replace("\"", "&quot;");
}
