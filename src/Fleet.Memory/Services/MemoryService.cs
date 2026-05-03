using Fleet.Memory.Configuration;
using Fleet.Memory.Data;
using Fleet.Memory.Models;
using Microsoft.Extensions.Options;

namespace Fleet.Memory.Services;

public sealed class MemoryService(
    MemoryFileStore fileStore,
    VectorStore vectorStore,
    IEmbeddingService embeddingService,
    IOptions<QdrantOptions> qdrantOptions,
    ILogger<MemoryService> logger)
{
    private readonly float _similarityThreshold = qdrantOptions.Value.SimilarityThreshold;

    /// <summary>
    /// Stores a new memory. Throws <see cref="InvalidDataException"/> if serialization produces
    /// unparseable YAML (error_serialization_validation). Throws <see cref="IOException"/> if the
    /// atomic rename fails (error_write_failed). On indexing infrastructure failures the file is
    /// committed to disk and IndexingWarning is set (warning_indexing_deferred).
    /// </summary>
    public async Task<(MemoryDocument Stored, List<(string Id, string Title, float Score)> SimilarMemories, string? IndexingWarning)> StoreAsync(MemoryDocument doc, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(doc.Id))
            doc.Id = Guid.NewGuid().ToString();

        doc.Project = MemoryDocument.NormalizeProject(doc.Project);

        // Check for similar memories before saving (dense-only for semantic similarity)
        var similarMemories = new List<(string Id, string Title, float Score)>();
        if (_similarityThreshold > 0)
        {
            var textToEmbed = $"{doc.Title}\n\n{doc.Content}";
            var embedding = await embeddingService.EmbedAsync(textToEmbed, ct);
            var candidates = await vectorStore.SearchDenseOnlyAsync(embedding, limit: 3, ct);

            foreach (var (filePath, score) in candidates)
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

        // SaveAsync throws InvalidDataException (serialization validation) or IOException (write/rename).
        // Both propagate to the tool caller for surfacing as error keywords.
        doc = await fileStore.SaveAsync(doc);

        // Index immediately at the final path (rename-then-index ordering ensures Qdrant
        // always receives the correct file path, never a temp path).
        string? indexingWarning = null;
        try
        {
            await IndexFileAsync(doc.FilePath, ct);
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

        return (doc, similarMemories, indexingWarning);
    }

    public async Task<List<MemoryDocument>> SearchAsync(string query, int limit = 10, string? type = null, string? project = null, string? agent = null, CancellationToken ct = default)
    {
        var queryEmbedding = await embeddingService.EmbedAsync(query, ct);

        var filters = new Dictionary<string, string>();
        if (type is not null) filters["memory_type"] = type;
        if (project is not null) filters["project"] = MemoryDocument.NormalizeProject(project);
        if (agent is not null) filters["agent_name"] = agent;

        var results = await vectorStore.SearchAsync(queryEmbedding, query, limit, filters.Count > 0 ? filters : null, ct);

        var documents = new List<MemoryDocument>();
        foreach (var (filePath, score) in results)
        {
            try
            {
                var doc = await fileStore.ParseFileAsync(filePath);
                if (doc is not null)
                    documents.Add(doc);
            }
            catch (InvalidDataException ex)
            {
                logger.LogError(ex, "Corrupt memory file skipped during search result assembly: {Path}", filePath);
            }
        }

        return documents;
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
            var doc = await GetAsync(id);
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
    /// </summary>
    public async Task<(MemoryDocument Doc, string? IndexingWarning)> UpdateAsync(string id, string? title = null, string? content = null, List<string>? tags = null, string? project = null, CancellationToken ct = default)
    {
        var filePath = fileStore.FindFileById(id)
            ?? throw new FileNotFoundException($"Memory not found: {id}");

        // fileStore.UpdateAsync throws InvalidDataException (pre-write validation) or IOException (rename).
        var doc = await fileStore.UpdateAsync(filePath, d =>
        {
            if (title is not null) d.Title = title;
            if (content is not null) d.Content = content;
            if (tags is not null) d.Tags = tags;
            if (project is not null) d.Project = MemoryDocument.NormalizeProject(project);
        });

        // Index immediately after the rename completes (file is at final path).
        string? indexingWarning = null;
        try
        {
            await IndexFileAsync(filePath, ct);
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

        return (doc, indexingWarning);
    }

    public async Task DeleteAsync(string id, bool permanent = false, CancellationToken ct = default)
    {
        var filePath = fileStore.FindFileById(id)
            ?? throw new FileNotFoundException($"Memory not found: {id}");

        fileStore.Delete(filePath, permanent);

        // Remove from Qdrant
        await vectorStore.DeleteAsync(filePath, ct);
    }

    public Dictionary<string, int> GetStats() => fileStore.GetStats();

    /// <summary>
    /// Indexes a memory file into Qdrant.
    /// Throws <see cref="InvalidDataException"/> if the file cannot be parsed (bad YAML or not a memory file).
    /// Other exceptions indicate infrastructure failures (Qdrant, embedding) and propagate as-is.
    /// </summary>
    public async Task IndexFileAsync(string filePath, CancellationToken ct = default)
    {
        // ParseFileAsync throws InvalidDataException for bad YAML (corrupt file).
        // Returns null only for files without a frontmatter header (not a memory file).
        var doc = await fileStore.ParseFileAsync(filePath);
        if (doc is null)
            throw new InvalidDataException($"File is not a parseable memory file: {filePath}");

        var textToEmbed = $"{doc.Title}\n\n{doc.Content}";
        var embedding = await embeddingService.EmbedAsync(textToEmbed, ct);

        var payload = new Dictionary<string, object>
        {
            ["file_path"] = filePath,
            ["memory_id"] = doc.Id,
            ["memory_type"] = doc.Type,
            ["agent_name"] = doc.Agent,
            ["project"] = MemoryDocument.NormalizeProject(doc.Project),
            ["title"] = doc.Title,
            ["tags"] = doc.Tags,
            ["created"] = doc.Created.ToString("o")
        };

        await vectorStore.UpsertAsync(filePath, embedding, textToEmbed, payload, ct);
        logger.LogInformation("Indexed memory {Id} ({Type}): {Title}", doc.Id, doc.Type, doc.Title);
    }

    public async Task RemoveFromIndexAsync(string filePath, CancellationToken ct = default)
    {
        await vectorStore.DeleteAsync(filePath, ct);
        logger.LogInformation("Removed {Path} from index", filePath);
    }
}
