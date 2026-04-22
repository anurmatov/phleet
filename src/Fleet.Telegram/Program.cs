using Fleet.Telegram.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<BotClientFactory>();
builder.Services.AddHostedService<PeerConfigHostedService>();

builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

app.MapMcp();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "fleet-telegram" }));

app.Run();
