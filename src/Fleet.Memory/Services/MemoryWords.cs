using System.Globalization;
using System.Text;

namespace Fleet.Memory.Services;

/// <summary>
/// Where the words are in a text, in any script. A word is a run of Unicode letters, decimal digits
/// and underscore; a combining mark continues the run it follows, so a decomposed accent does not cut
/// a word in two. Han and Hiragana are written without spaces between words, so there every
/// character is a word of its own. Offsets are UTF-16 and never fall inside a surrogate pair.
///
/// One definition for the keyword index (<see cref="Bm25Tokenizer"/>) and for marking the matched
/// words in a snippet (<see cref="SearchSnippets"/>): what is highlighted is what was indexed.
/// </summary>
public static class MemoryWords
{
    public readonly record struct Word(int Start, int Length);

    public static IEnumerable<Word> Scan(string text) => Scan(text, 0, text.Length);

    /// <summary>The words that lie wholly inside <c>[start, start + length)</c>.</summary>
    public static IEnumerable<Word> Scan(string text, int start, int length)
    {
        var end = start + length;
        var runStart = -1;
        var i = start;
        while (i < end)
        {
            // A lone surrogate decodes as U+FFFD with one unit consumed: not a letter, so it ends a run.
            Rune.DecodeFromUtf16(text.AsSpan(i, end - i), out var rune, out var consumed);

            if (IsUnspacedScript(rune))
            {
                if (runStart >= 0) yield return new Word(runStart, i - runStart);
                runStart = -1;
                yield return new Word(i, consumed);
            }
            else if (IsWordRune(rune, inRun: runStart >= 0))
            {
                if (runStart < 0) runStart = i;
            }
            else if (runStart >= 0)
            {
                yield return new Word(runStart, i - runStart);
                runStart = -1;
            }

            i += consumed;
        }

        if (runStart >= 0) yield return new Word(runStart, end - runStart);
    }

    /// <summary>True for a word <see cref="Scan(string)"/> returns one character at a time.</summary>
    public static bool IsUnspacedScript(Rune rune)
    {
        var v = rune.Value;
        return v is >= 0x3040 and <= 0x309F      // Hiragana
            or >= 0x3400 and <= 0x4DBF           // CJK extension A
            or >= 0x4E00 and <= 0x9FFF           // CJK unified ideographs
            or >= 0xF900 and <= 0xFAFF           // CJK compatibility ideographs
            or >= 0x20000 and <= 0x323AF;        // CJK extensions B and later
    }

    private static bool IsWordRune(Rune rune, bool inRun)
    {
        if (rune.Value == '_' || Rune.IsLetterOrDigit(rune)) return true;
        if (!inRun) return false;
        return Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark;
    }
}
