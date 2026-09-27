using System.Net;
using System.Text.RegularExpressions;

namespace Fleet.Shared;

/// <summary>
/// Local model mode for the claude and codex harnesses (#382): one stored server URL, validated
/// by one set of rules at write time, at provision time and at agent startup.
/// </summary>
/// <remarks>
/// <para>
/// <b>Local mode ⇔ provider <c>claude</c> or <c>codex</c> and a non-empty URL.</b> <c>null</c> and
/// <c>""</c> are off. The URL is an origin (<c>scheme://host[:port]</c>); Fleet derives each
/// harness's path: Claude Code appends <c>/v1/messages</c>, codex gets <see cref="CodexOssBaseUrl"/>.
/// </para>
/// <para>
/// Claude-only rules (tag vs Claude ids, the local effort vocabulary) stay in
/// <see cref="ClaudeLocalModel"/>, which delegates its URL checks here.
/// </para>
/// </remarks>
public static partial class LocalModel
{
    public const int MaxBaseUrlLength = 500;
    public const int MaxModelTagLength = 100;

    // The tag reaches a CLI argument (claude --model) or a JSON field; one safe token either way.
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:/-]*$")]
    private static partial Regex ModelTagPattern();

    public static bool IsEnabled(string? provider, string? baseUrl) =>
        (string.Equals(provider, "claude", StringComparison.Ordinal)
         || string.Equals(provider, "codex", StringComparison.Ordinal))
        && !string.IsNullOrEmpty(baseUrl);

    public static bool IsCodexEnabled(string? provider, string? baseUrl) =>
        string.Equals(provider, "codex", StringComparison.Ordinal) && !string.IsNullOrEmpty(baseUrl);

    /// <summary>
    /// The first fault in a local-mode configuration (L1, L2, then the harness rules), or null.
    /// Null as well when <paramref name="baseUrl"/> is null or empty.
    /// </summary>
    public static string? DescribeConfigFault(string? provider, string? baseUrl, string? model, string? effort)
    {
        if (string.IsNullOrEmpty(baseUrl))
            return null;

        // L1
        if (!string.Equals(provider, "claude", StringComparison.Ordinal)
            && !string.Equals(provider, "codex", StringComparison.Ordinal))
        {
            return "Local model runs on claude or codex; clear the local server URL first.";
        }

        // L2
        if (DescribeBaseUrlFault(baseUrl) is { } urlFault)
            return urlFault;

        return string.Equals(provider, "claude", StringComparison.Ordinal)
            ? ClaudeLocalModel.DescribeConfigFault(provider, baseUrl, model, effort)
            : DescribeCodexModelFault(model);
    }

    /// <summary>C1 and C2: a codex local model is <c>ollama/&lt;tag&gt;</c> or <c>lmstudio/&lt;tag&gt;</c>.</summary>
    public static string? DescribeCodexModelFault(string? model)
    {
        // C1
        var (provider, tag) = CodexLocalModelProviders.Split(model ?? "");
        if (provider is null)
            return "Codex local model must be ollama/<tag> or lmstudio/<tag>.";

        // C2
        return DescribeModelTagFault(tag);
    }

    /// <summary>The tag charset rule (V4 for claude, C2 for codex), or null.</summary>
    public static string? DescribeModelTagFault(string? tag) =>
        string.IsNullOrEmpty(tag) || tag.Length > MaxModelTagLength || !ModelTagPattern().IsMatch(tag)
            ? $"The local model tag must be 1–{MaxModelTagLength} characters matching "
              + "^[A-Za-z0-9][A-Za-z0-9._:/-]*$; it contains characters not allowed in a CLI argument."
            : null;

    /// <summary>
    /// L2 (the former V2 and V3): an http(s) origin with no credentials, path, query or fragment,
    /// not container-local. Null when sound. The text never includes the value: it may carry
    /// credentials.
    /// </summary>
    public static string? DescribeBaseUrlFault(string baseUrl)
    {
        if (baseUrl.Length > MaxBaseUrlLength)
            return BaseUrlFault($"is longer than {MaxBaseUrlLength} characters");

        // Checked on the raw string: Uri parsing would trim it and hide the difference.
        if (baseUrl.Trim().Length != baseUrl.Length)
            return BaseUrlFault("has surrounding whitespace");

        // '@' only ever delimits userinfo in an origin. Checked raw because "http://@host" parses
        // with an empty UserInfo, and GetLeftPart would then keep the '@' in the canonical form.
        if (baseUrl.Contains('@'))
            return BaseUrlFault("has credentials");

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
            return BaseUrlFault("is not an absolute URI");

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return BaseUrlFault("must use http or https");

        if (string.IsNullOrEmpty(uri.Host))
            return BaseUrlFault("has no host");

        if (uri.UserInfo.Length > 0)
            return BaseUrlFault("has credentials");

        if (uri.Query.Length > 0)
            return BaseUrlFault("has a query");

        if (uri.Fragment.Length > 0)
            return BaseUrlFault("has a fragment");

        if (uri.AbsolutePath != "/")
            return BaseUrlFault("has a path");

        if (IsContainerLocal(uri))
            return "Local server URL points at localhost, which inside an agent container is the container "
                 + "itself; use the inference server's LAN address or host name.";

        return null;
    }

    /// <summary>
    /// <c>scheme://host[:port]</c>: scheme and host lowercased, default port dropped, trailing
    /// <c>/</c> removed. Only meaningful for a value that passed <see cref="DescribeBaseUrlFault"/>.
    /// </summary>
    public static string CanonicalizeBaseUrl(string baseUrl) =>
        new Uri(baseUrl, UriKind.Absolute).GetLeftPart(UriPartial.Authority);

    /// <summary>The <c>CODEX_OSS_BASE_URL</c> codex needs for a canonical origin.</summary>
    public static string CodexOssBaseUrl(string origin) => origin + "/v1";

    private static string BaseUrlFault(string reason) =>
        $"Local server URL {reason}. It must be an http(s) origin such as http://<server-address>:11434 — "
      + "no path, credentials, query or fragment. Fleet adds /v1/messages for Claude and /v1 for Codex.";

    // V3: localhost (any case, trailing dot), 127.0.0.0/8, ::1, the unspecified addresses, and their
    // IPv4-mapped IPv6 forms. Uri has already folded 127.1 and 2130706433 into 127.0.0.1.
    private static bool IsContainerLocal(Uri uri)
    {
        var host = uri.IdnHost.TrimEnd('.');
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!IPAddress.TryParse(host, out var address))
            return false;

        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        return IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any);
    }
}
