using Fleet.Orchestrator.Services;

namespace Fleet.Orchestrator.Tests.Services;

public class KeepMarkerParserTests
{
    [Theory]
    [InlineData("<!-- keep:rule-a -->", "rule-a")]
    [InlineData("<!--keep:rule-a-->", "rule-a")]
    [InlineData("<!--\tkeep:x1 \t-->", "x1")]
    [InlineData("text before <!-- keep:a --> text after", "a")]
    public void Valid_marker_yields_slug(string text, string slug)
    {
        var scan = KeepMarkerParser.Parse(text);
        Assert.Equal([slug], scan.Slugs);
        Assert.Empty(scan.Invalid);
    }

    [Theory]
    [InlineData("<!-- keep:Bad -->")]                 // uppercase slug
    [InlineData("<!-- KEEP:bad -->")]                 // uppercase keyword
    [InlineData("<!-- keep:-bad -->")]                // slug must start alphanumeric
    [InlineData("<!-- keep:bad_slug -->")]            // illegal character
    [InlineData("<!-- keep: -->")]                    // missing slug
    [InlineData("<!-- keep :x -->")]                  // space before the colon
    [InlineData("<!-- keep:x extra text -->")]        // extra text in the comment
    [InlineData("<!-- keep:x\n-->")]                  // spans lines
    [InlineData("<!-- keep:x")]                       // unterminated
    public void Invalid_candidate_is_reported_and_yields_no_slug(string text)
    {
        var scan = KeepMarkerParser.Parse(text);
        Assert.Empty(scan.Slugs);
        Assert.Single(scan.Invalid);
        Assert.True(scan.HasInvalid);
    }

    [Fact]
    public void Slug_of_64_chars_is_valid_and_65_is_invalid()
    {
        var ok = new string('a', 64);
        var tooLong = new string('a', 65);

        Assert.Equal([ok], KeepMarkerParser.Parse($"<!-- keep:{ok} -->").Slugs);

        var scan = KeepMarkerParser.Parse($"<!-- keep:{tooLong} -->");
        Assert.Empty(scan.Slugs);
        Assert.Single(scan.Invalid);
    }

    [Fact]
    public void Marker_inside_a_fenced_code_block_counts()
    {
        var text = "prose\n```\n<!-- keep:fenced -->\n```\n";
        Assert.Equal(["fenced"], KeepMarkerParser.Parse(text).Slugs);
    }

    [Fact]
    public void Duplicates_are_deduplicated_in_first_occurrence_order()
    {
        var text = "<!-- keep:b --> <!-- keep:a --> <!-- keep:b -->";
        Assert.Equal(["b", "a"], KeepMarkerParser.Parse(text).Slugs);
    }

    [Fact]
    public void Html_escaped_example_is_not_a_candidate()
    {
        var scan = KeepMarkerParser.Parse("write &lt;!-- keep:x --&gt; to declare one");
        Assert.Empty(scan.Slugs);
        Assert.Empty(scan.Invalid);
    }

    [Fact]
    public void Ordinary_comment_is_not_a_candidate()
    {
        var scan = KeepMarkerParser.Parse("<!-- a note about keeping things -->");
        Assert.Empty(scan.Slugs);
        Assert.Empty(scan.Invalid);
    }

    [Fact]
    public void Invalid_snippet_is_capped_at_80_chars_and_stops_at_the_line_end()
    {
        var text = "<!-- keep:x " + new string('y', 200) + " -->";
        var snippet = Assert.Single(KeepMarkerParser.Parse(text).Invalid);
        Assert.Equal(KeepMarkerParser.MaxSnippetLength, snippet.Length);

        var multi = Assert.Single(KeepMarkerParser.Parse("<!-- keep:x more\nnext line -->").Invalid);
        Assert.Equal("<!-- keep:x more", multi);
    }

    [Fact]
    public void Valid_and_invalid_markers_are_reported_together()
    {
        var scan = KeepMarkerParser.Parse("<!-- keep:good --> and <!-- keep:Bad -->");
        Assert.Equal(["good"], scan.Slugs);
        Assert.Equal(["<!-- keep:Bad -->"], scan.Invalid);
    }

    [Fact]
    public void Missing_checks_presence_only_not_surrounding_text()
    {
        var full = "Rule one.\n<!-- keep:one -->\nRule two.\n<!-- keep:two -->";
        var card = "Condensed.\n<!-- keep:two --> different wording <!-- keep:one -->";
        Assert.Empty(KeepMarkerParser.Missing(full, card));
    }

    [Fact]
    public void Missing_lists_absent_slugs_and_allows_extra_card_slugs()
    {
        var full = "<!-- keep:one --> <!-- keep:two -->";
        var card = "<!-- keep:two --> <!-- keep:extra -->";
        Assert.Equal(["one"], KeepMarkerParser.Missing(full, card));
    }

    [Fact]
    public void Invalid_card_marker_does_not_satisfy_a_full_slug()
    {
        var full = "<!-- keep:one -->";
        var card = "<!-- keep:one extra -->";
        Assert.Equal(["one"], KeepMarkerParser.Missing(full, card));
    }

    [Fact]
    public void Empty_or_null_text_has_no_markers()
    {
        Assert.Empty(KeepMarkerParser.Parse(null).Slugs);
        Assert.Empty(KeepMarkerParser.Parse("").Invalid);
    }
}
