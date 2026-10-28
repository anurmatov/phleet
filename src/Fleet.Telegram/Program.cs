using Fleet.Shared.Security;
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

// #492: dedicated receiver for managed stock workloads, engaged exactly like fleet-memory's (only when
// StockOpenShell:AuthorityFile is set). The verified outside assertion then supplies ?agent= (and the
// record's fixed formatting_mode), so bot selection runs for the record's agent. Unset changes nothing.
if (app.UseStockServiceIdentity(app.Configuration, "telegram")) app.MapMcp("/mcp");
else app.MapMcp();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "fleet-telegram" }));

app.Run();
