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
// A throw alone does NOT reliably end the process, and reasoning about why is how this shipped
// wrong once already. What was actually measured: on the arm64 image the throw-only version never
// served /health (Kestrel never listened) but stayed resident at ~101% CPU with `docker ps`
// reporting "Up", which `restart: unless-stopped` never acts on; on x86-64 the same build aborted
// with SIGABRT (134) and wrote a core instead. The cause of the arm64 spin was never established —
// the obvious "a foreground thread from builder.Build() blocks teardown" explanation was tested
// directly and falsified, so it is not recorded here as fact.
//
// Exit(1) is therefore the point, not a detail: it terminates the process outright and does not
// depend on unhandled-exception propagation, so it is not subject to whatever differed between
// those platforms. Exiting is the call site's job and must stay here. Exit over FailFast: same
// non-zero status, without the crash dump — and that dump was the expensive part, a core per
// restart under a restart policy.
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

// Same reason as the startup gate above: a hosted service that throws from StartAsync (the
// hosted-provider loopback adapter failing to bind, #335) must end the process with a non-zero
// status, not rely on unhandled-exception propagation. RunAsync has disposed the host by the time
// it throws, so Exit here does not race the host's own shutdown.
try
{
    await app.RunAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Fleet.Agent host failed, exiting 1: {ex.Message}");
    Environment.Exit(1);
}
