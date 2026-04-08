using Fleet.Agent.Interfaces;

namespace Fleet.Agent.Tests;

public class AgentTransportHtmlTests
{
    // ── chunk-spanning blockquote ────────────────────────────────────────────

    [Fact]
    public void BalanceBlockquotesInChunk_UnclosedOpen_AppendsMissingCloser()
    {
        // Simulates chunk 1 when a blockquote opens but its closer lands in chunk 2
        var chunk = "<blockquote expandable>" + new string('x', 3977);
        var result = AgentTransport.BalanceBlockquotesInChunk(chunk);
        Assert.EndsWith("</blockquote>", result);
    }

    [Fact]
    public void BalanceBlockquotesInChunk_DanglingClose_StripsExcessCloser()
    {
        // Simulates chunk 2 when the opener was in chunk 1
        var chunk = new string('x', 100) + "</blockquote>";
        var result = AgentTransport.BalanceBlockquotesInChunk(chunk);
        Assert.DoesNotContain("</blockquote>", result, StringComparison.OrdinalIgnoreCase);
    }

    // ── short message (no split, must be unmodified) ─────────────────────────

    [Fact]
    public void BalanceBlockquotesInChunk_BalancedTags_ReturnsUnmodified()
    {
        var chunk = "<blockquote expandable>short content</blockquote>";
        var result = AgentTransport.BalanceBlockquotesInChunk(chunk);
        Assert.Equal(chunk, result);
    }

    // ── no blockquotes ───────────────────────────────────────────────────────

    [Fact]
    public void BalanceBlockquotesInChunk_NoBlockquotes_ReturnsUnmodified()
    {
        var chunk = "plain <b>html</b> message with no blockquotes";
        var result = AgentTransport.BalanceBlockquotesInChunk(chunk);
        Assert.Equal(chunk, result);
    }

    // ── multiple unclosed opens ───────────────────────────────────────────────

    [Fact]
    public void BalanceBlockquotesInChunk_MultipleUnclosedOpens_AppendsAllClosers()
    {
        var chunk = "<blockquote>outer<blockquote>inner";
        var result = AgentTransport.BalanceBlockquotesInChunk(chunk);
        var closeCount = CountOccurrences(result, "</blockquote>");
        Assert.Equal(2, closeCount);
    }

    private static int CountOccurrences(string text, string pattern)
    {
        var count = 0;
        var idx = 0;
        while ((idx = text.IndexOf(pattern, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            idx += pattern.Length;
        }
        return count;
    }
}
