using Fleet.Memory.Data;
using Fleet.Memory.Services;

namespace Fleet.Memory.Tests;

/// <summary>The parts of search that need nothing but their input. Invented text only.</summary>
public sealed class MemoryIdQueryTests
{
    [Theory]
    [InlineData("1a2b3c", "1a2b3c")]                                              // six digits
    [InlineData("1A2B3C4", "1a2b3c4")]                                            // seven, any case
    [InlineData("  1a2b3c4d  ", "1a2b3c4d")]                                      // the short id, trimmed
    [InlineData("1a2b3c4d-00", "1a2b3c4d00")]                                     // into the second group
    [InlineData("1a2b3c4d-", "1a2b3c4d")]
    [InlineData("1a2b3c4d00004000", "1a2b3c4d00004000")]                          // no dashes at all
    [InlineData("1a2b3c4d-0000-4000-8000-000000000001", "1a2b3c4d000040008000000000000001")]
    public void A_query_of_hexadecimal_digits_is_an_id(string query, string expected)
    {
        Assert.True(MemoryIdQuery.TryParse(query, out var prefix));
        Assert.Equal(expected, prefix);
    }

    [Theory]
    [InlineData("1a2b3")]                                      // five digits
    [InlineData("deploy")]                                     // not hexadecimal
    [InlineData("1a2b3c4d retry")]                             // not the whole query
    [InlineData("1a2b-3c4d")]                                  // a dash where a UUID has none
    [InlineData("-1a2b3c4d")]
    [InlineData("1a2b3c4d--0000")]
    [InlineData("1a2b3c4d-0000-4000-8000-0000000000012")]      // longer than a UUID
    [InlineData("")]
    public void Anything_else_is_not(string query)
    {
        Assert.False(MemoryIdQuery.TryParse(query, out _));
    }

    [Fact]
    public void An_id_is_compared_without_dashes_or_case()
    {
        Assert.Equal("1a2b3c4d000040008000000000000001", MemoryIdQuery.Key("1A2B3C4D-0000-4000-8000-000000000001"));
    }
}

public sealed class SearchSnippetsTests
{
    private static List<string> Marked(SearchSnippet snippet) =>
        snippet.Highlights.Select(h => snippet.Text.Substring(h.Start, h.Length)).ToList();

    private static string Words(string word, int count) => string.Join(" ", Enumerable.Repeat(word, count));

    [Fact]
    public void Short_content_is_the_snippet_with_white_space_runs_as_one_space()
    {
        var snippet = SearchSnippets.Start("First line.\n\n  Second   line.\n", "nothing here");

        Assert.Equal("First line. Second line.", snippet.Text);
        Assert.Empty(snippet.Highlights);
    }

    [Fact]
    public void The_start_of_long_content_ends_between_words_with_a_mark_for_what_is_left_out()
    {
        var content = Words("gateway", 60);   // 479 characters

        var snippet = SearchSnippets.Start(content, "unrelated");

        Assert.EndsWith("gateway…", snippet.Text);
        Assert.InRange(snippet.Text.Length, SearchSnippets.Length - 10, SearchSnippets.Length + 1);
        Assert.All(snippet.Text.TrimEnd('…').Split(' '), w => Assert.Equal("gateway", w));
    }

    [Fact]
    public void A_keyword_window_is_around_the_first_matching_word_and_marks_every_one()
    {
        var content = $"{Words("filler", 80)} The Retry budget was exhausted; retry later. {Words("padding", 80)}";

        var snippet = SearchSnippets.AroundFirstMatch(content, "retry budget");

        Assert.StartsWith("…", snippet.Text);
        Assert.EndsWith("…", snippet.Text);
        Assert.Contains("The Retry budget was exhausted; retry later.", snippet.Text);
        Assert.Equal(["Retry", "budget", "retry"], Marked(snippet));
        Assert.InRange(snippet.Text.Length, 150, SearchSnippets.Length + 2);
        // Cut between words on both sides.
        Assert.StartsWith("…filler ", snippet.Text);
        Assert.EndsWith(" padding…", snippet.Text);
    }

