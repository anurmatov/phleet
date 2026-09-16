using Fleet.Agent.Abstractions;
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
builder.Services.Configure<ClientChannelOptions>(builder.Configuration.GetSection(ClientChannelOptions.Section));

// Register services
builder.Services.AddSingleton<PromptBuilder>();
builder.Services.AddSingleton<ClaudeExecutor>();
builder.Services.AddSingleton<CodexExecutor>();
builder.Services.AddSingleton<GeminiExecutor>();

builder.Services.AddSingleton<IAgentExecutor>(sp =>
{
    var provider = sp.GetRequiredService<IOptions<AgentOptions>>().Value.Provider;
    return provider switch
    {
        "codex" => sp.GetRequiredService<CodexExecutor>(),
        "gemini" => sp.GetRequiredService<GeminiExecutor>(),
        _ => sp.GetRequiredService<ClaudeExecutor>(),
    };
});

builder.Services.AddSingleton<IFleetConnectionState, FleetConnectionState>();
builder.Services.AddSingleton<SessionManager>();
builder.Services.AddSingleton<GroupRelayService>();
builder.Services.AddSingleton<AllowlistHolder>();

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
    //
    // --- Outbound sink seam (#277 D-1) ---------------------------------------------------
    // The holder is dependency-free and IS the thing that breaks the circular DI
    // (AgentTransport -> TaskManager -> sink) that the transport's four self-assignments used
    // to work around. Consumers constructor-inject IMessageSink and get the holder; the
    // transport attaches itself to it during its own construction. Until then — and forever in
    // a host that never registers the transport (S4) — the holder serves NullMessageSink, so
    // omitting Telegram is a counted no-op instead of a NullReferenceException.
    builder.Services.AddSingleton<SinkSuppressionCounter>();
    builder.Services.AddSingleton<RelayCompletionCounter>();
    builder.Services.AddSingleton<MessageSinkHolder>();
    builder.Services.AddSingleton<IMessageSink>(sp => sp.GetRequiredService<MessageSinkHolder>());

    // --- Conversation event seam ---------------------------------------------------------
    // Registered before TaskManager so the publisher is available to it, and before
    // AgentTransport so RuntimeWiringService starts first (see below).
    builder.Services.AddSingleton<ConversationEventCounters>();
    builder.Services.AddSingleton<ConversationRegistry>();
    builder.Services.AddSingleton<IConversationRegistry>(sp => sp.GetRequiredService<ConversationRegistry>());
    builder.Services.AddSingleton<ConversationEventBus>();
    builder.Services.AddSingleton<IConversationEventPublisher>(sp => sp.GetRequiredService<ConversationEventBus>());
    builder.Services.AddSingleton<PrincipalBinder>();
    builder.Services.AddSingleton<ConversationIntake>();

    builder.Services.AddSingleton<TaskManager>();
    builder.Services.AddSingleton<CommandDispatcher>();
    builder.Services.AddSingleton<PromptAssembler>();
    builder.Services.AddSingleton<MessageRouter>();
    builder.Services.AddSingleton<GroupBehavior>();

    // --- Completion effects (#277 D-2) ----------------------------------------------------
    // Both subscribe to TaskManager in their own constructors and must outlive the Telegram
    // transport's absence: relay/bridge publication is what workflow delegations depend on, and
    // context buffering is what a cold executor needs, on every channel.
    //
    // The AddSingleton + factory-AddHostedService pair on the publisher is load-bearing, not
    // style. A bare AddHostedService<RelayCompletionPublisher>() registers the type ONLY as
    // IHostedService, so RuntimeWiringService's constructor injection of the concrete type
    // would fail to resolve — the guard meant to make a missing publisher loud would instead
    // make the process refuse to start for an unrelated-looking reason. Resolving that SAME
    // instance through the factory keeps one object: the one whose constructor subscribed and
    // whose IsAttached the guard reads. Registering both forms independently would yield two
    // instances, two subscriptions and two relay publications (#277 MUST NOT 16, MUST NOT 4).
    builder.Services.AddSingleton<RelayCompletionPublisher>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<RelayCompletionPublisher>());
    builder.Services.AddSingleton<CompletionContextBuffer>();

    // RuntimeWiringService is registered BEFORE AgentTransport on purpose. The host starts
    // hosted services in registration order, and this one asserts that both completion handlers
    // are already attached (they are — each subscribes in its constructor, and every constructor
    // runs before any StartAsync).
    builder.Services.AddHostedService<RuntimeWiringService>();
    builder.Services.AddHostedService<ConversationEventPump>();
    // Conditional (#277 S4). The transport is the Telegram adapter and nothing else depends on
    // it being constructed: the sink falls back to the counted no-op, relay publication and
    // context buffering have their own owners, and the startup guard checks those owners rather
    // than this class. Keying off token presence rather than an explicit setting is the open
    // question in #277 §11.1; either satisfies S4.
    //
    // A malformed token does NOT take this branch — the transport is registered and handles it
    // internally (MUST NOT 17), so startup_telegram_state can distinguish absent from malformed.
    if (!string.IsNullOrWhiteSpace(builder.Configuration[$"{TelegramOptions.Section}:{nameof(TelegramOptions.BotToken)}"]))
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
    builder.Services.AddSingleton<RichFallbackCounter>();
    builder.Services.AddSingleton<InjectionOutcomeCounter>();

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
