using Fleet.Bridge.Configuration;
using Fleet.Bridge.Services;

var builder = WebApplication.CreateBuilder(args);

// Configuration
builder.Services.Configure<BridgeOptions>(builder.Configuration.GetSection(BridgeOptions.Section));
builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection(RabbitMqOptions.Section));
builder.Services.Configure<TelegramOptions>(builder.Configuration.GetSection(TelegramOptions.Section));

// Services
builder.Services.AddSingleton<BridgeRelayService>();
builder.Services.AddSingleton<TelegramNotifier>();
builder.Services.AddHostedService<PeerConfigHostedService>();

// MCP Server
builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

// Initialize RabbitMQ connection on startup
var relay = app.Services.GetRequiredService<BridgeRelayService>();
await relay.InitializeAsync(CancellationToken.None);

app.MapMcp();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "fleet-bridge" }));

app.Run();
