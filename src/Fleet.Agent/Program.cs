using Fleet.Agent;
using Fleet.Agent.Configuration;
using Fleet.Agent.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAgentCoreServices(builder.Configuration);

// Determine mode from command-line args
var isCliMode = args.Any(a => a == "--task");

if (isCliMode)
    builder.Services.AddAgentCliServices();
else
    builder.Services.AddAgentDaemonServices(builder.Configuration);

var app = builder.Build();

// Refuses to start a misconfigured agent, before /health can answer ok for it.
AgentHostRegistration.ValidateStartupConfiguration(app.Services);

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
