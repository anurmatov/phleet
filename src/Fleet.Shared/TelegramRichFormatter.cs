using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Fleet.Shared;

/// <summary>
/// Converts a permissive Markdown-like subset to Telegram InputRichBlock structures
/// for use with sendRichMessage. Mirrors the token-by-token logic in TelegramFormatter
/// but emits typed blocks instead of HTML strings.
///
/// Recognized block-level constructs: fenced code (```), ATX headings (# / ##),
/// unordered lists (- / *), ordered lists (1.), GFM tables (| header | / separator / rows).
/// Recognized inline constructs within block text: **bold**, `code`, [label](url).
/// Anything unrecognized is treated as literal text.
/// </summary>
internal static class TelegramRichFormatter
{
    /// <summary>
    /// Converts <paramref name="text"/> to an array of <see cref="InputRichBlock"/>.
    /// Each logical block in the input maps to one rich block: fenced code →
    /// <see cref="InputRichBlockPreformatted"/>, heading → <see cref="InputRichBlockSectionHeading"/>,
    /// list → <see cref="InputRichBlockList"/>, GFM table → <see cref="InputRichBlockTable"/>,
    /// everything else → <see cref="InputRichBlockParagraph"/> with inline
    /// <see cref="RichText"/> elements for bold, code, links, and plain text.
    /// </summary>
    internal static InputRichBlock[] ConvertToRichBlocks(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return [new InputRichBlockParagraph { Text = new RichTextText { Text = string.Empty } }];

        var blocks = new List<InputRichBlock>();
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        int i = 0;

        while (i < lines.Length)
        {
            string line = lines[i];

            // ── Fenced code block: ```lang\n...\n``` ──────────────────────────
            if (StartsWithFence(line, out string fencedLang))
            {
                int closeIdx = -1;
                for (int j = i + 1; j < lines.Length; j++)
                {
                    if (StartsWithFence(lines[j], out _)) { closeIdx = j; break; }
                }
                if (closeIdx >= 0)
                {
                    var content = string.Join("\n", lines[(i + 1)..closeIdx]);
                    blocks.Add(new InputRichBlockPreformatted
                    {
                        Text = new RichTextText { Text = content },
                        Language = fencedLang.Length > 0 ? fencedLang : null,
                    });
                    i = closeIdx + 1;
                    continue;
                }
                // Unmatched ``` — consume the line to guarantee progress.
                // Following lines are absorbed by the paragraph accumulator below.
                i++;
                continue;
            }

            // ── ATX headings — check ## before # to avoid prefix collision ──
            if (line.StartsWith("## ", StringComparison.Ordinal) || line == "##")
            {
                var headingText = line.Length > 3 ? line[3..].Trim() : string.Empty;
                if (headingText.Length > 0)
                    blocks.Add(new InputRichBlockSectionHeading
                    {
                        Text = ParseInlineRichText(headingText),
                        Size = 2,
                    });
                // Bare ## (empty text): consume the line without emitting a block.
                i++;
                continue;
            }
            if (line.StartsWith("# ", StringComparison.Ordinal) || line == "#")
            {
                var headingText = line.Length > 2 ? line[2..].Trim() : string.Empty;
                if (headingText.Length > 0)
                    blocks.Add(new InputRichBlockSectionHeading
                    {
                        Text = ParseInlineRichText(headingText),
                        Size = 1,
                    });
                // Bare # (empty text): consume the line without emitting a block.
                i++;
                continue;
            }

            // ── List: consecutive "-" / "*" / "N." items ──────────────────────
            // A run of items is broken when the ordered/unordered kind changes,
            // so "- a\n1. b" produces two separate list blocks rather than one.
            if (IsListItem(line, out _, out int? firstItemNum))
            {
                bool isOrderedList = firstItemNum.HasValue;
                var items = new List<InputRichBlockListItem>();
                while (i < lines.Length && IsListItem(lines[i], out string itemText, out int? itemNum)
                       && itemNum.HasValue == isOrderedList)
                {
                    items.Add(new InputRichBlockListItem
                    {
                        Blocks      = [new InputRichBlockParagraph { Text = ParseInlineRichText(itemText) }],
                        HasCheckbox = false,
                        IsChecked   = false,
                        Value       = itemNum,
                    });
                    i++;
                }
                blocks.Add(new InputRichBlockList { Items = items });
                continue;
            }

            // ── GFM table: header row | separator row | data rows ─────────────
            // A "table" requires the *next* line to be a valid separator whose
            // column count matches the header. Mismatches fall through as paragraph text.
            if (IsPipeLine(line) && i + 1 < lines.Length && IsSeparatorLine(lines[i + 1]) &&
                SplitTableRow(line).Count == SplitTableRow(lines[i + 1]).Count)
            {
                int consumed = ConsumeTable(lines, i, out var tableBlock);
                blocks.Add(tableBlock);
                i += consumed;
                continue;
            }

            // ── Paragraph: accumulate until the next block-level token ─────────
            var paraLines = new List<string>();
            while (i < lines.Length)
            {
                string cur = lines[i];
                if (StartsWithFence(cur, out _)) break;
                if (cur.StartsWith("# ", StringComparison.Ordinal) || cur == "#") break;
                if (cur.StartsWith("## ", StringComparison.Ordinal) || cur == "##") break;
                if (IsListItem(cur, out _, out _)) break;
                if (IsPipeLine(cur) && i + 1 < lines.Length && IsSeparatorLine(lines[i + 1]) &&
                    SplitTableRow(cur).Count == SplitTableRow(lines[i + 1]).Count) break;
                paraLines.Add(cur);
                i++;
            }

            if (paraLines.Count > 0)
            {
                var paraText = string.Join("\n", paraLines);
                if (!string.IsNullOrWhiteSpace(paraText))
                    blocks.Add(new InputRichBlockParagraph { Text = ParseInlineRichText(paraText) });
            }
        }

        if (blocks.Count == 0)
            blocks.Add(new InputRichBlockParagraph { Text = new RichTextText { Text = string.Empty } });

        return [.. blocks];
    }

