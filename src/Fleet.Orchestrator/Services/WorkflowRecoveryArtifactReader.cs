using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fleet.Orchestrator.Configuration;
using Fleet.Shared;
using Microsoft.Extensions.Options;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// What one read of a failed run's issue or pull request established before a recovery (#476):
/// state, merge, head commit and branch, unfinished check runs and, for an issue, the open pull
/// requests that reference it. Facts only; <see cref="WorkflowRecoveryService"/> decides.
/// </summary>
public sealed record WorkflowRecoveryArtifactFacts(
    string Kind,
    string State,
    bool Merged,
    string? HeadSha,
    string? HeadRef,
    string? BranchSha,
    int PendingChecks,
    IReadOnlyList<long> OpenPullRequests)
{
    public const string Issue = "issue";
    public const string PullRequest = "pull";

    /// <summary>Canonical digest a reservation binds; any later change invalidates the proof.</summary>
    public string Digest => WorkflowOutcomeHashes.Sha256Hex(string.Join('\n',
        Kind, State, Merged ? "merged" : "unmerged", HeadSha ?? "-", HeadRef ?? "-", BranchSha ?? "-",
        PendingChecks.ToString(CultureInfo.InvariantCulture), string.Join(',', OpenPullRequests.Order())));
}

/// <summary>A read result: facts, or the payload-free reason they could not be established.</summary>
public sealed record WorkflowRecoveryArtifactRead(WorkflowRecoveryArtifactFacts? Facts, string? Unverified)
{
    public static WorkflowRecoveryArtifactRead Failed(string reason) => new(null, reason);
}

/// <summary>Bounded, read-only issue/PR/head/branch/checks read. Never throws; anything unclear is unverified.</summary>
public interface IWorkflowRecoveryArtifactReader
{
    Task<WorkflowRecoveryArtifactRead> ReadAsync(string repo, long? issueNumber, long? prNumber, CancellationToken ct);
}

