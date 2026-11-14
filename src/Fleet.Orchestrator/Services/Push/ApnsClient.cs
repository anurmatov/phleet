using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fleet.Orchestrator.Configuration;
using Microsoft.Extensions.Options;

namespace Fleet.Orchestrator.Services.Push;

/// <summary>What Apple's push service said about one notification for one device.</summary>
public enum ApnsOutcome
{
    /// <summary>Accepted for delivery. Not proof that it was shown.</summary>
    Accepted,
    /// <summary>The token is no longer valid for this app: uninstalled, or never right. Forget it.</summary>
    Unregistered,
    /// <summary>Anything else. The token is kept.</summary>
    Failed,
}

public sealed record ApnsResult(ApnsOutcome Outcome, string? Reason = null);

public interface IApnsClient
{
    Task<ApnsResult> SendAsync(string deviceToken, PushMessage message, CancellationToken ct);
}

/// <summary>
/// Sends one notification to one device over Apple's HTTP/2 provider API.
/// </summary>
public sealed partial class ApnsClient(HttpClient http, IOptions<PushOptions> options, ApnsProviderToken token, TimeProvider time) : IApnsClient
{
    public const string HttpClientName = "apns";

    /// <summary>A device token is hex. Checked before it is put in a URL path.</summary>
    [GeneratedRegex(@"\A[0-9a-fA-F]{32,200}\z")]
    private static partial Regex DeviceTokenShape();

    public static bool IsDeviceToken(string? value) => value is not null && DeviceTokenShape().IsMatch(value);

    public async Task<ApnsResult> SendAsync(string deviceToken, PushMessage message, CancellationToken ct)
    {
        if (!IsDeviceToken(deviceToken)) return new ApnsResult(ApnsOutcome.Unregistered, "malformed_token");

        var result = await SendOnceAsync(deviceToken, message, ct);
        if (result is { Outcome: ApnsOutcome.Failed, Reason: "ExpiredProviderToken" })
        {
            // The held token aged out between requests. One new token, one retry.
            token.Invalidate();
            result = await SendOnceAsync(deviceToken, message, ct);
        }
        return result;
    }

    private async Task<ApnsResult> SendOnceAsync(string deviceToken, PushMessage message, CancellationToken ct)
    {
        var o = options.Value;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{o.ApnsHost}/3/device/{deviceToken}")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new ByteArrayContent(message.ToPayload()),
        };
        request.Headers.TryAddWithoutValidation("authorization", "bearer " + token.Current(time.GetUtcNow()));
        request.Headers.TryAddWithoutValidation("apns-topic", o.ApnsTopic.Trim());
        request.Headers.TryAddWithoutValidation("apns-push-type", "alert");
        // A count on the icon is not worth waking a phone for; everything else is something a
        // person asked to be told about now.
        request.Headers.TryAddWithoutValidation("apns-priority", message.IsBadgeOnly ? "5" : "10");
        // An hour: a decision that has waited an hour for the phone to come back is stale news.
        request.Headers.TryAddWithoutValidation("apns-expiration", time.GetUtcNow().AddHours(1).ToUnixTimeSeconds().ToString());
        request.Headers.TryAddWithoutValidation("apns-collapse-id", message.CollapseId);

        HttpResponseMessage response;
        try { response = await http.SendAsync(request, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new ApnsResult(ApnsOutcome.Failed, "transport");
        }

        using (response)
        {
            if (response.IsSuccessStatusCode) return new ApnsResult(ApnsOutcome.Accepted);

            var reason = await ReadReasonAsync(response, ct);
            // 410 is "this token is no longer active for the topic". BadDeviceToken and
            // DeviceTokenNotForTopic are a token that was never right for this app.
            if (response.StatusCode == HttpStatusCode.Gone || reason is "BadDeviceToken" or "DeviceTokenNotForTopic" or "Unregistered")
                return new ApnsResult(ApnsOutcome.Unregistered, reason);
            return new ApnsResult(ApnsOutcome.Failed, reason ?? ((int)response.StatusCode).ToString());
        }
    }

    /// <summary>Apple's <c>{"reason": "…"}</c>, kept only when it looks like one of Apple's reason names.</summary>
    private static async Task<string?> ReadReasonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() : null;
            return reason is { Length: > 0 and <= 64 } && reason.All(char.IsAsciiLetterOrDigit) ? reason : null;
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>
/// One notification: what the person reads, and what the app needs to open the right screen.
/// </summary>
/// <param name="Kind"><c>gate</c>, <c>run</c>, <c>agent</c> or <c>chat</c>; <c>badge</c> for a count and nothing else.</param>
/// <param name="Title">One short line.</param>
/// <param name="Body">What it is about: a repository and number, or an agent's name. Never task text.</param>
/// <param name="Target">Identifiers the app routes on. Never a secret.</param>
/// <param name="Key">Identifies the event, so a repeat replaces rather than stacks.</param>
/// <param name="Badge">
/// How many runs are waiting for a person, for the app's icon. Null leaves the icon as it is. Set
/// when the notification is sent, not when it is made, so a notification sent again carries the
/// count as it is then.
/// </param>
public sealed record PushMessage(
    string Kind, string Title, string Body, IReadOnlyDictionary<string, string> Target, string Key, int? Badge = null)
{
    /// <summary>The kind of a notification that only sets the count on the app's icon.</summary>
    public const string BadgeKind = "badge";

    /// <summary>Shows nothing and makes no sound: it corrects the number on the icon.</summary>
    public bool IsBadgeOnly => Kind == BadgeKind;

    /// <summary>At most 64 bytes, as Apple requires, and stable for the same event.</summary>
    /// <remarks>
    /// Every count shares one identifier, so a phone that was out of reach is handed only the
    /// newest: two counts delivered in the wrong order would leave the older one on the icon.
    /// </remarks>
    public string CollapseId =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(IsBadgeOnly ? BadgeKind : Key)))[..40].ToLowerInvariant();

    public byte[] ToPayload()
    {
        var aps = new Dictionary<string, object>();
        if (!IsBadgeOnly)
        {
            aps["alert"] = new Dictionary<string, string> { ["title"] = Title, ["body"] = Body };
            aps["sound"] = "default";
            aps["thread-id"] = Kind;
        }
        if (Badge is { } badge) aps["badge"] = Math.Max(badge, 0);

        var phleet = new Dictionary<string, string>(Target) { ["kind"] = Kind };
        return JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["aps"] = aps,
            ["phleet"] = phleet,
        });
    }
}