    // ── Inline parser ──────────────────────────────────────────────────────────
    // Recognizes: `code`, **bold**, [label](url). All else is literal RichTextText.
    private static RichText ParseInlineRichText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return new RichTextText { Text = string.Empty };

        var inline = new List<RichText>();
        int i = 0;
        int len = text.Length;

        while (i < len)
        {
            // Inline code: `...`
            if (text[i] == '`')
            {
                int closeIdx = text.IndexOf('`', i + 1);
                if (closeIdx > i)
                {
                    inline.Add(new RichTextCode
                    {
                        Text = new RichTextText { Text = text.Substring(i + 1, closeIdx - i - 1) },
                    });
                    i = closeIdx + 1;
                    continue;
                }
            }

            // Bold: **...**  (CommonMark non-space adjacency rule)
            if (i + 1 < len && text[i] == '*' && text[i + 1] == '*')
            {
                int closeIdx = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (closeIdx > i)
                {
                    bool openAdj  = i + 2 < len && !char.IsWhiteSpace(text[i + 2]);
                    bool closeAdj = closeIdx > i + 2 && !char.IsWhiteSpace(text[closeIdx - 1]);
                    if (openAdj && closeAdj)
                    {
                        inline.Add(new RichTextBold
                        {
                            Text = new RichTextText { Text = text.Substring(i + 2, closeIdx - i - 2) },
                        });
                        i = closeIdx + 2;
                        continue;
                    }
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
                        var url   = text.Substring(labelClose + 2, urlClose - labelClose - 2).Trim();
                        if (IsValidUrl(url))
                        {
                            inline.Add(new RichTextUrl
                            {
                                Text = new RichTextText { Text = label },
                                Url  = url,
                            });
                            i = urlClose + 1;
                            continue;
                        }
                    }
                }
            }

