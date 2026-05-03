using Fleet.Memory.Configuration;
using Fleet.Memory.Data;
using Fleet.Memory.Services;

var builder = WebApplication.CreateBuilder(args);

// Configuration
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.Section));
builder.Services.Configure<QdrantOptions>(builder.Configuration.GetSection(QdrantOptions.Section));
builder.Services.Configure<EmbeddingOptions>(builder.Configuration.GetSection(EmbeddingOptions.Section));
builder.Services.Configure<AclOptions>(builder.Configuration.GetSection(AclOptions.Section));
builder.Services.Configure<OrchestratorOptions>(builder.Configuration.GetSection(OrchestratorOptions.Section));

// Data layer
builder.Services.AddSingleton<MemoryFileStore>();
builder.Services.AddSingleton<VectorStore>();

// Embedding provider — config-driven
var embeddingConfig = builder.Configuration.GetSection(EmbeddingOptions.Section).Get<EmbeddingOptions>()
    ?? new EmbeddingOptions();

if (embeddingConfig.Provider.Equals("ollama", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddHttpClient<IEmbeddingService, OllamaEmbeddingService>((client) =>
    {
        client.BaseAddress = new Uri(embeddingConfig.Ollama.Url);
        client.Timeout = TimeSpan.FromSeconds(30);
    });
}
else
{
    // ONNX: singleton (InferenceSession is thread-safe and expensive to create)
    builder.Services.AddSingleton<IEmbeddingService, OnnxEmbeddingService>();
}

// Services
builder.Services.AddSingleton<MemoryService>();
builder.Services.AddSingleton<ReadCounterService>();
builder.Services.AddSingleton<AclDeniedCounterService>();
builder.Services.AddHostedService<FileWatcherService>();

// ACL cache — registered as singleton so tools can inject it, and as hosted service for lifecycle
builder.Services.AddSingleton<AclCacheService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AclCacheService>());
builder.Services.AddHostedService<PeerConfigHostedService>();

// IHttpContextAccessor for agent attribution in MemoryGetTool and ACL enforcement
builder.Services.AddHttpContextAccessor();

// MCP Server
builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

app.MapMcp();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "fleet-memory" }));

// ── Internal REST API ─────────────────────────────────────────────────────────
// Internal-network only (not authenticated). The orchestrator proxies these to
// power the dashboard Memory page.

// GET /internal/memory — list all memories (metadata only, no content bodies)
app.MapGet("/internal/memory", async (MemoryService memoryService) =>
{
    var items = await memoryService.ListAsync();
    var result = items.Select(d =>
    {
        // VectorStore returns tags as a comma-joined string; split back to string[]
        // to match the string[] shape on the search/get endpoints.
        var rawTags = d.GetValueOrDefault("tags") ?? "";
        var tags = string.IsNullOrEmpty(rawTags)
            ? Array.Empty<string>()
            : rawTags.Split(", ", StringSplitOptions.RemoveEmptyEntries);
        return new
        {
            id         = d.GetValueOrDefault("memory_id") ?? d.GetValueOrDefault("id") ?? "",
            title      = d.GetValueOrDefault("title") ?? "",
            project    = d.GetValueOrDefault("project") ?? "",
            type       = d.GetValueOrDefault("memory_type") ?? "",
            tags,
            updated_at = d.GetValueOrDefault("created") ?? "",
        };
    });
    return Results.Ok(result);
});

// GET /internal/memory/ids — id-only list for SPA known-memory cache
app.MapGet("/internal/memory/ids", async (MemoryService memoryService) =>
{
    var items = await memoryService.ListAsync();
    var ids = items
        .Select(d => d.GetValueOrDefault("memory_id") ?? d.GetValueOrDefault("id") ?? "")
        .Where(id => !string.IsNullOrEmpty(id))
        .ToList();
    return Results.Ok(ids);
});

// GET /internal/memory/search?q=...
app.MapGet("/internal/memory/search", async (string? q, MemoryService memoryService) =>
{
    if (string.IsNullOrWhiteSpace(q))
        return Results.BadRequest(new { error = "Missing query parameter 'q'" });

    var docs = await memoryService.SearchAsync(q, limit: 20);
    var result = docs.Select(d => new
    {
        id         = d.Id,
        title      = d.Title,
        project    = d.Project,
        type       = d.Type,
        tags       = d.Tags,
        snippet    = d.Content.Length > 200 ? d.Content[..200] + "…" : d.Content,
        updated_at = d.Updated,
    });
    return Results.Ok(result);
});

