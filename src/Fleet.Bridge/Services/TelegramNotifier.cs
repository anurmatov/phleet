using Fleet.Bridge.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;

namespace Fleet.Bridge.Services;

/// <summary>
/// Lightweight Telegram client for posting bridge conversations to a dedicated group.
/// Token and chat ID are mutable — updated at runtime via <see cref="UpdateBotToken"/>
/// and <see cref="UpdateChatId"/>.
/// </summary>
public sealed class TelegramNotifier
{
    private TelegramBotClient? _bot;
    private long _chatId;
    private string? _token;
    private readonly ILogger<TelegramNotifier> _logger;
    private readonly object _lock = new();

    public TelegramNotifier(
        IOptions<BridgeOptions> bridgeConfig,
        IOptions<TelegramOptions> telegramConfig,
        ILogger<TelegramNotifier> logger)
    {
        _chatId = bridgeConfig.Value.BridgeChatId;
        _logger = logger;

        var token = telegramConfig.Value.BotToken;
        _token = string.IsNullOrEmpty(token) ? null : token;
        if (!string.IsNullOrEmpty(_token) && _chatId != 0)
            _bot = new TelegramBotClient(_token);
    }

    /// <summary>Updates the bot token and rebuilds the client.</summary>
    public void UpdateBotToken(string? token)
    {
        lock (_lock)
        {
            _token = string.IsNullOrWhiteSpace(token) ? null : token;
            RebuildBot();
        }
    }

    /// <summary>Updates the chat ID and rebuilds the client if a token is set.</summary>
    public void UpdateChatId(long chatId)
    {
        lock (_lock)
        {
            _chatId = chatId;
            RebuildBot();
        }
    }

    private void RebuildBot()
    {
        if (!string.IsNullOrEmpty(_token) && _chatId != 0)
        {
            _bot = new TelegramBotClient(_token);
            _logger.LogInformation("TelegramNotifier rebuilt with new config");
        }
        else
        {
            _bot = null;
        }
    }

    public async Task NotifyQuestionAsync(string agentName, string? project, string question)
    {
        TelegramBotClient? bot;
        long chatId;
        lock (_lock)
        {
            bot = _bot;
            chatId = _chatId;
        }

        if (bot is null) return;

        try
        {
            var header = string.IsNullOrEmpty(project)
                ? $"[bridge:{agentName}]"
                : $"[bridge:{agentName} | {project}]";

            var text = $"{header}\n{question}";
            if (text.Length > 4000)
                text = text[..4000] + "...";

            await bot.SendMessage(chatId, text);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send bridge question to Telegram");
        }
    }
}
