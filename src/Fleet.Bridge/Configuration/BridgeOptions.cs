namespace Fleet.Bridge.Configuration;

public sealed class BridgeOptions
{
    public const string Section = "Bridge";

    /// <summary>Telegram group chat ID for bridge conversation visibility.</summary>
    public long BridgeChatId { get; set; }

    /// <summary>Default timeout in seconds for waiting on the target agent's response.</summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>Target agent routing key. Must be set in configuration (e.g. Bridge:TargetAgent).</summary>
    public string TargetAgent { get; set; } = "";
}

public sealed class RabbitMqOptions
{
    public const string Section = "RabbitMq";

    public string Host { get; set; } = "";
    public string Exchange { get; set; } = "fleet.group";
}

public sealed class TelegramOptions
{
    public const string Section = "Telegram";

    public string BotToken { get; set; } = "";
}
