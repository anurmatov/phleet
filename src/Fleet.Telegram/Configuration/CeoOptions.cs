namespace Fleet.Telegram.Configuration;

public sealed class CeoOptions
{
    public const string Section = "Ceo";

    /// <summary>
    /// CEO's Telegram chat ID. 0 means not configured — send_to_ceo will return an error.
    /// Populated from env var TELEGRAM_USER_ID (mapped to Ceo__ChatId in docker-compose).
    /// The chat ID is never logged or returned to callers.
    /// </summary>
    public long ChatId { get; set; }
}