// GET /internal/memory/{id} — full content + metadata
app.MapGet("/internal/memory/{id}", async (string id, MemoryService memoryService) =>
{
    var doc = await memoryService.GetAsync(id);
    if (doc is null)
        return Results.NotFound(new { error = $"Memory not found: {id}" });

    return Results.Ok(new
    {
        id         = doc.Id,
        title      = doc.Title,
        type       = doc.Type,
        agent      = doc.Agent,
        project    = doc.Project,
        tags       = doc.Tags,
        source     = doc.Source,
        created_at = doc.Created,
        updated_at = doc.Updated,
        content    = doc.Content,
    });
});

// PUT /internal/memory/{id} — update with optimistic staleness check
app.MapPut("/internal/memory/{id}", async (string id, HttpRequest request, MemoryService memoryService) =>
{
    MemoryUpdateRequest? body;
    try { body = await request.ReadFromJsonAsync<MemoryUpdateRequest>(); }
    catch { return Results.BadRequest(new { error = "invalid_json" }); }

    if (body is null)
        return Results.BadRequest(new { error = "empty_payload" });

    var current = await memoryService.GetAsync(id);
    if (current is null)
        return Results.NotFound(new { error = $"Memory not found: {id}" });

    // Staleness check: last_seen_updated_at must match stored updated_at (±1s tolerance)
    if (!string.IsNullOrEmpty(body.LastSeenUpdatedAt)
        && DateTimeOffset.TryParse(body.LastSeenUpdatedAt, out var lastSeen)
        && Math.Abs((lastSeen - current.Updated).TotalSeconds) > 1)
    {
        return Results.Json(
            new { error = "stale", current_updated_at = current.Updated.ToString("o") },
            statusCode: 409);
    }

    try
    {
        var (updated, indexingWarning) = await memoryService.UpdateAsync(id,
            content: body.Content,
            tags: body.Tags,
            project: body.Project);
        return Results.Ok(new
        {
            id = updated.Id,
            updated_at = updated.Updated,
            indexing_warning = indexingWarning
        });
    }
    catch (InvalidDataException ex)
    {
        return Results.BadRequest(new { error = "error_serialization_validation", detail = ex.Message });
    }
    catch (IOException ex)
    {
        return Results.Json(new { error = "error_write_failed", detail = ex.Message }, statusCode: 500);
    }
});

// DELETE /internal/memory/{id}
app.MapDelete("/internal/memory/{id}", async (string id, MemoryService memoryService) =>
{
    try
    {
        await memoryService.DeleteAsync(id);
        return Results.Ok(new { deleted = id });
    }
    catch (FileNotFoundException)
    {
        return Results.NotFound(new { error = $"Memory not found: {id}" });
    }
});

// GET /internal/stats/reads — in-memory read counter snapshot (resets on restart)
app.MapGet("/internal/stats/reads", (ReadCounterService readCounter) =>
    Results.Ok(new { since = readCounter.Since, entries = readCounter.GetSnapshot() }));

// GET /internal/stats/acl-denied — in-memory ACL denial counter snapshot (resets on restart)
app.MapGet("/internal/stats/acl-denied", (AclDeniedCounterService deniedCounter) =>
    Results.Ok(new { since = deniedCounter.Since, entries = deniedCounter.GetSnapshot() }));

// GET /api/admin/memories/no-project-count — counts memories with no project set (migration planning aid)
app.MapGet("/api/admin/memories/no-project-count", async (MemoryService memoryService) =>
{
    var all = await memoryService.ListAsync();
    var count = all.Count(d => string.IsNullOrEmpty(d.GetValueOrDefault("project") ?? ""));
    return Results.Ok(new { no_project_count = count });
});

app.Run();

internal record MemoryUpdateRequest(
    string? Content,
    List<string>? Tags,
    string? Project,
    string? LastSeenUpdatedAt);