    [Fact]
    public void Offsets_are_into_the_snippet_as_shown_after_white_space_was_collapsed()
    {
        var snippet = SearchSnippets.AroundFirstMatch("alpha\n\n\n   beta\tgamma    beta", "beta");

        Assert.Equal("alpha beta gamma beta", snippet.Text);
        Assert.Equal([(6, 4), (17, 4)], snippet.Highlights);
    }

    [Fact]
    public void A_match_in_the_first_words_starts_the_snippet_at_the_start_with_no_mark_before_it()
    {
        var snippet = SearchSnippets.AroundFirstMatch($"Backoff doubles each time. {Words("more", 100)}", "backoff");

        Assert.StartsWith("Backoff doubles", snippet.Text);
        Assert.Equal(["Backoff"], Marked(snippet));
        Assert.EndsWith("…", snippet.Text);
    }

    [Fact]
    public void Words_in_other_scripts_are_found_and_marked()
    {
        var cyrillic = SearchSnippets.AroundFirstMatch($"{Words("слово", 60)} Очередь переполнена, очередь очищена. {Words("ещё", 60)}", "ОЧЕРЕДЬ");
        Assert.Equal(["Очередь", "очередь"], Marked(cyrillic));

        var greek = SearchSnippets.AroundFirstMatch("Η ουρά γέμισε χθες.", "ΟΥΡΆ");
        Assert.Equal(["ουρά"], Marked(greek));

        var cjk = SearchSnippets.AroundFirstMatch($"{string.Concat(Enumerable.Repeat("前置きの文。", 30))}再試行のキューが満杯です。", "満杯");
        Assert.Equal(["満", "杯"], Marked(cjk));
    }

    [Fact]
    public void A_snippet_never_starts_or_ends_inside_a_surrogate_pair_and_its_offsets_never_split_one()
    {
        // No space anywhere near the cut points: rockets (two UTF-16 units each) on both sides.
        var rockets = string.Concat(Enumerable.Repeat("\U0001F680", 200));
        var content = $"{rockets} needle {rockets}";

        foreach (var snippet in new[]
                 {
                     SearchSnippets.Start(content, "needle"),
                     SearchSnippets.AroundFirstMatch(content, "needle"),
                     SearchSnippets.Start(rockets + rockets, "needle"),
                     // A range that begins and ends in the middle of pairs, as a stale index could give.
                     SearchSnippets.Start(content, "needle", 1, 301),
                 })
        {
            var text = snippet.Text.Trim('…');
            Assert.NotEmpty(text);
            Assert.False(char.IsLowSurrogate(text[0]), snippet.Text);
            Assert.False(char.IsHighSurrogate(text[^1]), snippet.Text);
            for (var i = 0; i < text.Length; i++)
                if (char.IsHighSurrogate(text[i]))
                    Assert.True(i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]), "an unpaired surrogate inside the snippet");
        }

        var around = SearchSnippets.AroundFirstMatch(content, "needle");
        Assert.Equal(["needle"], Marked(around));
    }

    [Fact]
    public void A_range_limits_where_the_snippet_comes_from()
    {
        var first = Words("first", 40);
        var second = "Second part mentions the ledger once. " + Words("second", 40);
        var content = first + "\n\n" + second;
        var start = first.Length + 2;

        var meaning = SearchSnippets.Start(content, "anything", start, second.Length);
        Assert.StartsWith("…Second part mentions", meaning.Text);

        var keyword = SearchSnippets.AroundFirstMatch(content, "ledger", start, second.Length);
        Assert.Equal(["ledger"], Marked(keyword));
        Assert.StartsWith("…Second part", keyword.Text);
    }

    [Fact]
    public void A_keyword_that_is_only_outside_the_range_is_looked_for_in_the_whole_content()
    {
        var content = $"The ledger is described first. {Words("first", 60)}\n\n{Words("second", 60)}";
        var secondStart = content.IndexOf("second", StringComparison.Ordinal);

        var snippet = SearchSnippets.AroundFirstMatch(content, "ledger", secondStart, content.Length - secondStart);

        Assert.Equal(["ledger"], Marked(snippet));
        Assert.StartsWith("The ledger", snippet.Text);
    }

    [Fact]
    public void A_keyword_that_is_nowhere_in_the_content_gives_the_start_of_the_range()
    {
        // The match was in the title or a tag.
        var snippet = SearchSnippets.AroundFirstMatch($"Plain opening words. {Words("more", 80)}", "ledger");

        Assert.StartsWith("Plain opening words.", snippet.Text);
        Assert.Empty(snippet.Highlights);
    }

    [Theory]
    [InlineData(-5, 10)]
    [InlineData(9999, 10)]
    [InlineData(3, 99999)]
    [InlineData(0, 0)]
    public void A_range_that_does_not_fit_the_content_is_brought_inside_it(int start, int length)
    {
        var snippet = SearchSnippets.Start("short content here", "short", start, length);
        Assert.NotEmpty(snippet.Text);
        Assert.All(snippet.Highlights, h => Assert.InRange(h.Start + h.Length, 0, snippet.Text.Length));
    }

    [Fact]
    public void Empty_content_is_an_empty_snippet()
    {
        Assert.Equal("", SearchSnippets.AroundFirstMatch("", "word").Text);
        Assert.Equal("", SearchSnippets.Start("   \n ", "word").Text);
    }
}

