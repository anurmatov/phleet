using Fleet.Memory.Data;
using Fleet.Memory.Models;
using Fleet.Memory.Services;
using Microsoft.Extensions.Logging;

namespace Fleet.Memory.Tests;

/// <summary>
/// What a memory becomes in the index: one point when it is short, one per section when it is long,
/// and never more than one entry wherever memories are listed or counted. Invented memories only.
/// </summary>
public sealed class MemoryIndexingTests : IDisposable
{
    private static readonly MemoryIndexSettings Small = new() { SectionChars = 200, MaxSections = 200 };

    private readonly MemoryFixture _f = new(Small);

    public void Dispose() => _f.Dispose();

    private static string Words(string word, int count) => string.Join(" ", Enumerable.Repeat(word, count));

    /// <summary>Five paragraphs of 119 characters: five sections with a budget of 200.</summary>
    private static string LongContent(string word = "ledger") =>
        string.Join("\n\n", Enumerable.Range(0, 5).Select(i => $"part{i} " + Words(word, 16)));

    // ── A5: a short memory stays whole ──────────────────────────────────────

    [Fact]
    public async Task A_short_memory_is_one_point_embedded_as_title_blank_line_content_like_before()
    {
        var doc = await _f.AddAsync("Retry budget", "Three attempts, then the task is parked.", tags: ["queues"]);

        Assert.Equal(["Retry budget\n\nThree attempts, then the task is parked."], _f.Embedder.Inputs);
        Assert.Equal(MemoryService.EmbeddingText(doc), _f.Embedder.Inputs.Single());

        var point = Assert.Single(_f.Index.PointsOf(doc.FilePath));
        Assert.Equal(0, point.Payload[IndexPayload.SectionIndex]);
        Assert.Equal(1, point.Payload[IndexPayload.SectionCount]);
        Assert.Equal(0, point.Payload[IndexPayload.SectionStart]);
        Assert.Equal(doc.Content.Length, point.Payload[IndexPayload.SectionLength]);
        Assert.Equal(doc.Id, point.Payload[IndexPayload.MemoryId]);
        Assert.Equal(doc.Updated.ToUnixTimeSeconds(), point.Payload[IndexPayload.UpdatedTs]);
        Assert.Equal(MemoryIndexPlan.Format, point.Payload[IndexPayload.IndexFormat]);
    }

    [Fact]
    public async Task The_keyword_text_of_a_memory_is_its_title_its_tags_and_its_content()
    {
        var doc = await _f.AddAsync("Retry budget", "Three attempts.", tags: ["queues", "backpressure"]);

        Assert.Equal("Retry budget\nqueues\nbackpressure\nThree attempts.", _f.Index.PointsOf(doc.FilePath).Single().SparseText);
    }

    // ── A5: a long memory is indexed in sections ────────────────────────────

    [Fact]
    public async Task A_long_memory_is_one_point_per_section_each_embedded_as_the_title_and_that_section()
    {
        var content = LongContent();
        var doc = await _f.AddAsync("Ledger notes", content, tags: ["finance"]);

        var sections = MemorySections.Split(content, 200);
        Assert.Equal(5, sections.Count);
        Assert.Equal(sections.Select(s => "Ledger notes\n\n" + MemorySections.Text(content, s)), _f.Embedder.Inputs);

        var points = _f.Index.PointsOf(doc.FilePath);
        Assert.Equal([0, 1, 2, 3, 4], points.Select(p => (int)p.Payload[IndexPayload.SectionIndex]));
        Assert.All(points, p => Assert.Equal(5, p.Payload[IndexPayload.SectionCount]));
        Assert.Equal(sections.Select(s => (s.Start, s.Length)),
            points.Select(p => ((int)p.Payload[IndexPayload.SectionStart], (int)p.Payload[IndexPayload.SectionLength])));
        // Every point carries the memory's payload, and they all say the same hash.
        Assert.All(points, p => Assert.Equal(doc.Id, p.Payload[IndexPayload.MemoryId]));
        Assert.Single(points.Select(p => p.Payload[IndexPayload.IndexHash]).Distinct());

        // The title and the tags are keywords of the memory once, not of every section.
        Assert.StartsWith("Ledger notes\nfinance\npart0 ", points[0].SparseText);
        Assert.All(points.Skip(1), p => Assert.DoesNotContain("finance", p.SparseText));
    }

