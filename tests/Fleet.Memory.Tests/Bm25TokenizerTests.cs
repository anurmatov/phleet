using System.Security.Cryptography;
using System.Text;
using Fleet.Memory.Services;

namespace Fleet.Memory.Tests;

/// <summary>
/// The keyword side of search saw only <c>[a-z0-9_]</c>, so no word of a memory written in another
/// script could match. Invented text only.
/// </summary>
public sealed class Bm25TokenizerTests
{
    private static uint IndexOf(string term) =>
        BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes(term)), 0);

    [Fact]
    public void Ascii_words_give_the_terms_they_always_gave()
    {
        // What the old pattern produced for this text, so points indexed before the change still
        // match queries tokenized after it.
        Assert.Equal(["quick", "brown_fox", "jumps", "42", "times", "k8s"],
            Bm25Tokenizer.Terms("The quick Brown_Fox jumps 42 times, in K8s!"));

        var (indices, values) = Bm25Tokenizer.Tokenize("retry retry backoff");
        var byIndex = indices.Zip(values).ToDictionary(p => p.First, p => p.Second);
        Assert.Equal(2f, byIndex[IndexOf("retry")]);
        Assert.Equal(1f, byIndex[IndexOf("backoff")]);
    }

    [Theory]
    [InlineData("Очередь задач переполнена", new[] { "очередь", "задач", "переполнена" })]
    [InlineData("Η ΟΥΡΆ εργασιών γέμισε", new[] { "ουρά", "εργασιών", "γέμισε" })]
    [InlineData("Señal de caché agotada", new[] { "señal", "de", "caché", "agotada" })]
    public void Words_in_other_scripts_are_terms_lower_cased(string text, string[] expected)
    {
        Assert.Equal(expected, Bm25Tokenizer.Terms(text));
    }

    [Fact]
    public void Han_and_hiragana_are_one_term_per_character_and_katakana_stays_a_word()
    {
        Assert.Equal(["再", "試", "行", "の", "キュー", "満", "杯"], Bm25Tokenizer.Terms("再試行のキュー満杯"));
        // A supplementary-plane ideograph is one term, never half a surrogate pair.
        Assert.Equal(["\U0002000B", "山"], Bm25Tokenizer.Terms("\U0002000B山"));
    }

    [Fact]
    public void A_decomposed_accent_does_not_split_a_word_and_matches_the_composed_form()
    {
        // "й" and "é" written as a base letter followed by a combining mark.
        var decomposed = "ко\u0438\u0306ка cafe\u0301";
        Assert.Equal(["ко\u0439ка", "caf\u00E9"], Bm25Tokenizer.Terms(decomposed));
        Assert.Equal(Bm25Tokenizer.Tokenize("ко\u0439ка caf\u00E9").Indices.Order(), Bm25Tokenizer.Tokenize(decomposed).Indices.Order());
    }

    [Fact]
    public void Stop_words_one_letter_words_and_punctuation_give_nothing()
    {
        Assert.Empty(Bm25Tokenizer.Terms("the a I — x … !? \U0001F680 \uD800"));
        Assert.Equal(([], []), Bm25Tokenizer.Tokenize("the and of"));
    }

    [Fact]
    public void Word_offsets_never_fall_inside_a_surrogate_pair()
    {
        var text = "ok \U0001F680rocket\U0002000B end";
        var words = MemoryWords.Scan(text).Select(w => text.Substring(w.Start, w.Length)).ToList();
        Assert.Equal(["ok", "rocket", "\U0002000B", "end"], words);
    }

    [Fact]
    public void A_range_scan_returns_only_the_words_inside_it()
    {
        var text = "alpha beta gamma";
        var words = MemoryWords.Scan(text, 6, 4).Select(w => text.Substring(w.Start, w.Length)).ToList();
        Assert.Equal(["beta"], words);
    }
}
