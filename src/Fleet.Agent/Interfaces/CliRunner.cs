using Fleet.Agent.Configuration;
using Fleet.Agent.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Interfaces;

/// <summary>
/// Handles --task CLI flag: runs a single task via IAgentExecutor and exits.
/// Only registered when --task is present on the command line.
/// </summary>
public sealed class CliRunner : BackgroundService
{
    private readonly string _task;
    private readonly IAgentExecutor _executor;
    private readonly AgentOptions _config;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<CliRunner> _logger;

    public CliRunner(
        IOptions<AgentOptions> config,
        IAgentExecutor executor,
        IHostApplicationLifetime lifetime,
        ILogger<CliRunner> logger)
    {
        _config = config.Value;
        _executor = executor;
        _lifetime = lifetime;
        _logger = logger;

        // Extract --task value from command line args
        var args = Environment.GetCommandLineArgs();
        string? task = null;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--task")
            {
                task = args[i + 1];
                break;
            }
        }

        _task = task ?? throw new InvalidOperationException("CliRunner requires --task argument");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CLI mode: executing task for agent {AgentName}", _config.Name);
        Console.WriteLine($"[{_config.Name}] Executing task: {_task}");
        Console.WriteLine();

        try
        {
            Models.ExecutionStats? stats = null;
            await foreach (var progress in _executor.ExecuteAsync(_task, ct: stoppingToken))
            {
                if (progress.Stats is not null)
                    stats = progress.Stats;

                if (progress.IsSignificant)
                {
                    Console.WriteLine($"  [{progress.EventType}] {progress.Summary}");
                }

                if (progress.FinalResult is not null)
                {
                    Console.WriteLine();
                    Console.WriteLine("=== Result ===");
                    Console.WriteLine(progress.FinalResult);
                }
            }

            if (stats is not null)
                Console.WriteLine($"\n{stats.Format()}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CLI task failed");
            Console.Error.WriteLine($"Error: {ex.Message}");
        }
        finally
        {
            _lifetime.StopApplication();
        }
    }
}
