using Fleet.Memory.Services;

namespace Fleet.Memory.Tests;

/// <summary>
/// What a search returns, and in what order: by id, by title, by tag, then what the index finds by
/// keyword and by meaning. Invented memories only.
/// </summary>
public sealed class MemorySearchTests : IDisposable
{
    private static readonly MemoryIndexSettings Small = new() { SectionChars = 200, MaxSections = 200 };

    private readonly MemoryFixture _f = new(Small);

    public void Dispose() => _f.Dispose();

    private static string Words(string word, int count) => string.Join(" ", Enumerable.Repeat(word, count));

    private static List<(string Title, string Match)> Shape(IEnumerable<MemorySearchHit> hits) =>
        hits.Select(h => (h.Document.Title, h.Match)).ToList();

    private static List<string> Marked(MemorySearchHit hit) =>
        hit.Snippet.Highlights.Select(h => hit.Snippet.Text.Substring(h.Start, h.Length)).ToList();

    // ── A1: by id ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(13)]
    [InlineData(36)]
    public async Task A_query_that_is_an_id_or_the_start_of_one_finds_that_memory(int length)
    {
        const string id = "5e1f9a3c-7b2d-4c6e-8f10-a1b2c3d4e5f6";
        await _f.AddAsync("Unrelated neighbour", "Five ones and an eff: 5e1f9a is not in this text as an id.", id: "0b0b0b0b-1111-4222-8333-444444444444");
        var wanted = await _f.AddAsync("Retry budget", "Three attempts, then the task is parked.", id: id);
        var calls = _f.Embedder.Calls;

        var hits = await _f.Service.SearchAsync(id[..length]);

        var hit = Assert.Single(hits);
        Assert.Equal((wanted.Id, MemoryMatch.Id), (hit.Document.Id, hit.Match));
        Assert.Null(hit.Score);
        Assert.Equal("Three attempts, then the task is parked.", hit.Snippet.Text);
        // The id names the memory: the index and the embedder are not asked for its neighbours.
        Assert.Equal(calls, _f.Embedder.Calls);
        Assert.Equal(0, _f.Index.SearchCalls);
    }

    [Fact]
    public async Task An_id_is_found_whatever_its_case_and_without_its_dashes()
    {
        var wanted = await _f.AddAsync("Retry budget", "Three attempts.", id: "5e1f9a3c-7b2d-4c6e-8f10-a1b2c3d4e5f6");

        Assert.Equal(wanted.Id, Assert.Single(await _f.Service.SearchAsync(" 5E1F9A3C ")).Document.Id);
        Assert.Equal(wanted.Id, Assert.Single(await _f.Service.SearchAsync("5e1f9a3c7b2d")).Document.Id);
    }

    [Fact]
    public async Task An_ambiguous_prefix_returns_every_candidate()
    {
        var a = await _f.AddAsync("First", "One.", id: "5e1f9a3c-0000-4000-8000-000000000001");
        var b = await _f.AddAsync("Second", "Two.", id: "5e1f9a77-0000-4000-8000-000000000002");
        await _f.AddAsync("Third", "Three.", id: "6e1f9a3c-0000-4000-8000-000000000003");

        var hits = await _f.Service.SearchAsync("5e1f9a");

        Assert.Equal([(a.Id, MemoryMatch.Id), (b.Id, MemoryMatch.Id)], hits.Select(h => (h.Document.Id, h.Match)));
        // One more digit tells them apart.
        Assert.Equal(b.Id, Assert.Single(await _f.Service.SearchAsync("5e1f9a7")).Document.Id);
    }

    [Fact]
    public async Task A_memory_whose_title_names_the_id_comes_after_the_memory_itself()
    {
        var wanted = await _f.AddAsync("Original", "One.", id: "5e1f9a3c-0000-4000-8000-000000000001");
        var mention = await _f.AddAsync("Continuation of 5e1f9a3c", "Two.", id: "0b0b0b0b-0000-4000-8000-000000000002");

        var hits = await _f.Service.SearchAsync("5e1f9a3c");

        Assert.Equal([(wanted.Id, MemoryMatch.Id), (mention.Id, MemoryMatch.Title)], hits.Select(h => (h.Document.Id, h.Match)));
    }

    [Fact]
    public async Task Hexadecimal_looking_words_that_are_no_memorys_id_are_searched_as_words()
    {
        await _f.AddAsync("Styling", "The facade pattern hides the subsystem.", id: "0b0b0b0b-0000-4000-8000-000000000002");

        var hit = Assert.Single(await _f.Service.SearchAsync("facade"));

        Assert.Equal(MemoryMatch.Keyword, hit.Match);
    }

