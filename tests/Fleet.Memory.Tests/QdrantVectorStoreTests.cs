using Fleet.Memory.Configuration;
using Fleet.Memory.Data;
using Fleet.Memory.Models;
using Fleet.Memory.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Qdrant.Client;

namespace Fleet.Memory.Tests;

/// <summary>
/// For the tests that talk to a real Qdrant. Everything else in this project uses
/// <see cref="FakeVectorIndex"/>, which answers the way Qdrant is expected to; these tests are where
/// that expectation is checked: the real <see cref="VectorStore"/>, the real requests, a real server.
///
/// They run when <see cref="Variable"/> names a Qdrant's gRPC address and are skipped, with this
/// reason, when it does not. <c>bash tests/memory-qdrant/run.sh</c> starts a Qdrant of the release the
/// compose file pins (or <c>QDRANT_VERSION</c>), runs them and refuses a run that skipped any; CI
/// runs that script for the pinned release and the one before it.
/// </summary>
internal static class RealQdrant
{
    public const string Variable = "FLEET_MEMORY_TEST_QDRANT_URL";

    public const string Reason =
        "needs a real Qdrant: set " + Variable + " to its gRPC address (http://127.0.0.1:6334), "
        + "or run it with one: bash tests/memory-qdrant/run.sh";

    public static string? Url => Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } url ? url : null;
}

public sealed class QdrantFactAttribute : FactAttribute
{
    public QdrantFactAttribute() { if (RealQdrant.Url is null) Skip = RealQdrant.Reason; }
}

/// <summary>A collection of its own in the Qdrant under test, removed when the test is done.</summary>
internal sealed class QdrantCollection : IAsyncDisposable
{
    private readonly QdrantClient _client;

    public string Name { get; } = $"fleet_memtest_{Guid.NewGuid():N}";
    public VectorStore Store { get; }

    public QdrantCollection(int dimensions)
    {
        var url = new Uri(RealQdrant.Url ?? throw new InvalidOperationException(RealQdrant.Reason));
        _client = new QdrantClient(url.Host, url.Port, https: url.Scheme == "https");
        Store = new VectorStore(
            Options.Create(new QdrantOptions { Url = url.ToString(), Collection = Name }),
            Options.Create(new EmbeddingOptions { Dimensions = dimensions }),
            NullLogger<VectorStore>.Instance);
    }

    public async Task<int> CountAsync() => (int)await _client.CountAsync(Name);

    public async ValueTask DisposeAsync()
    {
        try { await _client.DeleteCollectionAsync(Name); } catch { /* best effort */ }
        _client.Dispose();
    }
}

/// <summary>
/// <see cref="VectorStore"/> against a real Qdrant, with vectors made by hand: what is written, what
/// each side of a search returns under each filter, what a listing shows, and what is removed.
/// Invented memories only.
/// </summary>
public sealed class QdrantVectorStoreTests : IAsyncLifetime
{
    private const int Dimensions = 8;
    private static readonly long March1 = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    private QdrantCollection _qdrant = null!;
    private VectorStore Store => _qdrant.Store;

    public async Task InitializeAsync()
    {
        if (RealQdrant.Url is null) return;
        _qdrant = new QdrantCollection(Dimensions);
        await Store.EnsureCollectionAsync();
    }

    public async Task DisposeAsync()
    {
        if (_qdrant is not null) await _qdrant.DisposeAsync();
    }

    /// <summary>A unit vector along one axis, leaning a little towards the next: axis 0 and 0.5 are near, 0 and 1 are not.</summary>
    private static float[] Axis(double axis)
    {
        var vector = new float[Dimensions];
        var whole = (int)Math.Floor(axis);
        var part = (float)(axis - whole);
        vector[whole % Dimensions] = 1 - part;
        vector[(whole + 1) % Dimensions] += part;
        return vector;
    }

