using System.Net.Http.Json;
using Fleet.Agent.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Services;

/// <summary>
/// Fire-and-forget forwarder that records incoming messages and bot responses to the
/// fleet-telegram per-chat ring buffer via its /internal/messages/record endpoint.
/// Failures are logged at Debug level and never propagate to the caller.
/// </summary>
public sealed class TelegramMessageForwarder
{
    private readonly string _baseUrl;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TelegramMessageForwarder> _logger;

    public TelegramMessageForwarder(
        IOptions<TelegramOptions> opts,
        IHttpClientFactory httpClientFactory,
        ILogger<TelegramMessageForwarder> logger)
    {
        _baseUrl = opts.Value.FleetTelegramBaseUrl.TrimEnd('/');
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Records a message to the fleet-telegram ring buffer. Fire-and-forget: never blocks the caller.
    /// Only records when <see cref="TelegramOptions.FleetTelegramBaseUrl"/> is configured and
    /// <paramref name="messageId"/> is non-zero.
    /// </summary>
    public void Record(long chatId, long messageId, string text, long senderUserId, string senderUsername)
    {
        if (string.IsNullOrEmpty(_baseUrl) || messageId == 0) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var client = _httpClientFactory.CreateClient();
                await client.PostAsJsonAsync($"{_baseUrl}/internal/messages/record", new
                {
                    chat_id = chatId,
                    message_id = messageId,
                    text,
                    sender_user_id = senderUserId,
                    sender_username = senderUsername,
                    timestamp = DateTimeOffset.UtcNow,
                });
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to record message {MessageId} to fleet-telegram ring buffer", messageId);
            }
        });
    }
}
