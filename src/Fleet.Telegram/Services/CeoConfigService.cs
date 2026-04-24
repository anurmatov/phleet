namespace Fleet.Telegram.Services;

/// <summary>
/// Mutable singleton that holds the CEO's Telegram chat ID, updated live from peer-config.
///
/// The value comes from the <c>TELEGRAM_USER_ID</c> literal key in the peer-config snapshot
/// (see <c>PeerConfigHostedService.OnConfigSnapshotReceived</c>), NOT from <c>Ceo:ChatId</c>
/// appsettings. The original spec described an <c>IOptions&lt;CeoOptions&gt;</c> approach
/// but that is startup-only; this service keeps the ID live-mutable so it propagates via
/// <c>config.changed</c> broadcasts without a container restart.
///
/// Deploy prerequisite: <c>TELEGRAM_USER_ID</c> must be listed in fleet-telegram's
/// <c>PEER_CONFIG_KEYS</c> env var in <c>~/fleet/docker-compose.yml</c> on macstudio
/// for peer-config to deliver the value to this service.
/// </summary>
public sealed class CeoConfigService
{
    private long _chatId;

    /// <summary>
    /// CEO's Telegram chat ID. 0 means not yet received from peer-config.
    /// </summary>
    public long ChatId => Interlocked.Read(ref _chatId);

    /// <summary>
    /// Called by PeerConfigHostedService on each config snapshot.
    /// Parses the raw string value and updates the chat ID atomically.
    /// </summary>
    public void Apply(string? rawValue)
    {
        var id = long.TryParse(rawValue, out var parsed) ? parsed : 0L;
        Interlocked.Exchange(ref _chatId, id);
    }
}
