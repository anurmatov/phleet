using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fleet.Conversations.Contracts;

namespace Fleet.Journal.Client;

/// <summary>What one ingest attempt produced.</summary>
/// <param name="Status">The HTTP status, or 0 when no response arrived (timeout, refused, reset).</param>
/// <param name="Error">The body's <c>error</c> code, when there is one.</param>
/// <param name="Reason">The body's <c>reason</c> or <c>field</c>, when there is one.</param>
/// <param name="RetryAfter">The <c>Retry-After</c> delay, when the server gave one.</param>
public sealed record JournalSendResult(int Status, string? Error, string? Reason, TimeSpan? RetryAfter)
{
    public bool IsTransportFailure => Status == 0;
}

/// <summary>
/// Posts one journal record to <c>POST /journal/v1/messages</c> with the agent's ingest token.
/// </summary>
/// <remarks>
/// ⚠️ The token travels only in the <c>Authorization</c> header. It is never logged, and it is
/// never part of an exception message this class produces.
/// </remarks>
public sealed class JournalHttpClient
{
    public const string MessagesPath = "/journal/v1/messages";
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan BindingTimeout = TimeSpan.FromSeconds(2);
    public const string TurnBindingPath = "/journal/v1/turn-binding";
    private static readonly JsonSerializerOptions BindingJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly string _token;
    private readonly string? _readToken;
    private readonly string? _crossChatToken;
    private readonly TimeSpan _timeout;

    /// <param name="http">A client whose <see cref="HttpClient.BaseAddress"/> is the journal listener.</param>
    /// <param name="token">The ingest token.</param>
    /// <param name="timeout">The per-request budget; <see cref="RequestTimeout"/> unless a test shortens it.</param>
    public JournalHttpClient(HttpClient http, string token, TimeSpan? timeout = null, string? readToken = null, string? crossChatToken = null)
    {
        _http = http;
        _token = token.Trim();
        _readToken = readToken?.Trim();
        _crossChatToken = crossChatToken?.Trim();
        _timeout = timeout ?? RequestTimeout;
    }

    public Task<JournalSendResult> PostAsync(ReadOnlyMemory<byte> body, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, MessagesPath, body, _timeout, ct);

    public Task<JournalSendResult> PutTurnBindingAsync(JournalTurnBinding binding, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, TurnBindingPath, JsonSerializer.SerializeToUtf8Bytes(binding, BindingJson), BindingTimeout, ct);

    /// <summary>Caller owns the response and its streamed body; the runtime's deadline bounds both.</summary>
    public async Task<HttpResponseMessage> OpenAttachmentContentAsync(JournalAttachmentRequest arguments, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_readToken)) throw new InvalidOperationException("journal_read_token_missing");
        using var request = new HttpRequestMessage(HttpMethod.Post, JournalAttachmentRequest.ContentPath)
        { Content = new ReadOnlyMemoryContent(JsonSerializer.SerializeToUtf8Bytes(arguments)) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _readToken);
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    public async Task<HttpResponseMessage> OpenSendAsync(JournalAttachmentRequest arguments, bool cross, bool content, CancellationToken ct)
    {
        var path = cross ? (content ? "/journal/v1/attachments/cross-chat/content" : "/journal/v1/attachments/cross-chat/send-handle")
            : content ? JournalAttachmentRequest.ContentPath : "/journal/v1/attachments/send-handle";
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        { Content = new ReadOnlyMemoryContent(JsonSerializer.SerializeToUtf8Bytes(arguments)) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cross ? _crossChatToken : _readToken);
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private async Task<JournalSendResult> SendAsync(
        HttpMethod method, string path, ReadOnlyMemory<byte> body, TimeSpan budget, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = new ReadOnlyMemoryContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(budget);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new JournalSendResult(0, "timeout", null, null);
        }
        catch (HttpRequestException)
        {
            return new JournalSendResult(0, "connection", null, null);
        }

        using (response)
        {
            string? error = null, reason = null;
            try
            {
                var text = await response.Content.ReadAsStringAsync(timeout.Token);
                if (text.Length > 0 && text.Length < 4096)
                {
                    using var document = JsonDocument.Parse(text);
                    if (document.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        error = Str(document.RootElement, "error");
                        reason = Str(document.RootElement, "reason") ?? Str(document.RootElement, "field");
                    }
                }
            }
            catch (Exception)
            {
                // A body that is not the listener's JSON: the status alone decides.
            }

            return new JournalSendResult((int)response.StatusCode, error, reason, RetryAfter(response));
        }
    }

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is not { } header) return null;
        if (header.Delta is { } delta) return delta;
        if (header.Date is { } date) return date - DateTimeOffset.UtcNow;
        return null;
    }
}
