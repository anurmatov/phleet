using System.Text;
using System.Text.RegularExpressions;
using Fleet.Memory.Configuration;
using Fleet.Memory.Data;
using Fleet.Memory.Services;
using Fleet.Memory.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Fleet.Memory.Tests;

/// <summary>
/// The #346 <c>Size:</c> lines on <c>memory_store</c> / <c>memory_update</c>, over a real
/// <see cref="MemoryService"/> and file store with an NSubstitute embedder. The embedder throws, so
/// nothing reaches Qdrant and every write carries today's <c>warning_indexing_deferred</c> line —
/// which also proves the report is produced when embedding fails. Synthetic text only.
/// </summary>
public sealed class MemoryToolSizeTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"fleet-memory-size-tests-{Guid.NewGuid():N}");
    private readonly MemoryFileStore _store;
    private readonly List<string> _embedded = [];

    public MemoryToolSizeTests()
    {
        Directory.CreateDirectory(_tempDir);
        _store = new MemoryFileStore(Options.Create(new StorageOptions { Path = _tempDir }), NullLogger<MemoryFileStore>.Instance);
        _store.EnsureDirectories();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private MemoryService Service(int guidanceBytes)
    {
        var embedder = Substitute.For<IEmbeddingService>();
        embedder
            .EmbedAsync(Arg.Do<string>(_embedded.Add), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<float[]>(new HttpRequestException("simulated")));

        var qdrant = Options.Create(new QdrantOptions { Url = "http://localhost:1", SimilarityThreshold = 0 });
        var vectors = new VectorStore(qdrant, Options.Create(new EmbeddingOptions()), NullLogger<VectorStore>.Instance);
        return new MemoryService(_store, vectors, embedder, qdrant, new MemorySizeGuidance(guidanceBytes, NullLogger.Instance), NullLogger<MemoryService>.Instance);
    }

    private static AclCacheService DisabledAcl()
    {
        var acl = new AclCacheService(Options.Create(new AclOptions()), Options.Create(new OrchestratorOptions()), NullLogger<AclCacheService>.Instance);
        acl.InjectAclForTesting([]);
        return acl;
    }

    private static Task<string> Store(MemoryService service, string title, string content) =>
        new MemoryStoreTool(service, DisabledAcl(), null!).StoreAsync("learning", title, content);

    private static Task<string> Update(MemoryService service, string id, string? content = null, string? tags = null) =>
        new MemoryUpdateTool(service, DisabledAcl(), null!).UpdateAsync(id, content: content, tags: tags);

    private static string IdOf(string storeOutput) => Regex.Match(storeOutput, @"\(id: ([0-9a-f-]{36}),").Groups[1].Value;

    /// <summary>Today's <c>memory_store</c> output for a write whose indexing was deferred.</summary>
    private static readonly Regex PreChangeStore = new(
        @"^Stored memory 't' \(id: [0-9a-f-]{36}, path: [^)]+\)\nwarning_indexing_deferred: memory written but not yet searchable — HttpRequestException: simulated");

    [Fact]
    public async Task Guidance_off_store_and_update_output_is_unchanged()
    {
        var service = Service(0);

        var stored = await Store(service, "t", new string('a', 50_000));
        Assert.Matches(PreChangeStore.ToString() + "$", stored);

        var id = IdOf(stored);
        var updated = await Update(service, id, content: "short");
        Assert.Equal($"Updated memory 't' (id: {id})\nwarning_indexing_deferred: memory updated but not yet searchable — HttpRequestException: simulated", updated);
    }

    [Fact]
    public async Task Guidance_on_store_appends_size_and_warning_after_the_unchanged_output()
    {
        var service = Service(25_000);

        var result = await Store(service, "t", new string('a', 25_000));

        var prefix = PreChangeStore.Match(result);
        Assert.True(prefix.Success, result);
        var id = IdOf(result);
        Assert.StartsWith(
            "\nSize: 25,003 UTF-8 bytes embedded; embedding-input guidance 25,000 (Embedding:InputGuidanceBytes)." +
            $"\nSize warning: Memory {id[..8]} embedding input (title + content) is 25,003 UTF-8 bytes, 3 over the 25,000-byte guidance (Embedding:InputGuidanceBytes). Saved anyway.",
            result[prefix.Length..]);
        Assert.True(File.Exists(_store.FindFileById(id)));

        // bytes is the exact EmbedAsync input.
        Assert.Equal(25_003, Encoding.UTF8.GetByteCount(Assert.Single(_embedded)));
    }

    [Fact]
    public async Task Service_reports_the_captured_embedding_input_and_previous_bytes()
    {
        var service = Service(10);

        var (stored, _, _, storeSize) = await service.StoreAsync(new Models.MemoryDocument
        {
            Id = Guid.NewGuid().ToString(), Type = "learning", Title = "дд", Content = "中 😀",
        });
        Assert.Equal(Encoding.UTF8.GetByteCount(_embedded[^1]), storeSize.Bytes);
        Assert.Null(storeSize.PreviousBytes);
        Assert.Equal("crossed", storeSize.Status);

        var (_, _, updateSize) = await service.UpdateAsync(stored.Id, content: "x");
        Assert.Equal(storeSize.Bytes, updateSize.PreviousBytes);
        Assert.Equal(Encoding.UTF8.GetByteCount(_embedded[^1]), updateSize.Bytes);
        Assert.Equal("under", updateSize.Status);
    }

    [Fact]
    public async Task Tags_only_update_of_an_over_guidance_memory_is_still_over()
    {
        var service = Service(1_000);
        var id = IdOf(await Store(service, "t", new string('a', 2_000)));

        var result = await Update(service, id, tags: "one,two");

        Assert.StartsWith($"Updated memory 't' (id: {id})\nwarning_indexing_deferred:", result);
        Assert.Contains("\nSize: 2,003 → 2,003 UTF-8 bytes embedded; embedding-input guidance 1,000 (Embedding:InputGuidanceBytes).\nSize warning: Memory ", result);
        Assert.Contains("embedding input is still over the 1,000-byte guidance", result);
    }

    [Fact]
    public async Task Errors_have_no_size_line()
    {
        var result = await Update(Service(1_000), "no-such-id", content: "x");
        Assert.Equal("Memory not found with ID: no-such-id", result);
    }
}
