using Fleet.Telegram.Services;
using Fleet.Shared.Mcp;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<BotClientFactory>();
builder.Services.AddSingleton<CeoConfigService>();
builder.Services.AddHostedService<PeerConfigHostedService>();
builder.Services.AddHttpContextAccessor();

// Every Bot API send goes through TelegramSender; receipts reach the broker only when
// Journal:ToolSendReceipts is true (#394).
builder.Services.AddToolSendReceipts(builder.Configuration);

// Who is calling: an agent by its identity token, held to the tools it is granted (read from the
// orchestrator). Whose bot sends is decided by that, never by the URL. See docs/agent-identity.md.
builder.Services.AddAgentIdentityFromOrchestrator(builder.Configuration);

builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly()
    .AddAgentToolAuthorization("fleet-telegram");

var app = builder.Build();

// The MCP endpoint is at the root: everything but the health check.
app.UseAgentIdentity(context => !context.Request.Path.Equals("/health", StringComparison.OrdinalIgnoreCase));

app.MapMcp();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "fleet-telegram" }));

app.Run();
