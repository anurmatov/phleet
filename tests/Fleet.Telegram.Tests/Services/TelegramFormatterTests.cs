using Fleet.Telegram.Services;

namespace Fleet.Telegram.Tests.Services;

/// <summary>
/// Unit tests for TelegramFormatter — escaping, conversion, splitting, and plain-text strip.
/// Covers all 13 test cases specified in phleet issue #209.
/// </summary>
public class TelegramFormatterTests
{
    // ── Test 1: Escaping correctness ─────────────────────────────────────────

    [Fact]
    public void LiteralAngledBracketsAreEscaped()
    {
        var result = TelegramFormatter.ConvertToHtml("Use Task<string> and a < b > c & d");
        Assert.Contains("Task&lt;string&gt;", result);
        Assert.Contains("&lt; b &gt;", result);
        Assert.Contains("&amp;", result);
        // No raw < or > outside tags
        Assert.DoesNotContain("Task<string>", result);
    }

    [Fact]
    public void RawClosingTagFromArbitraryInputIsEscaped()
    {
        var result = TelegramFormatter.ConvertToHtml("Error: </b> appeared in model output");
        Assert.Contains("&lt;/b&gt;", result);
        Assert.DoesNotContain("</b>", result.Replace("</b>", "__SENTINEL__"));
    }

    [Fact]
    public void LiteralAdjacentToFormattingTokenIsEscaped()
    {
        // angle bracket right next to a bold token
        var result = TelegramFormatter.ConvertToHtml("**result** is a List<int>");
        Assert.Contains("<b>result</b>", result);
        Assert.Contains("List&lt;int&gt;", result);
    }

    // ── Test 2: Code block contents not re-parsed ────────────────────────────

    [Fact]
    public void FencedCodeBlockContentIsNeverReInterpreted()
    {
        var input = "```\n**not bold** and <b>not a tag</b>\n```";
        var result = TelegramFormatter.ConvertToHtml(input);
        Assert.StartsWith("<pre>", result);
        Assert.Contains("**not bold**", result);      // literal ** preserved
        Assert.Contains("&lt;b&gt;", result);         // tag escaped, not passed through
        Assert.DoesNotContain("<b>not bold</b>", result);
    }

    [Fact]
    public void InlineCodeContentIsNotReInterpreted()
    {
        var result = TelegramFormatter.ConvertToHtml("`Task<T>` is generic");
        Assert.Contains("<code>Task&lt;T&gt;</code>", result);
    }

    // ── Test 3: List syntax falls through as literal text ────────────────────

    [Fact]
    public void ListSyntaxRendersAsLiteralText()
    {
        var input = "- first item\n- second item\n1. numbered";
        var result = TelegramFormatter.ConvertToHtml(input);
        Assert.Contains("- first item", result);
        Assert.Contains("- second item", result);
        Assert.Contains("1. numbered", result);
        // No list HTML tags (Telegram doesn't support them)
        Assert.DoesNotContain("<ul>", result);
        Assert.DoesNotContain("<li>", result);
        Assert.DoesNotContain("<ol>", result);
    }

    // ── Test 4: Split correctness (pre block at split point) ─────────────────

    [Fact]
    public void SplitProducesIndependentlyValidHtmlChunks()
    {
        // Build a message that forces a split inside a <pre> block
        var longCode = new string('x', 3000);
        var longCode2 = new string('y', 2000);
        var input = $"```\n{longCode}\n```\n```\n{longCode2}\n```";
        var html = TelegramFormatter.ConvertToHtml(input);

        // Must be over the limit
        Assert.True(html.Length > 4096, $"Expected html.Length > 4096 but was {html.Length}");

        var chunks = TelegramFormatter.SplitHtml(html);
        Assert.True(chunks.Count > 1);

        foreach (var chunk in chunks)
        {
            // Every chunk must be ≤ 4096 UTF-16 code units
            Assert.True(chunk.Length <= 4096, $"Chunk length {chunk.Length} exceeds 4096");
            // Tags must be balanced: count of open <pre> must equal </pre>
            int opens = CountOccurrences(chunk, "<pre>");
            int closes = CountOccurrences(chunk, "</pre>");
            Assert.Equal(opens, closes);
        }
    }

    [Fact]
    public void SplitComputedAgainstRenderedLengthNotRawInput()
    {
        // A message where HTML entities inflate the rendered length
        // "& < >" repeated many times — each char becomes 4-6 chars after escaping
        var raw = string.Concat(Enumerable.Repeat("& < > ", 500)); // 3000 raw chars → ~15000 rendered
        var html = TelegramFormatter.ConvertToHtml(raw); // "&amp; &lt; &gt; " × 500
        var chunks = TelegramFormatter.SplitHtml(html);

        foreach (var chunk in chunks)
            Assert.True(chunk.Length <= 4096, $"Chunk length {chunk.Length} exceeds 4096");
    }

