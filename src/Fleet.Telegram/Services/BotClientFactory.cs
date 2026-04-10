using Fleet.Telegram.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;

namespace Fleet.Telegram.Services;

/// <summary>
/// Singleton factory that maps agent names to <see cref="ITelegramBotClient"/> instances.
/// One client per non-empty token is created at startup and cached for the process lifetime.
/// </summary>
public sealed class BotClientFactory
{
    private readonly IReadOnlyDictionary<string, ITelegramBotClient> _clients;
    private readonly ITelegramBotClient? _fallbackClient;
    private readonly ILogger<BotClientFactory> _logger;

    public BotClientFactory(IOptions<TelegramBotsOptions> options, ILogger<BotClientFactory> logger)
    {
        _logger = logger;
        var opts = options.Value;

        var clients = new Dictionary<string, ITelegramBotClient>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, token) in opts.Tokens)
        {
            if (string.IsNullOrWhiteSpace(token))
                continue;

            clients[name] = new TelegramBotClient(token);
            _logger.LogInformation("Registered Telegram bot for agent '{AgentName}'", name);
        }

        _clients = clients;

        if (!string.IsNullOrWhiteSpace(opts.FallbackBotName) &&
            _clients.TryGetValue(opts.FallbackBotName, out var fallback))
        {
            _fallbackClient = fallback;
        }
        else
        {
            _logger.LogWarning(
                "Fallback bot '{FallbackBotName}' not found in configured tokens",
                opts.FallbackBotName);
        }
    }

    /// <summary>
    /// Returns the bot client for the given agent name, or the fallback client if the
    /// agent is unknown or has no configured token.
    /// </summary>
    public ITelegramBotClient? GetClient(string? agentName)
    {
        if (!string.IsNullOrWhiteSpace(agentName) &&
            _clients.TryGetValue(agentName, out var client))
            return client;

        return _fallbackClient;
    }

    /// <summary>Returns the fallback bot client directly.</summary>
    public ITelegramBotClient? GetFallbackClient() => _fallbackClient;

    /// <summary>Returns true if a dedicated client exists for this agent name.</summary>
    public bool HasClient(string? agentName) =>
        !string.IsNullOrWhiteSpace(agentName) && _clients.ContainsKey(agentName);
}