    // ── A1: title and tag, before what the index finds ──────────────────────

    [Fact]
    public async Task Title_then_tag_then_keyword_then_meaning_and_each_memory_once()
    {
        _f.Embedder.Same["postponement"] = "backoff";   // a different word that means the same

        var keyword = await _f.AddAsync("Queue notes", "Use backoff between attempts. Backoff, backoff, backoff.");
        var meaning = await _f.AddAsync("Scheduling", "Postponement grows with every failure.");
        var tagged = await _f.AddAsync("Timers", "Nothing about the word here.", tags: ["Backoff"]);
        var titled = await _f.AddAsync("Exponential BACKOFF rules", "The word backoff is in the content too, and in a tag.", tags: ["backoff"]);
        var unrelated = await _f.AddAsync("Elsewhere", "Completely different words: orchard, lantern, harbour.");

        var hits = await _f.Service.SearchAsync("backoff");

        // There is no cut-off: the meaning side returns its nearest, however far. The caller shows
        // the meaning hits apart, and the similarity says how near each is.
        Assert.Equal(
        [
            (titled.Title, MemoryMatch.Title),
            (tagged.Title, MemoryMatch.Tag),
            (keyword.Title, MemoryMatch.Keyword),
            (meaning.Title, MemoryMatch.Meaning),
            (unrelated.Title, MemoryMatch.Meaning),
        ], Shape(hits));

        // Only a hit by meaning alone carries a similarity.
        Assert.All(hits.Take(3), h => Assert.Null(h.Score));
        Assert.InRange(hits[3].Score!.Value, 0.2f, 1f);
        Assert.True(hits[4].Score < hits[3].Score);
    }

