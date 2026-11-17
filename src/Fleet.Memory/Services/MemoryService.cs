using Fleet.Memory.Configuration;
using Fleet.Memory.Data;
using Fleet.Memory.Models;
using Microsoft.Extensions.Options;

namespace Fleet.Memory.Services;

public sealed partial class MemoryService(
    MemoryFileStore fileStore,
    IVectorIndex vectorStore,
    IEmbeddingService embeddingService,
    IOptions<QdrantOptions> qdrantOptions,
    MemorySizeGuidance sizeGuidance,
    ILogger<MemoryService> logger,
    MemoryCatalogue? catalogue = null,
    MemoryIndexSettings? indexSettings = null)
{
    private readonly float _similarityThreshold = qdrantOptions.Value.SimilarityThreshold;
    private readonly MemoryCatalogue _catalogue = catalogue ?? new MemoryCatalogue();
    private readonly MemoryIndexSettings _settings = indexSettings ?? new MemoryIndexSettings();

    /// <summary>
    /// A memory's title and content as one string: what a memory of one section is embedded as, and
    /// what the #346 size report and guidance measure for every memory. A longer memory is embedded
    /// in sections (<see cref="MemoryIndexPlan"/>), each as the title, a blank line and the section.
    /// </summary>
    public static string EmbeddingText(MemoryDocument doc) => $"{doc.Title}\n\n{doc.Content}";

    /// <summary>
    /// Stores a new memory. Throws <see cref="InvalidDataException"/> if serialization produces
    /// unparseable YAML (error_serialization_validation). Throws <see cref="IOException"/> if the
    /// atomic rename fails (error_write_failed). On indexing infrastructure failures the file is
    /// committed to disk and IndexingWarning is set (warning_indexing_deferred).
    /// The pre-save similarity probe is best-effort: any failure except the caller's own cancellation
    /// skips the duplicate hint and the memory is saved anyway. The size report is produced after the
    /// file is committed, even when indexing then fails.
    /// </summary>
    public async Task<(MemoryDocument Stored, List<(string Id, string Title, float Score)> SimilarMemories, string? IndexingWarning, MemorySizeReport Size)> StoreAsync(
        MemoryDocument doc, CancellationToken ct = default, string surface = "mcp")
    {
        if (string.IsNullOrEmpty(doc.Id))
            doc.Id = Guid.NewGuid().ToString();

        doc.Project = MemoryDocument.NormalizeProject(doc.Project);

        // Check for similar memories before saving (dense-only for semantic similarity)
        var similarMemories = new List<(string Id, string Title, float Score)>();
        if (_similarityThreshold > 0)
        {
            try
            {
                // The new memory's first section against every indexed section, one candidate per
                // memory: a memory of one section is compared whole, as it always was.
                var probe = MemoryIndexPlan.Build(doc, doc.FilePath, _settings);
                var embedding = await embeddingService.EmbedAsync(probe.DenseText(probe.Sections[0]), ct);
                var candidates = SearchFusion.BestPerMemory(
                        await vectorStore.SearchDenseAsync(embedding, SimilarCandidates * SectionOverfetch, null, ct))
                    .Take(SimilarCandidates);

                foreach (var (filePath, score, _, _) in candidates)
                {
                    if (score >= _similarityThreshold)
                    {
                        try
                        {
                            var existing = await fileStore.ParseFileAsync(filePath);
                            if (existing is not null)
                                similarMemories.Add((existing.Id, existing.Title, score));
                        }
                        catch (InvalidDataException)
                        {
                            // Corrupt candidate file — skip it
                        }
                    }
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                // Best-effort: a failing or rejecting embedder (or Qdrant) must not stop the save.
                // The caller's own cancellation still propagates, and nothing is written.
                logger.LogWarning("Similarity check skipped for new memory {Id}: {ExceptionType}", doc.Id, ex.GetType().Name);
                similarMemories.Clear();
            }
        }

        // SaveAsync throws InvalidDataException (serialization validation) or IOException (write/rename).
        // Both propagate to the tool caller for surfacing as error keywords.
        doc = await fileStore.SaveAsync(doc);

        // Index immediately at the final path (rename-then-index ordering ensures Qdrant
        // always receives the correct file path, never a temp path).
        string? indexingWarning = null;
        MemoryDocument? onDisk = null;
        try
        {
            onDisk = await ParseForIndexAsync(doc.FilePath);
            await IndexDocumentAsync(onDisk, doc.FilePath, ct);
        }
        catch (InvalidDataException ex)
        {
            // Defensive: pre-write validation in SaveAsync uses the same content, so this
            // should not occur. If it does, clean up the committed file and propagate.
            logger.LogError(ex, "Post-save indexing parse failure for {Path} — deleting file", doc.FilePath);
            try { File.Delete(doc.FilePath); }
            catch (Exception deleteEx) { logger.LogWarning(deleteEx, "Failed to delete file after post-save parse failure: {Path}", doc.FilePath); }
            throw;
        }
        catch (Exception ex)
        {
            // Infrastructure failure (Qdrant unavailable, embedding timeout, etc.).
            // The file is safely committed to disk. FileWatcherService will re-index on recovery.
            logger.LogWarning(ex, "Indexing deferred for {Path} due to infrastructure failure", doc.FilePath);
            indexingWarning = $"warning_indexing_deferred: memory written but not yet searchable — {ex.GetType().Name}: {ex.Message}";
        }

        // Measured on the text IndexFileAsync embeds — the document as parsed back from disk. Only if
        // the read-back itself failed (an I/O fault, already reported as deferred) is the in-memory copy
        // used, which the pre-write round-trip validation makes identical.
        var size = sizeGuidance.Evaluate(doc.Id, previousText: null, EmbeddingText(onDisk ?? doc));
        sizeGuidance.LogWrite(size, surface, "store", doc.Id);

        return (doc, similarMemories, indexingWarning, size);
    }

    public async Task<List<Dictionary<string, string>>> ListAsync(string? type = null, string? project = null, string? agent = null, string? tag = null)
    {
        var filters = new Dictionary<string, string>();
        if (type is not null) filters["memory_type"] = type;
        if (project is not null) filters["project"] = MemoryDocument.NormalizeProject(project);
        if (agent is not null) filters["agent_name"] = agent;
        if (tag is not null) filters["tags"] = tag;

        return await vectorStore.ScrollFilteredAsync(filters.Count > 0 ? filters : null);
    }

    /// <summary>
    /// Like ListAsync but returns typed MemoryDocument objects (parsed from disk) so callers
    /// can rely on the typed Project field rather than the Qdrant payload dict key.
    /// </summary>
    public async Task<List<MemoryDocument>> ListDocumentsAsync(string? type = null, string? project = null, string? agent = null, string? tag = null)
    {
        var payloads = await ListAsync(type, project, agent, tag);
        var docs = new List<MemoryDocument>(payloads.Count);
        foreach (var payload in payloads)
        {
            if (!payload.TryGetValue("memory_id", out var id) || string.IsNullOrEmpty(id))
                continue;
            MemoryDocument? doc;
            try
            {
                doc = await GetAsync(id);
            }
            catch (AmbiguousMemoryIdException ex)
            {
                logger.LogWarning(ex, "Skipping memory {Id} in list: more than one file carries this id", id);
                continue;
            }
            if (doc is not null)
                docs.Add(doc);
        }
        return docs;
    }

    public async Task<MemoryDocument?> GetAsync(string id)
    {
        var filePath = fileStore.FindFileById(id);
        if (filePath is null)
            return null;

        try
        {
            return await fileStore.ParseFileAsync(filePath);
        }
        catch (InvalidDataException ex)
        {
            logger.LogError(ex, "Memory file {Id} at {Path} is corrupt and cannot be parsed", id, filePath);
            return null;
        }
    }

    /// <summary>
    /// Updates an existing memory. Throws <see cref="FileNotFoundException"/> if not found,
    /// <see cref="InvalidDataException"/> if the mutation produces unparseable YAML,
    /// or <see cref="IOException"/> if the atomic rename fails.
    /// On indexing infrastructure failures the file is committed and IndexingWarning is set.
    /// The size report compares the embedding text before and after the update; a tags-only or
    /// project-only update still reports it, because reindexing embeds the whole memory.
    /// </summary>
    public async Task<(MemoryDocument Doc, string? IndexingWarning, MemorySizeReport Size)> UpdateAsync(
        string id, string? title = null, string? content = null, List<string>? tags = null, string? project = null,
        CancellationToken ct = default, string surface = "mcp")
    {
        var filePath = fileStore.FindFileById(id)
            ?? throw new FileNotFoundException($"Memory not found: {id}");

        // fileStore.UpdateAsync throws InvalidDataException (pre-write validation) or IOException (rename).
        string? previousText = null;
        var doc = await fileStore.UpdateAsync(filePath, d =>
        {
            // Captured before any change is applied: d is the document as it is on disk.
            previousText = EmbeddingText(d);
            if (title is not null) d.Title = title;
            if (content is not null) d.Content = content;
            if (tags is not null) d.Tags = tags;
            if (project is not null) d.Project = MemoryDocument.NormalizeProject(project);
        });

        // Index immediately after the rename completes (file is at final path).
        string? indexingWarning = null;
        MemoryDocument? onDisk = null;
        try
        {
            onDisk = await ParseForIndexAsync(filePath);
            await IndexDocumentAsync(onDisk, filePath, ct);
        }
        catch (InvalidDataException ex)
        {
            // Defensive: pre-write validation in fileStore.UpdateAsync should have caught this.
            logger.LogError(ex, "Post-update indexing parse failure for {Path}", filePath);
            throw;
        }
        catch (Exception ex)
        {
            // Infrastructure failure — file is committed, watcher will retry.
            logger.LogWarning(ex, "Indexing deferred for {Path} due to infrastructure failure", filePath);
            indexingWarning = $"warning_indexing_deferred: memory updated but not yet searchable — {ex.GetType().Name}: {ex.Message}";
        }

        var size = sizeGuidance.Evaluate(doc.Id, previousText, EmbeddingText(onDisk ?? doc));
        sizeGuidance.LogWrite(size, surface, "update", doc.Id);

        return (doc, indexingWarning, size);
    }

    public async Task DeleteAsync(string id, bool permanent = false, CancellationToken ct = default)
    {
        var filePath = fileStore.FindFileById(id)
            ?? throw new FileNotFoundException($"Memory not found: {id}");

        fileStore.Delete(filePath, permanent);

        // Remove from the catalogue and from Qdrant
        await RemoveIndexEntryAsync(filePath, ct);
    }

    public Dictionary<string, int> GetStats() => fileStore.GetStats();

    /// <summary>
    /// #518: ACL-scoped counts for ACL-enabled <c>memory_stats</c>, from the files only (no index or embedding call). See
    /// <see cref="MemoryFileStore.GetScopedStatsAsync(Func{string?, bool}, CancellationToken)"/> for the failure contract.
    /// </summary>
    public Task<Dictionary<string, int>> GetScopedStatsAsync(Func<string?, bool> canRead, CancellationToken ct) =>
        fileStore.GetScopedStatsAsync(canRead, ct);
}
