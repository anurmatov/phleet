using Fleet.Memory.Configuration;
using Fleet.Memory.Data;
using Fleet.Memory.Services;

var builder = WebApplication.CreateBuilder(args);

// Configuration
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.Section));
builder.Services.Configure<QdrantOptions>(builder.Configuration.GetSection(QdrantOptions.Section));
builder.Services.Configure<EmbeddingOptions>(builder.Configuration.GetSection(EmbeddingOptions.Section));

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
builder.Services.AddHostedService<FileWatcherService>();

// MCP Server
builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

app.MapMcp();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "fleet-memory" }));

app.Run();
