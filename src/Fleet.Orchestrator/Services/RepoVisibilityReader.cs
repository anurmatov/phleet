using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fleet.Orchestrator.Configuration;
using Microsoft.Extensions.Options;

namespace Fleet.Orchestrator.Services;

/// <summary>What an unauthenticated repository read says about a target repo (#436 D6).</summary>
public enum RepoVisibility
{
    /// <summary>200 with <c>"private": false</c>.</summary>
    Public,

    /// <summary>200 with <c>"private": true</c>, or 404 (not visible without credentials).</summary>
    Private,

    /// <summary>Anything else: 403/429 rate limits, 5xx, other statuses, timeouts, unparsable bodies.</summary>
    Unknown,
}

/// <summary>
/// Reads a repository's visibility with one unauthenticated <c>GET {base}/repos/{owner}/{repo}</c>.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately carries no credential: the question is "can anyone read this repo", and a token
/// would answer a different one. A 404 therefore means "not public" — a missing or renamed repo
/// also returns 404, which is safe because a decision additionally requires the repo to be listed
/// in the grant's targets.
/// </para>
/// <para>
/// Fail-safe: every outcome that is not a clean 200 or 404 is <see cref="RepoVisibility.Unknown"/>,
/// which the caller refuses. An unknown visibility is never treated as private.
/// </para>
/// </remarks>
public sealed class RepoVisibilityReader(
    HttpClient http,
    IOptions<EpicGrantOptions> options,
    ILogger<RepoVisibilityReader> logger)
{
    /// <summary>Upper bound on one read, headers and body included.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private const string UserAgent = "phleet-orchestrator-epic-grants";
    private const int MaxBodyBytes = 1024 * 1024;
    private static readonly Regex RepoName = new("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant);

    public async Task<RepoVisibility> ReadAsync(string repo, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(repo) || !RepoName.IsMatch(repo))
            return RepoVisibility.Unknown;

        var slash = repo.IndexOf('/');
        var owner = repo[..slash];
        var name = repo[(slash + 1)..];
        var baseUrl = (options.Value.GitHubApiBaseUrl ?? "").TrimEnd('/');
        if (!Uri.TryCreate($"{baseUrl}/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}",
                UriKind.Absolute, out var url))
            return RepoVisibility.Unknown;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .WaitAsync(Timeout, cts.Token);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return RepoVisibility.Private;
            if (response.StatusCode != HttpStatusCode.OK)
            {
                logger.LogWarning("Repo visibility read for {Repo} returned HTTP {Status}; visibility unknown",
                    repo, (int)response.StatusCode);
                return RepoVisibility.Unknown;
            }

            if (response.Content.Headers.ContentLength > MaxBodyBytes)
                return RepoVisibility.Unknown;
            var body = await response.Content.ReadAsByteArrayAsync(cts.Token).WaitAsync(Timeout, cts.Token);
            if (body.Length > MaxBodyBytes)
                return RepoVisibility.Unknown;

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("private", out var isPrivate))
            {
                if (isPrivate.ValueKind == JsonValueKind.False) return RepoVisibility.Public;
                if (isPrivate.ValueKind == JsonValueKind.True) return RepoVisibility.Private;
            }

            logger.LogWarning("Repo visibility read for {Repo} returned no boolean 'private'; visibility unknown", repo);
            return RepoVisibility.Unknown;
        }
        catch (Exception ex)
        {
            // Timeout, cancellation, transport failure or an unparsable body: all unknown.
            logger.LogWarning("Repo visibility read for {Repo} failed ({Error}); visibility unknown",
                repo, ex.GetType().Name);
            return RepoVisibility.Unknown;
        }
    }
}
