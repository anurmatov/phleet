namespace Fleet.Telegram.Services;

/// <summary>
/// Mutable singleton that holds the CEO's Telegram chat ID, updated live from peer-config
/// (TELEGRAM_USER_ID literal key). Using a dedicated service rather than IOptions keeps the
/// peer-config update path decoupled from the ASP.NET configuration root.
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
