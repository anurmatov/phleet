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
//
// The throw alone does NOT end the process, and reasoning that it does is how this shipped wrong
// once already: builder.Build() has started a foreground thread by now, and .NET only tears a
// process down on an unhandled main-thread exception once no foreground threads remain. Booting
// the real image measured the result — Kestrel never listened, and the host sat at ~101% CPU
// while `docker ps` reported "Up", which `restart: unless-stopped` never acts on. A wedged
// container that still reports healthy is worse than the lazy check this replaced — nothing
// upstream reclaims it. Exiting is therefore the call site's job, and it must stay here.
//
// Exit(1) over FailFast: same non-zero status, without the crash dump FailFast writes.
try
{
    AgentHostRegistration.ValidateStartupConfiguration(app.Services);
}
catch (Exception ex)
{
    // The validator already logged the cause at Critical. Console.Error, not the logger, because
    // this path must not depend on DI resolving anything — a throw in here would hang the process
    // in exactly the way the exit exists to prevent. Catching Exception rather than the validator's
    // InvalidOperationException for the same reason: no exception type may reach the run loop.
    Console.Error.WriteLine($"Fleet.Agent startup aborted, exiting 1: {ex.Message}");
    Environment.Exit(1);
}

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
