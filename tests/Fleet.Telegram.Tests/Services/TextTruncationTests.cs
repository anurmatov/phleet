using Fleet.Shared;

namespace Fleet.Telegram.Tests.Services;

/// <summary>
/// Tests for the shared surrogate-safe cut helper.
///
/// This lives beside the formatter tests because <c>TelegramFormatter.SplitPlain</c> was the
/// original home of the guard; it is now shared with the consensus workflow's caps, so there is
/// exactly one definition of "where may I cut" rather than a copy per call site.
/// </summary>
public class TextTruncationTests
{
    [Fact]
    public void NegativeRequest_Throws()
    {
        // A negative budget is a caller bug. Clamping it to zero would hide the miscalculation
        // and silently return an empty string.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TextTruncation.SafeCutIndex("hello", -1));
    }

    [Fact]
    public void RequestBeyondLength_ClampsToLength()
    {
        Assert.Equal(5, TextTruncation.SafeCutIndex("hello", 99));
    }

    [Fact]
    public void ZeroRequest_ReturnsZero()
    {
        Assert.Equal(0, TextTruncation.SafeCutIndex("hello", 0));
    }

    [Fact]
    public void EmptySpan_ReturnsZero()
    {
        Assert.Equal(0, TextTruncation.SafeCutIndex("", 3));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(5)]
    public void BmpText_CutsExactlyWhereAsked(int cut)
    {
        Assert.Equal(cut, TextTruncation.SafeCutIndex("hello", cut));
    }

    [Fact]
    public void CutInsideSurrogatePair_MovesBackOne()
    {
        // "ab" + one astral character (2 UTF-16 code units) + "cd".
        const string text = "ab\U0001F600cd";
        Assert.Equal(6, text.Length);

        // Index 3 is the LOW surrogate — cutting there would leave a lone high surrogate.
        Assert.True(char.IsLowSurrogate(text[3]));
        Assert.Equal(2, TextTruncation.SafeCutIndex(text, 3));

        // Index 2 is the high surrogate and is already a valid boundary (before the pair).
        Assert.Equal(2, TextTruncation.SafeCutIndex(text, 2));

        // Index 4 is after the complete pair — also valid.
        Assert.Equal(4, TextTruncation.SafeCutIndex(text, 4));
    }

    [Fact]
    public void CutResult_NeverLeavesALoneSurrogate()
    {
        // Sweep every cut position across a string dense with astral characters and assert the
        // prefix is always well-formed. One backward step is enough because a low surrogate is
        // by definition preceded by its high surrogate.
        var text = string.Concat(Enumerable.Repeat("x\U0001F600", 20));

        for (var i = 0; i <= text.Length; i++)
        {
            var cut = TextTruncation.SafeCutIndex(text, i);
            var prefix = text[..cut];

            Assert.True(cut <= i, $"cut {cut} must not exceed request {i}");
            if (prefix.Length > 0)
                Assert.False(char.IsHighSurrogate(prefix[^1]),
                    $"cut at {cut} left a dangling high surrogate");
        }
    }

    [Fact]
    public void SplitPlain_DoesNotSplitASurrogatePairAtTheChunkBoundary()
    {
        // Regression guard on the caller: SplitPlain now delegates to SafeCutIndex rather than
        // carrying its own copy of the check. Build a string whose 4096-code-unit boundary lands
        // inside a pair, with no newline anywhere so the newline-preference branch cannot move
        // the cut and mask the surrogate case.
        var text = new string('a', 4095) + "\U0001F600" + new string('b', 4095);

        var chunks = TelegramFormatter.SplitPlain(text);

        Assert.True(chunks.Count > 1);
        foreach (var chunk in chunks)
        {
            Assert.False(char.IsHighSurrogate(chunk[^1]), "chunk ends on a dangling high surrogate");
            Assert.False(char.IsLowSurrogate(chunk[0]), "chunk starts on a dangling low surrogate");
        }
        Assert.Equal(text, string.Concat(chunks));
    }
}
