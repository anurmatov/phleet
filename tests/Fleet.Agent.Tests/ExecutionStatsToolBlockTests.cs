using System.Net;
using Fleet.Agent.Models;

namespace Fleet.Agent.Tests;

/// <summary>
/// The tool list under a turn's answer (<see cref="ExecutionStats.FormatToolBlock"/>): capped at
/// 2000 characters of HTML, cut between two entries, and never inside an encoded character.
/// </summary>
public class ExecutionStatsToolBlockTests
{
    private const string Open = "\n<blockquote expandable>";
    private const string Close = "</blockquote>";

    private static string Block(params ToolCallEntry[] calls) =>
        new ExecutionStats { ToolCalls = [.. calls] }.FormatToolBlock();

    private static string Body(string block)
    {
        Assert.StartsWith(Open, block, StringComparison.Ordinal);
        Assert.EndsWith(Close, block, StringComparison.Ordinal);
        return block[Open.Length..^Close.Length];
    }

    [Fact]
    public void NoToolCalls_IsNoBlock()
    {
        Assert.Equal("", new ExecutionStats().FormatToolBlock());
        Assert.Equal("", new ExecutionStats { ToolCalls = [] }.FormatToolBlock());
    }

    [Fact]
    public void UnderTheCap_EveryEntryIsALine_Encoded()
    {
        var block = Block(new ToolCallEntry("Read", "{\"file_path\":\"/workspace/example/a.txt\"}"), new ToolCallEntry("shell", "a && b < c"));

        Assert.Equal(
            Open + "Read({&quot;file_path&quot;:&quot;/workspace/example/a.txt&quot;})\nshell(a &amp;&amp; b &lt; c)" + Close,
            block);
    }

    /// <summary>
    /// The cut used to be made at character 2000 of the encoded text, wherever that fell: in the
    /// middle of an entry, and often in the middle of an entity such as <c>&amp;quot;</c>, which
    /// Telegram then shows as its raw remainder.
    /// </summary>
    [Fact]
    public void OverTheCap_TheCutFallsBetweenEntries_AndNoEncodedCharacterIsSplit()
    {
        // Every entry is quotes only, so each character of its arguments is a six-character entity
        // and a cut at any fixed offset is almost certain to land inside one.
        var calls = Enumerable.Range(1, 12).Select(i => new ToolCallEntry($"tool{i}", new string('"', 37))).ToArray();

        var body = Body(Block(calls));

        var lines = body.Split('\n');
        Assert.Equal("...", lines[^1]);
        var shown = lines[..^1];
        Assert.InRange(shown.Length, 1, calls.Length - 1);
        Assert.Equal(
            calls.Take(shown.Length).Select(c => $"{c.Name}({c.Args})"),
            shown.Select(WebUtility.HtmlDecode));
        Assert.True(string.Join("\n", shown).Length <= 2000);
        // The next entry is left out because it does not fit, not because the list was cut early.
        var next = WebUtility.HtmlEncode($"{calls[shown.Length].Name}({calls[shown.Length].Args})");
        Assert.True(string.Join("\n", shown).Length + 1 + next.Length > 2000);
    }

    [Fact]
    public void AnEntryThatExactlyFillsTheCap_IsShownWhole_WithoutAMark()
    {
        var args = new string('a', 2000 - "tool()".Length);

        Assert.Equal($"tool({args})", Body(Block(new ToolCallEntry("tool", args))));
    }

    /// <summary>
    /// One entry larger than the cap cannot be left out whole: nothing would be shown. It is cut,
    /// in its text and before encoding, so what is shown still decodes to a prefix of the entry.
    /// </summary>
    [Fact]
    public void AFirstEntryLargerThanTheCap_IsCutInItsText_BeforeEncoding()
    {
        var args = string.Concat(Enumerable.Repeat("<\"&'>😀", 400));

        var body = Body(Block(new ToolCallEntry("write_file", args), new ToolCallEntry("Read", "{}")));

        Assert.EndsWith("...", body, StringComparison.Ordinal);
        var encoded = body[..^3];
        Assert.InRange(encoded.Length, 1900, 2000);
        var decoded = WebUtility.HtmlDecode(encoded);
        Assert.StartsWith(decoded, $"write_file({args})", StringComparison.Ordinal);
        Assert.Equal(encoded, WebUtility.HtmlEncode(decoded));
        Assert.DoesNotContain("Read(", body, StringComparison.Ordinal);
    }
}