/// <summary>
/// <see cref="IWorkflowRecoveryArtifactReader"/> over the public GitHub REST API, unauthenticated,
/// like <see cref="RepoVisibilityReader"/>: no credential is added for recovery. A private or
/// missing repository answers 404, which is unverified, so recovery there stays blocked until a
/// separately approved credentialed read exists.
/// </summary>
public sealed class GitHubWorkflowRecoveryArtifactReader(
    HttpClient http,
    IOptions<WorkflowOutcomeOptions> options,
    ILogger<GitHubWorkflowRecoveryArtifactReader> logger) : IWorkflowRecoveryArtifactReader
{
    /// <summary>Upper bound on one request, headers and body included.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private const string UserAgent = "phleet-orchestrator-workflow-outcomes";
    private const int MaxBodyBytes = 2 * 1024 * 1024;
    private const int PageSize = 100;
    private const int MaxTimelinePages = 5;
    // GitHub owner and repository names; "." and ".." are excluded so a path segment can never climb.
    private static readonly Regex RepoName = new(@"^[A-Za-z0-9][A-Za-z0-9-]{0,38}/(?!\.{1,2}\z)[A-Za-z0-9_.-]{1,100}\z", RegexOptions.CultureInvariant);
    private static readonly Regex Sha = new("^[0-9a-f]{40}$", RegexOptions.CultureInvariant);

    public async Task<WorkflowRecoveryArtifactRead> ReadAsync(string repo, long? issueNumber, long? prNumber, CancellationToken ct)
    {
        if (!RepoName.IsMatch(repo ?? "")) return WorkflowRecoveryArtifactRead.Failed("artifact_repo_invalid");
        try
        {
            return prNumber is { } pr ? await PullAsync(repo!, pr, ct)
                : issueNumber is { } issue ? await IssueAsync(repo!, issue, ct)
                : WorkflowRecoveryArtifactRead.Failed("artifact_missing");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Timeout, transport failure or an unparsable body: never a fact.
            logger.LogWarning("workflow_recovery_artifact_read_failed repo={Repo} error={Error}", repo, ex.GetType().Name);
            return WorkflowRecoveryArtifactRead.Failed("artifact_read_failed");
        }
    }

    private async Task<WorkflowRecoveryArtifactRead> PullAsync(string repo, long number, CancellationToken ct)
    {
        var (status, pull) = await GetAsync($"repos/{repo}/pulls/{number.ToString(CultureInfo.InvariantCulture)}", ct);
        using (pull)
        {
            if (status != HttpStatusCode.OK || pull is null) return Unread(status);
            var root = pull.RootElement;
            var state = root.GetProperty("state").GetString();
            var merged = root.GetProperty("merged").ValueKind == JsonValueKind.True;
            var head = root.GetProperty("head");
            var sha = head.GetProperty("sha").GetString();
            var headRef = head.GetProperty("ref").GetString();
            var headRepo = head.TryGetProperty("repo", out var r) && r.ValueKind == JsonValueKind.Object ? r.GetProperty("full_name").GetString() : null;
            // Git forbids ".." in a ref name; one here is not a branch this read can address.
            if (state is not ("open" or "closed") || sha is null || !Sha.IsMatch(sha) || string.IsNullOrEmpty(headRef) || headRef.Contains(".."))
                return WorkflowRecoveryArtifactRead.Failed("artifact_unparsable");

            var (checksStatus, checks) = await GetAsync($"repos/{repo}/commits/{sha}/check-runs?per_page={PageSize}", ct);
            int pending;
            using (checks)
            {
                if (checksStatus != HttpStatusCode.OK || checks is null) return Unread(checksStatus);
                var runs = checks.RootElement.GetProperty("check_runs");
                if (checks.RootElement.GetProperty("total_count").GetInt32() > runs.GetArrayLength())
                    return WorkflowRecoveryArtifactRead.Failed("artifact_checks_truncated");
                pending = runs.EnumerateArray().Count(c => c.GetProperty("status").GetString() != "completed");
            }

            // A deleted head repository (fork removed) leaves no branch to read: the branch is gone.
            string? branchSha = null;
            if (headRepo is not null)
            {
                if (!RepoName.IsMatch(headRepo)) return WorkflowRecoveryArtifactRead.Failed("artifact_unparsable");
                var (branchStatus, branch) = await GetAsync($"repos/{headRepo}/branches/{Uri.EscapeDataString(headRef)}", ct);
                using (branch)
                {
                    if (branchStatus == HttpStatusCode.OK && branch is not null)
                        branchSha = branch.RootElement.GetProperty("commit").GetProperty("sha").GetString();
                    else if (branchStatus != HttpStatusCode.NotFound)
                        return Unread(branchStatus);
                }
            }
            return new(new WorkflowRecoveryArtifactFacts(WorkflowRecoveryArtifactFacts.PullRequest, state, merged, sha, headRef, branchSha, pending, []), null);
        }
    }

    private async Task<WorkflowRecoveryArtifactRead> IssueAsync(string repo, long number, CancellationToken ct)
    {
        var path = $"repos/{repo}/issues/{number.ToString(CultureInfo.InvariantCulture)}";
        var (status, issue) = await GetAsync(path, ct);
        string? state;
        using (issue)
        {
            if (status != HttpStatusCode.OK || issue is null) return Unread(status);
            // The issues endpoint also answers for pull requests; the run named an issue.
            if (issue.RootElement.TryGetProperty("pull_request", out _)) return WorkflowRecoveryArtifactRead.Failed("artifact_kind_mismatch");
            state = issue.RootElement.GetProperty("state").GetString();
            if (state is not ("open" or "closed")) return WorkflowRecoveryArtifactRead.Failed("artifact_unparsable");
        }

        var open = new SortedSet<long>();
        for (var page = 1; ; page++)
        {
            if (page > MaxTimelinePages) return WorkflowRecoveryArtifactRead.Failed("artifact_timeline_truncated");
            var (timelineStatus, timeline) = await GetAsync($"{path}/timeline?per_page={PageSize}&page={page}", ct);
            using (timeline)
            {
                if (timelineStatus != HttpStatusCode.OK || timeline is null) return Unread(timelineStatus);
                var events = timeline.RootElement;
                foreach (var e in events.EnumerateArray())
                {
                    if (e.TryGetProperty("event", out var kind) && kind.GetString() == "cross-referenced"
                        && e.TryGetProperty("source", out var source) && source.TryGetProperty("issue", out var referencing)
                        && referencing.TryGetProperty("pull_request", out _)
                        && referencing.GetProperty("state").GetString() == "open")
                        open.Add(referencing.GetProperty("number").GetInt64());
                }
                if (events.GetArrayLength() < PageSize) break;
            }
        }
        return new(new WorkflowRecoveryArtifactFacts(WorkflowRecoveryArtifactFacts.Issue, state, false, null, null, null, 0, [.. open]), null);
    }

    private static WorkflowRecoveryArtifactRead Unread(HttpStatusCode? status) =>
        WorkflowRecoveryArtifactRead.Failed(status == HttpStatusCode.NotFound ? "artifact_not_found_or_private" : "artifact_read_failed");

    /// <summary>One bounded GET. A non-200 status returns no document; a body over the cap is an error.</summary>
    private async Task<(HttpStatusCode? Status, JsonDocument? Body)> GetAsync(string path, CancellationToken ct)
    {
        var baseUrl = (options.Value.GitHubApiBaseUrl ?? "").TrimEnd('/');
        if (!Uri.TryCreate(baseUrl + "/" + path, UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http"))
            return (null, null);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).WaitAsync(Timeout, cts.Token);
        if (response.StatusCode != HttpStatusCode.OK) return (response.StatusCode, null);
        if (response.Content.Headers.ContentLength > MaxBodyBytes) throw new InvalidDataException("artifact body too large");
        var body = await response.Content.ReadAsByteArrayAsync(cts.Token).WaitAsync(Timeout, cts.Token);
        if (body.Length > MaxBodyBytes) throw new InvalidDataException("artifact body too large");
        return (HttpStatusCode.OK, JsonDocument.Parse(Encoding.UTF8.GetString(body)));
    }
}