    private static IndexPoint Section(
        string path, int index, int count, float[] dense, string sparse, string hash = "hash-1",
        string project = "alpha", string type = "learning", string agent = "dev", string[]? tags = null, long? updatedTs = null) =>
        new(path, index, dense, sparse, new Dictionary<string, object>
        {
            [IndexPayload.FilePath] = path,
            [IndexPayload.MemoryId] = "id-of-" + path,
            [IndexPayload.MemoryType] = type,
            [IndexPayload.AgentName] = agent,
            [IndexPayload.Project] = project,
            [IndexPayload.Title] = "Title of " + path,
            [IndexPayload.Tags] = new List<string>(tags ?? []),
            [IndexPayload.Created] = "2026-03-01T00:00:00.0000000+00:00",
            [IndexPayload.Updated] = "2026-03-01T00:00:00.0000000+00:00",
            [IndexPayload.UpdatedTs] = updatedTs ?? March1,
            [IndexPayload.SectionIndex] = index,
            [IndexPayload.SectionCount] = count,
            [IndexPayload.SectionStart] = index * 100,
            [IndexPayload.SectionLength] = 100,
            [IndexPayload.IndexHash] = hash,
            [IndexPayload.IndexFormat] = MemoryIndexPlan.Format,
        });

    /// <summary>A point as the service wrote it before memories were indexed in sections.</summary>
    private static IndexPoint Legacy(string path, float[] dense, string sparse, string project = "alpha", string[]? tags = null) =>
        new(path, 0, dense, sparse, new Dictionary<string, object>
        {
            ["file_path"] = path,
            ["memory_id"] = "id-of-" + path,
            ["memory_type"] = "learning",
            ["agent_name"] = "dev",
            ["project"] = project,
            ["title"] = "Title of " + path,
            ["tags"] = new List<string>(tags ?? []),
            ["created"] = "2026-01-01T00:00:00.0000000+00:00",
        });

    private static List<IndexPoint> Sections(string path, int count, string sparse = "ledger", string hash = "hash-1", double axis = 0) =>
        Enumerable.Range(0, count).Select(i => Section(path, i, count, Axis(axis), $"{sparse} part{i}", hash)).ToList();

    [QdrantFact]
    public async Task Each_side_limits_distinct_memories_not_sections_and_keeps_the_best_section()
    {
        await Store.UpsertAsync(Sections("/m/many.md", 150, "needle", axis: 0));
        await Store.UpsertAsync([Section("/m/other.md", 0, 1, Axis(0.5), "needle other")]);
        foreach (var hits in new[]
        {
            await Store.SearchDenseAsync(Axis(0), 2),
            await Store.SearchSparseAsync("needle", 2),
        })
        {
            Assert.Equal(2, hits.Count);
            Assert.Equal(2, hits.Select(h => h.FilePathKey).Distinct().Count());
            Assert.Contains(hits, h => h.FilePathKey == "/m/other.md");
        }
    }

    // ── Writing, and reading back at start ──────────────────────────────────

    [QdrantFact]
    public async Task The_collection_is_made_once_and_kept()
    {
        await Store.UpsertAsync([Section("/m/a.md", 0, 1, Axis(0), "kept")]);

        await Store.EnsureCollectionAsync();   // every start

        Assert.Equal(1, await _qdrant.CountAsync());
    }

    [QdrantFact]
    public async Task A_memory_of_more_sections_than_one_batch_is_written_whole_and_read_back_as_one_current_memory()
    {
        // The real plan of a real long memory: 130 paragraphs, 130 sections, three upsert batches.
        var doc = new MemoryDocument
        {
            Id = Guid.NewGuid().ToString(), Type = "learning", Title = "Ledger notes", Tags = ["finance"], Project = "alpha",
            Content = string.Join("\n\n", Enumerable.Range(0, 130).Select(i => $"part{i} " + string.Join(" ", Enumerable.Repeat("ledger", 20)))),
        };
        var plan = MemoryIndexPlan.Build(doc, "/m/long.md", new MemoryIndexSettings { SectionChars = 200 });
        Assert.Equal(130, plan.Sections.Count);

        await Store.UpsertAsync(plan.Sections.Select(s => new IndexPoint("/m/long.md", s.Index, Axis(s.Index), plan.SparseText(s), plan.Payload(s))).ToList());

        Assert.Equal(130, await _qdrant.CountAsync());
        var rows = await Store.ScrollIndexInfoAsync();
        Assert.Equal(130, rows.Count);
        Assert.Equal(Enumerable.Range(0, 130), rows.Select(r => r.SectionIndex).Order());
        Assert.All(rows, r => Assert.Equal(("/m/long.md", plan.Hash, MemoryIndexPlan.Format, 130), (r.FilePathKey, r.Hash, r.Format, r.SectionCount)));
        // What the start makes of it: one memory, whole, with a hash to trust.
        Assert.Equal(plan.Hash, Assert.Single(MemoryIndexPlan.IndexedHashes(rows)).Value);

        // Written again, it is the same 130 points: the ids are the same every time.
        await Store.UpsertAsync(plan.Sections.Select(s => new IndexPoint("/m/long.md", s.Index, Axis(s.Index), plan.SparseText(s), plan.Payload(s))).ToList());
        Assert.Equal(130, await _qdrant.CountAsync());
    }