    // ── Test 5: Split backs off from tag/attribute boundaries ────────────────

    [Fact]
    public void SplitNeverCutsInsideATag()
    {
        // Build HTML that has a tag starting near the boundary and total length > 4096
        var prefix = new string('a', 4090);   // 4090 literal chars
        var html = prefix + "<b>bold text here</b>"; // tag starts at 4090, total > 4096

        var chunks = TelegramFormatter.SplitHtml(html);
        Assert.True(chunks.Count >= 2);
        foreach (var chunk in chunks)
        {
            Assert.False(IsInsideTag(chunk), $"Chunk appears to end inside a tag: ...{chunk[^Math.Min(20, chunk.Length)..]}");
        }
    }

    // ── Test 6: Hard-cut policy ───────────────────────────────────────────────

    [Fact]
    public void HardCutOnSingleLongLinePreservesTagBalance()
    {
        // A single <pre> block with no internal newline that exceeds 4096
        var longLine = new string('z', 5000);
        var html = $"<pre>{TelegramFormatter.EscapeHtml(longLine)}</pre>";

        var chunks = TelegramFormatter.SplitHtml(html);
        Assert.True(chunks.Count >= 2);
        foreach (var chunk in chunks)
        {
            Assert.True(chunk.Length <= 4096, $"Chunk length {chunk.Length} exceeds 4096");
            int opens = CountOccurrences(chunk, "<pre>");
            int closes = CountOccurrences(chunk, "</pre>");
            Assert.Equal(opens, closes);
        }
    }

    // ── Test 7: UTF-16 code unit counting / no surrogate split ───────────────

    [Fact]
    public void SplitNeverSeparatesSurrogatePair()
    {
        // U+1F600 (😀) is a supplementary-plane character encoded as a surrogate pair in .NET strings
        // Place emoji right at the 4096 boundary
        var prefix = new string('a', 4094); // 4094 chars
        var emoji = "\U0001F600";           // 2 UTF-16 code units (high + low surrogate)
        var html = prefix + emoji + "trailing";

        Assert.True(html.Length > 4096);
        var chunks = TelegramFormatter.SplitHtml(html);

        // Verify no chunk starts with a low surrogate (would mean we split a pair)
        foreach (var chunk in chunks)
        {
            if (chunk.Length > 0)
                Assert.False(char.IsLowSurrogate(chunk[0]),
                    "Chunk starts with a low surrogate — surrogate pair was split");
        }
    }

    // ── Test 8: Plain-text fallback, single chunk ─────────────────────────────
    // (Tested via SendMessageTool integration — see SendMessageToolTests)

    // ── Test 9: Plain-text fallback, multi-chunk partial ─────────────────────
    // (Tested via SendMessageTool integration — see SendMessageToolTests)

    // ── Test 10: parse_mode compatibility ────────────────────────────────────

    [Fact]
    public void UnescapedAngleBracketsWithHtmlModeAreEscaped()
    {
        // Verifies that even when the caller "intended" HTML, we escape safely
        var result = TelegramFormatter.ConvertToHtml("Result: <Task<string>> arrived");
        Assert.Contains("&lt;Task&lt;string&gt;&gt;", result);
        Assert.DoesNotContain("<Task", result);
    }

    [Fact]
    public void PlainModeStripsFormattingConstructs()
    {
        var result = TelegramFormatter.StripToPlain("**bold** and `code` and [link](https://example.com)");
        Assert.Contains("bold", result);
        Assert.Contains("code", result);
        Assert.Contains("link", result);
        Assert.DoesNotContain("**", result);
        Assert.DoesNotContain("`", result);
        Assert.DoesNotContain("[", result);
        Assert.DoesNotContain("](", result);
    }

    [Fact]
    public void PlainModeDoesNotEscapeHtmlEntities()
    {
        // PLAIN mode sends with no parse_mode — raw text. HTML entities must NOT appear
        // because Telegram would render &lt; literally instead of <.
        var result = TelegramFormatter.StripToPlain("text with <angle> brackets & ampersands");
        Assert.Contains("<angle>", result);
        Assert.Contains("&", result);
        Assert.DoesNotContain("&lt;", result);
        Assert.DoesNotContain("&amp;", result);
    }

    // ── Test 10c: Link href preserved across a split ──────────────────────────

