using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Fleet.Memory.Configuration;
using Fleet.Memory.Data;
using Fleet.Memory.Models;
using Fleet.Memory.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fleet.Memory.Tests;

/// <summary>
/// The search index, in memory. It keeps what Qdrant would keep and answers the way Qdrant is asked
/// to: a point replaces the one with the same id (the ids are the real ones, from
/// <see cref="VectorStore.PointIdFor"/>), the meaning side is cosine similarity, the keyword side is
/// the overlap of the sparse terms, and the filters are the ones <see cref="VectorStore"/> builds.
/// </summary>
internal sealed class FakeVectorIndex : IVectorIndex
{
    public sealed record Point(string FilePathKey, int SectionIndex, float[] Dense, string SparseText, Dictionary<string, object> Payload);

    private readonly Dictionary<Guid, Point> _points = [];

    /// <summary>Every call fails, as when Qdrant cannot be reached.</summary>
    public bool Down;
    /// <summary>The next removal of stale points fails, once: a write that stopped half-way.</summary>
    public bool FailNextDeleteStale;
    public int SearchCalls;

    public IReadOnlyList<Point> Points { get { lock (_points) return _points.Values.ToList(); } }
    public IReadOnlyList<Point> PointsOf(string filePath) =>
        Points.Where(p => p.FilePathKey == filePath).OrderBy(p => p.SectionIndex).ToList();

    /// <summary>A point as the service wrote it before memories were indexed in sections.</summary>
    public void AddLegacyPoint(MemoryDocument doc, float[] dense)
    {
        var payload = new Dictionary<string, object>
        {
            ["file_path"] = doc.FilePath,
            ["memory_id"] = doc.Id,
            ["memory_type"] = doc.Type,
            ["agent_name"] = doc.Agent,
            ["project"] = MemoryDocument.NormalizeProject(doc.Project),
            ["title"] = doc.Title,
            ["tags"] = doc.Tags,
            ["created"] = doc.Created.ToString("o"),
        };
        lock (_points)
            _points[VectorStore.PointIdFor(doc.FilePath, 0)] = new Point(doc.FilePath, 0, dense, $"{doc.Title}\n\n{doc.Content}", payload);
    }

    private void ThrowIfDown()
    {
        if (Down) throw new HttpRequestException("synthetic index outage");
    }

    public Task EnsureCollectionAsync(CancellationToken ct = default)
    {
        ThrowIfDown();
        return Task.CompletedTask;
    }

    public Task UpsertAsync(IReadOnlyList<IndexPoint> points, CancellationToken ct = default)
    {
        ThrowIfDown();
        lock (_points)
            foreach (var p in points)
                _points[VectorStore.PointIdFor(p.FilePathKey, p.SectionIndex)] = new Point(p.FilePathKey, p.SectionIndex, p.Dense, p.SparseText, p.Payload);
        return Task.CompletedTask;
    }

    public Task DeleteStaleAsync(string filePathKey, string keepHash, CancellationToken ct = default)
    {
        ThrowIfDown();
        if (FailNextDeleteStale)
        {
            FailNextDeleteStale = false;
            throw new HttpRequestException("synthetic index outage");
        }
        Remove(p => p.FilePathKey == filePathKey && !Equals(p.Payload.GetValueOrDefault(IndexPayload.IndexHash), keepHash));
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string filePathKey, CancellationToken ct = default)
    {
        ThrowIfDown();
        Remove(p => p.FilePathKey == filePathKey);
        return Task.CompletedTask;
    }

    private void Remove(Func<Point, bool> which)
    {
        lock (_points)
            foreach (var id in _points.Where(p => which(p.Value)).Select(p => p.Key).ToList())
                _points.Remove(id);
    }

    public Task<List<IndexMatch>> SearchDenseAsync(float[] queryEmbedding, int limit, IndexFilter? filter = null, CancellationToken ct = default)
    {
        ThrowIfDown();
        Interlocked.Increment(ref SearchCalls);
        return Task.FromResult(Points
            .Where(p => Passes(p, filter))
            .Select(p => (Point: p, Score: Cosine(queryEmbedding, p.Dense)))
            .OrderByDescending(x => x.Score).ThenBy(x => x.Point.FilePathKey, StringComparer.Ordinal).ThenBy(x => x.Point.SectionIndex)
            .DistinctBy(x => x.Point.FilePathKey)
            .Take(limit)
            .Select(x => Match(x.Point, x.Score))
            .ToList());
    }

