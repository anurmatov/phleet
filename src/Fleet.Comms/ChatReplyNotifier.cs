using System.Net.Http.Headers;
using Fleet.Conversations.Journal;
using Fleet.Shared;

namespace Fleet.Comms;

/// <summary>
/// Tells the orchestrator that an agent replied in a conversation no client is attached to, so the
/// phone gets a notification (#530 step 9).
/// </summary>
public interface IChatReplyNotifier
{
    /// <summary>Fire and forget: never throws, never delays the agent's commit.</summary>
    void Replied(string agent, string conversationId, ulong seq);
}

/// <summary>
/// <see cref="IChatReplyNotifier"/> over the orchestrator's <c>POST /internal/push/chat-reply</c>.
/// </summary>
/// <remarks>
/// Identifiers only — which agent, which conversation, which position — under a
/// <c>cj1.chat-push.fleet-comms</c> token. A notification that cannot be delivered is lost, which
/// is the right failure: the reply itself is already durable and the app shows it when opened.
/// </remarks>
public sealed class OrchestratorChatReplyNotifier : IChatReplyNotifier, IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http;
    private readonly Uri? _uri;
    private readonly string _token;
    private readonly ILogger _logger;

    public OrchestratorChatReplyNotifier(HttpClient http, string orchestratorUrl, byte[] key, ILogger<OrchestratorChatReplyNotifier> logger)
    {
        _http = http;
        _logger = logger;
        _token = JournalTokens.Mint(key, SignIn.ChatReplyPushTokenPurpose, SignIn.CheckTokenSubject);
        if (Uri.TryCreate(orchestratorUrl, UriKind.Absolute, out var origin) && origin.Scheme is "http" or "https")
            _uri = new Uri(origin, SignIn.ChatReplyPushPath);
    }

    public void Dispose() => _http.Dispose();

    public void Replied(string agent, string conversationId, ulong seq)
    {
        if (_uri is null) return;
        _ = SendAsync(agent, conversationId, seq);
    }

    /// <summary>Awaitable for tests; production calls <see cref="Replied"/>.</summary>
    internal async Task SendAsync(string agent, string conversationId, ulong seq)
    {
        try
        {
            using var deadline = new CancellationTokenSource(Timeout);
            using var request = new HttpRequestMessage(HttpMethod.Post, _uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            request.Content = JsonContent.Create(new { agent, conversationId, seq });

            using var response = await _http.SendAsync(request, deadline.Token);
            if (!response.IsSuccessStatusCode)
                _logger.LogWarning("chat reply push was refused: {Status}", (int)response.StatusCode);
        }
        catch (Exception e)
        {
            // Type only: a transport message can carry a host name.
            _logger.LogWarning("chat reply push failed: {Error}", e.GetType().Name);
        }
    }
}
