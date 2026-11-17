namespace Fleet.Memory.Services;

/// <summary>One section of a memory's content: a character range, in UTF-16 units.</summary>
public readonly record struct MemorySection(int Index, int Start, int Length);

/// <summary>
/// Cuts a memory's content into the sections it is indexed as.
///
/// Content no longer than the budget is one section, the whole content. Longer content is cut where
/// a reader would cut it: before a Markdown heading and at a paragraph break (a blank line), never
/// inside a fenced code block's heading-looking lines. Neighbouring paragraphs are packed together
/// while they fit the budget, a heading stays with the paragraph under it, and a heading starts a
/// new section once the current one is half full. A single paragraph longer than the budget is cut
/// at a line end, else at a sentence end, else at a space, else at the budget, and never inside a
/// surrogate pair.
///
/// The sections are disjoint and in order, and a section's range is read straight out of the
/// content. Together they cover it from its first character to its last, except for a stretch of
/// white space that fits no section. No section is longer than the budget.
/// </summary>
public static class MemorySections
{
    public static List<MemorySection> Split(string content, int budget)
    {
        if (budget < 1) throw new ArgumentOutOfRangeException(nameof(budget));
        if (content.Length <= budget)
            return [new MemorySection(0, 0, content.Length)];

        var sections = new List<(int Start, int Length)>();
        var curStart = 0;
        var curLength = 0;

        void Flush()
        {
            if (curLength > 0) sections.Add((curStart, curLength));
            curStart += curLength;
            curLength = 0;
        }

        foreach (var block in Blocks(content))
        {
            if (block.Length > budget)
            {
                // Too long for any section: cut it. Its last piece stays open so that short
                // paragraphs after it can join it.
                Flush();
                var pieces = Cut(content, block.Start, block.Length, budget);
                for (var p = 0; p < pieces.Count - 1; p++)
                    sections.Add(pieces[p]);
                (curStart, curLength) = pieces[^1];
            }
            else if (curLength + block.Length > budget || (block.Heading && curLength >= budget / 2))
            {
                Flush();
                curLength = block.Length;
            }
            else
            {
                curLength += block.Length;
            }
        }
        Flush();

        // A section of nothing but white space has nothing to embed: it joins the one before it
        // when that fits, and is dropped otherwise. The first section is always kept.
        var result = new List<MemorySection>(sections.Count);
        foreach (var (start, length) in sections)
        {
            if (result.Count > 0 && content.AsSpan(start, length).IsWhiteSpace())
            {
                var last = result[^1];
                if (last.Length + length <= budget)
                    result[^1] = last with { Length = last.Length + length };
                continue;
            }
            result.Add(new MemorySection(result.Count, start, length));
        }
        return result;
    }

    /// <summary>The text of one section as it is embedded and matched: its range without the white space around it.</summary>
    public static string Text(string content, MemorySection section) =>
        content.Substring(section.Start, section.Length).Trim();

    private readonly record struct Block(int Start, int Length, bool Heading);

    /// <summary>
    /// The content as consecutive blocks: a new one begins at a heading line and at the first line
    /// after a blank one. A block that so far holds only headings takes the next block too.
    /// </summary>
    private static List<Block> Blocks(string content)
    {
        var blocks = new List<Block>();
        var blockStart = 0;
        var blockIsHeading = false;   // the block begins with a heading
        var onlyHeadings = true;      // every non-blank line of the block so far is a heading
        var sawText = false;          // the block has a non-blank line
        var afterBlank = false;
        var inFence = false;

        var lineStart = 0;
        while (lineStart < content.Length)
        {
            var lineEnd = content.IndexOf('\n', lineStart);
            var next = lineEnd < 0 ? content.Length : lineEnd + 1;
            var line = content.AsSpan(lineStart, (lineEnd < 0 ? content.Length : lineEnd) - lineStart);
            var trimmed = line.TrimStart();
            var blank = trimmed.IsEmpty || trimmed.IsWhiteSpace();
            var fence = trimmed.StartsWith("```") || trimmed.StartsWith("~~~");
            var heading = !inFence && !fence && IsHeading(line);

            if (!blank)
            {
                var boundary = sawText && (heading || afterBlank) && !onlyHeadings;
                if (boundary)
                {
                    blocks.Add(new Block(blockStart, lineStart - blockStart, blockIsHeading));
                    blockStart = lineStart;
                    blockIsHeading = heading;
                    onlyHeadings = true;
                    sawText = false;
                }
                if (!sawText) blockIsHeading = heading;
                sawText = true;
                if (!heading) onlyHeadings = false;
            }

            if (fence) inFence = !inFence;
            afterBlank = blank;
            lineStart = next;
        }

        blocks.Add(new Block(blockStart, content.Length - blockStart, blockIsHeading));
        return blocks;
    }

    /// <summary>An ATX heading: up to three spaces, one to six '#', then a space or the line's end.</summary>
    private static bool IsHeading(ReadOnlySpan<char> line)
    {
        var i = 0;
        while (i < line.Length && i < 3 && line[i] == ' ') i++;
        var hashes = 0;
        while (i < line.Length && line[i] == '#') { i++; hashes++; }
        if (hashes is < 1 or > 6) return false;
        return i == line.Length || line[i] is ' ' or '\t' or '\r';
    }

    /// <summary>Pieces of at most <paramref name="budget"/> characters that together are the range.</summary>
    private static List<(int Start, int Length)> Cut(string content, int start, int length, int budget)
    {
        var pieces = new List<(int, int)>();
        var end = start + length;
        while (end - start > budget)
        {
            var cut = CutPoint(content, start, budget);
            // A budget of one character cannot hold a surrogate pair: take the pair whole.
            if (cut <= start) cut = start + budget + 1;
            pieces.Add((start, cut - start));
            start = cut;
        }
        pieces.Add((start, end - start));
        return pieces;
    }

    private static int CutPoint(string content, int start, int budget)
    {
        var limit = start + budget;          // exclusive end of the longest piece allowed
        var floor = start + budget / 2;      // do not cut a piece shorter than half the budget

        // After a line end.
        for (var i = limit; i > floor; i--)
            if (content[i - 1] == '\n') return i;

        // After a sentence end: its mark, then the white space that follows.
        for (var i = limit; i > floor; i--)
        {
            var mark = content[i - 1];
            if (mark is '。' or '！' or '？') return i;
            if (i < content.Length && char.IsWhiteSpace(content[i]) && content[i - 1] is '.' or '!' or '?' or '…')
                return i + 1 <= limit ? i + 1 : i;
        }

        // After white space.
        for (var i = limit; i > floor; i--)
            if (char.IsWhiteSpace(content[i - 1])) return i;

        // Nowhere better: at the budget, but not between the halves of a surrogate pair.
        return char.IsLowSurrogate(content[limit]) && char.IsHighSurrogate(content[limit - 1]) ? limit - 1 : limit;
    }
}
