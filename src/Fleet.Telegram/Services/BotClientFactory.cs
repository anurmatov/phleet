using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Telegram.Bot;

namespace Fleet.Telegram.Services;

/// <summary>
/// Singleton factory that maps agent names to <see cref="ITelegramBotClient"/> instances.
///
/// Two-dict scheme: <c>_tokensByAgent</c> maps agent name → bot token (replaced wholesale
/// on every <see cref="ApplyAgentTokens"/> call); <c>_clientsByToken</c> caches one
/// <see cref="ITelegramBotClient"/> per distinct token so agents sharing a token share one
/// connection pool and token rotation evicts only the stale client.
/// </summary>
public sealed class BotClientFactory
{
    private readonly object _tokensByAgentLock = new();

    // agent name → bot token; replaced wholesale on every ApplyAgentTokens call
    private Dictionary<string, string> _tokensByAgent = new(StringComparer.OrdinalIgnoreCase);

    // bot token → ITelegramBotClient; GetOrAdd on lookup, eviction on rotation
    private readonly ConcurrentDictionary<string, ITelegramBotClient> _clientsByToken =
        new(StringComparer.Ordinal);

    private volatile ITelegramBotClient? _notifierClient;
    private readonly ILogger<BotClientFactory> _logger;
    private readonly Func<string, ITelegramBotClient> _createClient;

    public BotClientFactory(ILogger<BotClientFactory> logger,
        Func<string, ITelegramBotClient>? createClient = null)
    {
        _logger = logger;
        _createClient = createClient ?? (t => new TelegramBotClient(t));
    }

    /// <summary>
    /// Replaces the agent→token map wholesale and evicts <see cref="_clientsByToken"/>
    /// entries whose tokens are no longer in use.
    ///
    /// Empty/whitespace tokens are logged as errors and skipped.
    /// Race window: a concurrent <see cref="GetClient"/> between eviction and swap may
    /// briefly recreate an orphaned client — self-heals on the next apply.
    /// </summary>
    public void ApplyAgentTokens(Dictionary<string, string> agentTokenMap)
    {
        // Filter out empty tokens before building the incoming set
        var filtered = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (agentName, token) in agentTokenMap)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                _logger.LogError(
                    "Skipping agent '{AgentName}' — token is empty (reason={Reason})",
                    agentName, "empty_token");
                continue;
            }
            filtered[agentName] = token;
        }

        // Evict stale clients whose tokens are no longer referenced
        var incoming = new HashSet<string>(filtered.Values, StringComparer.Ordinal);
        foreach (var token in _clientsByToken.Keys.ToList())
            if (!incoming.Contains(token))
                _clientsByToken.TryRemove(token, out _);

        // Swap agent→token dict wholesale
        lock (_tokensByAgentLock)
            _tokensByAgent = new Dictionary<string, string>(filtered, StringComparer.OrdinalIgnoreCase);

        _logger.LogInformation("ApplyAgentTokens: {Count} agents configured", filtered.Count);
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
            _notifierClient = _createClient(token);
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
    ///
    /// Lookup path:
    /// 0. null/whitespace agentName → return notifier (no log)
    /// 1. hit in <c>_tokensByAgent</c> → <c>_clientsByToken.GetOrAdd(token, …)</c> → return
    ///    miss → LogWarning(reason=no_bot_token_configured) → return notifier
    /// </summary>
    public ITelegramBotClient? GetClient(string? agentName)
    {
        if (string.IsNullOrWhiteSpace(agentName))
            return _notifierClient;

        Dictionary<string, string> snapshot;
        lock (_tokensByAgentLock) snapshot = _tokensByAgent;

        if (!snapshot.TryGetValue(agentName, out var token))
        {
            _logger.LogWarning(
                "No dedicated Telegram bot configured for agent '{Agent}' — " +
                "using notifier bot fallback (reason={Reason})",
                agentName, "no_bot_token_configured");
            return _notifierClient;
        }

        return _clientsByToken.GetOrAdd(token, _createClient);
    }

    /// <summary>Returns the fallback notifier bot client directly.</summary>
    public ITelegramBotClient? GetFallbackClient() => _notifierClient;

    /// <summary>Returns true if a dedicated client exists for this agent name.</summary>
    public bool HasClient(string? agentName)
    {
        if (string.IsNullOrWhiteSpace(agentName)) return false;
        Dictionary<string, string> snapshot;
        lock (_tokensByAgentLock) snapshot = _tokensByAgent;
        return snapshot.ContainsKey(agentName);
    }
}
