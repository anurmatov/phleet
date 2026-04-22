using Fleet.Shared;
using Fleet.Temporal.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fleet.Temporal.Services;

/// <summary>
/// Hosted service that bootstraps fleet-temporal-bridge config from the orchestrator on startup
/// and subscribes to <c>config.changed</c> events for live updates.
///
/// Literal keys fetched (declared via PEER_CONFIG_KEYS env var):
///   FLEET_CTO_AGENT                   — updates <see cref="FleetWorkflowConfig.Instance"/>.CtoAgent
///   FLEET_GROUP_CHAT_ID               — updates <see cref="FleetWorkflowConfig.GroupChatId"/>
///   AUTHTOKENREFRESH__CLAUDECLIENTID  — logged; underlying provider reads from compose env at startup
///   AUTHTOKENREFRESH__CODEXCLIENTID   — logged; underlying provider reads from compose env at startup
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

        // Fail fast if FLEET_CTO_AGENT was not resolved — workflow escalations and UWE
        // template expressions that reference {{config.CtoAgent}} will silently route nowhere.
        if (string.IsNullOrWhiteSpace(FleetWorkflowConfig.Instance.CtoAgent))
            throw new InvalidOperationException(
                "FLEET_CTO_AGENT is not set in .env — fleet-temporal-bridge cannot route workflow " +
                "escalations or resolve {{config.CtoAgent}} template expressions. " +
                "Set FLEET_CTO_AGENT in .env and restart.");

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

        if (snapshot.Literals.TryGetValue("FLEET_GROUP_CHAT_ID", out var chatIdStr) &&
            long.TryParse(chatIdStr, out var chatId) && chatId != 0)
        {
            FleetWorkflowConfig.UpdateGroupChatId(chatId);
            _logger.LogInformation("FleetWorkflowConfig.GroupChatId updated to {ChatId}", chatId);
        }

        // AUTHTOKENREFRESH keys: log receipt for observability; underlying AuthTokenRefreshOptions
        // reads from compose env at startup (phleet uses compile-time constants). When phleet
        // adopts configurable client IDs these values will wire in here.
        if (snapshot.Literals.ContainsKey("AUTHTOKENREFRESH__CLAUDECLIENTID") ||
            snapshot.Literals.ContainsKey("AUTHTOKENREFRESH__CODEXCLIENTID"))
        {
            _logger.LogInformation("AUTHTOKENREFRESH client IDs received via peer-config (applied on next restart)");
        }

        return Task.CompletedTask;
    }
}
