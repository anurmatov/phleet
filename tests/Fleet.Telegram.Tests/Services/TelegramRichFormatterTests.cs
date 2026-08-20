using System.Text.Json;
using Fleet.Shared;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Fleet.Telegram.Tests.Services;

/// <summary>
/// Tests for TelegramRichFormatter.ConvertToRichBlocks — Phase 2 (issue #218):
/// ATX headings, unordered/ordered lists, and GFM tables.
///
/// Also covers the three mandatory scenarios from the issue spec:
///   1. Cross-mode regression — LegacyHtml/PlainText paths are byte-identical pre/post change.
///   2. Fallback content semantics — a GFM table passes through TelegramFormatter as literal Markdown.
///   3. Malformed table — a pipe row without a separator row must NOT throw; treat as paragraph.
/// </summary>
public class TelegramRichFormatterTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static T Single<T>(InputRichBlock[] blocks) where T : InputRichBlock
    {
        Assert.Single(blocks);
        return Assert.IsType<T>(blocks[0]);
    }

    private static string PlainOf(RichText rt) => rt switch
    {
        RichTextText t  => t.Text,
        RichTextBold b  => PlainOf(b.Text),
        RichTextCode c  => PlainOf(c.Text),
        RichTextUrl u   => PlainOf(u.Text),
        RichTextArray a => string.Concat(a.Array.Select(PlainOf)),
        _               => string.Empty,
    };

    // ── ATX Heading tests ─────────────────────────────────────────────────────

    [Fact]
    public void H1_ProducesHeadingBlockWithSize1()
    {
        var blocks = TelegramRichFormatter.ConvertToRichBlocks("# Hello World");
        var h = Single<InputRichBlockSectionHeading>(blocks);
        Assert.Equal(1, h.Size);
        Assert.Equal("Hello World", PlainOf(h.Text));
    }

    [Fact]
    public void H2_ProducesHeadingBlockWithSize2()
    {
        var blocks = TelegramRichFormatter.ConvertToRichBlocks("## Section Title");
        var h = Single<InputRichBlockSectionHeading>(blocks);
        Assert.Equal(2, h.Size);
        Assert.Equal("Section Title", PlainOf(h.Text));
    }

    [Fact]
    public void H1_WithInlineBold_ParsesInlineFormatting()
    {
        var blocks = TelegramRichFormatter.ConvertToRichBlocks("# My **bold** heading");
        var h = Single<InputRichBlockSectionHeading>(blocks);
        Assert.Equal(1, h.Size);
        // PlainOf collapses bold markers
        Assert.Equal("My bold heading", PlainOf(h.Text));
        // The text node must be a RichTextArray containing a bold element
        var arr = Assert.IsType<RichTextArray>(h.Text);
        Assert.Contains(arr.Array, rt => rt is RichTextBold);
    }

    [Fact]
    public void H3_And_Beyond_FallThrough_AsParagraph()
    {
        // Only # and ## are recognised as headings
        var blocks = TelegramRichFormatter.ConvertToRichBlocks("### Not a heading");
        Single<InputRichBlockParagraph>(blocks);
    }

    [Fact]
    public void ParagraphFollowedByHeading_ProducesTwoBlocks()
    {
        var input = "Some text\n## Section";
        var blocks = TelegramRichFormatter.ConvertToRichBlocks(input);
        Assert.Equal(2, blocks.Length);
        Assert.IsType<InputRichBlockParagraph>(blocks[0]);
        var h = Assert.IsType<InputRichBlockSectionHeading>(blocks[1]);
        Assert.Equal(2, h.Size);
    }

    // ── Unordered list tests ──────────────────────────────────────────────────

    [Fact]
    public void UnorderedList_ProducesListBlockWithNullValues()
    {
        var input = "- item one\n- item two\n- item three";
        var blocks = TelegramRichFormatter.ConvertToRichBlocks(input);
        var list = Single<InputRichBlockList>(blocks);
        var items = list.Items.ToList();
        Assert.Equal(3, items.Count);
        Assert.All(items, item => Assert.Null(item.Value));
        Assert.Equal("item one",   PlainOf(Assert.IsType<InputRichBlockParagraph>(items[0].Blocks.First()).Text));
        Assert.Equal("item two",   PlainOf(Assert.IsType<InputRichBlockParagraph>(items[1].Blocks.First()).Text));
        Assert.Equal("item three", PlainOf(Assert.IsType<InputRichBlockParagraph>(items[2].Blocks.First()).Text));
    }

    [Fact]
    public void UnorderedList_StarPrefix_ProducesListBlock()
    {
        var input = "* alpha\n* beta";
        var blocks = TelegramRichFormatter.ConvertToRichBlocks(input);
        Single<InputRichBlockList>(blocks);
    }

    [Fact]
    public void UnorderedList_ItemWithInlineCode_ParsesInlineFormatting()
    {
        var input = "- run `dotnet test`\n- done";
        var blocks = TelegramRichFormatter.ConvertToRichBlocks(input);
        var list = Single<InputRichBlockList>(blocks);
        var first = Assert.IsType<InputRichBlockParagraph>(list.Items.First().Blocks.First());
        var arr = Assert.IsType<RichTextArray>(first.Text);
        Assert.Contains(arr.Array, rt => rt is RichTextCode);
    }

    // ── Ordered list tests ────────────────────────────────────────────────────

    [Fact]
    public void OrderedList_ProducesListBlockWithSequentialValues()
    {
        var input = "1. first\n2. second\n3. third";
        var blocks = TelegramRichFormatter.ConvertToRichBlocks(input);
        var list = Single<InputRichBlockList>(blocks);
        var items = list.Items.ToList();
        Assert.Equal(3, items.Count);
        Assert.Equal(1, items[0].Value);
        Assert.Equal(2, items[1].Value);
        Assert.Equal(3, items[2].Value);
        Assert.Equal("first",  PlainOf(Assert.IsType<InputRichBlockParagraph>(items[0].Blocks.First()).Text));
        Assert.Equal("second", PlainOf(Assert.IsType<InputRichBlockParagraph>(items[1].Blocks.First()).Text));
        Assert.Equal("third",  PlainOf(Assert.IsType<InputRichBlockParagraph>(items[2].Blocks.First()).Text));
    }

    [Fact]
    public void OrderedList_NonStartingAt1_PreservesNumbers()
    {
        // GFM allows starting at any number; we preserve the author's numbering.
        var input = "5. fifth\n6. sixth";
        var blocks = TelegramRichFormatter.ConvertToRichBlocks(input);
        var list = Single<InputRichBlockList>(blocks);
        var items = list.Items.ToList();
        Assert.Equal(5, items[0].Value);
        Assert.Equal(6, items[1].Value);
    }

    [Fact]
    public void ListFollowedByParagraph_ProducesTwoBlocks()
    {
        var input = "- item\n\nsome text";
        var blocks = TelegramRichFormatter.ConvertToRichBlocks(input);
        Assert.Equal(2, blocks.Length);
        Assert.IsType<InputRichBlockList>(blocks[0]);
        Assert.IsType<InputRichBlockParagraph>(blocks[1]);
    }

    // ── GFM table tests ───────────────────────────────────────────────────────

    [Fact]
    public void Table_BasicTwoColumn_ProducesTableBlock()
    {
        var input = "| A | B |\n|---|---|\n| a1 | b1 |\n| a2 | b2 |";
        var blocks = TelegramRichFormatter.ConvertToRichBlocks(input);
        var table = Single<InputRichBlockTable>(blocks);
        Assert.True(table.IsBordered);

        var rows = table.Cells.ToList();
        Assert.Equal(3, rows.Count); // header + 2 data rows

        var header = rows[0].ToList();
        Assert.Equal(2, header.Count);
        Assert.True(header[0].IsHeader);
        Assert.True(header[1].IsHeader);
        Assert.Equal("A", PlainOf(header[0].Text));
        Assert.Equal("B", PlainOf(header[1].Text));
    }

    [Fact]
    public void Table_AlignmentSeparators_MappedToEnum()
    {
        var input = "| L | C | R |\n|:--|:-:|--:|\n| l | c | r |";
        var blocks = TelegramRichFormatter.ConvertToRichBlocks(input);
        var table = Single<InputRichBlockTable>(blocks);
        var rows = table.Cells.ToList();
        var header = rows[0].ToList();
        Assert.Equal(RichBlockTableCellAlign.Left,   header[0].Align);
        Assert.Equal(RichBlockTableCellAlign.Center, header[1].Align);
        Assert.Equal(RichBlockTableCellAlign.Right,  header[2].Align);
    }

    [Fact]
    public void Table_EscapedPipe_TreatedAsLiteralPipeInCell()
    {
        var input = "| A |\n|---|\n| a\\|b |";
        var blocks = TelegramRichFormatter.ConvertToRichBlocks(input);
        var table = Single<InputRichBlockTable>(blocks);
        var rows = table.Cells.ToList();
        var dataRow = rows[1].ToList();
        Assert.Equal("a|b", PlainOf(dataRow[0].Text));
    }

    [Fact]
    public void Table_CellWithInlineBold_ParsesInlineFormatting()
    {
        var input = "| Col |\n|-----|\n| **bold** |";
        var blocks = TelegramRichFormatter.ConvertToRichBlocks(input);
        var table = Single<InputRichBlockTable>(blocks);
        var dataRow = table.Cells.ToList()[1].ToList();
        Assert.Equal("bold", PlainOf(dataRow[0].Text));
        Assert.IsType<RichTextBold>(dataRow[0].Text);
    }

    [Fact]
    public void Table_HeaderAlignmentAppliedToDataRows()
    {
        var input = "| L | R |\n|:--|--:|\n| a | b |";
        var blocks = TelegramRichFormatter.ConvertToRichBlocks(input);
        var table = Single<InputRichBlockTable>(blocks);
        var rows = table.Cells.ToList();
        var dataRow = rows[1].ToList();
        Assert.Equal(RichBlockTableCellAlign.Left,  dataRow[0].Align);
        Assert.Equal(RichBlockTableCellAlign.Right, dataRow[1].Align);
    }

    // ── Mandatory spec test 3: Malformed table (no separator row → paragraph) ─

    [Fact]
    public void MalformedTable_NoSeparatorRow_FallsThroughAsParagraph_DoesNotThrow()
    {
        // Two pipe-containing lines but the second is NOT a separator row.
        var input = "| Col A | Col B |\n| data1 | data2 |";
        var blocks = TelegramRichFormatter.ConvertToRichBlocks(input);
        // Must produce a paragraph, not throw, not produce a table block.
        Assert.DoesNotContain(blocks, b => b is InputRichBlockTable);
        Assert.All(blocks, b => Assert.IsType<InputRichBlockParagraph>(b));
    }

    [Fact]
    public void MalformedTable_SeparatorWithNoLeadingPipeTable_NoThrow()
    {
        // Separator on line 2 but line 1 has no pipe — not a valid table header.
        var input = "not a table\n|---|\n| data |";
        var ex = Record.Exception(() => TelegramRichFormatter.ConvertToRichBlocks(input));
        Assert.Null(ex);
    }

    // ── Mandatory spec test 2: Fallback content semantics ──────────────────────
    // In LegacyHtml mode, TelegramFormatter (not TelegramRichFormatter) is called.
    // A GFM table must pass through as literal Markdown text.

    [Fact]
    public void LegacyHtml_Table_PassesThroughAsLiteralMarkdown()
    {
        var tableMarkdown = "| A | B |\n|---|---|\n| a | b |";
        // TelegramFormatter.FormatAndSplit is the LegacyHtml path.
        var htmlChunks = TelegramFormatter.FormatAndSplit(tableMarkdown);
        // The pipe characters and dashes must survive (HTML-escaped as needed but no table structure).
        var combined = string.Join("", htmlChunks);
        Assert.Contains("|", combined);
        Assert.Contains("---", combined);
        // Must NOT contain any HTML element that would look like a rendered table.
        Assert.DoesNotContain("<table", combined);
        Assert.DoesNotContain("<tr", combined);
        Assert.DoesNotContain("<td", combined);
    }

    // ── Mandatory spec test 1: Cross-mode regression ───────────────────────────
    // TelegramFormatter (LegacyHtml) output for headings and lists must be
    // byte-identical before and after adding Phase 2 rich-block support.
    // Specifically: "# heading" must appear as literal "# heading" in HTML, not
    // as a heading element, because TelegramFormatter is the LegacyHtml path.

    [Fact]
    public void LegacyHtml_Heading_IsLiteralHashInOutput()
    {
        var input = "# Hello";
        var html  = string.Join("", TelegramFormatter.FormatAndSplit(input));
        // Must be unchanged — '# Hello' in LegacyHtml, not an H1 block.
        Assert.Contains("# Hello", html);
        Assert.DoesNotContain("<h1", html);
    }

    [Fact]
    public void LegacyHtml_UnorderedList_IsLiteralDashesInOutput()
    {
        var input = "- item one\n- item two";
        var html  = string.Join("", TelegramFormatter.FormatAndSplit(input));
        Assert.Contains("- item one", html);
        Assert.DoesNotContain("<ul", html);
        Assert.DoesNotContain("<li", html);
    }

    [Fact]
    public void PlainText_HeadingLineRetainedWithHashPrefix()
    {
        var input  = "# Hello World";
        var chunks = TelegramFormatter.SplitPlain(input);
        Assert.Single(chunks);
        Assert.Equal("# Hello World", chunks[0]);
    }

    // ── Mixed-content round-trip ──────────────────────────────────────────────

    [Fact]
    public void MixedContent_HeadingListTableParagraph_AllBlocksEmitted()
    {
        var input = string.Join("\n",
            "# Title",
            "Intro paragraph.",
            "- alpha",
            "- beta",
            "| X | Y |",
            "|---|---|",
            "| 1 | 2 |",
            "Footer."
        );

        var blocks = TelegramRichFormatter.ConvertToRichBlocks(input);
        Assert.Equal(5, blocks.Length);
        Assert.IsType<InputRichBlockSectionHeading>(blocks[0]);
        Assert.IsType<InputRichBlockParagraph>(blocks[1]);
        Assert.IsType<InputRichBlockList>(blocks[2]);
        Assert.IsType<InputRichBlockTable>(blocks[3]);
        Assert.IsType<InputRichBlockParagraph>(blocks[4]);
    }

    [Fact]
    public void FencedCode_InsideMixedContent_StillProducesPreformatted()
    {
        var input = "## Setup\n```bash\ndotnet build\n```\n- done";
        var blocks = TelegramRichFormatter.ConvertToRichBlocks(input);
        Assert.Equal(3, blocks.Length);
        Assert.IsType<InputRichBlockSectionHeading>(blocks[0]);
        var pre = Assert.IsType<InputRichBlockPreformatted>(blocks[1]);
        Assert.Equal("bash", pre.Language);
        Assert.IsType<InputRichBlockList>(blocks[2]);
    }

    [Fact]
    public void FencedCode_ContainingHeadingMarker_IsNotParsedAsHeading()
    {
        var input = "```\n# not a heading\n```";
        var blocks = TelegramRichFormatter.ConvertToRichBlocks(input);
        var pre = Single<InputRichBlockPreformatted>(blocks);
        Assert.Contains("# not a heading", ((RichTextText)pre.Text).Text);
    }

    // ── HasCheckbox / IsChecked defaults ──────────────────────────────────────

    [Fact]
    public void ListItems_HasCheckboxFalse_IsCheckedFalse()
    {
        var blocks = TelegramRichFormatter.ConvertToRichBlocks("- item");
        var list   = Single<InputRichBlockList>(blocks);
        var item   = list.Items.First();
        Assert.False(item.HasCheckbox);
        Assert.False(item.IsChecked);
    }

    // ── Serialization regression — enum zero-value guard ─────────────────────
    // All non-nullable Bot API enums on constructed RichBlock* types must be
    // explicitly set to a valid (non-zero) member, otherwise Telegram.Bot's
    // EnumConverter throws JsonException at wire-send time.
    // This test catches any future addition of an enum property that is left
    // at its default 0, which the construction-only tests above cannot catch.

    [Fact]
    public void Table_WithHeadingAndLists_SerializesWithoutException()
    {
        // Input contains all three Phase-2 block types plus a paragraph.
        // The table is the critical case: RichBlockTableCell.Valign must be
        // set to a non-zero value or Telegram.Bot's EnumConverter throws.
        var input = string.Join("\n",
            "# Heading",
            "```bash",
            "dotnet build",
            "```",
            "- alpha",
            "- beta",
            "1. one",
            "| A | B |",
            "|---|---|",
            "| x | y |",
            "footer"
        );

        var blocks = TelegramRichFormatter.ConvertToRichBlocks(input);
        var richMessage = new InputRichMessage { Blocks = blocks };

        // Use Telegram.Bot's own JsonSerializerOptions — this is the exact
        // serializer path that sendRichMessage uses on the wire.
        var ex = Record.Exception(() => JsonSerializer.Serialize(richMessage, JsonBotAPI.Options));
        Assert.Null(ex);
    }
}
