using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fleet.Shared;

namespace Fleet.SecretProxy;

/// <summary>
/// Mints GitHub App installation tokens for a <see cref="SecretBindingRules.KindGitHubApp"/> binding.
/// The App's private key stays here: an agent holds a placeholder, and a request it sends to a bound
/// GitHub host leaves with an installation token it never sees.
/// </summary>
/// <remarks>
/// <para>
/// Which installation: the one whose account owns the repository, organisation or user the request
/// names. A request that names none, or names an account with no installation, uses the primary
/// installation: the last organisation in GitHub's listing, or the first installation when there is
/// no organisation. That is the routing the agent image's <c>gh-auth.sh</c> has always applied.
/// </para>
/// <para>
/// Tokens live in memory only and are re-minted five minutes before they expire. Nothing here is
/// logged except fixed reasons and status codes.
/// </para>
/// </remarks>
internal sealed partial class GitHubAppTokens(
    HttpMessageInvoker http, SecretProxyOptions options, ILogger<GitHubAppTokens> logger, TimeProvider? time = null)
{
    private static readonly TimeSpan InstallationsLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TokenRenewBefore = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(15);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, App> _apps = new(StringComparer.Ordinal);

    [GeneratedRegex(@"\A[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})\z", RegexOptions.CultureInvariant)]
    private static partial Regex Login();

    /// <summary>
    /// The account a request names, from the URL that is actually sent upstream, or null. REST hosts
    /// name it as <c>/repos/{owner}/…</c>, <c>/orgs/{org}/…</c> or <c>/users/{user}/…</c>; the git
    /// host as <c>/{owner}/{repo}/…</c>.
    /// </summary>
    internal static string? OwnerOf(Uri uri)
    {
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? owner = null;
        if (segments.Length >= 2 && segments[0] is "repos" or "orgs" or "users")
            owner = segments[1];
        else if (segments.Length >= 2
                 && !uri.Host.StartsWith("api.", StringComparison.Ordinal)
                 && !uri.Host.StartsWith("uploads.", StringComparison.Ordinal))
            owner = segments[0];
        return owner is not null && Login().IsMatch(owner) ? owner : null;
    }

    /// <summary>
    /// An installation token for <paramref name="owner"/> (or the primary installation), or null when
    /// none can be had; <paramref name="reason"/> then holds a fixed code.
    /// </summary>
    public async Task<(string? Token, string? Reason)> GetTokenAsync(
        SecretProxyBinding binding, string? owner, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(binding.Value) || string.IsNullOrWhiteSpace(binding.AppId))
            return (null, "github_app_not_configured");

        // A rotated key or App id is a different App: its cached tokens must not be reused.
        var identity = binding.Key + ":" + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(binding.AppId + "\n" + binding.Value)));
        var app = _apps.GetOrAdd(identity, _ => new App());
        foreach (var stale in _apps.Keys.Where(k => k != identity && k.StartsWith(binding.Key + ":", StringComparison.Ordinal)))
            _apps.TryRemove(stale, out _);

        await app.Gate.WaitAsync(ct);
        try
        {
            var now = _time.GetUtcNow();
            if (app.FailedUntil > now)
                return (null, app.FailureReason);

            if (app.Installations is null || now - app.InstallationsAt > InstallationsLifetime)
            {
                var (installations, fault) = await ListInstallationsAsync(binding, ct);
                if (installations is null)
                {
                    // Keep serving from a list we already have; a brief GitHub outage must not
                    // take every agent down. Without one, back off instead of hammering the API.
                    if (app.Installations is null)
                        return Fail(app, now, fault!);
                }
                else
                {
                    app.Installations = installations;
                    app.InstallationsAt = now;
                }
            }

            var installation = Select(app.Installations!, owner);
            if (installation is null)
                return Fail(app, now, "github_app_no_installation");

            if (app.Tokens.TryGetValue(installation.Id, out var cached) && cached.ExpiresAt - now > TokenRenewBefore)
                return (cached.Token, null);

            var (token, expiresAt, tokenFault) = await MintAsync(binding, installation.Id, ct);
            if (token is null)
                return Fail(app, now, tokenFault!);

            app.Tokens[installation.Id] = (token, expiresAt);
            return (token, null);
        }
        finally
        {
            app.Gate.Release();
        }
    }

    private (string? Token, string? Reason) Fail(App app, DateTimeOffset now, string reason)
    {
        app.FailedUntil = now + FailureBackoff;
        app.FailureReason = reason;
        return (null, reason);
    }

    /// <summary>The owner's installation, else the primary: the last organisation, else the first installation.</summary>
    internal static Installation? Select(IReadOnlyList<Installation> installations, string? owner)
    {
        if (owner is not null
            && installations.FirstOrDefault(i => string.Equals(i.Login, owner, StringComparison.OrdinalIgnoreCase)) is { } owned)
            return owned;
        return installations.LastOrDefault(i => i.IsOrganization) ?? installations.FirstOrDefault();
    }

    private async Task<(IReadOnlyList<Installation>? Installations, string? Fault)> ListInstallationsAsync(
        SecretProxyBinding binding, CancellationToken ct)
    {
        if (CreateJwt(binding) is not { } jwt)
            return (null, "github_app_key_invalid");

        var all = new List<Installation>();
        for (var page = 1; page <= 10; page++)
        {
            using var request = Request(HttpMethod.Get, $"/app/installations?per_page=100&page={page}", jwt);
            try
            {
                using var response = await http.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("SecretProxy GitHub App {Key}: listing installations returned {Status}",
                        binding.Key, (int)response.StatusCode);
                    return (null, "github_app_rejected");
                }

                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                    return (null, "github_app_response_invalid");
                var count = 0;
                foreach (var item in document.RootElement.EnumerateArray())
                {
                    count++;
                    if (item.TryGetProperty("id", out var id) && id.TryGetInt64(out var value)
                        && item.TryGetProperty("account", out var account) && account.ValueKind == JsonValueKind.Object
                        && account.TryGetProperty("login", out var login) && login.GetString() is { Length: > 0 } name)
                    {
                        var type = account.TryGetProperty("type", out var t) ? t.GetString() : null;
                        all.Add(new Installation(value, name, type == "Organization"));
                    }
                }
                if (count < 100)
                    break;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException
                                       || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                logger.LogWarning("SecretProxy GitHub App {Key}: listing installations failed ({Error})",
                    binding.Key, ex.GetBaseException().GetType().Name);
                return (null, "github_app_unreachable");
            }
        }

        return (all, null);
    }

    private async Task<(string? Token, DateTimeOffset ExpiresAt, string? Fault)> MintAsync(
        SecretProxyBinding binding, long installationId, CancellationToken ct)
    {
        if (CreateJwt(binding) is not { } jwt)
            return (null, default, "github_app_key_invalid");

        using var request = Request(HttpMethod.Post, $"/app/installations/{installationId}/access_tokens", jwt);
        try
        {
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("SecretProxy GitHub App {Key}: minting a token returned {Status}",
                    binding.Key, (int)response.StatusCode);
                return (null, default, "github_app_rejected");
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("token", out var token) && token.GetString() is { Length: > 0 } value
                && document.RootElement.TryGetProperty("expires_at", out var expires)
                && expires.TryGetDateTimeOffset(out var expiresAt))
            {
                logger.LogInformation("SecretProxy GitHub App {Key}: minted an installation token", binding.Key);
                return (value, expiresAt, null);
            }
            return (null, default, "github_app_response_invalid");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException
                                   || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning("SecretProxy GitHub App {Key}: minting a token failed ({Error})",
                binding.Key, ex.GetBaseException().GetType().Name);
            return (null, default, "github_app_unreachable");
        }
    }

    private HttpRequestMessage Request(HttpMethod method, string pathAndQuery, string jwt)
    {
        var request = new HttpRequestMessage(method, options.GitHubApiBaseUrl.TrimEnd('/') + pathAndQuery);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("User-Agent", "fleet-secrets");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    /// <summary>
    /// An RS256 App JWT: issued 60 s ago for clock skew, valid 9 minutes. Null when the stored key is
    /// not a usable RSA key. <c>.env</c> holds the PEM base64-encoded; a bare PEM is accepted too.
    /// </summary>
    private string? CreateJwt(SecretProxyBinding binding)
    {
        try
        {
            var stored = binding.Value!.Trim();
            var pem = stored.Contains("-----BEGIN", StringComparison.Ordinal)
                ? stored
                : Encoding.UTF8.GetString(Convert.FromBase64String(stored));

            using var rsa = RSA.Create();
            rsa.ImportFromPem(pem);

            var now = _time.GetUtcNow().ToUnixTimeSeconds();
            var header = Base64Url("""{"alg":"RS256","typ":"JWT"}"""u8);
            var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(
                new Dictionary<string, object> { ["iat"] = now - 60, ["exp"] = now + 540, ["iss"] = binding.AppId!.Trim() }));
            var signature = rsa.SignData(
                Encoding.ASCII.GetBytes(header + "." + payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return header + "." + payload + "." + Base64Url(signature);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return null;
        }
    }

    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal sealed record Installation(long Id, string Login, bool IsOrganization);

    private sealed class App
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public IReadOnlyList<Installation>? Installations { get; set; }
        public DateTimeOffset InstallationsAt { get; set; }
        public Dictionary<long, (string Token, DateTimeOffset ExpiresAt)> Tokens { get; } = [];
        public DateTimeOffset FailedUntil { get; set; }
        public string? FailureReason { get; set; }
    }
}