    [Fact]
    public async Task Point_ids_are_the_same_every_time_and_the_first_is_the_one_a_memory_always_had()
    {
        var doc = await _f.AddAsync("Ledger notes", LongContent());
        var before = _f.Index.Points.Count;

        _f.Restart(Small);                       // nothing remembered: it is embedded and written again
        await _f.Service.IndexFileAsync(doc.FilePath);

        Assert.Equal(before, _f.Index.Points.Count);
        Assert.NotEqual(VectorStore.PointIdFor(doc.FilePath, 0), VectorStore.PointIdFor(doc.FilePath, 1));
        Assert.Equal(VectorStore.PointIdFor(doc.FilePath, 1), VectorStore.PointIdFor(doc.FilePath, 1));
        Assert.NotEqual(VectorStore.PointIdFor(doc.FilePath, 1), VectorStore.PointIdFor(doc.FilePath + "x", 1));
    }

    [Fact]
    public async Task An_update_replaces_every_section_and_leaves_none_of_the_longer_version()
    {
        var doc = await _f.AddAsync("Ledger notes", LongContent());
        Assert.Equal(5, _f.Index.PointsOf(doc.FilePath).Count);

        await _f.Service.UpdateAsync(doc.Id, content: $"{Words("short", 20)}\n\n{Words("second", 20)}");

        var points = _f.Index.PointsOf(doc.FilePath);
        Assert.Equal([0, 1], points.Select(p => (int)p.Payload[IndexPayload.SectionIndex]));
        Assert.All(points, p => Assert.Equal(2, p.Payload[IndexPayload.SectionCount]));
        Assert.All(points, p => Assert.DoesNotContain("ledger", p.SparseText));

        await _f.Service.UpdateAsync(doc.Id, content: "Now it is short.");
        Assert.Single(_f.Index.PointsOf(doc.FilePath));
    }

    [Fact]
    public async Task A_delete_removes_every_section()
    {
        var keep = await _f.AddAsync("Kept", LongContent("keeper"));
        var doc = await _f.AddAsync("Ledger notes", LongContent());

        await _f.Service.DeleteAsync(doc.Id);

        Assert.Empty(_f.Index.PointsOf(doc.FilePath));
        Assert.Equal(5, _f.Index.PointsOf(keep.FilePath).Count);
        Assert.DoesNotContain(_f.Service.Catalogue.Entries, e => e.Id == doc.Id);
    }

    [Fact]
    public async Task Removing_a_file_from_the_index_removes_every_section()
    {
        var doc = await _f.AddAsync("Ledger notes", LongContent());
        File.Delete(doc.FilePath);

        await _f.Service.RemoveFromIndexAsync(doc.FilePath);

        Assert.Empty(_f.Index.Points);
        Assert.Empty(_f.Service.Catalogue.Entries);
    }

    [Fact]
    public async Task Lists_and_ids_show_a_memory_of_many_sections_once()
    {
        var longDoc = await _f.AddAsync("Ledger notes", LongContent(), project: "alpha", tags: ["finance"]);
        var shortDoc = await _f.AddAsync("Retry budget", "Three attempts.", project: "alpha");

        var listed = await _f.Service.ListAsync();
        Assert.Equal(new[] { longDoc.Id, shortDoc.Id }.Order(), listed.Select(d => d["memory_id"]).Order());

        // What /internal/memory and /internal/memory/ids read, and the filters memory_list passes.
        Assert.Equal(2, (await _f.Service.ListAsync(project: "alpha")).Count);
        Assert.Equal(longDoc.Id, Assert.Single(await _f.Service.ListAsync(tag: "finance"))["memory_id"]);
        Assert.Equal("finance", Assert.Single(await _f.Service.ListAsync(tag: "finance"))["tags"]);
        Assert.Equal(new[] { longDoc.Id, shortDoc.Id }.Order(), (await _f.Service.ListDocumentsAsync()).Select(d => d.Id).Order());
    }

