using Fleet.Agent.Configuration;
using Fleet.Agent.Interfaces;
using Fleet.Agent.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Bind configuration sections
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.Section));
builder.Services.Configure<TelegramOptions>(builder.Configuration.GetSection(TelegramOptions.Section));
builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection(RabbitMqOptions.Section));
builder.Services.Configure<WhisperOptions>(builder.Configuration.GetSection(WhisperOptions.Section));
builder.Services.Configure<TtsOptions>(builder.Configuration.GetSection(TtsOptions.Section));

// Register services
builder.Services.AddSingleton<PromptBuilder>();
builder.Services.AddSingleton<ClaudeExecutor>();
builder.Services.AddSingleton<CodexExecutor>();

builder.Services.AddSingleton<IAgentExecutor>(sp =>
{
    var provider = sp.GetRequiredService<IOptions<AgentOptions>>().Value.Provider;
    return provider switch
    {
        "codex" => sp.GetRequiredService<CodexExecutor>(),
        _ => sp.GetRequiredService<ClaudeExecutor>(),
    };
});

builder.Services.AddSingleton<IFleetConnectionState, FleetConnectionState>();
builder.Services.AddSingleton<SessionManager>();
builder.Services.AddSingleton<GroupRelayService>();

// Determine mode from command-line args
var isCliMode = args.Any(a => a == "--task");

if (isCliMode)
{
    // CLI mode: run a single task and exit
    builder.Services.AddHostedService<CliRunner>();
}
else
{
    // Daemon mode: Telegram transport + services
    // AgentTransport injects itself as IMessageSink into these services
    builder.Services.AddSingleton<TaskManager>();
    builder.Services.AddSingleton<CommandDispatcher>();
    builder.Services.AddSingleton<PromptAssembler>();
    builder.Services.AddSingleton<MessageRouter>();
    builder.Services.AddSingleton<GroupBehavior>();
    builder.Services.AddHostedService<AgentTransport>();
    builder.Services.AddHttpClient();
    builder.Services.AddHttpClient("whisper", client =>
    {
        client.Timeout = TimeSpan.FromSeconds(180);
    });
    builder.Services.AddHttpClient("tts", client =>
    {
        client.Timeout = TimeSpan.FromSeconds(60);
    });
    builder.Services.AddSingleton<VoiceTranscriptionService>();
    builder.Services.AddSingleton<TtsService>();

    builder.Services.AddHostedService<WarmupService>();
    builder.Services.AddHostedService<OrchestratorHeartbeatService>();
}

var app = builder.Build();

if (!isCliMode)
{
    // One-shot startup sweep: clean up attachment files left over from prior runs.
    // No background timer — sweep is amortised lazily on each new photo write too.
    var telegramOpts = app.Services.GetRequiredService<IOptions<TelegramOptions>>().Value;
    if (telegramOpts.PersistAttachments)
    {
        var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
        var sweepLogger = loggerFactory.CreateLogger("AttachmentSweeper");
        try
        {
            AttachmentSweeper.SweepExpired(
                telegramOpts.AttachmentDir,
                telegramOpts.AttachmentRetentionHours,
                sweepLogger);
        }
        catch (Exception ex)
        {
            sweepLogger.LogWarning(ex, "Startup attachment sweep failed — agent will continue without cleanup");
        }
    }

    var startedAt = DateTimeOffset.UtcNow;

    app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

    app.MapPost("/cancel", async (IServiceProvider sp) =>
    {
        var taskManager = sp.GetRequiredService<TaskManager>();
        await taskManager.CancelAllAsync();
        return Results.Ok(new { message = "All tasks cancelled and queue cleared" });
    });

    app.MapPost("/cancel/{**taskId}", async (string taskId, IServiceProvider sp) =>
    {
        var taskManager = sp.GetRequiredService<TaskManager>();
        var decoded = Uri.UnescapeDataString(taskId);
        var cancelled = await taskManager.CancelByBridgeTaskIdAsync(decoded);
        return Results.Ok(new { cancelled, taskId = decoded });
    });

    app.MapPost("/cancel_bg/{taskId}", async (string taskId, IServiceProvider sp) =>
    {
        var taskManager = sp.GetRequiredService<TaskManager>();
        var cancelled = await taskManager.CancelBackgroundTaskAsync(taskId);
        return cancelled
            ? Results.Ok(new { message = $"Cancel requested for background task '{taskId}'" })
            : Results.NotFound(new { error = $"Background task '{taskId}' not found" });
    });

    app.MapGet("/status", (IServiceProvider sp) =>
    {
        var agentOptions = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
        var executor = sp.GetRequiredService<IAgentExecutor>();
        var taskManager = sp.GetRequiredService<TaskManager>();

        var (status, currentTask, _) = taskManager.GetOrchestratorStatus();
        var buildCommit = Environment.GetEnvironmentVariable("FLEET_BUILD_COMMIT");

        return Results.Ok(new
        {
            agent = agentOptions.Name,
            role = agentOptions.Role,
            model = agentOptions.Model,
            provider = agentOptions.Provider,
            status,
            currentTask,
            uptime = (long)(DateTimeOffset.UtcNow - startedAt).TotalSeconds,
            version = buildCommit,
            claude = new
            {
                warm = executor.IsProcessWarm,
                lastActivity = executor.LastActivity,
                lastSessionId = executor.LastSessionId,
            }
        });
    });
}

await app.RunAsync();
