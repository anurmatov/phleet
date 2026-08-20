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
    /// Returns the text with Markdown-like formatting stripped — suitable for a
    /// plain-text (no parse_mode) send. Does NOT HTML-escape, because plain-text
    /// sends have no parse_mode and entities would render literally.
    /// </summary>
    public static string StripToPlain(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;
        return StripMarkdown(text);
    }

    /// <summary>
    /// Returns true when <paramref name="html"/> contains any formatting tokens.
    /// Kept for backward compatibility; the send path now always uses Html parse_mode
    /// when the formatter produced the output.
    /// </summary>
    public static bool HasFormatting(string html) =>
        html.Contains('<') && (html.Contains("</b>") || html.Contains("</code>") ||
            html.Contains("</pre>") || html.Contains("</a>"));

    /// <summary>
    /// Returns true when <paramref name="html"/> is structurally valid for Telegram's
    /// HTML parser: all recognized tags are balanced and HTML entities are well-formed.
    /// Called as a pre-flight check before sending to avoid unnecessary Bot API rejections.
    /// </summary>
    internal static bool ValidateHtml(string html)
    {
        var stack = new Stack<string>(); // full open-tag content for each unclosed tag
        int i = 0;
        while (i < html.Length)
        {
            char c = html[i];

            if (c == '&')
            {
                // Only entities we emit: &lt; (4) &gt; (4) &amp; (5) &quot; (6)
                // Bounds: need to access html[i+N] so condition is i+N < html.Length
                if (i + 3 < html.Length && html[i + 1] == 'l' && html[i + 2] == 't' && html[i + 3] == ';') { i += 4; continue; }
                if (i + 3 < html.Length && html[i + 1] == 'g' && html[i + 2] == 't' && html[i + 3] == ';') { i += 4; continue; }
                if (i + 4 < html.Length && html[i + 1] == 'a' && html[i + 2] == 'm' && html[i + 3] == 'p' && html[i + 4] == ';') { i += 5; continue; }
                if (i + 5 < html.Length && html[i + 1] == 'q' && html[i + 2] == 'u' && html[i + 3] == 'o' && html[i + 4] == 't' && html[i + 5] == ';') { i += 6; continue; }
                return false; // unknown or unescaped ampersand
            }

            if (c == '<')
            {
                int end = html.IndexOf('>', i + 1);
                if (end < 0) return false; // unclosed tag bracket
                string tag = html.Substring(i + 1, end - i - 1).Trim();
                if (tag.StartsWith('/'))
                {
                    string name = tag[1..].Split(' ')[0].ToLowerInvariant();
                    if (stack.Count == 0 || TagName(stack.Peek()) != name) return false;
                    stack.Pop();
                }
                else
                {
                    string name = TagName(tag);
                    if (name is "b" or "code" or "pre" or "a")
                        stack.Push(tag);
                }
                i = end + 1;
                continue;
            }

            i++;
        }
        return stack.Count == 0;
    }

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
            int firstPassSplit = FindSplitPoint(remaining, TelegramMaxLength);
            string firstPassTag = GetOpenTagAt(remaining, firstPassSplit);
            string firstPassTagName = firstPassTag.Length > 0 ? TagName(firstPassTag) : string.Empty;

            // Close tag length is based on tag name only (e.g. "</a>" = 4 chars for tag name "a").
            int closeTagLen = firstPassTagName.Length > 0 ? 3 + firstPassTagName.Length : 0;

            // Second pass: tighten the budget so chunk + close tag ≤ 4096.
            int effectiveBudget = TelegramMaxLength - closeTagLen;
            int splitAt = closeTagLen > 0
                ? FindSplitPoint(remaining, effectiveBudget)
                : firstPassSplit;

            // Re-check open tag at the tighter split point (may differ from first pass).
            string openTag = GetOpenTagAt(remaining, splitAt);      // full content e.g. "a href=\"...\""
            string openTagName = openTag.Length > 0 ? TagName(openTag) : string.Empty;

            // Close with tag name only; reopen with full tag including attributes.
            string chunk = openTagName.Length > 0
                ? remaining[..splitAt] + "</" + openTagName + ">"
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
    /// Returns the full content of the innermost unclosed open tag at <paramref name="splitAt"/>
    /// (e.g. <c>a href="https://example.com"</c> for an anchor), so the chunk can be properly
    /// closed and the next chunk reopened with the identical tag including attributes.
    /// Returns empty string if no tag is open.
    /// </summary>
    private static string GetOpenTagAt(string html, int splitAt)
    {
        var segment = html[..splitAt];
        var stack = new Stack<string>(); // full tag content between < and >

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
                if (stack.Count > 0 && TagName(stack.Peek()) == name)
                    stack.Pop();
            }
            else
            {
                string name = TagName(tag);
                // Only track tags we emit: b, code, pre, a
                if (name is "b" or "code" or "pre" or "a")
                    stack.Push(tag); // push FULL tag content, preserving attributes
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

    /// <summary>Extracts the tag name (first word, lowercased) from full tag content.</summary>
    private static string TagName(string openTag) =>
        openTag.Split(' ')[0].ToLowerInvariant();

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
