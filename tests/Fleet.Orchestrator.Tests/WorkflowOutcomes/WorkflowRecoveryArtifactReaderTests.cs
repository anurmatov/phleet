using System.Net;
using System.Text;
using Fleet.Orchestrator.Configuration;
using Fleet.Orchestrator.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fleet.Orchestrator.Tests.WorkflowOutcomes;

/// <summary>
/// #476 recovery: the issue/PR/head/branch/checks read against scripted GitHub responses. Only a
/// complete, well-formed answer is a fact; every other status, truncation or shape is unverified.
/// </summary>
public sealed class WorkflowRecoveryArtifactReaderTests
{
    private const string Head = "0123456789abcdef0123456789abcdef01234567";

    private sealed class ScriptedGitHub : HttpMessageHandler
    {
        public Dictionary<string, (HttpStatusCode Status, string Body)> Routes { get; } = new(StringComparer.Ordinal);
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            Requests.Add(path);
            Assert.Null(request.Headers.Authorization);
            var (status, body) = Routes.TryGetValue(path, out var route) ? route : (HttpStatusCode.NotFound, "{}");
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static (GitHubWorkflowRecoveryArtifactReader Reader, ScriptedGitHub GitHub) Create()
    {
        var github = new ScriptedGitHub();
        var reader = new GitHubWorkflowRecoveryArtifactReader(new HttpClient(github),
            Options.Create(new WorkflowOutcomeOptions { GitHubApiBaseUrl = "https://github.example" }),
            NullLogger<GitHubWorkflowRecoveryArtifactReader>.Instance);
        return (reader, github);
    }

    private static string PullBody(string state = "closed", bool merged = false, string headRepo = "owner/repo") =>
        "{\"state\":\"" + state + "\",\"merged\":" + (merged ? "true" : "false") + ",\"head\":{\"sha\":\"" + Head
        + "\",\"ref\":\"feat/x\",\"repo\":{\"full_name\":\"" + headRepo + "\"}}}";

    private static string Checks(params string[] statuses) =>
        "{\"total_count\":" + statuses.Length + ",\"check_runs\":[" + string.Join(",", statuses.Select(s => "{\"status\":\"" + s + "\"}")) + "]}";

    private static void ClosedPullWithDeletedBranch(ScriptedGitHub github)
    {
        github.Routes["/repos/owner/repo/pulls/17"] = (HttpStatusCode.OK, PullBody());
        github.Routes[$"/repos/owner/repo/commits/{Head}/check-runs?per_page=100"] = (HttpStatusCode.OK, Checks("completed", "completed"));
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "recovery")]
    public async Task PullRequest_StateHeadBranchAndChecks_AreReadUnauthenticated()
    {
        var (reader, github) = Create();
        ClosedPullWithDeletedBranch(github);

        var read = await reader.ReadAsync("owner/repo", null, 17, default);

        Assert.Null(read.Unverified);
        Assert.Equal(new WorkflowRecoveryArtifactFacts("pull", "closed", false, Head, "feat/x", null, 0, []).Digest, read.Facts!.Digest);
        Assert.Contains("/repos/owner/repo/branches/feat%2Fx", github.Requests);

        github.Routes["/repos/owner/repo/branches/feat%2Fx"] = (HttpStatusCode.OK, "{\"commit\":{\"sha\":\"fedcba9876543210fedcba9876543210fedcba98\"}}");
        github.Routes[$"/repos/owner/repo/commits/{Head}/check-runs?per_page=100"] = (HttpStatusCode.OK, Checks("completed", "in_progress", "queued"));
        var moved = (await reader.ReadAsync("owner/repo", null, 17, default)).Facts!;
        Assert.Equal(("fedcba9876543210fedcba9876543210fedcba98", 2), (moved.BranchSha, moved.PendingChecks));
    }

