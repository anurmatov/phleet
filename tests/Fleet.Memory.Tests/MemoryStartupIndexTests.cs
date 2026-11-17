using Microsoft.Extensions.Configuration;
using Fleet.Memory.Configuration;
using Fleet.Memory.Data;
using Fleet.Memory.Models;
using Fleet.Memory.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Fleet.Memory.Tests;

/// <summary>
/// The scan at start. It used to embed every file, every time. Now it reads what the index holds
/// and embeds only what is new, changed, or written in an older format. Invented memories only.
/// </summary>
public sealed class MemoryStartupIndexTests : IDisposable
{
    private static readonly MemoryIndexSettings Small = new() { SectionChars = 200, MaxSections = 200, EmbedderId = "fake/a/512" };

    private readonly MemoryFixture _f = new(Small);
    private readonly CapturingLogger<FileWatcherService> _log = new();

    public void Dispose() => _f.Dispose();

    private static string Words(string word, int count) => string.Join(" ", Enumerable.Repeat(word, count));

    /// <summary>Five sections with a budget of 200.</summary>
    private static string LongContent(string word = "ledger") =>
        string.Join("\n\n", Enumerable.Range(0, 5).Select(i => $"part{i} " + Words(word, 16)));

    /// <summary>The process starts: a service that remembers nothing, and its scan of the files.</summary>
    private async Task<int> StartAsync(MemoryIndexSettings? settings = null)
    {
        var service = _f.Restart(settings ?? Small);
        var watcher = new FileWatcherService(service, _f.Store, _f.Index, new MemorySizeGuidance(0, NullLogger.Instance),
            Options.Create(new StorageOptions { Path = _f.Dir }), _log);
        var before = _f.Embedder.Calls;
        await watcher.FullIndexAsync(CancellationToken.None);
        return _f.Embedder.Calls - before;
    }

    private string LastSummary() => _log.Entries.Last(e => e.Message.StartsWith("Full index complete:")).Message;

    private async Task<(MemoryDocument Short, MemoryDocument Long, MemoryDocument Other)> SeedAsync()
    {
        var a = await _f.AddAsync("Retry budget", "Three attempts, then the task is parked.", tags: ["queues"]);
        var b = await _f.AddAsync("Ledger notes", LongContent());
        var c = await _f.AddAsync("Очередь задач", "Очередь переполнена после сбоя.");
        return (a, b, c);
    }

