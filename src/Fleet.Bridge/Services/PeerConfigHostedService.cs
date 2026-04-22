using Fleet.Shared;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fleet.Bridge.Services;

/// <summary>
/// Hosted service that bootstraps fleet-bridge config from the orchestrator on startup
/// and subscribes to <c>config.changed</c> events for live updates.
///
/// Literal keys: FLEET_CTO_AGENT, FLEET_GROUP_CHAT_ID, TELEGRAM_NOTIFIER_BOT_TOKEN
/// (declared via PEER_CONFIG_KEYS env var).
/// </summary>
public sealed class PeerConfigHostedService : IHostedService, IAsyncDisposable
{
    private readonly PeerConfigClient _client;
    private readonly BridgeRelayService _relay;
    private readonly TelegramNotifier _notifier;
    private readonly ILogger<PeerConfigHostedService> _logger;

    public PeerConfigHostedService(
        BridgeRelayService relay,
        TelegramNotifier notifier,
        ILogger<PeerConfigHostedService> logger)
    {
        _relay = relay;
        _notifier = notifier;
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
            _relay.TargetAgent = ctoAgent;
            _logger.LogInformation("BridgeRelayService.TargetAgent updated to '{Agent}'", ctoAgent);
        }

        if (snapshot.Literals.TryGetValue("FLEET_GROUP_CHAT_ID", out var chatIdStr) &&
            long.TryParse(chatIdStr, out var chatId))
        {
            _relay.BridgeChatId = chatId;
            _notifier.UpdateChatId(chatId);
            _logger.LogInformation("BridgeChatId updated to {ChatId}", chatId);
        }

        if (snapshot.Literals.TryGetValue("TELEGRAM_NOTIFIER_BOT_TOKEN", out var notifierToken))
        {
            _notifier.UpdateBotToken(notifierToken);
            _logger.LogInformation("TelegramNotifier bot token updated");
        }

        return Task.CompletedTask;
    }
}
