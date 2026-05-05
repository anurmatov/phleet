using Fleet.Agent.Interfaces;

namespace Fleet.Agent.Tests;

public class ReplyToTokenTests
{
    // ── token at start ───────────────────────────────────────────────────────

    [Fact]
    public void ExtractReplyToToken_AtStart_ExtractsAndStrips()
    {
        var (text, id) = AgentTransport.ExtractReplyToToken("[reply_to: 42] Hello world");
        Assert.Equal("Hello world", text);
        Assert.Equal(42, id);
    }

    [Fact]
    public void ExtractReplyToToken_AtEnd_ExtractsAndStrips()
    {
        var (text, id) = AgentTransport.ExtractReplyToToken("Hello world [reply_to: 99]");
        Assert.Equal("Hello world", text);
        Assert.Equal(99, id);
    }

    [Fact]
    public void ExtractReplyToToken_MidText_NotUsedAsReplyTarget()
    {
        var (text, id) = AgentTransport.ExtractReplyToToken("Hello [reply_to: 55] world");
        // Token is stripped (text may have internal whitespace) but not used as reply target
        Assert.Contains("Hello", text);
        Assert.Contains("world", text);
        Assert.DoesNotContain("[reply_to:", text);
        Assert.Null(id);
    }

    [Fact]
    public void ExtractReplyToToken_NoToken_ReturnsOriginal()
    {
        var (text, id) = AgentTransport.ExtractReplyToToken("Hello world");
        Assert.Equal("Hello world", text);
        Assert.Null(id);
    }

    [Fact]
    public void ExtractReplyToToken_WithSpaces_AtStart()
    {
        var (text, id) = AgentTransport.ExtractReplyToToken("  [reply_to: 7] Some text");
        Assert.Equal("Some text", text);
        Assert.Equal(7, id);
    }

    // ── invalid message IDs ──────────────────────────────────────────────────

    [Fact]
    public void ExtractReplyToToken_ZeroId_NotUsedAsReplyTarget()
    {
        var (text, id) = AgentTransport.ExtractReplyToToken("[reply_to: 0] Hello");
        Assert.Equal("Hello", text);
        Assert.Null(id);
    }

    [Fact]
    public void ExtractReplyToToken_NegativeId_NotUsedAsReplyTarget()
    {
        var (text, id) = AgentTransport.ExtractReplyToToken("[reply_to: -5] Hello");
        Assert.Equal("Hello", text);
        Assert.Null(id);
    }

    // ── multiple tokens ──────────────────────────────────────────────────────

    [Fact]
    public void ExtractReplyToToken_MultipleTokens_FirstUsedRestStripped()
    {
        var (text, id) = AgentTransport.ExtractReplyToToken("[reply_to: 10] Hi [reply_to: 20]");
        // First token is at start → use it; both stripped
        Assert.Equal("Hi", text.Trim());
        Assert.Equal(10, id);
    }

    [Fact]
    public void ExtractReplyToToken_OnlyToken_EmptyTextAfterStrip()
    {
        var (text, id) = AgentTransport.ExtractReplyToToken("[reply_to: 42]");
        Assert.Equal(string.Empty, text);
        Assert.Equal(42, id);
    }

    // ── whitespace variants ──────────────────────────────────────────────────

    [Fact]
    public void ExtractReplyToToken_NoSpaceInsideToken()
    {
        var (text, id) = AgentTransport.ExtractReplyToToken("[reply_to:123] Go");
        Assert.Equal("Go", text);
        Assert.Equal(123, id);
    }
}