    [Theory]
    [InlineData("model-path")]
    [InlineData("vocab-path")]
    [InlineData("model-content")]
    [InlineData("vocab-content")]
    public async Task Configured_onnx_model_and_vocabulary_changes_reembed_unchanged_memories(string change)
    {
        var oldDir = Path.Combine(_f.Dir, "old");
        var newDir = Path.Combine(_f.Dir, "new");
        Directory.CreateDirectory(oldDir);
        Directory.CreateDirectory(newDir);
        var model = Path.Combine(oldDir, "model.onnx");
        var vocab = Path.Combine(oldDir, "vocab.txt");
        await File.WriteAllTextAsync(model, "synthetic model");
        await File.WriteAllTextAsync(vocab, "synthetic vocabulary");
        MemoryIndexSettings Settings() => MemoryIndexSettings.FromConfiguration(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Embedding:Onnx:ModelPath"] = model,
                ["Embedding:Onnx:VocabPath"] = vocab,
            }).Build(), NullLogger.Instance);
        await _f.WriteAsync("Notes", "Unchanged body.");
        Assert.Equal(1, await StartAsync(Settings()));
        Assert.Equal(0, await StartAsync(Settings()));
        switch (change)
        {
            case "model-path": File.Copy(model, Path.Combine(newDir, "model.onnx")); model = Path.Combine(newDir, "model.onnx"); break;
            case "vocab-path": File.Copy(vocab, Path.Combine(newDir, "vocab.txt")); vocab = Path.Combine(newDir, "vocab.txt"); break;
            case "model-content": await File.WriteAllTextAsync(model, "replacement model"); break;
            case "vocab-content": await File.WriteAllTextAsync(vocab, "replacement vocabulary"); break;
        }
        Assert.Equal(1, await StartAsync(Settings()));
        Assert.Equal(0, await StartAsync(Settings()));
    }

    [Fact]
    public async Task Files_that_have_not_changed_are_not_embedded_again()
    {
        await SeedAsync();
        var points = _f.Index.Points.Count;

        Assert.Equal(0, await StartAsync());

        Assert.Equal(points, _f.Index.Points.Count);
        Assert.Equal("Full index complete: 3 indexed (0 embedded in 0 sections, 3 unchanged and not embedded), 0 orphans removed", LastSummary());
        // Still searchable, and known to the catalogue of the new process.
        Assert.Equal(MemoryMatch.Keyword, (await _f.Service.SearchAsync("переполнена")).First().Match);
        Assert.Equal(3, _f.Service.Catalogue.Count);
    }

    [Fact]
    public async Task A_changed_file_and_a_new_file_are_embedded_and_the_rest_is_not()
    {
        var (shortDoc, longDoc, _) = await SeedAsync();
        // While the service was not running: one edit, one new file.
        await _f.Store.UpdateAsync(shortDoc.FilePath, d => d.Content = "Five attempts now.");
        var added = await _f.WriteAsync("Added while stopped", "A new memory.");

        Assert.Equal(2, await StartAsync());

        Assert.Equal("Full index complete: 4 indexed (2 embedded in 2 sections, 2 unchanged and not embedded), 0 orphans removed", LastSummary());
        Assert.Contains("Five attempts now.", _f.Index.PointsOf(shortDoc.FilePath).Single().SparseText);
        Assert.Single(_f.Index.PointsOf(added.FilePath));
        Assert.Equal(5, _f.Index.PointsOf(longDoc.FilePath).Count);
    }

    [Fact]
    public async Task Every_file_is_embedded_once_after_a_change_of_format_and_not_at_the_start_after_that()
    {
        var (shortDoc, longDoc, other) = await SeedAsync();
        // The index as an earlier version left it: one point per memory, no hash, no format, no sections.
        foreach (var doc in new[] { shortDoc, longDoc, other })
        {
            await _f.Index.DeleteAsync(doc.FilePath);
            _f.Index.AddLegacyPoint(doc, _f.Embedder.Embed("old"));
        }
        Assert.Equal(3, _f.Index.Points.Count);

        Assert.Equal(1 + 5 + 1, await StartAsync());

        // No old point is left beside the new ones, as a duplicate or as an orphan.
        Assert.Equal(7, _f.Index.Points.Count);
        Assert.All(_f.Index.Points, p => Assert.Equal(MemoryIndexPlan.Format, p.Payload[IndexPayload.IndexFormat]));
        Assert.Equal("Full index complete: 3 indexed (3 embedded in 7 sections, 0 unchanged and not embedded), 0 orphans removed", LastSummary());

        Assert.Equal(0, await StartAsync());
    }

    [Fact]
    public async Task A_point_written_in_another_format_is_not_trusted_even_with_the_right_hash()
    {
        var (shortDoc, _, _) = await SeedAsync();
        _f.Index.PointsOf(shortDoc.FilePath).Single().Payload[IndexPayload.IndexFormat] = MemoryIndexPlan.Format - 1;

        Assert.Equal(1, await StartAsync());
        Assert.Equal(MemoryIndexPlan.Format, _f.Index.PointsOf(shortDoc.FilePath).Single().Payload[IndexPayload.IndexFormat]);
    }

    [Theory]
    [InlineData(300, 200, "fake/a/512")]     // the section budget
    [InlineData(200, 2, "fake/a/512")]       // the cap
    [InlineData(200, 200, "fake/b/512")]     // the embedding model
    public async Task A_change_of_what_the_index_is_made_with_embeds_every_file_again(int sectionChars, int maxSections, string embedder)
    {
        var (shortDoc, longDoc, other) = await SeedAsync();
        var settings = new MemoryIndexSettings { SectionChars = sectionChars, MaxSections = maxSections, EmbedderId = embedder };

        var embedded = await StartAsync(settings);

        var expected = new[] { shortDoc, longDoc, other }
            .Sum(d => Math.Min(maxSections, MemorySections.Split(d.Content, sectionChars).Count));
        Assert.Equal(expected, embedded);
        // The sections of the old cut that the new one does not have are gone.
        Assert.Equal(expected, _f.Index.Points.Count);
        Assert.Equal(0, await StartAsync(settings));
    }

    [Fact]
    public async Task Every_section_of_a_memory_whose_file_is_gone_is_removed()
    {
        var (shortDoc, longDoc, _) = await SeedAsync();
        File.Delete(longDoc.FilePath);

        Assert.Equal(0, await StartAsync());

        Assert.Empty(_f.Index.PointsOf(longDoc.FilePath));
        Assert.Single(_f.Index.PointsOf(shortDoc.FilePath));
        Assert.Equal("Full index complete: 2 indexed (0 embedded in 0 sections, 2 unchanged and not embedded), 1 orphans removed", LastSummary());
        Assert.DoesNotContain(await _f.Service.ListAsync(), d => d["memory_id"] == longDoc.Id);
    }

    [Fact]
    public async Task An_embedder_that_fails_on_one_file_does_not_stop_the_others_and_that_file_is_done_at_the_next_pass()
    {
        var a = await _f.WriteAsync("First", "alpha text");
        var b = await _f.WriteAsync("Second", LongContent("bravo"));
        var c = await _f.WriteAsync("Third", "charlie text");
        _f.Embedder.FailWhen = text => text.Contains("part3 ");   // the fourth section of the long one

        await StartAsync();

        Assert.Single(_f.Index.PointsOf(a.FilePath));
        Assert.Single(_f.Index.PointsOf(c.FilePath));
        Assert.Empty(_f.Index.PointsOf(b.FilePath));              // nothing half-written
        Assert.Contains("2 embedded in 2 sections", LastSummary());
        // It is in the catalogue all the same, so its id and title are found.
        var first = (await _f.Service.SearchAsync("Second")).First();
        Assert.Equal((b.Id, MemoryMatch.Title), (first.Document.Id, first.Match));

        _f.Embedder.FailWhen = null;
        Assert.Equal(5, await StartAsync());                      // only the one that failed
        Assert.Equal(5, _f.Index.PointsOf(b.FilePath).Count);
    }

    [Fact]
    public async Task With_the_embedder_down_the_scan_still_ends_and_memories_are_found_by_id_title_and_tag()
    {
        var doc = await _f.WriteAsync("Retry budget", "Three attempts.", id: "5e1f9a3c-0000-4000-8000-000000000001", tags: ["queues"]);
        _f.Embedder.Down = true;

        await StartAsync();

        Assert.Empty(_f.Index.Points);
        Assert.Equal(doc.Id, Assert.Single(await _f.Service.SearchAsync("5e1f9a3c")).Document.Id);
        Assert.Equal(MemoryMatch.Title, Assert.Single(await _f.Service.SearchAsync("retry budget")).Match);
        Assert.Equal(MemoryMatch.Tag, Assert.Single(await _f.Service.SearchAsync("queues")).Match);
    }

    [Fact]
    public async Task The_catalogue_is_complete_when_the_watcher_has_started_whatever_the_index_is_doing()
    {
        // The host starts the web server only after this service's StartAsync has returned. A search
        // that arrived while the catalogue was still empty answered an id with its nearest neighbours.
        var doc = await _f.WriteAsync("Retry budget", "Three attempts.", id: "5e1f9a3c-0000-4000-8000-000000000001", tags: ["queues"]);
        var neighbour = await _f.AddAsync("Neighbour", "Three attempts too.");
        var service = _f.Restart(Small);
        // An index that does not answer: nothing of the scan can have run.
        var silent = Substitute.For<IVectorIndex>();
        silent.EnsureCollectionAsync(Arg.Any<CancellationToken>())
            .Returns(call => Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>()));
        var watcher = new FileWatcherService(service, _f.Store, silent, new MemorySizeGuidance(0, NullLogger.Instance),
            Options.Create(new StorageOptions { Path = _f.Dir }), _log);

        await watcher.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(2, service.Catalogue.Count);
            var hit = Assert.Single(await service.SearchAsync("5e1f9a3c"));
            Assert.Equal((doc.Id, MemoryMatch.Id), (hit.Document.Id, hit.Match));
            Assert.Equal(MemoryMatch.Tag, (await service.SearchAsync("queues")).First().Match);
            Assert.NotEqual(neighbour.Id, hit.Document.Id);
        }
        finally
        {
            await watcher.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_memory_removed_while_the_index_is_being_read_is_not_remembered_as_indexed()
    {
        var (shortDoc, _, _) = await SeedAsync();
        var bytes = await File.ReadAllBytesAsync(shortDoc.FilePath);

        // The read of the index has the memory's points in hand; before it records them, the memory
        // is deleted through the API.
        var index = new PausingIndex(_f.Index);
        var service = new MemoryService(_f.Store, index, _f.Embedder, Options.Create(new QdrantOptions { Url = "http://localhost:1", SimilarityThreshold = 0 }),
            new MemorySizeGuidance(0, NullLogger.Instance), NullLogger<MemoryService>.Instance, new MemoryCatalogue(), Small);
        var load = service.LoadIndexStateAsync();
        await index.Reading.Task;
        await service.DeleteAsync(shortDoc.Id, permanent: true);
        index.Continue.SetResult();
        await load;
        Assert.Empty(_f.Index.PointsOf(shortDoc.FilePath));

        // The same file comes back, byte for byte. The index does not hold it: it must be embedded,
        // not skipped because the read once saw points with this hash.
        await File.WriteAllBytesAsync(shortDoc.FilePath, bytes);
        await service.IndexFileAsync(shortDoc.FilePath);

        Assert.Single(_f.Index.PointsOf(shortDoc.FilePath));
    }

    /// <summary>An index whose read of the points stops, with them in hand, until it is told to go on.</summary>
    private sealed class PausingIndex(FakeVectorIndex inner) : IVectorIndex
    {
        public readonly TaskCompletionSource Reading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<List<IndexPointInfo>> ScrollIndexInfoAsync(CancellationToken ct = default)
        {
            var rows = await inner.ScrollIndexInfoAsync(ct);
            Reading.SetResult();
            await Continue.Task;
            return rows;
        }

        public Task EnsureCollectionAsync(CancellationToken ct = default) => inner.EnsureCollectionAsync(ct);
        public Task UpsertAsync(IReadOnlyList<IndexPoint> points, CancellationToken ct = default) => inner.UpsertAsync(points, ct);
        public Task DeleteStaleAsync(string filePathKey, string keepHash, CancellationToken ct = default) => inner.DeleteStaleAsync(filePathKey, keepHash, ct);
        public Task DeleteAsync(string filePathKey, CancellationToken ct = default) => inner.DeleteAsync(filePathKey, ct);
        public Task<List<IndexMatch>> SearchDenseAsync(float[] queryEmbedding, int limit, IndexFilter? filter = null, CancellationToken ct = default) => inner.SearchDenseAsync(queryEmbedding, limit, filter, ct);
        public Task<List<IndexMatch>> SearchSparseAsync(string queryText, int limit, IndexFilter? filter = null, CancellationToken ct = default) => inner.SearchSparseAsync(queryText, limit, filter, ct);
        public Task<List<Dictionary<string, string>>> ScrollFilteredAsync(Dictionary<string, string>? filters = null, CancellationToken ct = default) => inner.ScrollFilteredAsync(filters, ct);
    }
}
