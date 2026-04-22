using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Telegram.Bot;

namespace Fleet.Telegram.Services;

/// <summary>
/// Singleton factory that maps agent names to <see cref="ITelegramBotClient"/> instances.
/// Backed by a <see cref="ConcurrentDictionary"/>; updated at runtime via
/// <see cref="ApplyAgentDerived"/> and <see cref="ApplyNotifierToken"/>.
/// </summary>
public sealed class BotClientFactory
{
    private readonly ConcurrentDictionary<string, ITelegramBotClient> _clients =
        new(StringComparer.OrdinalIgnoreCase);

    private volatile ITelegramBotClient? _notifierClient;
    private readonly ILogger<BotClientFactory> _logger;

    public BotClientFactory(ILogger<BotClientFactory> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Diff-based reconcile: removes bots not in the map, adds/updates changed ones.
    /// On invalid token: logs error and skips that entry.
    /// </summary>
    public void ApplyAgentDerived(Dictionary<string, string> agentTokenMap)
    {
        // Remove agents no longer in the map
        foreach (var existingKey in _clients.Keys.ToList())
        {
            if (!agentTokenMap.ContainsKey(existingKey))
            {
                _clients.TryRemove(existingKey, out _);
                _logger.LogInformation("Removed Telegram bot for agent '{AgentName}'", existingKey);
            }
        }

        // Add or update
        foreach (var (agentName, token) in agentTokenMap)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                _logger.LogError("Skipping agent '{AgentName}' — token is empty", agentName);
                continue;
            }

            try
            {
                var client = new TelegramBotClient(token);
                _clients[agentName] = client;
                _logger.LogInformation("Registered Telegram bot for agent '{AgentName}'", agentName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Invalid token for agent '{AgentName}' — skipping", agentName);
            }
        }
    }

    /// <summary>Updates the fallback notifier client.</summary>
    public void ApplyNotifierToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            _notifierClient = null;
            _logger.LogInformation("Notifier bot token cleared");
            return;
        }

        try
        {
            _notifierClient = new TelegramBotClient(token);
            _logger.LogInformation("Notifier bot client updated");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Invalid notifier bot token — keeping previous client");
        }
    }

    /// <summary>
    /// Returns the bot client for the given agent name, or the fallback notifier client
    /// if the agent is unknown or has no configured token.
    /// </summary>
    public ITelegramBotClient? GetClient(string? agentName)
    {
        if (!string.IsNullOrWhiteSpace(agentName) &&
            _clients.TryGetValue(agentName, out var client))
            return client;

        return _notifierClient;
    }

    /// <summary>Returns the fallback notifier bot client directly.</summary>
    public ITelegramBotClient? GetFallbackClient() => _notifierClient;

    /// <summary>Returns true if a dedicated client exists for this agent name.</summary>
    public bool HasClient(string? agentName) =>
        !string.IsNullOrWhiteSpace(agentName) && _clients.ContainsKey(agentName);
}
