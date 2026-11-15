namespace Fleet.Orchestrator.Configuration;

/// <summary>
/// The usage feed's host integration (off by default): where the orchestrator sends each agent's
/// turn usage rows and reads them back from (<c>Usage__ReceiverUrl</c>), and the receiver's bearer
/// (<c>Usage__AuthToken</c>).
/// </summary>
/// <remarks>
/// The bearer belongs to this process alone. It is never returned by an endpoint, never logged and
/// never given to an agent: an agent receives only <c>Usage__Enabled</c>, and only when
/// <see cref="IsReady"/>.
/// </remarks>
public sealed class UsageOptions
{
    public const string Section = "Usage";

    public bool Enabled { get; init; }

    /// <summary>The receiver's fixed origin, e.g. <c>http://usage-receiver:3900</c>. No path, query or credentials.</summary>
    public string ReceiverUrl { get; init; } = "";

    public string AuthToken { get; init; } = "";

    /// <summary>Read from configuration. An <c>Enabled</c> other than <c>true</c> is off.</summary>
    public static UsageOptions From(IConfiguration configuration)
    {
        var section = configuration.GetSection(Section);
        return new UsageOptions
        {
            Enabled = string.Equals(section[nameof(Enabled)]?.Trim(), "true", StringComparison.OrdinalIgnoreCase),
            ReceiverUrl = section[nameof(ReceiverUrl)]?.Trim() ?? "",
            AuthToken = section[nameof(AuthToken)]?.Trim() ?? "",
        };
    }

    /// <summary>
    /// What makes an enabled integration unusable, or null. Names the setting, never its value.
    /// </summary>
    public string? DescribeFault()
    {
        if (ReceiverOrigin is null) return "usage_receiver_url_invalid";
        if (AuthToken.Length == 0 || AuthToken.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            return "usage_auth_token_missing";
        return null;
    }

    public bool IsReady => Enabled && DescribeFault() is null;

    /// <summary>
    /// The receiver as an absolute http(s) origin, or null when it is anything else: a path, a
    /// query, a fragment or user info would let configuration point requests somewhere other than
    /// the receiver's own <c>/api/usage</c>.
    /// </summary>
    public Uri? ReceiverOrigin =>
        Uri.TryCreate(ReceiverUrl, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && uri.UserInfo.Length == 0
        && uri.AbsolutePath == "/"
        && uri.Query.Length == 0
        && uri.Fragment.Length == 0
        && !ReceiverUrl.Contains('?') && !ReceiverUrl.Contains('#')
            ? new Uri(uri.GetLeftPart(UriPartial.Authority))
            : null;
}
