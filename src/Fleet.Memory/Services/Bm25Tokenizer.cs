using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Fleet.Memory.Services;

/// <summary>
/// Produces sparse BM25 term-frequency vectors compatible with Qdrant's server-side IDF modifier.
/// Tokenizes text, computes term frequencies, and hashes tokens to uint32 indices.
/// Qdrant applies IDF weighting automatically when the sparse vector is configured with Modifier.Idf.
/// </summary>
public static partial class Bm25Tokenizer
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
        var tokens = TokenizeText(text);
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

    private static List<string> TokenizeText(string text)
    {
        var lower = text.ToLowerInvariant();
        var matches = WordPattern().Matches(lower);

        var tokens = new List<string>();
        foreach (Match match in matches)
        {
            var word = match.Value;
            if (word.Length >= 2 && !StopWords.Contains(word))
                tokens.Add(word);
        }

        return tokens;
    }

    private static uint HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return BitConverter.ToUInt32(bytes, 0);
    }

    [GeneratedRegex(@"[a-z0-9_]+")]
    private static partial Regex WordPattern();
}
