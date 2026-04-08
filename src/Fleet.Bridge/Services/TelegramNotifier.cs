using Fleet.Bridge.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;

namespace Fleet.Bridge.Services;

/// <summary>
/// Lightweight Telegram client for posting bridge conversations to a dedicated group.
/// </summary>
public sealed class TelegramNotifier
{
    private readonly TelegramBotClient? _bot;
    private readonly long _chatId;
    private readonly ILogger<TelegramNotifier> _logger;

    public TelegramNotifier(
        IOptions<BridgeOptions> bridgeConfig,
        IOptions<TelegramOptions> telegramConfig,
        ILogger<TelegramNotifier> logger)
    {
        _chatId = bridgeConfig.Value.BridgeChatId;
        _logger = logger;

        var token = telegramConfig.Value.BotToken;
        if (!string.IsNullOrEmpty(token) && _chatId != 0)
            _bot = new TelegramBotClient(token);
    }

    public async Task NotifyQuestionAsync(string agentName, string? project, string question)
    {
        if (_bot is null) return;

        try
        {
            var header = string.IsNullOrEmpty(project)
                ? $"[bridge:{agentName}]"
                : $"[bridge:{agentName} | {project}]";

            var text = $"{header}\n{question}";
            if (text.Length > 4000)
                text = text[..4000] + "...";

            await _bot.SendMessage(_chatId, text);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send bridge question to Telegram");
        }
    }
}
