using Fleet.Agent.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Services;

/// <summary>
/// Fires a lightweight ping to the agent executor on startup to pre-spawn it,
/// so it's ready when real tasks arrive via Temporal/RabbitMQ.
/// Without this, the first Temporal activity delegates a task before the executor
/// process exists, causing a multi-second cold-start delay that can look like a hang.
/// Works for all providers (claude, codex).
/// </summary>
public sealed class WarmupService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(3);

    private readonly IAgentExecutor _executor;
    private readonly ILogger<WarmupService> _logger;
    private readonly TimeSpan _startupDelay;
    private readonly int _warmupTimeoutSeconds;
    private readonly TaskManager? _taskManager;

    public WarmupService(IAgentExecutor executor, IOptions<AgentOptions> options, ILogger<WarmupService> logger,
        TaskManager? taskManager = null)
        : this(executor, options.Value.WarmupTimeoutSeconds, logger, taskManager: taskManager)
    {
    }

    internal WarmupService(IAgentExecutor executor, int warmupTimeoutSeconds, ILogger<WarmupService> logger,
        TimeSpan? startupDelay = null, TaskManager? taskManager = null)
    {
        _executor = executor;
        _warmupTimeoutSeconds = warmupTimeoutSeconds;
        _logger = logger;
        _startupDelay = startupDelay ?? StartupDelay;
        _taskManager = taskManager;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give the host a moment to finish startup before we spawn the executor process
        await Task.Delay(_startupDelay, stoppingToken);

        _logger.LogInformation(
            "WarmupService: sending warmup ping to pre-spawn executor process (timeout {TimeoutSeconds}s)",
            _warmupTimeoutSeconds);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            cts.CancelAfter(TimeSpan.FromSeconds(_warmupTimeoutSeconds));

            // The ping is a turn the task queue knows nothing about. A real message can arrive while
            // it runs; tell the queue, so it does not inject a second one into the ping.
            using var sideUse = _taskManager?.BeginExecutorSideUse();

            await foreach (var progress in _executor.ExecuteAsync("ping", ct: cts.Token))
            {
                if (progress.EventType == "result")
                {
                    if (progress.IsErrorResult)
                        _logger.LogError("WarmupService: warmup returned error — executor may be misconfigured: {Result}", progress.FinalResult ?? "(no message)");
                    else
                        _logger.LogInformation("WarmupService: warmup complete — executor process is ready");
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "WarmupService: warmup timed out or was cancelled after {TimeoutSeconds}s — executor will cold-start on first real task",
                _warmupTimeoutSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WarmupService: warmup failed — executor will cold-start on first real task");
        }
    }
}
