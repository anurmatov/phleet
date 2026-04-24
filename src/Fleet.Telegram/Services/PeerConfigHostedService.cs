using Fleet.Shared;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fleet.Telegram.Services;

/// <summary>
/// Hosted service that bootstraps fleet-telegram config from the orchestrator on startup
/// and subscribes to <c>config.changed</c> events for live updates.
///
/// Template key: <c>TELEGRAM_{SHORTNAME}_BOT_TOKEN</c> (declared via PEER_AGENT_DERIVED_KEYS).
/// Literal keys: <c>FLEET_CTO_AGENT</c>, <c>TELEGRAM_CTO_BOT_TOKEN</c>,
/// <c>TELEGRAM_NOTIFIER_BOT_TOKEN</c>, <c>TELEGRAM_USER_ID</c> (declared via PEER_CONFIG_KEYS).
/// </summary>
public sealed class PeerConfigHostedService : IHostedService, IAsyncDisposable
{
    private readonly PeerConfigClient _client;
    private readonly BotClientFactory _factory;
    private readonly CeoConfigService _ceoConfig;
    private readonly ILogger<PeerConfigHostedService> _logger;

    public PeerConfigHostedService(
        BotClientFactory factory,
        CeoConfigService ceoConfig,
        ILogger<PeerConfigHostedService> logger)
    {
        _factory = factory;
        _ceoConfig = ceoConfig;
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
        // Update fallback notifier client
        snapshot.Literals.TryGetValue("TELEGRAM_NOTIFIER_BOT_TOKEN", out var notifierToken);
        _factory.ApplyNotifierToken(notifierToken);

        // Build the merged agent token map: start with derived tokens, then overlay the CTO literal.
        const string template = "TELEGRAM_{SHORTNAME}_BOT_TOKEN";
        var agentTokenMap = snapshot.AgentDerived.TryGetValue(template, out var derived)
            ? new Dictionary<string, string>(derived, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // CTO bot token is a well-known literal — register it under the CTO agent name.
        if (snapshot.Literals.TryGetValue("FLEET_CTO_AGENT", out var ctoAgent) &&
            !string.IsNullOrWhiteSpace(ctoAgent) &&
            snapshot.Literals.TryGetValue("TELEGRAM_CTO_BOT_TOKEN", out var ctoToken) &&
            !string.IsNullOrWhiteSpace(ctoToken))
        {
            agentTokenMap[ctoAgent] = ctoToken;
        }

        _factory.ApplyAgentDerived(agentTokenMap);

        // CEO chat ID — live-updated from TELEGRAM_USER_ID literal key.
        snapshot.Literals.TryGetValue("TELEGRAM_USER_ID", out var ceoUserIdRaw);
        _ceoConfig.Apply(ceoUserIdRaw);

        _logger.LogInformation(
            "PeerConfig applied: notifier={HasNotifier}, agents={AgentCount}, ceoChatIdConfigured={CeoConfigured}",
            !string.IsNullOrWhiteSpace(notifierToken),
            agentTokenMap.Count,
            _ceoConfig.ChatId != 0);

        return Task.CompletedTask;
    }
}
