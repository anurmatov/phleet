using System.Globalization;
using System.Text.RegularExpressions;
using Fleet.Shared.Security;

namespace Fleet.Agent.Services;

/// <summary>
/// Managed stock OpenShell only (#492): the agent's own bot token never enters the workload. Stock
/// issues a placeholder in <see cref="StockCredentials.TelegramBotToken"/>; Telegram.Bot is built with
/// <c>&lt;botId&gt;:&lt;placeholder&gt;</c>, which passes its numeric-prefix token validation, and this
/// handler strips only that <c>&lt;botId&gt;:</c> wrapper. Requests leave as exactly
/// <c>https://api.telegram.org/bot&lt;placeholder&gt;/&lt;method&gt;</c> or
/// <c>https://api.telegram.org/file/bot&lt;placeholder&gt;/&lt;path&gt;</c>, which the gateway substitutes
/// on egress. Any other scheme, host, port or shape is refused: no fallback, no literal token.
/// </summary>
public sealed class StockTelegramHttpHandler : DelegatingHandler
{
    public const string ApiHost = "api.telegram.org";
    private static readonly Regex Method = new(@"\A[A-Za-z]{1,64}\z", RegexOptions.CultureInvariant);
    private readonly string _wrapped;
    private readonly string _placeholder;

    /// <param name="sdkToken">The wrapped SDK token from <see cref="SdkToken"/>; anything else refuses.</param>
    public StockTelegramHttpHandler(string sdkToken, HttpMessageHandler inner) : base(inner)
    {
        var colon = sdkToken.IndexOf(':');
        if (colon < 1 || SdkToken(long.TryParse(sdkToken[..colon], NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0,
                sdkToken[(colon + 1)..]) != sdkToken)
            throw new InvalidOperationException("stock_telegram_token_invalid");
        _wrapped = sdkToken;
        _placeholder = sdkToken[(colon + 1)..];
    }

    /// <summary>The SDK token for the nonsecret bot id and the issued placeholder; null when either is unusable.</summary>
    public static string? SdkToken(long botId, string? placeholder) =>
        botId > 0 && StockCredentials.IsPlaceholder(placeholder, StockCredentials.TelegramBotToken)
            ? botId.ToString(CultureInfo.InvariantCulture) + ":" + placeholder : null;

    /// <summary>The client handed to Telegram.Bot in managed mode, over the ordinary proxy-aware handler.</summary>
    public static HttpClient CreateClient(string sdkToken) => new(new StockTelegramHttpHandler(sdkToken, new SocketsHttpHandler()));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.RequestUri = Rewrite(request.RequestUri);
        return base.SendAsync(request, cancellationToken);
    }

    /// <summary>The exact stock-substitutable request URI, or a refusal. The message never carries the URI.</summary>
    internal Uri Rewrite(Uri? uri)
    {
        if (uri is { IsAbsoluteUri: true } && uri.Scheme == Uri.UriSchemeHttps && uri.Host == ApiHost && uri.Port == 443
            && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0)
        {
            var path = uri.AbsolutePath;
            var api = "/bot" + _wrapped + "/";
            if (path.StartsWith(api, StringComparison.Ordinal) && Method.IsMatch(path[api.Length..]))
                return new Uri($"https://{ApiHost}/bot{_placeholder}/{path[api.Length..]}");
            var file = "/file/bot" + _wrapped + "/";
            // A file path is Telegram's getFile result; it never carries another placeholder.
            if (path.StartsWith(file, StringComparison.Ordinal) && path.Length > file.Length
                && !path[file.Length..].Contains("openshell:", StringComparison.OrdinalIgnoreCase))
                return new Uri($"https://{ApiHost}/file/bot{_placeholder}/{path[file.Length..]}");
        }
        throw new HttpRequestException("stock_telegram_request_refused");
    }
}
