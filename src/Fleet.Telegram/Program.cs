using Fleet.Telegram.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<BotClientFactory>();
builder.Services.AddSingleton<CeoConfigService>();
builder.Services.AddHostedService<PeerConfigHostedService>();
builder.Services.AddHttpContextAccessor();

// Every Bot API send goes through TelegramSender; receipts reach the broker only when
// Journal:ToolSendReceipts is true (#394).
builder.Services.AddToolSendReceipts(builder.Configuration);

builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

app.MapMcp();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "fleet-telegram" }));

app.Run();
