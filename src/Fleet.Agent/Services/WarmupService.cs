using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
    private readonly IAgentExecutor _executor;
    private readonly ILogger<WarmupService> _logger;

    public WarmupService(IAgentExecutor executor, ILogger<WarmupService> logger)
    {
        _executor = executor;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give the host a moment to finish startup before we spawn the executor process
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);

        _logger.LogInformation("WarmupService: sending warmup ping to pre-spawn executor process");

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            cts.CancelAfter(TimeSpan.FromSeconds(60));

            await foreach (var progress in _executor.ExecuteAsync("ping", ct: cts.Token))
            {
                if (progress.EventType == "result")
                {
                    _logger.LogInformation("WarmupService: warmup complete — executor process is ready");
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("WarmupService: warmup timed out or was cancelled — executor will cold-start on first real task");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WarmupService: warmup failed — executor will cold-start on first real task");
        }
    }
}