    [Fact]
    public async Task A_title_that_is_the_query_comes_before_one_that_starts_with_it_and_one_that_contains_it()
    {
        var contains = await _f.AddAsync("About the retry budget", "c", updated: new DateTimeOffset(2026, 3, 3, 0, 0, 0, TimeSpan.Zero));
        var starts = await _f.AddAsync("Retry budget for uploads", "b", updated: new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero));
        var exact = await _f.AddAsync("Retry Budget", "a", updated: new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));

        var hits = await _f.Service.SearchAsync("retry budget");

        Assert.Equal([exact.Title, starts.Title, contains.Title], hits.Select(h => h.Document.Title));
        Assert.All(hits, h => Assert.Equal(MemoryMatch.Title, h.Match));
    }

    [Fact]
    public async Task A_tag_is_matched_whole_not_as_a_part()
    {
        await _f.AddAsync("One", "Nothing here.", tags: ["queues-eu"]);
        var whole = await _f.AddAsync("Two", "Nothing here either.", tags: ["Queues"]);

        var hits = await _f.Service.SearchAsync("queues");

        Assert.Equal((whole.Title, MemoryMatch.Tag), Shape(hits).First());
        Assert.DoesNotContain(hits, h => h.Document.Title == "One" && h.Match == MemoryMatch.Tag);
    }

    [Fact]
    public async Task The_limit_counts_every_kind_together()
    {
        for (var i = 0; i < 4; i++) await _f.AddAsync($"Backoff note {i}", "backoff");
        for (var i = 0; i < 4; i++) await _f.AddAsync($"Other {i}", "Use backoff here.");

        var hits = await _f.Service.SearchAsync("backoff", limit: 6);

        Assert.Equal(6, hits.Count);
        Assert.Equal(4, hits.Count(h => h.Match == MemoryMatch.Title));
        Assert.Equal(2, hits.Count(h => h.Match == MemoryMatch.Keyword));
    }

    // ── A2: keywords in any script, and tags ────────────────────────────────

    [Theory]
    [InlineData("Очередь задач переполнена после сбоя.", "очередь")]
    [InlineData("Η ουρά εργασιών γέμισε μετά την αποτυχία.", "ΟΥΡΆ")]
    [InlineData("再試行のキューが満杯になった。", "満杯")]
    [InlineData("La señal de caché se agotó.", "caché")]
    public async Task Text_in_any_script_is_found_by_keyword(string content, string query)
    {
        var wanted = await _f.AddAsync("Note 1", content);
        await _f.AddAsync("Note 2", "Plain English about something else entirely.");

        var hits = await _f.Service.SearchAsync(query);

        Assert.Equal((wanted.Id, MemoryMatch.Keyword), (hits[0].Document.Id, hits[0].Match));
        Assert.NotEmpty(Marked(hits[0]));
        Assert.DoesNotContain(hits, h => h.Document.Title == "Note 2" && h.Match == MemoryMatch.Keyword);
    }

    [Fact]
    public async Task A_word_of_a_tag_is_a_keyword_of_the_memory()
    {
        // "Tag" hits need the whole tag. A word inside a tag is found by keyword, and only because
        // the tags are part of the keyword text.
        var tagged = await _f.AddAsync("Timers", "Nothing about that word here.", tags: ["postgres-migration"]);
        await _f.AddAsync("Other", "Unrelated text.");

        var hit = Assert.Single(await _f.Service.SearchAsync("postgres"), h => h.Match == MemoryMatch.Keyword);

        Assert.Equal(tagged.Id, hit.Document.Id);
    }

    // ── A3: snippets ────────────────────────────────────────────────────────

    /// <summary>Three paragraphs, each too long to share a section of 200 characters with another.</summary>
    private static readonly string ThreeSections =
        $"{Words("filler", 22)}\n\n{Words("padding", 19)}\n\nThe ledger is reconciled nightly. {Words("ledger", 16)}";

    [Fact]
    public async Task A_keyword_hit_shows_the_passage_that_matched_with_the_words_marked()
    {
        await _f.AddAsync("Finance", ThreeSections);

        var hit = Assert.Single(await _f.Service.SearchAsync("nightly"));

        Assert.Equal(MemoryMatch.Keyword, hit.Match);
        Assert.StartsWith("…The ledger is reconciled nightly. ledger", hit.Snippet.Text);
        Assert.Equal(["nightly"], Marked(hit));
    }

    [Fact]
    public async Task A_meaning_hit_shows_the_start_of_the_section_that_matched()
    {
        _f.Embedder.Same["bookkeeping"] = "ledger";
        await _f.AddAsync("Finance", ThreeSections);

        var hit = Assert.Single(await _f.Service.SearchAsync("bookkeeping"));

        Assert.Equal(MemoryMatch.Meaning, hit.Match);
        Assert.StartsWith("…The ledger is reconciled nightly. ledger", hit.Snippet.Text);
        Assert.Empty(hit.Snippet.Highlights);
        Assert.NotNull(hit.Score);
    }

    // ── A5: one hit per memory ──────────────────────────────────────────────

    [Fact]
    public async Task A_memory_of_many_matching_sections_is_one_hit_and_does_not_crowd_out_the_others()
    {
        // Eight sections, each with more of the word than the short memory has.
        var longDoc = await _f.AddAsync("Long", string.Join("\n\n", Enumerable.Range(0, 8).Select(i => $"part{i} " + Words("ledger", 20))));
        var shortDoc = await _f.AddAsync("Short", "One ledger.");
        Assert.Equal(8, _f.Index.PointsOf(longDoc.FilePath).Count);

        var hits = await _f.Service.SearchAsync("ledger", limit: 2);

        Assert.Equal([(longDoc.Title, MemoryMatch.Keyword), (shortDoc.Title, MemoryMatch.Keyword)], Shape(hits));
    }

    // ── A4: filters ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Filters_apply_to_every_kind()
    {
        var old = new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero);
        var recent = new DateTimeOffset(2026, 3, 10, 0, 0, 0, TimeSpan.Zero);
        _f.Embedder.Same["postponement"] = "backoff";

        // One memory of each kind that passes every filter below, and one of each that fails one.
        await _f.AddAsync("Backoff in", "x", id: "aaaaaa01-0000-4000-8000-000000000001", tags: ["keep"], project: "alpha", type: "decision", agent: "dev", updated: recent);
        await _f.AddAsync("Tagged in", "x", tags: ["keep", "backoff"], project: "alpha", type: "decision", agent: "dev", updated: recent);
        await _f.AddAsync("Keyword in", "Use backoff.", tags: ["keep"], project: "alpha", type: "decision", agent: "dev", updated: recent);
        await _f.AddAsync("Meaning in", "Postponement grows.", tags: ["keep"], project: "alpha", type: "decision", agent: "dev", updated: recent);

        await _f.AddAsync("Backoff out by project", "x", tags: ["keep"], project: "beta", type: "decision", agent: "dev", updated: recent);
        await _f.AddAsync("Tagged out by type", "x", tags: ["keep", "backoff"], project: "alpha", type: "learning", agent: "dev", updated: recent);
        await _f.AddAsync("Keyword out by tag", "Use backoff.", tags: ["other"], project: "alpha", type: "decision", agent: "dev", updated: recent);
        await _f.AddAsync("Meaning out by date", "Postponement grows.", tags: ["keep"], project: "alpha", type: "decision", agent: "dev", updated: old);
        await _f.AddAsync("Keyword out by agent", "Use backoff.", tags: ["keep"], project: "alpha", type: "decision", agent: "ops", updated: recent);

        var hits = await _f.Service.SearchAsync("backoff", limit: 20, type: "decision", project: "Alpha", agent: "dev",
            tag: "keep", since: new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(
        [
            ("Backoff in", MemoryMatch.Title),
            ("Tagged in", MemoryMatch.Tag),
            ("Keyword in", MemoryMatch.Keyword),
            ("Meaning in", MemoryMatch.Meaning),
        ], Shape(hits));

        // An id is filtered like the rest: outside the filter it names no memory.
        Assert.Equal(MemoryMatch.Id, Assert.Single(await _f.Service.SearchAsync("aaaaaa01", project: "alpha")).Match);
        Assert.DoesNotContain(await _f.Service.SearchAsync("aaaaaa01", project: "beta"), h => h.Match == MemoryMatch.Id);
    }

    [Fact]
    public async Task Since_includes_the_day_it_names_and_tag_is_exact()
    {
        var day = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        await _f.AddAsync("Before", "Use backoff.", tags: ["Queues"], updated: day.AddSeconds(-1));
        await _f.AddAsync("On", "Use backoff.", tags: ["queues"], updated: day);
        await _f.AddAsync("After", "Use backoff.", tags: ["queues-eu"], updated: day.AddDays(5));

        Assert.Equal(["After", "On"], (await _f.Service.SearchAsync("backoff", since: day)).Select(h => h.Document.Title).Order());
        Assert.Equal(["On"], (await _f.Service.SearchAsync("backoff", tag: "queues")).Select(h => h.Document.Title));
    }

    // ── The optional cut-off ────────────────────────────────────────────────

    [Fact]
    public async Task By_default_no_meaning_hit_is_dropped_and_the_setting_drops_the_weak_ones_only()
    {
        _f.Embedder.Same["postponement"] = "backoff";
        await _f.AddAsync("Strong", "Postponement.");
        await _f.AddAsync("Weak", "Postponement among many other unrelated words: orchard lantern harbour meadow willow ember.");
        await _f.AddAsync("Keyword", "backoff among many other unrelated words: orchard lantern harbour meadow willow ember granite.");

        var all = await _f.Service.SearchAsync("backoff");
        Assert.Equal(["Keyword", "Strong", "Weak"], all.Select(h => h.Document.Title).Order());
        var weak = all.Single(h => h.Document.Title == "Weak").Score!.Value;
        var strong = all.Single(h => h.Document.Title == "Strong").Score!.Value;
        Assert.True(weak < strong);

        _f.Restart(Small with { MinMeaningScore = (weak + strong) / 2 });
        foreach (var path in Directory.EnumerateFiles(_f.Dir, "*.md", SearchOption.AllDirectories)) await _f.Service.IndexFileAsync(path);
        var cut = await _f.Service.SearchAsync("backoff");

        // The keyword hit is as weak by meaning as the dropped one: the cut-off is not for it.
        Assert.Equal(["Keyword", "Strong"], cut.Select(h => h.Document.Title).Order());
    }

    // ── When part of it cannot answer ───────────────────────────────────────

    [Fact]
    public async Task Without_the_embedder_ids_titles_tags_and_keywords_are_still_found()
    {
        var doc = await _f.AddAsync("Retry budget", "Use backoff between attempts.", id: "5e1f9a3c-0000-4000-8000-000000000001", tags: ["queues"]);
        _f.Embedder.Down = true;

        Assert.Equal(MemoryMatch.Id, Assert.Single(await _f.Service.SearchAsync("5e1f9a")).Match);
        Assert.Equal(MemoryMatch.Title, Assert.Single(await _f.Service.SearchAsync("retry")).Match);
        Assert.Equal(MemoryMatch.Tag, Assert.Single(await _f.Service.SearchAsync("QUEUES")).Match);
        Assert.Equal(MemoryMatch.Keyword, Assert.Single(await _f.Service.SearchAsync("backoff")).Match);
        Assert.Equal(doc.Id, Assert.Single(await _f.Service.SearchAsync("backoff")).Document.Id);
    }

    [Fact]
    public async Task One_or_two_characters_match_a_title_only_when_they_are_the_whole_title()
    {
        await _f.AddAsync("Retry budget", "Use backoff between attempts.");
        await _f.AddAsync("Rebuild notes", "How the index is rebuilt.");
        var whole = await _f.AddAsync("Re", "A reply prefix.");
        _f.Index.Down = true;

        var hits = await _f.Service.SearchAsync("re");

        var hit = Assert.Single(hits);
        Assert.Equal((whole.Id, MemoryMatch.Title), (hit.Document.Id, hit.Match));
        Assert.Equal(2, (await _f.Service.SearchAsync("reb")).Count + (await _f.Service.SearchAsync("ret")).Count);
    }

    [Fact]
    public async Task Without_the_index_ids_titles_and_tags_are_still_found()
    {
        await _f.AddAsync("Retry budget", "Use backoff between attempts.", id: "5e1f9a3c-0000-4000-8000-000000000001", tags: ["queues"]);
        _f.Index.Down = true;

        Assert.Equal(MemoryMatch.Id, Assert.Single(await _f.Service.SearchAsync("5e1f9a")).Match);
        Assert.Equal(MemoryMatch.Title, Assert.Single(await _f.Service.SearchAsync("retry")).Match);
        Assert.Equal(MemoryMatch.Tag, Assert.Single(await _f.Service.SearchAsync("queues")).Match);
    }

    [Fact]
    public async Task Nothing_found_is_not_said_when_part_of_the_search_could_not_run()
    {
        await _f.AddAsync("Retry budget", "Use backoff between attempts.");

        _f.Embedder.Down = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => _f.Service.SearchAsync("orchard"));

        _f.Embedder.Down = false;
        _f.Index.Down = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => _f.Service.SearchAsync("orchard"));

        _f.Index.Down = false;
        Assert.Empty(await _f.Service.SearchAsync("orchard", type: "decision"));
    }

    [Fact]
    public async Task A_cancelled_search_is_cancelled_not_answered()
    {
        await _f.AddAsync("Retry budget", "Use backoff between attempts.");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _f.Service.SearchAsync("backoff", ct: cts.Token));
    }

    [Fact]
    public async Task A_memory_not_indexed_yet_is_found_by_id_title_and_tag_from_the_files()
    {
        // The first start after an upgrade: every file has been read, none has been embedded.
        var doc = await _f.WriteAsync("Retry budget", "Use backoff between attempts.", id: "5e1f9a3c-0000-4000-8000-000000000001", tags: ["queues"]);
        _f.Service.SeedCatalogue([doc]);

        Assert.Equal(MemoryMatch.Id, Assert.Single(await _f.Service.SearchAsync("5e1f9a3c")).Match);
        Assert.Equal(MemoryMatch.Title, Assert.Single(await _f.Service.SearchAsync("budget")).Match);
        Assert.Equal(MemoryMatch.Tag, Assert.Single(await _f.Service.SearchAsync("queues")).Match);
        Assert.Empty(_f.Index.Points);
    }

    [Fact]
    public async Task A_memory_still_indexed_the_old_way_is_found_and_shown_from_its_whole_content()
    {
        // During that first start: a point from before sections, with no section range and no tags
        // in its keywords.
        var doc = await _f.WriteAsync("Finance", $"{Words("filler", 60)} The ledger is reconciled nightly.");
        _f.Index.AddLegacyPoint(doc, _f.Embedder.Embed($"{doc.Title}\n\n{doc.Content}"));
        _f.Service.SeedCatalogue([doc]);

        var hit = Assert.Single(await _f.Service.SearchAsync("ledger"));

        Assert.Equal(MemoryMatch.Keyword, hit.Match);
        Assert.Contains("The ledger is reconciled nightly.", hit.Snippet.Text);
        Assert.Equal(["ledger"], Marked(hit));
    }

    [Fact]
    public async Task A_file_that_went_corrupt_or_away_after_it_was_indexed_is_left_out()
    {
        var corrupt = await _f.AddAsync("Backoff one", "Use backoff.");
        var gone = await _f.AddAsync("Backoff two", "Use backoff.");
        var fine = await _f.AddAsync("Backoff three", "Use backoff.");
        await File.WriteAllTextAsync(corrupt.FilePath, "---\nid: x\ntags: [unclosed\n---\ncontent");
        File.Delete(gone.FilePath);

        var hits = await _f.Service.SearchAsync("backoff");

        Assert.Equal(fine.Id, Assert.Single(hits).Document.Id);
    }

    [Fact]
    public async Task A_blank_query_or_no_room_finds_nothing()
    {
        await _f.AddAsync("Retry budget", "Use backoff.");

        Assert.Empty(await _f.Service.SearchAsync("   "));
        Assert.Empty(await _f.Service.SearchAsync("backoff", limit: 0));
    }
}
