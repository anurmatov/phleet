using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Fleet.Conversations.Journal;
namespace Fleet.Comms.Routes;
public interface IJournalCrossChatAuthorization
{
    Task<(bool Available, bool Effective, string? Member)> CheckAsync(string subject, long botId,
        long? chatId, long? userId, CancellationToken ct);
}
/// <summary>Comms holds only its derived service credential, never Telegram secrets.</summary>
public sealed class JournalCrossChatAuthorizationClient(HttpClient http, string url, byte[] key) : IJournalCrossChatAuthorization, IDisposable
{
    public void Dispose() => http.Dispose();
    private readonly string _token = JournalTokens.Mint(key, JournalTokens.PurposeCrossChatAuthz, "fleet-comms");
    public async Task<(bool Available, bool Effective, string? Member)> CheckAsync(string subject, long botId, long? chatId, long? userId, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var origin) || origin.Scheme is not ("http" or "https")) return (false, false, null);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(origin, "/internal/journal/cross-chat-authorization"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            request.Content = chatId is not null
                ? JsonContent.Create(new { subject, botId, chatId, userId })
                : JsonContent.Create(new { subject, botId });
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode != System.Net.HttpStatusCode.OK) return (false, false, null);
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            var buffer = new byte[1025]; var count = 0;
            while (count < buffer.Length) { var n = await stream.ReadAsync(buffer.AsMemory(count), deadline.Token); if (n == 0) break; count += n; }
            if (count > 1024) return (false, false, null);
            using var json = JsonDocument.Parse(buffer.AsMemory(0, count));
            if (!json.RootElement.TryGetProperty("switch", out var state)) return (false, false, null);
            if (state.GetString() == "off") return (true, false, null);
            if (state.GetString() != "effective" || !json.RootElement.TryGetProperty("member", out var member)) return (false, false, null);
            return (true, true, member.ValueKind == JsonValueKind.Null ? null : member.GetString());
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or JsonException or InvalidOperationException)
        { return (false, false, null); }
    }
}
