using System.Text;

namespace Fleet.Memory.Services;

/// <summary>A piece of a memory's content shown with a search hit, and where the query's words are in it.</summary>
/// <param name="Text">About <see cref="SearchSnippets.Length"/> characters, white space runs shown as one space.</param>
/// <param name="Highlights">Start and length of each query word in <paramref name="Text"/>, in UTF-16 units, in order.</param>
public sealed record SearchSnippet(string Text, IReadOnlyList<(int Start, int Length)> Highlights);

/// <summary>
/// Cuts the snippet of a search hit out of a memory's content.
///
/// A snippet starts and ends between words, never inside a surrogate pair, and carries "…" where
/// content was left out before or after it. The query's words are found the way the keyword index
/// finds them (<see cref="MemoryWords"/>, <see cref="Bm25Tokenizer.Term"/>), so what is marked is
/// what matched.
/// </summary>
public static class SearchSnippets
{
    /// <summary>The length a snippet aims for, in characters, before the marks for left-out text.</summary>
    public const int Length = 200;

    /// <summary>How much of the window comes before the first matched word.</summary>
    private const int Lead = 60;

    private const string Ellipsis = "…";

    /// <summary>The start of a range of the content, with the query's words in it marked.</summary>
    public static SearchSnippet Start(string content, string query, int rangeStart = 0, int rangeLength = -1)
    {
        var (start, end) = Clamp(content, rangeStart, rangeLength);
        return Build(content, start, end, start, Terms(query));
    }

    /// <summary>
    /// A window around the first place a word of the query occurs in the range, or in the whole
    /// content when the range has none (the match was in the title or a tag), or the start of the
    /// range when the content has none either.
    /// </summary>
    public static SearchSnippet AroundFirstMatch(string content, string query, int rangeStart = 0, int rangeLength = -1)
    {
        var terms = Terms(query);
        var (start, end) = Clamp(content, rangeStart, rangeLength);

        var match = FirstMatch(content, start, end, terms);
        if (match < 0 && (start > 0 || end < content.Length))
        {
            match = FirstMatch(content, 0, content.Length, terms);
            if (match >= 0) (start, end) = (0, content.Length);
        }

        return Build(content, start, end, match < 0 ? start : Math.Max(start, match - Lead), terms, match);
    }

    private static HashSet<string> Terms(string query) => new(Bm25Tokenizer.Terms(query), StringComparer.Ordinal);

    private static (int Start, int End) Clamp(string content, int rangeStart, int rangeLength)
    {
        // The range comes from the index, which may be older than the file: keep it inside the content.
        var start = Math.Clamp(rangeStart, 0, content.Length);
        var end = rangeLength < 0 ? content.Length : Math.Clamp(start + rangeLength, start, content.Length);
        // ...and off the middle of a surrogate pair.
        if (SplitsPair(content, start)) start++;
        if (SplitsPair(content, end)) end--;
        if (start >= end) (start, end) = (0, content.Length);
        return (start, end);
    }

    private static bool SplitsPair(string content, int index) =>
        index > 0 && index < content.Length && char.IsLowSurrogate(content[index]) && char.IsHighSurrogate(content[index - 1]);

    private static int FirstMatch(string content, int start, int end, HashSet<string> terms)
    {
        if (terms.Count == 0) return -1;
        foreach (var word in MemoryWords.Scan(content, start, end - start))
        {
            var term = Bm25Tokenizer.Term(content, word);
            if (term is not null && terms.Contains(term)) return word.Start;
        }
        return -1;
    }

    /// <summary>
    /// The snippet of <c>[rangeStart, rangeEnd)</c> that begins at or just after <paramref name="from"/>.
    /// </summary>
    private static SearchSnippet Build(string content, int rangeStart, int rangeEnd, int from, HashSet<string> terms, int mustShow = -1)
    {
        // Begin at the range's first visible character, or at the first word boundary at or after "from".
        var begin = from <= rangeStart ? rangeStart : WordBoundaryAtOrAfter(content, from, rangeEnd);
        if (mustShow >= 0 && begin > mustShow) begin = mustShow;
        while (begin < rangeEnd && char.IsWhiteSpace(content[begin])) begin++;

        var stop = Math.Min(rangeEnd, begin + Length);
        if (stop < rangeEnd) stop = WordBoundaryAtOrBefore(content, stop, begin);
        while (stop > begin && char.IsWhiteSpace(content[stop - 1])) stop--;

        var leftOutBefore = content.AsSpan(0, begin).Trim().Length > 0;
        var leftOutAfter = content.AsSpan(stop).Trim().Length > 0;

        // White space runs become one space. Each kept character remembers where it lands, so the
        // words found in the content can be marked in the snippet.
        var text = new StringBuilder(stop - begin + 2);
        if (leftOutBefore) text.Append(Ellipsis);
        var position = new int[stop - begin + 1];
        var pendingSpace = false;
        for (var i = begin; i < stop; i++)
        {
            if (char.IsWhiteSpace(content[i]))
            {
                pendingSpace = true;
                position[i - begin] = text.Length;
                continue;
            }
            if (pendingSpace) text.Append(' ');
            pendingSpace = false;
            position[i - begin] = text.Length;
            text.Append(content[i]);
        }
        position[stop - begin] = text.Length;
        if (leftOutAfter) text.Append(Ellipsis);

        var highlights = new List<(int, int)>();
        if (terms.Count > 0)
        {
            foreach (var word in MemoryWords.Scan(content, begin, stop - begin))
            {
                var term = Bm25Tokenizer.Term(content, word);
                if (term is null || !terms.Contains(term)) continue;
                var at = position[word.Start - begin];
                highlights.Add((at, position[word.Start + word.Length - begin] - at));
            }
        }

        return new SearchSnippet(text.ToString(), highlights);
    }

    /// <summary>The first place at or after <paramref name="index"/> that is not inside a word.</summary>
    private static int WordBoundaryAtOrAfter(string content, int index, int end)
    {
        if (index >= end) return end;
        // Already between words: the character before is not part of one.
        if (!IsWordChar(content, index - 1)) return index;
        var i = index;
        while (i < end && IsWordChar(content, i)) i++;
        return i;
    }

    /// <summary>The last place at or before <paramref name="index"/> that is not inside a word, but after <paramref name="begin"/>.</summary>
    private static int WordBoundaryAtOrBefore(string content, int index, int begin)
    {
        var i = index;
        while (i > begin && IsWordChar(content, i - 1) && IsWordChar(content, i)) i--;
        // One unbroken word longer than the snippet: cut it, but not through a surrogate pair.
        if (i == begin) i = index;
        return i > begin && char.IsLowSurrogate(content[i]) && char.IsHighSurrogate(content[i - 1]) ? i - 1 : i;
    }

    /// <summary>
    /// Whether the character at an index is part of a run that must not be cut: a letter, digit,
    /// underscore, combining mark, or either half of a surrogate pair. Han and Hiragana characters
    /// are words by themselves, so a cut between two of them is between words.
    /// </summary>
    private static bool IsWordChar(string content, int index)
    {
        if (index < 0 || index >= content.Length) return false;
        var c = content[index];
        if (char.IsSurrogate(c)) return true;
        if (MemoryWords.IsUnspacedScript(new Rune(c))) return false;
        return c == '_' || char.IsLetterOrDigit(c) || char.GetUnicodeCategory(c) is
            System.Globalization.UnicodeCategory.NonSpacingMark or
            System.Globalization.UnicodeCategory.SpacingCombiningMark or
            System.Globalization.UnicodeCategory.EnclosingMark;
    }
}
