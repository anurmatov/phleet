namespace Fleet.Orchestrator.Services;

/// <summary>
/// Background service that periodically scans all registered agents and pushes
/// state-change WebSocket events when an agent transitions to Stale or Dead.
/// Also refreshes container start times from Docker every 60 seconds.
/// </summary>
public sealed class AgentStateMonitorService : BackgroundService
{
    private static readonly TimeSpan CheckInterval        = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ContainerRefreshInterval = TimeSpan.FromSeconds(60);

    private readonly AgentRegistry _registry;
    private readonly DockerService _docker;
    private readonly ILogger<AgentStateMonitorService> _logger;

    private DateTimeOffset _lastContainerRefresh = DateTimeOffset.MinValue;

    private readonly StockOpenShellRuntime? _managed;

    public AgentStateMonitorService(AgentRegistry registry, DockerService docker, ILogger<AgentStateMonitorService> logger,
        StockOpenShellRuntime? managed = null)
    {
        _managed = managed;
        _registry = registry;
        _docker   = docker;
        _logger   = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AgentStateMonitorService started (check interval: {Interval}s)", CheckInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(CheckInterval, stoppingToken);

            foreach (var agent in _registry.GetAll())
            {
                var effective = agent.EffectiveStatus;

                if (effective is "stale" or "dead")
                {
                    _logger.LogDebug("Agent {Agent} is {Status} (last seen {LastSeen:s}Z)", agent.AgentName, effective, agent.LastSeen);
                    _registry.BroadcastStateIfChanged(agent);
                }
            }

            // #492: the existing status refresh also watches applied managed bindings for an
            // unexpected gateway restart and runs the bounded revoke/verified-owned Stop handling.
            if (_managed is { Enabled: true })
                foreach (var agent in _registry.GetAll().Where(a => a.AppliedRuntimeKind == "openshell").ToList())
                {
                    try { await _managed.CheckGenerationAsync(agent.AgentName, stoppingToken); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    { _logger.LogWarning("Managed generation check for {Agent} failed ({Type})", agent.AgentName, ex.GetType().Name); }
                    // #492 review F11: and re-observes its containers (bounded, cached per agent).
                    try { await _managed.RefreshContainersAsync(agent.AgentName, _registry, stoppingToken); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    { _logger.LogWarning("Managed container refresh for {Agent} failed ({Type})", agent.AgentName, ex.GetType().Name); }
                }

            // #502: on this same 10-second pass, applied and pending subscription bindings past their outside expiry get
            // the bounded revoke-and-stop. Already held (needs_attention) or revoked records are skipped, never retried.
            if (_managed is { Enabled: true })
                foreach (var agent in _registry.GetAll().Where(a => a.RuntimeKind == "openshell" || a.AppliedRuntimeKind == "openshell").ToList())
                {
                    try { await _managed.CheckSubscriptionExpiryAsync(agent.AgentName, stoppingToken); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    { _logger.LogWarning("Managed subscription expiry check for {Agent} failed ({Type})", agent.AgentName, ex.GetType().Name); }
                }

            // Refresh container start times periodically
            if (DateTimeOffset.UtcNow - _lastContainerRefresh >= ContainerRefreshInterval)
            {
                _lastContainerRefresh = DateTimeOffset.UtcNow;
                await RefreshContainerStartTimesAsync(stoppingToken);
            }
        }
    }

    private async Task RefreshContainerStartTimesAsync(CancellationToken ct)
    {
        foreach (var agent in _registry.GetAll())
        {
            if (string.IsNullOrEmpty(agent.ContainerName)) continue;
            try
            {
                var startedAt = await _docker.GetContainerStartedAtAsync(agent.ContainerName);
                _registry.UpdateContainerStartedAt(agent.AgentName, startedAt);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to refresh container start time for {Agent}", agent.AgentName);
            }
            if (ct.IsCancellationRequested) break;
        }
    }
}
