using Fleet.Shared;
using Fleet.Temporal.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fleet.Temporal.Services;

/// <summary>
/// Hosted service that bootstraps fleet-temporal-bridge config from the orchestrator on startup
/// and subscribes to <c>config.changed</c> events for live updates.
///
/// Literal keys: FLEET_CTO_AGENT, FLEET_GROUP_CHAT_ID, AUTHTOKENREFRESH__CLAUDECLIENTID,
/// AUTHTOKENREFRESH__CODEXCLIENTID (declared via PEER_CONFIG_KEYS env var).
/// </summary>
public sealed class PeerConfigHostedService : IHostedService, IAsyncDisposable
{
    private readonly PeerConfigClient _client;
    private readonly ILogger<PeerConfigHostedService> _logger;

    public PeerConfigHostedService(ILogger<PeerConfigHostedService> logger)
    {
        _logger = logger;
        _client = PeerConfigClient.FromEnvironment(logger);
        _client.OnChanged = OnConfigSnapshotReceived;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _client.BootstrapAsync(cancellationToken);
        await _client.SubscribeAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async ValueTask DisposeAsync() => await _client.DisposeAsync();

    private Task OnConfigSnapshotReceived(ConfigSnapshot snapshot)
    {
        if (snapshot.Literals.TryGetValue("FLEET_CTO_AGENT", out var ctoAgent) &&
            !string.IsNullOrWhiteSpace(ctoAgent))
        {
            FleetWorkflowConfig.UpdateCtoAgent(ctoAgent);
            _logger.LogInformation("FleetWorkflowConfig.CtoAgent updated to '{Agent}'", ctoAgent);
        }

        return Task.CompletedTask;
    }
}
