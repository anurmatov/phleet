using System.Security.Cryptography;
using System.Text;

namespace Fleet.Memory.Services;

/// <summary>
/// Produces sparse BM25 term-frequency vectors compatible with Qdrant's server-side IDF modifier.
/// Tokenizes text, computes term frequencies, and hashes tokens to uint32 indices.
/// Qdrant applies IDF weighting automatically when the sparse vector is configured with Modifier.Idf.
///
/// A term is a word as <see cref="MemoryWords"/> finds it, lower-cased with the invariant culture and
/// composed (NFC), so text in any script has terms. A word of plain ASCII letters, digits and
/// underscore gives the term it always gave. English stop words and one-character words are left
/// out; a Han or Hiragana character is a word by itself and is kept.
/// </summary>
public static class Bm25Tokenizer
{
    private static readonly HashSet<string> StopWords =
    [
        "a", "an", "the", "and", "or", "but", "in", "on", "at", "to", "for",
        "of", "with", "by", "from", "is", "it", "this", "that", "was", "are",
        "be", "has", "had", "have", "not", "no", "as", "if", "its", "do", "so",
        "we", "he", "she", "they", "you", "i", "my", "me", "our", "your"
    ];

    /// <summary>
    /// Tokenizes text and returns sparse vector components: indices (token hashes) and values (term frequencies).
    /// </summary>
    public static (uint[] Indices, float[] Values) Tokenize(string text)
    {
        var tokens = Terms(text);
        if (tokens.Count == 0)
            return ([], []);

        // Count term frequencies
        var termFreqs = new Dictionary<string, int>();
        foreach (var token in tokens)
        {
            termFreqs.TryGetValue(token, out var count);
            termFreqs[token] = count + 1;
        }

        // Convert to sparse vector: hash each term to a uint32 index, value = term frequency
        var indices = new uint[termFreqs.Count];
        var values = new float[termFreqs.Count];
        var i = 0;

        foreach (var (term, freq) in termFreqs)
        {
            indices[i] = HashToken(term);
            values[i] = freq;
            i++;
        }

        return (indices, values);
    }

    /// <summary>The terms of a text in the order they occur, repeats included.</summary>
    public static List<string> Terms(string text)
    {
        var tokens = new List<string>();
        foreach (var word in MemoryWords.Scan(text))
        {
            var term = Term(text, word);
            if (term is not null)
                tokens.Add(term);
        }

        return tokens;
    }

    /// <summary>The term one word is indexed as, or null when the word is not indexed.</summary>
    public static string? Term(string text, MemoryWords.Word word)
    {
        var term = text.Substring(word.Start, word.Length).ToLowerInvariant();
        if (!term.IsNormalized(NormalizationForm.FormC))
            term = term.Normalize(NormalizationForm.FormC);

        if (StopWords.Contains(term))
            return null;
        if (term.Length >= 2)
            return term;
        return term.Length == 1 && Rune.TryCreate(term[0], out var rune) && MemoryWords.IsUnspacedScript(rune) ? term : null;
    }

    private static uint HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return BitConverter.ToUInt32(bytes, 0);
    }
}