public sealed class SearchFusionTests
{
    private static IndexMatch M(string path, float score = 0.5f, int start = 0, int length = -1) => new(path, score, start, length);

    [Fact]
    public void A_memory_stands_for_its_best_ranked_section_only()
    {
        var best = SearchFusion.BestPerMemory([M("a", 0.9f, 100, 50), M("b", 0.8f), M("a", 0.7f, 0, 100), M("b", 0.6f, 5, 5)]);

        Assert.Equal([M("a", 0.9f, 100, 50), M("b", 0.8f)], best);
    }

    [Fact]
    public void A_hit_of_the_keyword_side_is_a_keyword_hit_and_one_of_the_meaning_side_alone_carries_its_similarity()
    {
        var fused = SearchFusion.Fuse(
            denseRanked: [M("both", 0.81f, 0, 10), M("dense-only", 0.62f, 40, 20)],
            sparseRanked: [M("sparse-only", 3f, 7, 9), M("both", 2f, 300, 30)]);

        var both = fused.Single(h => h.FilePathKey == "both");
        Assert.True(both.Keyword);
        Assert.Equal(0.81f, both.DenseScore);
        Assert.Equal(300, both.Section.SectionStart);   // the section the keyword was found in

        var denseOnly = fused.Single(h => h.FilePathKey == "dense-only");
        Assert.False(denseOnly.Keyword);
        Assert.Equal(0.62f, denseOnly.DenseScore);
        Assert.Equal(40, denseOnly.Section.SectionStart);

        var sparseOnly = fused.Single(h => h.FilePathKey == "sparse-only");
        Assert.True(sparseOnly.Keyword);
        Assert.Null(sparseOnly.DenseScore);
    }

    [Fact]
    public void Scores_are_reciprocal_ranks_with_the_constant_two_added_over_both_sides()
    {
        var fused = SearchFusion.Fuse(
            denseRanked: [M("a"), M("b"), M("c")],
            sparseRanked: [M("b"), M("d")]);

        // b: second by meaning (1/3) and first by keyword (1/2). a: first by meaning (1/2).
        // d: second by keyword (1/3). c: third by meaning (1/4).
        Assert.Equal(["b", "a", "d", "c"], fused.Select(h => h.FilePathKey));
        Assert.Equal(1f / 3 + 1f / 2, fused[0].Fused, 5);
        Assert.Equal(1f / 2, fused[1].Fused, 5);
        Assert.Equal(1f / 3, fused[2].Fused, 5);
        Assert.Equal(1f / 4, fused[3].Fused, 5);
    }

    [Fact]
    public void Sections_do_not_push_other_memories_down_the_ranking()
    {
        // Three sections of "long" come before "other" on the meaning side: "other" is still second.
        var fused = SearchFusion.Fuse(
            denseRanked: [M("long", 0.9f), M("long", 0.8f), M("long", 0.7f), M("other", 0.6f)],
            sparseRanked: []);

        Assert.Equal(["long", "other"], fused.Select(h => h.FilePathKey));
        Assert.Equal(1f / 3, fused[1].Fused, 5);
    }
}