    [QdrantFact]
    public async Task A_point_from_before_sections_reads_back_as_such_and_is_overwritten_by_the_first_section()
    {
        await Store.UpsertAsync([Legacy("/m/old.md", Axis(0), "old words")]);

        var row = Assert.Single(await Store.ScrollIndexInfoAsync());
        Assert.Equal(("/m/old.md", "", 0, 0, 1), (row.FilePathKey, row.Hash, row.Format, row.SectionIndex, row.SectionCount));
        Assert.Null(MemoryIndexPlan.IndexedHashes([row])["/m/old.md"]);

        await Store.UpsertAsync(Sections("/m/old.md", 3));

        Assert.Equal(3, await _qdrant.CountAsync());   // not four: section 0 took the old point's place
        Assert.All(await Store.ScrollIndexInfoAsync(), r => Assert.Equal("hash-1", r.Hash));
    }

    // ── The meaning side ────────────────────────────────────────────────────

    [QdrantFact]
    public async Task The_meaning_side_returns_the_nearest_points_first_with_their_section_range()
    {
        await Store.UpsertAsync(
        [
            Section("/m/far.md", 0, 1, Axis(4), "x"),
            Section("/m/long.md", 0, 2, Axis(2), "x"),
            Section("/m/long.md", 1, 2, Axis(0.2), "x"),
            Section("/m/near.md", 0, 1, Axis(0), "x"),
            Legacy("/m/old.md", Axis(0.5), "x"),
        ]);

        var matches = await Store.SearchDenseAsync(Axis(0), limit: 3);

        Assert.Equal(["/m/near.md", "/m/long.md", "/m/old.md"], matches.Select(m => m.FilePathKey));
        Assert.Equal(1f, matches[0].Score, 3);
        Assert.True(matches[0].Score > matches[1].Score && matches[1].Score > matches[2].Score);
        // The second section of the long memory, and its range; the old point has none.
        Assert.Equal((100, 100), (matches[1].SectionStart, matches[1].SectionLength));
        Assert.Equal((0, -1), (matches[2].SectionStart, matches[2].SectionLength));
    }

    // ── The keyword side ────────────────────────────────────────────────────

    [QdrantFact]
    public async Task The_keyword_side_returns_only_points_that_share_a_word_with_the_query_best_first()
    {
        await Store.UpsertAsync(
        [
            Section("/m/once.md", 0, 1, Axis(0), "The ledger is reconciled nightly."),
            Section("/m/thrice.md", 0, 1, Axis(1), "Ledger, ledger, ledger: reconciled."),
            Section("/m/cyrillic.md", 0, 1, Axis(2), "Очередь задач переполнена после сбоя."),
            Section("/m/greek.md", 0, 1, Axis(3), "Η ουρά εργασιών γέμισε."),
            Section("/m/cjk.md", 0, 1, Axis(4), "再試行のキューが満杯になった。"),
            Section("/m/other.md", 0, 1, Axis(5), "Nothing to do with any of it."),
        ]);

        async Task<List<string>> Found(string query) => (await Store.SearchSparseAsync(query, limit: 10)).Select(m => m.FilePathKey).ToList();

        Assert.Equal(["/m/thrice.md", "/m/once.md"], await Found("ledger"));
        Assert.Equal(["/m/cyrillic.md"], await Found("ОЧЕРЕДЬ"));
        Assert.Equal(["/m/greek.md"], await Found("ουρά"));
        Assert.Equal(["/m/cjk.md"], await Found("満杯"));
        Assert.Empty(await Found("harbour"));
        // No keyword in the query at all: nothing is asked and nothing is found.
        Assert.Empty(await Found("the of and"));

        Assert.All(await Store.SearchSparseAsync("ledger reconciled", limit: 10), m => Assert.True(m.Score > 0));
        Assert.Single(await Store.SearchSparseAsync("ledger", limit: 1));
    }

