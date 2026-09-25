using System.Runtime.CompilerServices;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Fleet.Shared;
using Microsoft.Extensions.Logging;

namespace Fleet.Agent.Tests;

/// <summary>
/// #357: <see cref="WarmupService"/> must run its startup ping under the per-agent
/// <c>WarmupTimeoutSeconds</c> instead of the old hard-coded 60 s, and a timeout must stay
/// non-fatal. The configured seconds appear in the start and timeout log lines so operators can
/// tell configuration apart from a slow or wedged provider.
/// </summary>
/// <remarks>
/// The startup delay is injected as zero so no test waits the fixed 3 s host grace period. The
/// timeout itself is the production <c>CancellationTokenSource.CancelAfter</c> path exercised with
/// a 1 s configured value — the service trusts provisioned config, so range enforcement belongs
/// to the orchestrator write paths, not here.
/// </remarks>
public class WarmupServiceTests
{
    [Fact]
    public void AgentOptions_DefaultsToTheSharedDefault()
    {
        var defaults = new AgentOptions { Name = "agent1", Role = "r", WorkDir = "/tmp" };
        Assert.Equal(WarmupTimeout.DefaultSeconds, defaults.WarmupTimeoutSeconds);
    }

    [Fact]
    public async Task ConfiguredTimeout_CancelsThePing_AndStaysNonFatal()
    {
        var logger = new CapturingLogger<WarmupService>();
        // Never yields a result: the only way the ping finishes is CancelAfter firing.
        var executor = new HangingExecutor();
        using var service = new WarmupService(executor, warmupTimeoutSeconds: 1, logger, startupDelay: TimeSpan.Zero);

        await service.StartAsync(CancellationToken.None);
        await RunTask(service.ExecuteTask!);

        var timeout = logger.Entries.Single(e => e.Level == LogLevel.Warning);
        Assert.Contains("cold-start", timeout.Message);
        Assert.Contains("after 1s", timeout.Message);
    }

    [Fact]
    public async Task StartLine_ContainsTheConfiguredSeconds()
    {
        var logger = new CapturingLogger<WarmupService>();
        var executor = new ResultExecutor("pong");
        using var service = new WarmupService(executor, warmupTimeoutSeconds: 180, logger, startupDelay: TimeSpan.Zero);

        await service.StartAsync(CancellationToken.None);
        await RunTask(service.ExecuteTask!);

        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Information && e.Message.Contains("timeout 180s"));
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Information && e.Message.Contains("warmup complete"));
    }

    [Fact]
    public async Task ErrorResult_IsLoggedNotThrown()
    {
        var logger = new CapturingLogger<WarmupService>();
        var executor = new ResultExecutor("boom", isError: true);
        using var service = new WarmupService(executor, warmupTimeoutSeconds: 60, logger, startupDelay: TimeSpan.Zero);

        await service.StartAsync(CancellationToken.None);
        await RunTask(service.ExecuteTask!);

        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Error && e.Message.Contains("boom"));
    }

    private static Task RunTask(Task task) =>
        task.WaitAsync(TimeSpan.FromSeconds(10));

    private sealed class ResultExecutor(string result, bool isError = false) : IAgentExecutor
    {
        public string? LastSessionId => null;
        public DateTimeOffset LastActivity => DateTimeOffset.UtcNow;
        public bool IsProcessWarm => true;

#pragma warning disable CS1998
        public async IAsyncEnumerable<AgentProgress> ExecuteAsync(
            string task,
            IReadOnlyList<MessageImage>? images = null,
            IReadOnlyList<MessageDocument>? documents = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new AgentProgress
            {
                EventType = "result",
                Summary = result,
                FinalResult = result,
                IsErrorResult = isError,
            };
        }
#pragma warning restore CS1998

        public Task<MidTurnInjectionResult> TryInjectMessageAsync(string task, IReadOnlyList<MessageImage>? images = null, IReadOnlyList<MessageDocument>? documents = null, CancellationToken ct = default)
            => Task.FromResult(MidTurnInjectionResult.NoActiveTurn());
        public Task StopProcessAsync() => Task.CompletedTask;
        public Task<bool> TryStopProcessAsync() => Task.FromResult(false);
        public void RequestRestart() { }
        public IAsyncEnumerable<AgentProgress> SendCommandAsync(string command, CancellationToken ct = default) => ExecuteAsync(command, ct: ct);
        public IReadOnlyCollection<BackgroundTaskInfo> GetActiveBackgroundTasks() => [];
        public Task<bool> CancelBackgroundTaskAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class HangingExecutor : IAgentExecutor
    {
        public string? LastSessionId => null;
        public DateTimeOffset LastActivity => DateTimeOffset.UtcNow;
        public bool IsProcessWarm => false;

        public async IAsyncEnumerable<AgentProgress> ExecuteAsync(
            string task,
            IReadOnlyList<MessageImage>? images = null,
            IReadOnlyList<MessageDocument>? documents = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            // The ping is cancelled by the service's CancelAfter; surface nothing before that.
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { throw; }
            yield break;
        }

        public Task<MidTurnInjectionResult> TryInjectMessageAsync(string task, IReadOnlyList<MessageImage>? images = null, IReadOnlyList<MessageDocument>? documents = null, CancellationToken ct = default)
            => Task.FromResult(MidTurnInjectionResult.NoActiveTurn());
        public Task StopProcessAsync() => Task.CompletedTask;
        public Task<bool> TryStopProcessAsync() => Task.FromResult(false);
        public void RequestRestart() { }
        public IAsyncEnumerable<AgentProgress> SendCommandAsync(string command, CancellationToken ct = default) => ExecuteAsync(command, ct: ct);
        public IReadOnlyCollection<BackgroundTaskInfo> GetActiveBackgroundTasks() => [];
        public Task<bool> CancelBackgroundTaskAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
