using Fleet.Memory.Configuration;
using Fleet.Memory.Data;
using Fleet.Memory.Models;
using Fleet.Memory.Services;
using Fleet.Memory.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Fleet.Memory.Tests;

/// <summary>
/// #469: <see cref="MemoryFileStore.FindFileById"/> matched the short ID anywhere in the file name,
/// so a memory whose title (and therefore slug) names another memory's short ID could be returned for
/// that other memory — and <c>memory_update</c> then wrote to the wrong record. Synthetic data only.
/// </summary>
public sealed class MemoryFileStoreFindByIdTests : IDisposable
{
    private const string IdA = "1a2b3c4d-0000-4000-8000-000000000001";
    private const string IdC = "1a2b3c4d-0000-4000-8000-000000000002";
    private const string IdB = "9f8e7d6c-0000-4000-8000-000000000003";

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"fleet-memory-findbyid-tests-{Guid.NewGuid():N}");
    private readonly MemoryFileStore _store;

    public MemoryFileStoreFindByIdTests()
    {
        Directory.CreateDirectory(_tempDir);
        _store = new MemoryFileStore(Options.Create(new StorageOptions { Path = _tempDir }), NullLogger<MemoryFileStore>.Instance);
        _store.EnsureDirectories();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private async Task<string> Save(string id, string title, string type = "learning", string project = "")
    {
        var doc = await _store.SaveAsync(new MemoryDocument { Id = id, Type = type, Title = title, Content = "c", Project = project });
        return doc.FilePath;
    }

    private MemoryService Service()
    {
        var embedder = Substitute.For<IEmbeddingService>();
        embedder.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<float[]>(new HttpRequestException("simulated")));

        var qdrant = Options.Create(new QdrantOptions { Url = "http://localhost:1", SimilarityThreshold = 0 });
        var vectors = new VectorStore(qdrant, Options.Create(new EmbeddingOptions()), NullLogger<VectorStore>.Instance);
        return new MemoryService(_store, vectors, embedder, qdrant, new MemorySizeGuidance(0, NullLogger.Instance), NullLogger<MemoryService>.Instance);
    }

    private static AclCacheService DisabledAcl()
    {
        var acl = new AclCacheService(Options.Create(new AclOptions()), Options.Create(new OrchestratorOptions()), NullLogger<AclCacheService>.Instance);
        acl.InjectAclForTesting([]);
        return acl;
    }

    [Fact]
    public async Task Short_id_named_only_in_a_slug_matches_nothing()
    {
        var pathB = await Save(IdB, $"Continuation of {IdA[..8]}");
        Assert.Contains(IdA[..8], Path.GetFileName(pathB));

        Assert.Null(_store.FindFileById(IdA[..8]));
        Assert.Null(_store.FindFileById(IdA));
    }

    [Fact]
    public async Task Slug_naming_another_short_id_does_not_capture_its_lookups()
    {
        // B is saved under a type enumerated before A's, so a first-contains match would return B.
        var pathB = await Save(IdB, $"Continuation of {IdA[..8]}", type: "task_result");
        var pathA = await Save(IdA, "Original memory");

        Assert.Equal(pathA, _store.FindFileById(IdA[..8]));
        Assert.Equal(pathA, _store.FindFileById(IdA));
        Assert.Equal(pathB, _store.FindFileById(IdB));
        Assert.Equal(pathB, _store.FindFileById(IdB[..8]));
    }

    [Fact]
    public async Task Full_uuid_skips_a_file_whose_front_matter_id_differs()
    {
        var pathC = await Save(IdC, "Shares the short id", type: "task_result");
        var pathA = await Save(IdA, "Original memory");

        Assert.Equal(pathA, _store.FindFileById(IdA));
        Assert.Equal(pathC, _store.FindFileById(IdC));
        Assert.Equal(pathA, _store.FindFileById(IdA.ToUpperInvariant()));
    }

    [Fact]
    public async Task Short_id_shared_by_two_memories_is_ambiguous()
    {
        await Save(IdA, "Original memory");
        await Save(IdC, "Shares the short id");

        var ex = Assert.Throws<AmbiguousMemoryIdException>(() => _store.FindFileById(IdA[..8]));
        Assert.Contains("Pass the full UUID", ex.Message);
    }

    [Fact]
    public async Task Memory_update_with_a_full_uuid_changes_only_that_memory()
    {
        var pathB = await Save(IdB, $"Continuation of {IdA[..8]}", type: "task_result", project: "beta");
        var pathA = await Save(IdA, "Original memory", project: "alpha");

        var result = await new MemoryUpdateTool(Service(), DisabledAcl(), null!).UpdateAsync(IdA, project: "gamma");

        Assert.StartsWith($"Updated memory 'Original memory' (id: {IdA})", result);
        Assert.Equal("gamma", (await _store.ParseFileAsync(pathA))!.Project);
        Assert.Equal("beta", (await _store.ParseFileAsync(pathB))!.Project);
    }

    [Fact]
    public async Task Memory_tools_report_an_ambiguous_short_id_instead_of_picking_one()
    {
        await Save(IdA, "Original memory");
        await Save(IdC, "Shares the short id");
        var service = Service();
        var shortId = IdA[..8];

        var get = await new MemoryGetTool(service, new ReadCounterService(), DisabledAcl(), new HttpContextAccessor(), NullLogger<MemoryGetTool>.Instance).GetAsync(shortId);
        var update = await new MemoryUpdateTool(service, DisabledAcl(), null!).UpdateAsync(shortId, project: "gamma");
        var delete = await new MemoryDeleteTool(service, DisabledAcl(), new HttpContextAccessor(), NullLogger<MemoryDeleteTool>.Instance).DeleteAsync(shortId);

        Assert.StartsWith($"memory_get: Ambiguous memory ID '{shortId}'", get);
        Assert.StartsWith($"memory_update: Ambiguous memory ID '{shortId}'", update);
        Assert.StartsWith($"memory_delete: Ambiguous memory ID '{shortId}'", delete);
        Assert.NotNull(_store.FindFileById(IdA));
        Assert.NotNull(_store.FindFileById(IdC));
    }
}