    [Theory]
    [Trait("WorkflowOutcomeSuite", "recovery")]
    [InlineData("pull 404", "artifact_not_found_or_private")]
    [InlineData("pull 500", "artifact_read_failed")]
    [InlineData("pull rate limited", "artifact_read_failed")]
    [InlineData("pull malformed", "artifact_read_failed")]
    [InlineData("pull bad head", "artifact_unparsable")]
    [InlineData("checks truncated", "artifact_checks_truncated")]
    [InlineData("checks 502", "artifact_read_failed")]
    [InlineData("branch 500", "artifact_read_failed")]
    public async Task PullRequest_AnythingButACompleteAnswer_IsUnverified(string fault, string reason)
    {
        var (reader, github) = Create();
        ClosedPullWithDeletedBranch(github);
        switch (fault)
        {
            case "pull 404": github.Routes.Remove("/repos/owner/repo/pulls/17"); break;
            case "pull 500": github.Routes["/repos/owner/repo/pulls/17"] = (HttpStatusCode.InternalServerError, "{}"); break;
            case "pull rate limited": github.Routes["/repos/owner/repo/pulls/17"] = (HttpStatusCode.Forbidden, "{\"message\":\"rate limit\"}"); break;
            case "pull malformed": github.Routes["/repos/owner/repo/pulls/17"] = (HttpStatusCode.OK, "{\"state\":\"closed\"}"); break;
            case "pull bad head": github.Routes["/repos/owner/repo/pulls/17"] = (HttpStatusCode.OK, PullBody().Replace(Head, "not-a-sha")); break;
            case "checks truncated":
                github.Routes[$"/repos/owner/repo/commits/{Head}/check-runs?per_page=100"] = (HttpStatusCode.OK, "{\"total_count\":101,\"check_runs\":[{\"status\":\"completed\"}]}");
                break;
            case "checks 502": github.Routes[$"/repos/owner/repo/commits/{Head}/check-runs?per_page=100"] = (HttpStatusCode.BadGateway, "{}"); break;
            case "branch 500": github.Routes["/repos/owner/repo/branches/feat%2Fx"] = (HttpStatusCode.InternalServerError, "{}"); break;
        }

        var read = await reader.ReadAsync("owner/repo", null, 17, default);

        Assert.Equal((null, reason), (read.Facts, read.Unverified));
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "recovery")]
    public async Task Issue_StateAndOpenReferencingPullRequests_AcrossTimelinePages()
    {
        var (reader, github) = Create();
        github.Routes["/repos/owner/repo/issues/5"] = (HttpStatusCode.OK, "{\"state\":\"open\"}");
        var full = "[" + string.Join(",", Enumerable.Range(0, 100).Select(_ => "{\"event\":\"labeled\"}")) + "]";
        github.Routes["/repos/owner/repo/issues/5/timeline?per_page=100&page=1"] = (HttpStatusCode.OK, full);
        github.Routes["/repos/owner/repo/issues/5/timeline?per_page=100&page=2"] = (HttpStatusCode.OK, """
            [{"event":"cross-referenced","source":{"issue":{"number":31,"state":"open","pull_request":{}}}},
             {"event":"cross-referenced","source":{"issue":{"number":32,"state":"closed","pull_request":{}}}},
             {"event":"cross-referenced","source":{"issue":{"number":40,"state":"open"}}}]
            """);

        var facts = (await reader.ReadAsync("owner/repo", 5, null, default)).Facts!;

        Assert.Equal(("issue", "open"), (facts.Kind, facts.State));
        Assert.Equal([31L], facts.OpenPullRequests);
    }

    [Theory]
    [Trait("WorkflowOutcomeSuite", "recovery")]
    [InlineData("is a pull request", "artifact_kind_mismatch")]
    [InlineData("timeline over five pages", "artifact_timeline_truncated")]
    [InlineData("timeline 500", "artifact_read_failed")]
    [InlineData("issue 404", "artifact_not_found_or_private")]
    public async Task Issue_AnythingButACompleteAnswer_IsUnverified(string fault, string reason)
    {
        var (reader, github) = Create();
        github.Routes["/repos/owner/repo/issues/5"] = (HttpStatusCode.OK, "{\"state\":\"open\"}");
        var full = "[" + string.Join(",", Enumerable.Range(0, 100).Select(_ => "{\"event\":\"labeled\"}")) + "]";
        github.Routes["/repos/owner/repo/issues/5/timeline?per_page=100&page=1"] = (HttpStatusCode.OK, "[]");
        switch (fault)
        {
            case "is a pull request": github.Routes["/repos/owner/repo/issues/5"] = (HttpStatusCode.OK, "{\"state\":\"open\",\"pull_request\":{}}"); break;
            case "timeline over five pages":
                for (var page = 1; page <= 5; page++) github.Routes[$"/repos/owner/repo/issues/5/timeline?per_page=100&page={page}"] = (HttpStatusCode.OK, full);
                break;
            case "timeline 500": github.Routes["/repos/owner/repo/issues/5/timeline?per_page=100&page=1"] = (HttpStatusCode.InternalServerError, "[]"); break;
            case "issue 404": github.Routes.Remove("/repos/owner/repo/issues/5"); break;
        }

        var read = await reader.ReadAsync("owner/repo", 5, null, default);

        Assert.Equal((null, reason), (read.Facts, read.Unverified));
    }

    [Theory]
    [Trait("WorkflowOutcomeSuite", "recovery")]
    [InlineData("owner/repo/extra")]
    [InlineData("../repo")]
    [InlineData("owner/..")]
    [InlineData("owner/.")]
    [InlineData("-owner/repo")]
    [InlineData("owner/repo\n")]
    [InlineData("")]
    public async Task InvalidRepository_IsNeverRequested(string repo)
    {
        var (reader, github) = Create();

        Assert.Equal("artifact_repo_invalid", (await reader.ReadAsync(repo, null, 17, default)).Unverified);
        Assert.Empty(github.Requests);
    }
}