    [Fact]
    public async Task A_listed_memory_says_when_it_was_updated_not_when_it_was_created()
    {
        var created = new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);
        var doc = await _f.Store.SaveAsync(new MemoryDocument
        {
            Id = Guid.NewGuid().ToString(), Type = "learning", Title = "Retry budget", Content = LongContent(),
            Created = created, Updated = created.AddDays(40),
        });
        await _f.Service.IndexFileAsync(doc.FilePath);
        var old = await _f.WriteAsync("Indexed before", "Three attempts.", updated: created);
        _f.Index.AddLegacyPoint(old, _f.Embedder.Embed("old"));

        var listed = await _f.Service.ListAsync();

        // What GET /internal/memory answers as updated_at.
        var entry = listed.Single(e => e["memory_id"] == doc.Id);
        Assert.Equal(created.AddDays(40), DateTimeOffset.Parse(Fleet.Memory.Endpoints.MemoryListEntry.UpdatedAt(entry)));
        Assert.NotEqual(entry["created"], Fleet.Memory.Endpoints.MemoryListEntry.UpdatedAt(entry));
        // A point from before the payload carried it has only the created time to give.
        var legacy = listed.Single(e => e["memory_id"] == old.Id);
        Assert.Equal(legacy["created"], Fleet.Memory.Endpoints.MemoryListEntry.UpdatedAt(legacy));
        Assert.Equal("", Fleet.Memory.Endpoints.MemoryListEntry.UpdatedAt(new Dictionary<string, string>()));
    }

    [Fact]
    public async Task The_duplicate_hint_names_a_memory_of_many_similar_sections_once()
    {
        using var f = new MemoryFixture(Small, similarityThreshold: 0.85f);
        // Five sections, every one of them the same words as the memory about to be stored.
        var existing = await f.AddAsync("Ledger notes", string.Join("\n\n", Enumerable.Repeat(Words("ledger", 16), 5)));
        Assert.Equal(5, f.Index.PointsOf(existing.FilePath).Count);

        var (_, similar, _, _) = await f.Service.StoreAsync(new MemoryDocument
        {
            Id = Guid.NewGuid().ToString(), Type = "learning", Title = "Ledger notes", Content = Words("ledger", 16),
        });

        var hint = Assert.Single(similar);
        Assert.Equal(existing.Id, hint.Id);
    }

    [Fact]
    public async Task Past_the_cap_the_first_sections_are_indexed_and_one_warning_names_only_the_id_and_the_counts()
    {
        using var f = new MemoryFixture(new MemoryIndexSettings { SectionChars = 200, MaxSections = 3 });
        var doc = await f.AddAsync("SECRET-TITLE", LongContent("secretword"));

        Assert.Equal([0, 1, 2], f.Index.PointsOf(doc.FilePath).Select(p => (int)p.Payload[IndexPayload.SectionIndex]));
        Assert.All(f.Index.PointsOf(doc.FilePath), p => Assert.Equal(3, p.Payload[IndexPayload.SectionCount]));
        Assert.Equal(3, f.Embedder.Calls);

        var warning = Assert.Single(f.Log.Entries, e => e.Level == LogLevel.Warning);
        Assert.Equal($"Memory {doc.Id} has 5 sections; only the first 3 are indexed", warning.Message);
    }

    // ── Old points ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_point_from_before_sections_is_overwritten_not_left_beside_the_new_ones()
    {
        var shortDoc = await _f.WriteAsync("Retry budget", "Three attempts.");
        var longDoc = await _f.WriteAsync("Ledger notes", LongContent());
        _f.Index.AddLegacyPoint(shortDoc, _f.Embedder.Embed("old"));
        _f.Index.AddLegacyPoint(longDoc, _f.Embedder.Embed("old"));

        await _f.Service.IndexFileAsync(shortDoc.FilePath);
        await _f.Service.IndexFileAsync(longDoc.FilePath);

        Assert.Single(_f.Index.PointsOf(shortDoc.FilePath));
        Assert.Equal(5, _f.Index.PointsOf(longDoc.FilePath).Count);
        Assert.All(_f.Index.Points, p => Assert.Equal(MemoryIndexPlan.Format, p.Payload[IndexPayload.IndexFormat]));
    }

    // ── Failures ────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_embedder_that_fails_half_way_leaves_the_index_as_it_was_and_the_next_attempt_does_it_all()
    {
        var doc = await _f.AddAsync("Ledger notes", LongContent());
        var before = _f.Index.PointsOf(doc.FilePath).Select(p => p.SparseText).ToList();

        // An edit behind the service's back, then indexing with the embedder failing on the third section.
        await _f.Store.UpdateAsync(doc.FilePath, d => d.Content = LongContent("journal"));
        _f.Embedder.FailWhen = text => text.Contains("part2 ");
        await Assert.ThrowsAsync<HttpRequestException>(() => _f.Service.IndexFileAsync(doc.FilePath));

        Assert.Equal(before, _f.Index.PointsOf(doc.FilePath).Select(p => p.SparseText));

        _f.Embedder.FailWhen = null;
        await _f.Service.IndexFileAsync(doc.FilePath);
        Assert.All(_f.Index.PointsOf(doc.FilePath), p => Assert.Contains("journal", p.SparseText));
    }

    [Fact]
    public async Task A_write_that_stopped_before_the_old_points_were_removed_is_done_again()
    {
        var doc = await _f.AddAsync("Ledger notes", LongContent());
        await _f.Store.UpdateAsync(doc.FilePath, d => d.Content = "Now it is short.");

        _f.Index.FailNextDeleteStale = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => _f.Service.IndexFileAsync(doc.FilePath));
        Assert.Equal(5, _f.Index.PointsOf(doc.FilePath).Count);   // one new point, four left over

        var calls = _f.Embedder.Calls;
        await _f.Service.IndexFileAsync(doc.FilePath);
        Assert.Equal(calls + 1, _f.Embedder.Calls);
        Assert.Single(_f.Index.PointsOf(doc.FilePath));
    }

    [Fact]
    public async Task A_file_that_cannot_be_parsed_is_not_in_the_catalogue()
    {
        var doc = await _f.AddAsync("Retry budget", "Three attempts.");
        Assert.Single(_f.Service.Catalogue.Entries);

        await File.WriteAllTextAsync(doc.FilePath, "---\nid: x\ntags: [unclosed\n---\ncontent");
        await Assert.ThrowsAsync<InvalidDataException>(() => _f.Service.IndexFileAsync(doc.FilePath));

        Assert.Empty(_f.Service.Catalogue.Entries);
    }

    [Fact]
    public async Task A_memory_is_in_the_catalogue_even_when_it_could_not_be_indexed()
    {
        _f.Embedder.Down = true;
        var doc = await _f.WriteAsync("Retry budget", "Three attempts.", tags: ["queues"]);
        await Assert.ThrowsAsync<HttpRequestException>(() => _f.Service.IndexFileAsync(doc.FilePath));

        var entry = Assert.Single(_f.Service.Catalogue.Entries);
        Assert.Equal((doc.Id, "Retry budget", doc.FilePath), (entry.Id, entry.Title, entry.FilePath));
        Assert.Equal(["queues"], entry.Tags);
    }

    // ── A6: what has not changed is not embedded again ──────────────────────

    [Fact]
    public async Task Indexing_the_same_file_again_embeds_nothing()
    {
        var doc = await _f.AddAsync("Ledger notes", LongContent());
        var calls = _f.Embedder.Calls;

        await _f.Service.IndexFileAsync(doc.FilePath);   // the watcher's event after an API write

        Assert.Equal(calls, _f.Embedder.Calls);
        Assert.Equal((1, 5, 1), _f.Service.IndexCounts);
    }

    [Fact]
    public async Task A_change_to_anything_the_points_are_made_from_embeds_again()
    {
        var doc = await _f.AddAsync("Ledger notes", "Short.", tags: ["a"], project: "alpha");

        foreach (var change in new Action<MemoryDocument>[]
                 {
                     d => d.Content = "Changed.",
                     d => d.Title = "Other title",
                     d => d.Tags = ["a", "b"],
                     d => d.Project = "beta",
                 })
        {
            await _f.Store.UpdateAsync(doc.FilePath, change);
            var calls = _f.Embedder.Calls;
            await _f.Service.IndexFileAsync(doc.FilePath);
            Assert.Equal(calls + 1, _f.Embedder.Calls);
        }

        Assert.Equal("beta", _f.Index.PointsOf(doc.FilePath).Single().Payload[IndexPayload.Project]);
    }

    [Fact]
    public void The_hash_depends_on_the_model_and_the_section_settings()
    {
        var doc = new MemoryDocument { Id = "id", Type = "learning", Title = "t", Content = "c" };
        var hash = MemoryIndexPlan.HashOf(doc, "/p", new MemoryIndexSettings { EmbedderId = "onnx/a/384" });

        Assert.Equal(hash, MemoryIndexPlan.HashOf(doc, "/p", new MemoryIndexSettings { EmbedderId = "onnx/a/384" }));
        Assert.NotEqual(hash, MemoryIndexPlan.HashOf(doc, "/p", new MemoryIndexSettings { EmbedderId = "ollama/b/1024" }));
        Assert.NotEqual(hash, MemoryIndexPlan.HashOf(doc, "/p", new MemoryIndexSettings { EmbedderId = "onnx/a/384", SectionChars = 800 }));
        Assert.NotEqual(hash, MemoryIndexPlan.HashOf(doc, "/p", new MemoryIndexSettings { EmbedderId = "onnx/a/384", MaxSections = 50 }));
        Assert.NotEqual(hash, MemoryIndexPlan.HashOf(doc, "/q", new MemoryIndexSettings { EmbedderId = "onnx/a/384" }));
        // The min similarity only changes what a search leaves out, never what is indexed.
        Assert.Equal(hash, MemoryIndexPlan.HashOf(doc, "/p", new MemoryIndexSettings { EmbedderId = "onnx/a/384", MinMeaningScore = 0.5f }));
    }

    [Fact]
    public void Points_that_are_not_one_whole_memory_in_the_current_format_have_no_hash_to_trust()
    {
        const int f = MemoryIndexPlan.Format;
        var hashes = MemoryIndexPlan.IndexedHashes(
        [
            new IndexPointInfo("whole", "h1", f, 0, 2), new IndexPointInfo("whole", "h1", f, 1, 2),
            new IndexPointInfo("one", "h2", f, 0, 1),
            new IndexPointInfo("legacy", "", 0, 0, 1),
            new IndexPointInfo("old-format", "h3", f - 1, 0, 1),
            new IndexPointInfo("missing-a-section", "h4", f, 0, 3), new IndexPointInfo("missing-a-section", "h4", f, 2, 3),
            new IndexPointInfo("two-versions", "h5", f, 0, 2), new IndexPointInfo("two-versions", "h6", f, 1, 2),
            new IndexPointInfo("left-over", "h7", f, 0, 1), new IndexPointInfo("left-over", "h7", f, 1, 1),
        ]);

        Assert.Equal("h1", hashes["whole"]);
        Assert.Equal("h2", hashes["one"]);
        foreach (var path in new[] { "legacy", "old-format", "missing-a-section", "two-versions", "left-over" })
            Assert.Null(hashes[path]);
    }
}