    // ── Filters, on both sides ──────────────────────────────────────────────

    [QdrantFact]
    public async Task Each_filter_narrows_both_sides_and_a_point_without_the_updated_time_is_outside_a_since()
    {
        await Store.UpsertAsync(
        [
            Section("/m/base.md", 0, 1, Axis(0), "backoff", project: "alpha", type: "decision", agent: "dev", tags: ["keep", "queues"], updatedTs: March1 + 10),
            Section("/m/project.md", 0, 1, Axis(0.1), "backoff", project: "beta", type: "decision", agent: "dev", tags: ["keep"], updatedTs: March1 + 10),
            Section("/m/type.md", 0, 1, Axis(0.2), "backoff", project: "alpha", type: "learning", agent: "dev", tags: ["keep"], updatedTs: March1 + 10),
            Section("/m/agent.md", 0, 1, Axis(0.3), "backoff", project: "alpha", type: "decision", agent: "ops", tags: ["keep"], updatedTs: March1 + 10),
            Section("/m/tag.md", 0, 1, Axis(0.4), "backoff", project: "alpha", type: "decision", agent: "dev", tags: ["keeper", "other"], updatedTs: March1 + 10),
            Section("/m/date.md", 0, 1, Axis(0.5), "backoff", project: "alpha", type: "decision", agent: "dev", tags: ["keep"], updatedTs: March1 - 1),
            Section("/m/on-the-day.md", 0, 1, Axis(0.6), "backoff", project: "alpha", type: "decision", agent: "dev", tags: ["keep"], updatedTs: March1),
            Legacy("/m/old.md", Axis(0.7), "backoff", project: "alpha", tags: ["keep"]),
        ]);

        async Task Check(IndexFilter? filter, params string[] expected)
        {
            var dense = await Store.SearchDenseAsync(Axis(0), limit: 20, filter);
            var sparse = await Store.SearchSparseAsync("backoff", limit: 20, filter);
            Assert.Equal(expected.Order(), dense.Select(m => m.FilePathKey).Order());
            Assert.Equal(expected.Order(), sparse.Select(m => m.FilePathKey).Order());
        }

        static IndexFilter Keywords(params (string Key, string Value)[] pairs) => new(pairs.ToDictionary(p => p.Key, p => p.Value));
        string[] all = ["/m/base.md", "/m/project.md", "/m/type.md", "/m/agent.md", "/m/tag.md", "/m/date.md", "/m/on-the-day.md", "/m/old.md"];

        await Check(null, all);
        await Check(new IndexFilter(new Dictionary<string, string>()), all);
        await Check(Keywords((IndexPayload.Project, "beta")), "/m/project.md");
        await Check(Keywords((IndexPayload.MemoryType, "learning")), "/m/type.md", "/m/old.md");
        await Check(Keywords((IndexPayload.AgentName, "ops")), "/m/agent.md");
        // A tag is one of the memory's tags, exactly: "keep" is not "keeper".
        await Check(Keywords((IndexPayload.Tags, "keep")), all.Except(["/m/tag.md"]).ToArray());
        await Check(Keywords((IndexPayload.Tags, "queues")), "/m/base.md");
        // On or after the day; the old point has no updated time and is outside until it is indexed again.
        await Check(new IndexFilter(new Dictionary<string, string>(), March1), all.Except(["/m/date.md", "/m/old.md"]).ToArray());
        await Check(new IndexFilter(new Dictionary<string, string>(), March1 + 1), all.Except(["/m/date.md", "/m/on-the-day.md", "/m/old.md"]).ToArray());
        // All of them together.
        await Check(
            new IndexFilter(new Dictionary<string, string>
            {
                [IndexPayload.Project] = "alpha", [IndexPayload.MemoryType] = "decision", [IndexPayload.AgentName] = "dev", [IndexPayload.Tags] = "keep",
            }, March1),
            "/m/base.md", "/m/on-the-day.md");
        await Check(Keywords((IndexPayload.Project, "nowhere")));
    }