            // Literal text — batch until the next special character
            int litStart = i;
            while (i < len)
            {
                if (text[i] == '`' ||
                    (i + 1 < len && text[i] == '*' && text[i + 1] == '*') ||
                    text[i] == '[')
                    break;
                i++;
            }
            if (i > litStart)
                inline.Add(new RichTextText { Text = text[litStart..i] });
            else
            {
                // A special character that matched no construct. Emit it as literal
                // text and advance one position to guarantee loop progress.
                inline.Add(new RichTextText { Text = text[i..(i + 1)] });
                i++;
            }
        }

        return inline.Count == 0 ? new RichTextText { Text = string.Empty }
             : inline.Count == 1 ? inline[0]
             : new RichTextArray { Array = [.. inline] };
    }

    // ── GFM table helpers ─────────────────────────────────────────────────────

    private static bool IsPipeLine(string line) => line.Contains('|');

    private static bool IsSeparatorLine(string line)
    {
        if (!line.Contains('|')) return false;
        var cells = SplitTableRow(line);
        var nonEmpty = cells.Where(c => c.Trim().Length > 0).ToList();
        return nonEmpty.Count > 0 && nonEmpty.All(c => IsSeparatorCell(c.Trim()));
    }

    // A separator cell contains only '-' and ':' with at least one '-'.
    private static bool IsSeparatorCell(string cell) =>
        cell.Length > 0 && cell.Contains('-') && cell.All(c => c == '-' || c == ':');

    // Split a pipe-delimited table row, respecting \| escapes.
    // Leading and trailing pipes are stripped before splitting.
    private static List<string> SplitTableRow(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length > 0 && trimmed[0] == '|') trimmed = trimmed[1..];
        if (trimmed.Length > 0 && trimmed[^1] == '|') trimmed = trimmed[..^1];

        var cells = new List<string>();
        var sb    = new System.Text.StringBuilder();

        for (int i = 0; i < trimmed.Length; i++)
        {
            if (trimmed[i] == '\\' && i + 1 < trimmed.Length && trimmed[i + 1] == '|')
            {
                sb.Append('|');
                i++;
            }
            else if (trimmed[i] == '|')
            {
                cells.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(trimmed[i]);
            }
        }
        cells.Add(sb.ToString());
        return cells;
    }

    // Derive alignment from a GFM separator cell string.
    private static RichBlockTableCellAlign ParseSeparatorAlign(string cell)
    {
        var t = cell.Trim();
        bool leftColon  = t.StartsWith(':');
        bool rightColon = t.EndsWith(':');
        if (leftColon && rightColon) return RichBlockTableCellAlign.Center;
        if (rightColon)              return RichBlockTableCellAlign.Right;
        return RichBlockTableCellAlign.Left;
    }

    // Build an InputRichBlockTable from lines starting at `start`.
    // Returns the number of lines consumed (header + separator + data rows).
    private static int ConsumeTable(string[] lines, int start, out InputRichBlockTable table)
    {
        var headerCells = SplitTableRow(lines[start]);
        var sepCells    = SplitTableRow(lines[start + 1]);
        var aligns = sepCells
            .Select(c => c.Trim().Length > 0 ? ParseSeparatorAlign(c) : RichBlockTableCellAlign.Left)
            .ToList();
        int colCount = headerCells.Count;

        var rows = new List<IEnumerable<RichBlockTableCell>>();

        // Header row — IsHeader = true
        rows.Add(Enumerable.Range(0, colCount).Select(col => new RichBlockTableCell
        {
            Text     = ParseInlineRichText(col < headerCells.Count ? headerCells[col].Trim() : string.Empty),
            IsHeader = true,
            Align    = col < aligns.Count ? aligns[col] : RichBlockTableCellAlign.Left,
            Valign   = RichBlockTableCellValign.Middle,
        }).ToList());

        int i = start + 2;
        while (i < lines.Length && IsPipeLine(lines[i]))
        {
            var dataCells = SplitTableRow(lines[i]);
            rows.Add(Enumerable.Range(0, colCount).Select(col => new RichBlockTableCell
            {
                Text     = ParseInlineRichText(col < dataCells.Count ? dataCells[col].Trim() : string.Empty),
                IsHeader = false,
                Align    = col < aligns.Count ? aligns[col] : RichBlockTableCellAlign.Left,
                Valign   = RichBlockTableCellValign.Middle,
            }).ToList());
            i++;
        }

        table = new InputRichBlockTable
        {
            Cells      = rows,
            IsBordered = true,
            IsStriped  = false,
        };
        return i - start;
    }

    // ── Block-level helpers ───────────────────────────────────────────────────

    // Returns true if `line` opens or closes a fenced code block (```).
    private static bool StartsWithFence(string line, out string lang)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            lang = trimmed[3..].Trim();
            return true;
        }
        lang = string.Empty;
        return false;
    }

    // Returns true when `line` is a list item and outputs the item text and
    // the 1-based item number (null for unordered lists).
    private static bool IsListItem(string line, out string itemText, out int? itemNumber)
    {
        // Unordered: "- text" or "* text"
        if (line.StartsWith("- ", StringComparison.Ordinal) ||
            line.StartsWith("* ", StringComparison.Ordinal))
        {
            itemText   = line[2..].Trim();
            itemNumber = null;
            return true;
        }
        // Ordered: "N. text" (N = one or more ASCII digits)
        int idx = 0;
        while (idx < line.Length && char.IsAsciiDigit(line[idx])) idx++;
        if (idx > 0 && idx + 1 < line.Length &&
            line[idx] == '.' && line[idx + 1] == ' ' &&
            int.TryParse(line[..idx], out int num))
        {
            itemText   = line[(idx + 2)..].Trim();
            itemNumber = num;
            return true;
        }
        itemText   = string.Empty;
        itemNumber = null;
        return false;
    }

    private static bool IsValidUrl(string url) =>
        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}
