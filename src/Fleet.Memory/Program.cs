using Fleet.Memory.Configuration;
using Fleet.Memory.Data;
using Fleet.Memory.Endpoints;
using Fleet.Memory.Models;
using Fleet.Memory.Services;
using Fleet.Shared.Mcp;

var builder = WebApplication.CreateBuilder(args);

// A Qdrant API key (Qdrant:ApiKey, off by default) is sent only over TLS. A key with a URL that is
// not https ends the process here, before the index or anything else connects: the key is neither
// sent in the clear nor dropped to connect without it. The explicit exit, as in
// Fleet.Orchestrator/Program.cs: this must not depend on how a throw propagates.
if (QdrantConnection.DescribeFault(
        builder.Configuration[$"{QdrantOptions.Section}:Url"], builder.Configuration[$"{QdrantOptions.Section}:ApiKey"]) is { } qdrantFault)
{
    Console.Error.WriteLine($"Fleet.Memory startup aborted, exiting 1: {qdrantFault}");
    Environment.Exit(1);
}

// Configuration
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.Section));
builder.Services.Configure<QdrantOptions>(builder.Configuration.GetSection(QdrantOptions.Section));
builder.Services.Configure<EmbeddingOptions>(builder.Configuration.GetSection(EmbeddingOptions.Section));
builder.Services.Configure<AclOptions>(builder.Configuration.GetSection(AclOptions.Section));
builder.Services.Configure<OrchestratorOptions>(builder.Configuration.GetSection(OrchestratorOptions.Section));

// Data layer
builder.Services.AddSingleton<MemoryFileStore>();
builder.Services.AddSingleton<VectorStore>();
builder.Services.AddSingleton<IVectorIndex>(sp => sp.GetRequiredService<VectorStore>());

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

// Embedding-input size guidance (#346), off by default. Parsed from the raw config string, never
// bound into EmbeddingOptions above: a typo there would be a crash loop, here it is one Warning.
builder.Services.AddSingleton(sp => MemorySizeGuidance.FromConfiguration(
    builder.Configuration, sp.GetRequiredService<ILogger<MemorySizeGuidance>>()));

// Search: the in-memory catalogue, and the section and cut-off settings. Like the guidance above,
// the settings are parsed from the raw strings: a bad value is the default and one Warning.
builder.Services.AddSingleton<MemoryCatalogue>();
builder.Services.AddSingleton(sp => MemoryIndexSettings.FromConfiguration(
    builder.Configuration, sp.GetRequiredService<ILogger<MemoryIndexSettings>>()));

// Services
builder.Services.AddSingleton<MemoryService>();
builder.Services.AddSingleton<ReadCounterService>();
builder.Services.AddSingleton<AclDeniedCounterService>();
builder.Services.AddHostedService<FileWatcherService>();

// ACL cache — registered as singleton so tools can inject it, and as hosted service for lifecycle
builder.Services.AddSingleton<AclCacheService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AclCacheService>());
builder.Services.AddHostedService<PeerConfigHostedService>();

// Who is calling: an agent by its identity token, held to the tools it is granted (read from the
// orchestrator). Tools ask AgentCallerHttp for the agent, never the URL. See docs/agent-identity.md.
builder.Services.AddAgentIdentityFromOrchestrator(builder.Configuration);

// MCP Server
builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly()
    .AddAgentToolAuthorization(MemoryIdentity.ServerName);

var app = builder.Build();

// Resolve the guidance now so an invalid value warns at startup, then log the active value.
app.Services.GetRequiredService<MemorySizeGuidance>().LogStartup(
    embeddingConfig.Provider,
    embeddingConfig.Provider.Equals("ollama", StringComparison.OrdinalIgnoreCase)
        ? embeddingConfig.Ollama.Model
        : Path.GetFileNameWithoutExtension(embeddingConfig.Onnx.ModelPath));

// Said only when a key is in use (nothing new is logged or read otherwise), and never the key.
var qdrantOptions = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<QdrantOptions>>().Value;
if (!string.IsNullOrWhiteSpace(qdrantOptions.ApiKey))
    app.Logger.LogInformation("Qdrant connection: {Connection}", QdrantConnection.From(qdrantOptions));

// The MCP endpoint is at the root, so everything but the health check and the service routes is it.
app.UseAgentIdentity(context => MemoryIdentity.IsMcp(context.Request.Path));
// /internal/* reads, changes and deletes any memory with no project check: a service's, never an agent's.
app.UseServiceIdentity(context => MemoryIdentity.IsInternal(context.Request.Path));

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
            updated_at = MemoryListEntry.UpdatedAt(d),
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

// GET /internal/memory/search?q=...&project=&type=&tag=&since=&limit=
app.MapGet("/internal/memory/search", MemorySearchEndpoint.HandleAsync);

// GET /internal/memory/{id} — full content + metadata
app.MapGet("/internal/memory/{id}", async (string id, MemoryService memoryService, MemorySizeGuidance sizeGuidance) =>
{
    MemoryDocument? doc;
    try { doc = await memoryService.GetAsync(id); }
    catch (AmbiguousMemoryIdException ex) { return Results.BadRequest(new { error = "ambiguous_id", detail = ex.Message }); }
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
        // #346: what the dashboard meter measures against — the exact embedding input's size.
        size_guidance = new
        {
            unit        = MemorySizeGuidance.Unit,
            bytes       = MemorySizeGuidance.Utf8Bytes(MemoryService.EmbeddingText(doc)),
            limit_bytes = sizeGuidance.Enabled ? sizeGuidance.LimitBytes : (int?)null,
            limit_key   = MemorySizeGuidance.Key,
        },
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

    MemoryDocument? current;
    try { current = await memoryService.GetAsync(id); }
    catch (AmbiguousMemoryIdException ex) { return Results.BadRequest(new { error = "ambiguous_id", detail = ex.Message }); }
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
        var (updated, indexingWarning, size) = await memoryService.UpdateAsync(id,
            content: body.Content,
            tags: body.Tags,
            project: body.Project,
            surface: "rest");
        return Results.Ok(new
        {
            id = updated.Id,
            updated_at = updated.Updated,
            indexing_warning = indexingWarning,
            size,
        });
    }
    catch (AmbiguousMemoryIdException ex)
    {
        return Results.BadRequest(new { error = "ambiguous_id", detail = ex.Message });
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
    catch (AmbiguousMemoryIdException ex)
    {
        return Results.BadRequest(new { error = "ambiguous_id", detail = ex.Message });
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