    // ── Listing ─────────────────────────────────────────────────────────────

    [QdrantFact]
    public async Task A_listing_is_one_entry_per_memory_whatever_its_sections_and_whatever_the_filter()
    {
        // More points than one scroll page (500), so the pages are joined too.
        var longPoints = Enumerable.Range(0, 520)
            .Select(i => Section("/m/long.md", i, 520, Axis(i), "ledger", project: "alpha", type: "decision", tags: ["finance", "ops"]))
            .ToList();
        await Store.UpsertAsync(longPoints);
        await Store.UpsertAsync(Sections("/m/medium.md", 5));
        await Store.UpsertAsync([Section("/m/short.md", 0, 1, Axis(1), "short", project: "beta", tags: ["finance"])]);
        await Store.UpsertAsync([Legacy("/m/old.md", Axis(2), "old", project: "alpha", tags: ["finance"])]);
        Assert.Equal(520 + 5 + 1 + 1, await _qdrant.CountAsync());

        var listed = await Store.ScrollFilteredAsync();

        Assert.Equal(["/m/long.md", "/m/medium.md", "/m/old.md", "/m/short.md"], listed.Select(e => e["file_path"]).Order());
        var entry = listed.Single(e => e["file_path"] == "/m/long.md");
        Assert.Equal(("id-of-/m/long.md", "Title of /m/long.md", "alpha", "decision", "finance, ops"),
            (entry["memory_id"], entry["title"], entry["project"], entry["memory_type"], entry["tags"]));
        Assert.Equal("2026-03-01T00:00:00.0000000+00:00", entry["updated"]);

        async Task<IEnumerable<string>> Listed(string key, string value) =>
            (await Store.ScrollFilteredAsync(new Dictionary<string, string> { [key] = value })).Select(e => e["file_path"]).Order();

        Assert.Equal(["/m/long.md", "/m/old.md", "/m/short.md"], await Listed("tags", "finance"));
        Assert.Equal(["/m/long.md"], await Listed("tags", "ops"));
        Assert.Equal(["/m/short.md"], await Listed("project", "beta"));
        Assert.Equal(["/m/long.md"], await Listed("memory_type", "decision"));
        Assert.Empty(await Listed("project", "nowhere"));
    }

    // ── Removing ────────────────────────────────────────────────────────────

    [QdrantFact]
    public async Task After_a_memory_shrinks_the_points_of_its_longer_version_are_removed_and_no_other_memorys()
    {
        await Store.UpsertAsync(Sections("/m/shrinks.md", 7, hash: "hash-old"));
        await Store.UpsertAsync(Sections("/m/other.md", 4, hash: "hash-old"));
        await Store.UpsertAsync([Legacy("/m/older.md", Axis(3), "old")]);

        // The new version: two sections. They overwrite the first two points; five are left over.
        await Store.UpsertAsync(Sections("/m/shrinks.md", 2, hash: "hash-new"));
        Assert.Equal(7 + 4 + 1, await _qdrant.CountAsync());

        await Store.DeleteStaleAsync("/m/shrinks.md", "hash-new");

        var rows = await Store.ScrollIndexInfoAsync();
        Assert.Equal([0, 1], rows.Where(r => r.FilePathKey == "/m/shrinks.md").Select(r => r.SectionIndex).Order());
        Assert.All(rows.Where(r => r.FilePathKey == "/m/shrinks.md"), r => Assert.Equal(("hash-new", 2), (r.Hash, r.SectionCount)));
        Assert.Equal(4, rows.Count(r => r.FilePathKey == "/m/other.md"));
        Assert.Single(rows, r => r.FilePathKey == "/m/older.md");
        Assert.Equal("hash-new", MemoryIndexPlan.IndexedHashes(rows)["/m/shrinks.md"]);

        // Nothing is stale now: asking again removes nothing.
        await Store.DeleteStaleAsync("/m/shrinks.md", "hash-new");
        Assert.Equal(2 + 4 + 1, await _qdrant.CountAsync());
    }

