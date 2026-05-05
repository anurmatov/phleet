using System.Text.Json;
using Fleet.Telegram.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<BotClientFactory>();
builder.Services.AddSingleton<CeoConfigService>();
builder.Services.AddSingleton<MessageStore>();
builder.Services.AddHostedService<PeerConfigHostedService>();
builder.Services.AddHttpContextAccessor();

builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

app.MapMcp();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "fleet-telegram" }));

// Internal endpoint (Docker network only — no auth) for fleet-agent to push incoming messages
// into the per-chat ring buffer so get_message / get_recent_messages can resolve them.
app.MapPost("/internal/messages/record", async (HttpContext ctx, MessageStore store) =>
{
    using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
    var root = doc.RootElement;

    if (!root.TryGetProperty("chat_id", out var chatIdProp) || !chatIdProp.TryGetInt64(out var chatId))
        return Results.BadRequest(new { error = "chat_id required" });
    if (!root.TryGetProperty("message_id", out var msgIdProp) || !msgIdProp.TryGetInt64(out var messageId))
        return Results.BadRequest(new { error = "message_id required" });

    var text = root.TryGetProperty("text", out var tp) ? tp.GetString() ?? "" : "";
    var senderUserId = root.TryGetProperty("sender_user_id", out var suidp) && suidp.TryGetInt64(out var suid) ? suid : 0L;
    var senderUsername = root.TryGetProperty("sender_username", out var sunp) ? sunp.GetString() ?? "" : "";
    var timestamp = root.TryGetProperty("timestamp", out var tsp) && DateTimeOffset.TryParse(tsp.GetString(), out var ts)
        ? ts : DateTimeOffset.UtcNow;

    store.Record(chatId, messageId, text, senderUserId, senderUsername, timestamp);
    return Results.Ok();
});

app.Run();
