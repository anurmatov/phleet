using System.Collections.Concurrent;
using System.Text.Json;
namespace Fleet.Orchestrator.Services;
/// <summary>Never logs request URLs: the bot credential is part of Telegram's URL.</summary>
public sealed class TelegramMembershipClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _slots = new(8, 8);
    private readonly ConcurrentDictionary<string, (long Id, DateTimeOffset Until)> _bots = new();
    public TelegramMembershipClient(HttpMessageHandler? handler = null, Uri? baseUri = null)
    { _http = new HttpClient(handler ?? new SocketsHttpHandler()) { BaseAddress = baseUri ?? new Uri("https://api.telegram.org/"), Timeout = Timeout.InfiniteTimeSpan }; }
    public async Task<string> CheckAsync(string? token, long botId, long chatId, long userId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || !_slots.Wait(0)) return "unverified";
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            if (!_bots.TryGetValue(token, out var cached) || cached.Until <= DateTimeOffset.UtcNow)
            {
                using var me = await GetAsync($"bot{token}/getMe", budget.Token);
                if (me is null || !me.RootElement.GetProperty("result").TryGetProperty("id", out var id) || !id.TryGetInt64(out var resolved)) return "unverified";
                cached = (resolved, DateTimeOffset.UtcNow.AddMinutes(10));
                _bots[token] = cached;
            }
            if (cached.Id != botId) return "unverified";
            using var reply = await GetAsync(FormattableString.Invariant($"bot{token}/getChatMember?chat_id={chatId}&user_id={userId}"), budget.Token);
            if (reply is null) return "unverified";
            var result = reply.RootElement.GetProperty("result");
            return result.GetProperty("status").GetString() switch
            {
                "creator" or "administrator" or "member" => "member",
                "restricted" => result.TryGetProperty("is_member", out var member) && member.ValueKind == JsonValueKind.True ? "member" : "not_member",
                "left" or "kicked" => "not_member",
                _ => "unverified",
            };
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or KeyNotFoundException or IOException)
        { return "unverified"; }
        finally { _slots.Release(); }
    }
    private async Task<JsonDocument?> GetAsync(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var data = new byte[65537]; var count = 0;
        while (count < data.Length) { var n = await stream.ReadAsync(data.AsMemory(count), ct); if (n == 0) break; count += n; }
        if (count > 65536) return null;
        var json = JsonDocument.Parse(data.AsMemory(0, count));
        if (!json.RootElement.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True) { json.Dispose(); return null; }
        return json;
    }
    public void Dispose() { _http.Dispose(); _slots.Dispose(); }
}
