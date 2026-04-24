using Fleet.Telegram.Configuration;
using Fleet.Telegram.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<CeoOptions>(builder.Configuration.GetSection(CeoOptions.Section));
builder.Services.AddSingleton<BotClientFactory>();
builder.Services.AddHostedService<PeerConfigHostedService>();
builder.Services.AddHttpContextAccessor();

builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

app.MapMcp();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "fleet-telegram" }));

app.Run();