    public Task<List<IndexMatch>> SearchSparseAsync(string queryText, int limit, IndexFilter? filter = null, CancellationToken ct = default)
    {
        ThrowIfDown();
        Interlocked.Increment(ref SearchCalls);
        var (queryIndices, queryValues) = Bm25Tokenizer.Tokenize(queryText);
        var query = queryIndices.Zip(queryValues).ToDictionary(p => p.First, p => p.Second);

        return Task.FromResult(Points
            .Where(p => Passes(p, filter))
            .Select(p =>
            {
                var (indices, values) = Bm25Tokenizer.Tokenize(p.SparseText);
                var score = 0f;
                for (var i = 0; i < indices.Length; i++)
                    if (query.TryGetValue(indices[i], out var weight)) score += weight * values[i];
                return (Point: p, Score: score);
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).ThenBy(x => x.Point.FilePathKey, StringComparer.Ordinal).ThenBy(x => x.Point.SectionIndex)
            .DistinctBy(x => x.Point.FilePathKey)
            .Take(limit)
            .Select(x => Match(x.Point, x.Score))
            .ToList());
    }

    private static IndexMatch Match(Point p, float score) => new(
        p.FilePathKey, score,
        p.Payload.TryGetValue(IndexPayload.SectionStart, out var start) ? Convert.ToInt32(start) : 0,
        p.Payload.TryGetValue(IndexPayload.SectionLength, out var length) ? Convert.ToInt32(length) : -1);

    private static bool Passes(Point p, IndexFilter? filter)
    {
        if (filter is null) return true;
        foreach (var (key, value) in filter.Keywords)
            if (!MatchesKeyword(p, key, value)) return false;
        if (filter.UpdatedSince is { } since)
            return p.Payload.TryGetValue(IndexPayload.UpdatedTs, out var ts) && Convert.ToInt64(ts) >= since;
        return true;
    }

    private static bool MatchesKeyword(Point p, string key, string value) =>
        p.Payload.TryGetValue(key, out var held) && held switch
        {
            string s => s == value,
            IEnumerable<string> list => list.Contains(value),
            _ => false,
        };

    public Task<List<Dictionary<string, string>>> ScrollFilteredAsync(Dictionary<string, string>? filters = null, CancellationToken ct = default)
    {
        ThrowIfDown();
        // As VectorStore.BuildListFilter: the keywords, and no point whose section index is 1 or more.
        return Task.FromResult(Points
            .Where(p => (filters ?? []).All(f => MatchesKeyword(p, f.Key, f.Value)))
            .Where(p => !(p.Payload.TryGetValue(IndexPayload.SectionIndex, out var index) && Convert.ToInt32(index) >= 1))
            .OrderBy(p => p.FilePathKey, StringComparer.Ordinal)
            .Select(p => p.Payload.ToDictionary(
                kv => kv.Key,
                kv => kv.Value switch
                {
                    string s => s,
                    IEnumerable<string> list => string.Join(", ", list),
                    _ => "",   // a number has no string value in a Qdrant payload
                }))
            .ToList());
    }

    public Task<List<IndexPointInfo>> ScrollIndexInfoAsync(CancellationToken ct = default)
    {
        ThrowIfDown();
        return Task.FromResult(Points.Select(p => new IndexPointInfo(
            p.FilePathKey,
            p.Payload.GetValueOrDefault(IndexPayload.IndexHash) as string ?? "",
            p.Payload.TryGetValue(IndexPayload.IndexFormat, out var format) ? Convert.ToInt32(format) : 0,
            p.Payload.TryGetValue(IndexPayload.SectionIndex, out var index) ? Convert.ToInt32(index) : 0,
            p.Payload.TryGetValue(IndexPayload.SectionCount, out var count) ? Convert.ToInt32(count) : 1)).ToList());
    }

    private static float Cosine(float[] a, float[] b)
    {
        float dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        return na == 0 || nb == 0 ? 0 : dot / (MathF.Sqrt(na) * MathF.Sqrt(nb));
    }
}

/// <summary>
/// An embedder that needs no model: a text is the bag of its words, each word one direction, so
/// texts that share words are similar and texts that share none are not. <see cref="Same"/> makes
/// two different words one direction, which is how a test gets a match by meaning that shares no
/// keyword. It records what it was asked to embed.
/// </summary>
internal sealed class FakeEmbedder : IEmbeddingService
{
    private const int Dimensions = 512;