    [QdrantFact]
    public async Task A_point_without_a_hash_is_stale_too()
    {
        // A file whose old point was not overwritten, were that ever to happen: it has no hash to keep.
        await Store.UpsertAsync([Legacy("/m/a.md", Axis(0), "old")]);
        await Store.UpsertAsync(Sections("/m/a.md", 3).Skip(1).ToList());
        Assert.Equal(3, await _qdrant.CountAsync());

        await Store.DeleteStaleAsync("/m/a.md", "hash-1");

        Assert.Equal([1, 2], (await Store.ScrollIndexInfoAsync()).Select(r => r.SectionIndex).Order());
    }

    [QdrantFact]
    public async Task Removing_a_file_removes_every_one_of_its_points_and_no_other()
    {
        await Store.UpsertAsync(Sections("/m/gone.md", 60));
        await Store.UpsertAsync(Sections("/m/gone.md.bak", 3));   // a path that begins the same
        await Store.UpsertAsync([Legacy("/m/old-gone.md", Axis(1), "old")]);
        await Store.UpsertAsync(Sections("/m/kept.md", 2));

        await Store.DeleteAsync("/m/gone.md");
        await Store.DeleteAsync("/m/old-gone.md");
        await Store.DeleteAsync("/m/never-there.md");

        var rows = await Store.ScrollIndexInfoAsync();
        Assert.Equal(["/m/gone.md.bak", "/m/kept.md"], rows.Select(r => r.FilePathKey).Distinct().Order());
        Assert.Equal(5, rows.Count);
        Assert.DoesNotContain(await Store.SearchSparseAsync("ledger", 100), m => m.FilePathKey == "/m/gone.md");
        Assert.DoesNotContain(await Store.SearchDenseAsync(Axis(0), 100), m => m.FilePathKey == "/m/gone.md");
    }
}