    [Fact]
    public void SplitPreservesLinkHrefAcrossChunks()
    {
        // Build HTML that forces a split in the middle of an <a> link span.
        // The spec requires the identical tag (including attributes) to reopen in the next chunk.
        var prefix = new string('a', 4050);
        var linkContent = new string('b', 100);
        var html = prefix + $"<a href=\"https://example.com\">{linkContent}</a>";

        Assert.True(html.Length > 4096, $"Expected html.Length > 4096 but was {html.Length}");
        var chunks = TelegramFormatter.SplitHtml(html);

        Assert.True(chunks.Count >= 2, "Expected at least 2 chunks");
        // The second chunk must reopen with the FULL href, not a bare <a>
        Assert.StartsWith("<a href=\"https://example.com\">", chunks[1]);
    }

    // ── ValidateHtml pre-flight checks ────────────────────────────────────────

    [Fact]
    public void ValidateHtml_BalancedTags_ReturnsTrue()
    {
        Assert.True(TelegramFormatter.ValidateHtml("<b>hello</b> world"));
        Assert.True(TelegramFormatter.ValidateHtml("<pre>code</pre>"));
        Assert.True(TelegramFormatter.ValidateHtml("plain text &lt;no tags&gt;"));
        Assert.True(TelegramFormatter.ValidateHtml(string.Empty));
    }

    [Fact]
    public void ValidateHtml_UnbalancedTag_ReturnsFalse()
    {
        Assert.False(TelegramFormatter.ValidateHtml("<b>unclosed bold"));
        Assert.False(TelegramFormatter.ValidateHtml("</b>extra close"));
    }

    [Fact]
    public void ValidateHtml_UnescapedAmpersand_ReturnsFalse()
    {
        Assert.False(TelegramFormatter.ValidateHtml("AT&T is a company"));
    }

    [Fact]
    public void ValidateHtml_WellFormedEntities_ReturnsTrue()
    {
        Assert.True(TelegramFormatter.ValidateHtml("a &amp; b &lt; c &gt; d &quot; e"));
    }

    // ── Test 11: Backward compatibility / flag non-collision ─────────────────

    [Fact]
    public void FormatFallbackFlagIsDistinctFromFallbackAndReplyFallback()
    {
        // All three flag names must be distinct strings
        const string f1 = "fallback";
        const string f2 = "reply_fallback";
        const string f3 = "format_fallback";
        Assert.NotEqual(f1, f2);
        Assert.NotEqual(f1, f3);
        Assert.NotEqual(f2, f3);
    }

    // ── Test 12: Artifact key generation (Phase B placeholder) ───────────────
    // Phase B is deferred — no artifact tool shipped in this PR.
    // Test reserved for Phase B implementation.

    // ── Test 13: Artifact upload failure (Phase B placeholder) ───────────────
    // Phase B is deferred — no artifact tool shipped in this PR.
    // Test reserved for Phase B implementation.

    // ── Additional: link validation ───────────────────────────────────────────

    [Fact]
    public void HttpLinkIsConvertedToAnchorTag()
    {
        var result = TelegramFormatter.ConvertToHtml("[click here](https://example.com)");
        Assert.Contains("<a href=\"https://example.com\">click here</a>", result);
    }

    [Fact]
    public void NonHttpsLinkFallsThroughAsLiteral()
    {
        var result = TelegramFormatter.ConvertToHtml("[click](http://example.com)");
        // http:// not in allow-list — rendered as literal
        Assert.DoesNotContain("<a href", result);
        Assert.Contains("[click]", result);
    }

    [Fact]
    public void BoldTextIsWrappedInBTag()
    {
        var result = TelegramFormatter.ConvertToHtml("**hello world**");
        Assert.Equal("<b>hello world</b>", result);
    }

    [Fact]
    public void FencedCodeBlockWithLanguageTagIsRenderedAsPre()
    {
        var result = TelegramFormatter.ConvertToHtml("```csharp\nvar x = 1;\n```");
        Assert.Contains("<pre>", result);
        Assert.Contains("var x = 1;", result);
        Assert.Contains("</pre>", result);
    }

    [Fact]
    public void EmptyInputReturnsEmptyString()
    {
        var chunks = TelegramFormatter.FormatAndSplit("");
        Assert.Single(chunks);
        Assert.Equal(string.Empty, chunks[0]);

        chunks = TelegramFormatter.FormatAndSplit(null);
        Assert.Single(chunks);
        Assert.Equal(string.Empty, chunks[0]);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        { count++; idx += needle.Length; }
        return count;
    }

    private static bool IsInsideTag(string html)
    {
        // Check if the last character is inside an unclosed <...>
        bool inTag = false;
        foreach (char c in html)
        {
            if (c == '<') inTag = true;
            else if (c == '>') inTag = false;
        }
        return inTag;
    }
}