    public ConcurrentQueue<string> Inputs { get; } = new();
    public bool Down;
    /// <summary>Fails for an input this accepts.</summary>
    public Func<string, bool>? FailWhen;
    /// <summary>Word → the word it means the same as.</summary>
    public Dictionary<string, string> Same { get; } = new(StringComparer.Ordinal);

    public int Calls => Inputs.Count;

    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Inputs.Enqueue(text);
        if (Down || FailWhen?.Invoke(text) == true)
            return Task.FromException<float[]>(new HttpRequestException("synthetic embedder outage"));
        return Task.FromResult(Embed(text));
    }

    public float[] Embed(string text)
    {
        var vector = new float[Dimensions];
        foreach (var term in Bm25Tokenizer.Terms(text))
        {
            var concept = Same.GetValueOrDefault(term, term);
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(concept));
            vector[BitConverter.ToUInt32(hash, 0) % Dimensions] += 1;
        }
        return vector;
    }
}

internal sealed class CapturingLogger<T> : ILogger<T>
{
    public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Enqueue((logLevel, formatter(state, exception)));
}

/// <summary>
/// A memory service over real files in a temporary directory, the in-memory index and the
/// model-free embedder. Invented memories only.
/// </summary>
internal sealed class MemoryFixture : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), $"fleet-memory-search-tests-{Guid.NewGuid():N}");
    public MemoryFileStore Store { get; }
    public FakeVectorIndex Index { get; } = new();
    public FakeEmbedder Embedder { get; } = new();
    public CapturingLogger<MemoryService> Log { get; } = new();
    public MemoryService Service { get; private set; }

    private readonly float _similarityThreshold;

    public MemoryFixture(MemoryIndexSettings? settings = null, float similarityThreshold = 0)
    {
        Directory.CreateDirectory(Dir);
        Store = new MemoryFileStore(Options.Create(new StorageOptions { Path = Dir }), NullLogger<MemoryFileStore>.Instance);
        Store.EnsureDirectories();
        _similarityThreshold = similarityThreshold;
        Service = NewService(settings);
    }

    /// <summary>A service as after a restart: the same files and index, nothing remembered.</summary>
    public MemoryService Restart(MemoryIndexSettings? settings = null) => Service = NewService(settings);

    private MemoryService NewService(MemoryIndexSettings? settings)
    {
        var qdrant = Options.Create(new QdrantOptions { Url = "http://localhost:1", SimilarityThreshold = _similarityThreshold });
        return new MemoryService(Store, Index, Embedder, qdrant, new MemorySizeGuidance(0, NullLogger.Instance), Log,
            new MemoryCatalogue(), settings ?? new MemoryIndexSettings());
    }

    public static string NewId(string first8) => $"{first8}-0000-4000-8000-{Guid.NewGuid().ToString("N")[..12]}";

    /// <summary>Writes a memory file. Not indexed: see <see cref="AddAsync"/>.</summary>
    public Task<MemoryDocument> WriteAsync(
        string title, string content, string? id = null, string[]? tags = null, string project = "",
        string type = "learning", string agent = "", DateTimeOffset? updated = null)
    {
        var at = updated ?? new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        return Store.SaveAsync(new MemoryDocument
        {
            Id = id ?? Guid.NewGuid().ToString(),
            Type = type,
            Title = title,
            Content = content,
            Tags = [.. tags ?? []],
            Project = project,
            Agent = agent,
            Created = at,
            Updated = at,
        });
    }

    /// <summary>Writes a memory file and indexes it, as the watcher would.</summary>
    public async Task<MemoryDocument> AddAsync(
        string title, string content, string? id = null, string[]? tags = null, string project = "",
        string type = "learning", string agent = "", DateTimeOffset? updated = null)
    {
        var doc = await WriteAsync(title, content, id, tags, project, type, agent, updated);
        await Service.IndexFileAsync(doc.FilePath);
        return doc;
    }

    public void Dispose()
    {
        try { Directory.Delete(Dir, recursive: true); } catch { /* best effort */ }
    }
}