/// <summary>
/// The memory service over real files and a real Qdrant, with the model-free embedder: the whole
/// path a search, an update, a removal and a start take, where <see cref="MemorySearchTests"/> and
/// <see cref="MemoryStartupIndexTests"/> take it over the in-memory index. Invented memories only.
/// </summary>
public sealed class QdrantMemoryServiceTests : IAsyncLifetime
{
    private static readonly MemoryIndexSettings Small = new() { SectionChars = 200, MaxSections = 200, EmbedderId = "fake/a/512" };

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"fleet-memory-qdrant-tests-{Guid.NewGuid():N}");
    private readonly FakeEmbedder _embedder = new();
    private readonly CapturingLogger<FileWatcherService> _watcherLog = new();
    private QdrantCollection _qdrant = null!;
    private MemoryFileStore _files = null!;
    private MemoryService _service = null!;

    public async Task InitializeAsync()
    {
        if (RealQdrant.Url is null) return;
        Directory.CreateDirectory(_dir);
        _files = new MemoryFileStore(Options.Create(new StorageOptions { Path = _dir }), NullLogger<MemoryFileStore>.Instance);
        _files.EnsureDirectories();
        _qdrant = new QdrantCollection(dimensions: 512);
        await _qdrant.Store.EnsureCollectionAsync();
        Restart();
    }

    public async Task DisposeAsync()
    {
        if (_qdrant is not null) await _qdrant.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>A service as after a restart: the same files and index, nothing remembered.</summary>
    private void Restart() =>
        _service = new MemoryService(_files, _qdrant.Store, _embedder,
            Options.Create(new QdrantOptions { Url = "http://unused", SimilarityThreshold = 0.85f }),
            new MemorySizeGuidance(0, NullLogger.Instance), NullLogger<MemoryService>.Instance, new MemoryCatalogue(), Small);

    /// <summary>The process starts: nothing remembered, then the scan of the files. Returns how many texts were embedded.</summary>
    private async Task<int> StartAsync()
    {
        Restart();
        var watcher = new FileWatcherService(_service, _files, _qdrant.Store, new MemorySizeGuidance(0, NullLogger.Instance),
            Options.Create(new StorageOptions { Path = _dir }), _watcherLog);
        var before = _embedder.Calls;
        await watcher.FullIndexAsync(CancellationToken.None);
        return _embedder.Calls - before;
    }

    private string LastSummary() => _watcherLog.Entries.Last(e => e.Message.StartsWith("Full index complete:")).Message;

    private static string Words(string word, int count) => string.Join(" ", Enumerable.Repeat(word, count));

    private static string LongContent(string word = "ledger") =>
        string.Join("\n\n", Enumerable.Range(0, 5).Select(i => $"part{i} " + Words(word, 16)));

    private Task<MemoryDocument> WriteAsync(string title, string content, string? id = null, string[]? tags = null,
        string project = "", string type = "learning", DateTimeOffset? updated = null)
    {
        var at = updated ?? new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        return _files.SaveAsync(new MemoryDocument
        {
            Id = id ?? Guid.NewGuid().ToString(), Type = type, Title = title, Content = content,
            Tags = [.. tags ?? []], Project = project, Created = at, Updated = at,
        });
    }

    private async Task<MemoryDocument> AddAsync(string title, string content, string? id = null, string[]? tags = null,
        string project = "", string type = "learning", DateTimeOffset? updated = null)
    {
        var doc = await WriteAsync(title, content, id, tags, project, type, updated);
        await _service.IndexFileAsync(doc.FilePath);
        return doc;
    }

    private async Task<int> PointsOf(MemoryDocument doc) =>
        (await _qdrant.Store.ScrollIndexInfoAsync()).Count(r => r.FilePathKey == doc.FilePath);

    [QdrantFact]
    public async Task Every_kind_of_hit_comes_back_from_the_real_index_in_order_with_the_filters_applied()
    {
        _embedder.Same["postponement"] = "backoff";
        var day = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var titled = await AddAsync("Backoff rules", "Short.", id: "5e1f9a3c-0000-4000-8000-000000000001", project: "alpha", updated: day.AddDays(2));
        var tagged = await AddAsync("Timers", "Nothing about the word here.", tags: ["backoff"], project: "alpha", updated: day.AddDays(2));
        var keyword = await AddAsync("Queue notes", $"{LongContent("filler")}\n\nUse backoff between attempts. {Words("after", 20)}", project: "alpha", tags: ["keep"], updated: day.AddDays(2));
        var meaning = await AddAsync("Scheduling", "Postponement grows with every failure.", project: "alpha", updated: day.AddDays(-2));
        var cyrillic = await AddAsync("Очередь", "Очередь задач переполнена после сбоя.", project: "beta", updated: day.AddDays(2));

        var hits = await _service.SearchAsync("backoff", limit: 4);
        Assert.Equal(
            [(titled.Id, MemoryMatch.Title), (tagged.Id, MemoryMatch.Tag), (keyword.Id, MemoryMatch.Keyword), (meaning.Id, MemoryMatch.Meaning)],
            hits.Select(h => (h.Document.Id, h.Match)));
        Assert.InRange(hits[3].Score!.Value, 0.2f, 1.001f);
        // The long memory is one hit, shown from the section the word is in.
        Assert.True(await PointsOf(keyword) > 3);
        Assert.Contains("Use backoff between attempts.", hits[2].Snippet.Text);
        Assert.Equal("backoff", hits[2].Snippet.Text.Substring(hits[2].Snippet.Highlights[0].Start, hits[2].Snippet.Highlights[0].Length));

        Assert.Equal((titled.Id, MemoryMatch.Id), (await _service.SearchAsync("5e1f9a")).Select(h => (h.Document.Id, h.Match)).Single());
        var first = (await _service.SearchAsync("переполнена")).First();
        Assert.Equal((cyrillic.Id, MemoryMatch.Keyword), (first.Document.Id, first.Match));

        // tag and since reach the index as filters: the keyword hit has the tag, the meaning hit is too old.
        Assert.Equal([keyword.Id], (await _service.SearchAsync("backoff", tag: "keep")).Select(h => h.Document.Id));
        Assert.DoesNotContain(await _service.SearchAsync("backoff", since: day), h => h.Document.Id == meaning.Id);
        Assert.Contains(await _service.SearchAsync("backoff", since: day.AddDays(-5)), h => h.Document.Id == meaning.Id);
        Assert.All(await _service.SearchAsync("backoff", project: "beta"), h => Assert.Equal("beta", h.Document.Project));
    }

    [QdrantFact]
    public async Task An_update_a_delete_and_the_duplicate_hint_treat_a_memory_of_many_sections_as_one()
    {
        var doc = await AddAsync("Ledger notes", string.Join("\n\n", Enumerable.Repeat(Words("ledger", 16), 5)));
        var other = await AddAsync("Kept", LongContent("keeper"));
        Assert.Equal(5, await PointsOf(doc));

        // Five sections as near as can be to the new memory: named once.
        var (stored, similar, warning, _) = await _service.StoreAsync(new MemoryDocument
        {
            Id = Guid.NewGuid().ToString(), Type = "learning", Title = "Ledger notes", Content = Words("ledger", 16),
        });
        Assert.Null(warning);
        Assert.Equal(doc.Id, Assert.Single(similar).Id);

        await _service.UpdateAsync(doc.Id, content: $"{Words("short", 20)}\n\n{Words("second", 20)}");
        Assert.Equal(2, await PointsOf(doc));

        Assert.Equal(new[] { doc.Id, other.Id, stored.Id }.Order(), (await _service.ListAsync()).Select(e => e["memory_id"]).Order());

        await _service.DeleteAsync(doc.Id);
        Assert.Equal(0, await PointsOf(doc));
        Assert.Equal(5, await PointsOf(other));
    }

    [QdrantFact]
    public async Task A_start_replaces_old_points_removes_orphans_and_the_next_start_embeds_nothing()
    {
        var shortDoc = await WriteAsync("Retry budget", "Three attempts, then the task is parked.", tags: ["queues"]);
        var longDoc = await WriteAsync("Ledger notes", LongContent());
        var gone = await WriteAsync("Gone", LongContent("vanished"));
        // The index as an earlier version left it, and a memory of this version whose file is gone.
        await _qdrant.Store.UpsertAsync(new[] { shortDoc, longDoc }.Select(d => new IndexPoint(d.FilePath, 0, _embedder.Embed("old"), "old",
            new Dictionary<string, object>
            {
                ["file_path"] = d.FilePath, ["memory_id"] = d.Id, ["memory_type"] = d.Type, ["agent_name"] = "", ["project"] = "",
                ["title"] = d.Title, ["tags"] = d.Tags, ["created"] = d.Created.ToString("o"),
            })).ToList());
        await _service.IndexFileAsync(gone.FilePath);
        File.Delete(gone.FilePath);
        Assert.Equal(1 + 1 + 5, await _qdrant.CountAsync());

        Assert.Equal(1 + 5, await StartAsync());

        Assert.Equal("Full index complete: 2 indexed (2 embedded in 6 sections, 0 unchanged and not embedded), 1 orphans removed", LastSummary());
        var rows = await _qdrant.Store.ScrollIndexInfoAsync();
        Assert.Equal(6, rows.Count);
        Assert.All(rows, r => Assert.Equal(MemoryIndexPlan.Format, r.Format));
        Assert.DoesNotContain(rows, r => r.FilePathKey == gone.FilePath);
        Assert.Equal(new[] { shortDoc.Id, longDoc.Id }.Order(), (await _service.ListAsync()).Select(e => e["memory_id"]).Order());

        // The second start reads the payloads and embeds nothing.
        Assert.Equal(0, await StartAsync());
        Assert.Equal("Full index complete: 2 indexed (0 embedded in 0 sections, 2 unchanged and not embedded), 0 orphans removed", LastSummary());

        // One file edited while stopped: that one only.
        await _files.UpdateAsync(shortDoc.FilePath, d => d.Content = "Five attempts now.");
        Assert.Equal(1, await StartAsync());
        Assert.Equal(6, await _qdrant.CountAsync());
    }
}
