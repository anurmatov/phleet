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

    public async Task<(MemoryDocument Stored, List<(string Id, string Title, float Score)> SimilarMemories)> StoreAsync(MemoryDocument doc, CancellationToken ct = default)
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
                    var existing = await fileStore.ParseFileAsync(filePath);
                    if (existing is not null)
                        similarMemories.Add((existing.Id, existing.Title, score));
                }
            }
        }

        doc = await fileStore.SaveAsync(doc);

        // Embedding and indexing is handled by the FileWatcherService
        // but we can do it eagerly here for immediate availability
        await IndexFileAsync(doc.FilePath, ct);

        return (doc, similarMemories);
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
            var doc = await fileStore.ParseFileAsync(filePath);
            if (doc is not null)
                documents.Add(doc);
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

    public async Task<MemoryDocument?> GetAsync(string id)
    {
        var filePath = fileStore.FindFileById(id);
        if (filePath is null)
            return null;

        return await fileStore.ParseFileAsync(filePath);
    }

    public async Task<MemoryDocument> UpdateAsync(string id, string? title = null, string? content = null, List<string>? tags = null, string? project = null, CancellationToken ct = default)
    {
        var filePath = fileStore.FindFileById(id)
            ?? throw new FileNotFoundException($"Memory not found: {id}");

        var doc = await fileStore.UpdateAsync(filePath, d =>
        {
            if (title is not null) d.Title = title;
            if (content is not null) d.Content = content;
            if (tags is not null) d.Tags = tags;
            if (project is not null) d.Project = MemoryDocument.NormalizeProject(project);
        });

        // Re-index will be handled by FileWatcher
        return doc;
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

    public async Task IndexFileAsync(string filePath, CancellationToken ct = default)
    {
        var doc = await fileStore.ParseFileAsync(filePath);
        if (doc is null)
        {
            logger.LogWarning("Could not parse file for indexing: {Path}", filePath);
            return;
        }

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
