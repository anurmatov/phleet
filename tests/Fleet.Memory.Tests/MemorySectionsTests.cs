using Fleet.Memory.Services;

namespace Fleet.Memory.Tests;

/// <summary>How a memory's content is cut into the sections it is indexed as. Invented text only.</summary>
public sealed class MemorySectionsTests
{
    private static string Words(string word, int count) => string.Join(" ", Enumerable.Repeat(word, count));

    private static List<string> Texts(string content, int budget) =>
        MemorySections.Split(content, budget).Select(s => MemorySections.Text(content, s)).ToList();

    /// <summary>In order, not overlapping, from the first character to the last, none over the budget.</summary>
    private static void AssertCoversContent(string content, int budget)
    {
        var sections = MemorySections.Split(content, budget);
        var at = 0;
        for (var i = 0; i < sections.Count; i++)
        {
            Assert.Equal(i, sections[i].Index);
            Assert.Equal(at, sections[i].Start);
            Assert.InRange(sections[i].Length, 1, budget);
            at += sections[i].Length;
        }
        Assert.Equal(content.Length, at);
    }

    [Theory]
    [InlineData("")]
    [InlineData("One short paragraph.")]
    [InlineData("# Heading\n\nFirst paragraph.\n\nSecond paragraph.")]
    public void Content_within_the_budget_is_one_section_the_whole_content(string content)
    {
        var section = Assert.Single(MemorySections.Split(content, 200));
        Assert.Equal(new MemorySection(0, 0, content.Length), section);
    }

    [Fact]
    public void Content_exactly_at_the_budget_is_still_one_section()
    {
        var content = new string('a', 200);
        Assert.Single(MemorySections.Split(content, 200));
        Assert.Equal(2, MemorySections.Split(content + "b", 200).Count);
    }

    [Fact]
    public void Paragraphs_are_packed_together_while_they_fit_and_cut_at_a_paragraph_break()
    {
        var a = Words("alpha", 12);   // 71 characters
        var b = Words("bravo", 12);
        var c = Words("charlie", 12); // 95 characters
        var content = $"{a}\n\n{b}\n\n{c}";

        Assert.Equal([$"{a}\n\n{b}", c], Texts(content, 200));
        AssertCoversContent(content, 200);
    }

    [Fact]
    public void A_heading_starts_a_new_section_once_the_current_one_is_half_full()
    {
        var intro = Words("intro", 20);   // 119 characters: over half of 200
        var body = Words("body", 6);
        var tail = Words("tail", 20);

        // The heading and its paragraph would fit after the intro; a heading is a better place to cut.
        var texts = Texts($"{intro}\n\n## Details\n\n{body}\n\n{tail}", 200);
        Assert.Equal([intro, $"## Details\n\n{body}\n\n{tail}"], texts);

        // The same text without the heading mark is packed by size alone.
        var plain = Texts($"{intro}\n\nDetails\n\n{body}\n\n{tail}", 200);
        Assert.Equal([$"{intro}\n\nDetails\n\n{body}", tail], plain);
    }

    [Fact]
    public void A_heading_stays_with_the_paragraph_under_it()
    {
        var first = Words("first", 28);          // 167 characters: the heading alone would still fit after it
        var under = Words("under", 20);          // 119 characters
        var content = $"{first}\n\n## Next\n\n{under}";

        var texts = Texts(content, 200);
        Assert.Equal([first, $"## Next\n\n{under}"], texts);
    }

    [Fact]
    public void A_heading_looking_line_inside_a_code_fence_is_not_a_heading()
    {
        var before = Words("before", 18);        // 125 characters: over half
        var content = $"{before}\n```\n# not a heading\nx = 1\n```\n{Words("after", 30)}";

        var texts = Texts(content, 200);
        // No blank line and no real heading before the fence: nothing may cut "before" from its code.
        Assert.Contains("# not a heading", texts[0]);
        AssertCoversContent(content, 200);
    }

    [Fact]
    public void A_paragraph_longer_than_the_budget_is_cut_at_line_ends_then_sentences_then_spaces()
    {
        var lines = string.Join("\n", Enumerable.Range(0, 12).Select(i => $"line {i:00} " + Words("x", 20)));
        Assert.All(Texts(lines, 200), t => Assert.StartsWith("line ", t));
        AssertCoversContent(lines, 200);

        var sentences = string.Join(" ", Enumerable.Range(0, 12).Select(i => $"Sentence {i:00} goes on for a while and then it stops."));
        Assert.All(Texts(sentences, 200), t => Assert.StartsWith("Sentence ", t));
        Assert.All(Texts(sentences, 200), t => Assert.EndsWith("stops.", t));
        AssertCoversContent(sentences, 200);

        var spaced = Words("unpunctuated", 60);
        Assert.All(Texts(spaced, 200), t => Assert.All(t.Split(' '), w => Assert.Equal("unpunctuated", w)));
        AssertCoversContent(spaced, 200);
    }

    [Fact]
    public void Text_with_no_place_to_cut_is_cut_at_the_budget_but_never_inside_a_surrogate_pair()
    {
        // 150 rockets: 300 UTF-16 units and not one space. A cut at an odd offset would split a pair.
        var content = string.Concat(Enumerable.Repeat("\U0001F680", 150));

        var sections = MemorySections.Split(content, 101);
        Assert.True(sections.Count >= 3);
        foreach (var section in sections)
        {
            var text = content.Substring(section.Start, section.Length);
            Assert.False(char.IsLowSurrogate(text[0]), "a section begins with the second half of a pair");
            Assert.False(char.IsHighSurrogate(text[^1]), "a section ends with the first half of a pair");
            Assert.InRange(section.Length, 1, 101);
        }
        Assert.Equal(content.Length, sections.Sum(s => s.Length));
    }

    [Fact]
    public void Cyrillic_and_cjk_text_is_cut_like_any_other()
    {
        var cyrillic = string.Join("\n\n", Enumerable.Range(0, 8).Select(i => $"Абзац {i}. " + Words("очередь", 12)));
        AssertCoversContent(cyrillic, 200);
        Assert.All(Texts(cyrillic, 200), t => Assert.StartsWith("Абзац ", t));

        var cjk = string.Concat(Enumerable.Repeat("再試行のキューが満杯です。", 40));
        AssertCoversContent(cjk, 100);
        Assert.All(Texts(cjk, 100), t => Assert.EndsWith("。", t));
    }

    [Fact]
    public void Windows_line_ends_are_paragraph_breaks_too()
    {
        var a = Words("alpha", 25);
        var b = Words("bravo", 25);
        Assert.Equal([a, b], Texts($"{a}\r\n\r\n{b}", 200));
    }
}
