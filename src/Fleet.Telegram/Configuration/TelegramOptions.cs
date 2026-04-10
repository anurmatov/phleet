namespace Fleet.Telegram.Configuration;

public sealed class TelegramBotsOptions
{
    public const string Section = "TelegramBots";

    /// <summary>
    /// Map of agent-name → bot token. Keys are configurable per deployment.
    /// Empty strings are treated as absent — the fallback bot is used instead.
    /// </summary>
    public Dictionary<string, string> Tokens { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Key in <see cref="Tokens"/> to use when the requested agent is unknown
    /// or its token is empty. Defaults to "notifier".
    /// </summary>
    public string FallbackBotName { get; set; } = "notifier";
}
