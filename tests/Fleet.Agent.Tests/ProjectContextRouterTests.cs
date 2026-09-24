using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static Fleet.Agent.Tests.ProjectContextTestSupport;

namespace Fleet.Agent.Tests;

/// <summary>
/// #347 "Routing: resolved at intake" — the pure resolution function, the routing-block
/// validation, the relay signal parsing, and the DI/config binding of the pinned block.
/// </summary>
public class ProjectContextRouterTests
{
    private static ProjectContextRoutingTable Table(
        IEnumerable<string> assigned,
        IEnumerable<string> cards,
        params (string Kind, string Value, string Project)[] routes)
    {
        Assert.True(ProjectContextRoutingTable.TryCreate(Routing(cards, routes), assigned.ToList(), out var table, out var error), error);
        return table;
    }

    private static ProjectContextRouter Router(
        ProjectContextRoutingOptions? routing, IEnumerable<string> assigned, ILogger<ProjectContextRouter>? logger = null) =>
        new(Options.Create(new AgentOptions
        {
            Name = "agent-a", Role = "test", WorkDir = "/tmp",
            Projects = assigned.ToList(),
            ProjectContextRouting = routing,
        }), logger ?? NullLogger<ProjectContextRouter>.Instance);

    // ── precedence, no merge, fall-through ──────────────────────────────────

    [Fact]
    public void Precedence_RepoBeatsWorkflowBeatsChat_AndLevelsNeverMerge()
    {
        var table = Table(["project-a", "project-b", "project-c"], ["project-a", "project-b", "project-c"],
            ("repo", Repo, "project-a"), ("workflow", Workflow, "project-b"), ("chat", ChatValue, "project-c"));

        var all = ProjectContextRouter.Resolve(new ProjectContextSignals(Repo, Workflow, Chat), table);
        Assert.Equal("repo", all.SignalKind);
        Assert.Equal(["project-a"], all.Requests.Select(r => r.Project));
        Assert.All(all.Requests, r => Assert.Equal("repo", r.SignalKind));

        var noRepo = ProjectContextRouter.Resolve(new ProjectContextSignals(null, Workflow, Chat), table);
        Assert.Equal(["project-b"], noRepo.Requests.Select(r => r.Project));

        var chatOnly = ProjectContextRouter.Resolve(new ProjectContextSignals(Chat: Chat), table);
        Assert.Equal(["project-c"], chatOnly.Requests.Select(r => r.Project));
    }

    [Fact]
    public void FallsThrough_OnlyWhenALevelHasZeroMatches()
    {
        // The repo signal is present but no route names it: that level matched nothing, so the
        // workflow level is consulted.
        var table = Table(["project-a", "project-b"], ["project-a", "project-b"],
            ("repo", "org/other", "project-a"), ("workflow", Workflow, "project-b"));

        var result = ProjectContextRouter.Resolve(new ProjectContextSignals(Repo, Workflow), table);

        Assert.Equal("workflow", result.SignalKind);
        Assert.Equal(["project-b"], result.Requests.Select(r => r.Project));
    }

    [Fact]
    public void WinningLevelWhoseMatchesAreAllEffectiveFull_RequestsNothing_AndDoesNotFallThrough()
    {
        // AC 12: repo → project-b (full), workflow → project-a (card). The repo level wins, requests
        // nothing because project-b is already resident, and project-a is NOT attached.
        var table = Table(["project-a", "project-b"], ["project-a"],
            ("repo", Repo, "project-b"), ("workflow", Workflow, "project-a"));

        var result = ProjectContextRouter.Resolve(new ProjectContextSignals(Repo, Workflow), table);

        Assert.Equal("repo", result.SignalKind);
        Assert.Equal(["project-b"], result.Matched);
        Assert.Empty(result.Requests);
        Assert.Equal(["project-b:full_mode"], result.Skipped.Select(s => s.ToString()));
    }

    [Fact]
    public void MixedModesAtTheWinningLevel_KeepOnlyCardProjects_OrderedByNameIgnoringCase()
    {
        var table = Table(["Project-C", "project-a", "project-b"], ["Project-C", "project-a"],
            ("chat", ChatValue, "Project-C"), ("chat", ChatValue, "project-b"), ("chat", ChatValue, "project-a"));

        var result = ProjectContextRouter.Resolve(new ProjectContextSignals(Chat: Chat), table);

        Assert.Equal(["project-a", "Project-C"], result.Requests.Select(r => r.Project));
        Assert.Equal(["project-b:full_mode"], result.Skipped.Select(s => s.ToString()));
        Assert.All(result.Requests, r => Assert.Equal(3, r.FullVersion));
    }

    [Fact]
    public void MoreThanThreeCardMatches_RequestNothing_AndReportRouteTooBroad()
    {
        string[] projects = ["project-a", "project-b", "project-c", "project-d"];
        var table = Table(projects, projects, projects.Select(p => ("workflow", Workflow, p)).ToArray());

        var result = ProjectContextRouter.Resolve(new ProjectContextSignals(Workflow: Workflow), table);

        Assert.Empty(result.Requests);
        Assert.True(result.TooBroad);
        Assert.Equal(projects.Select(p => $"{p}:route_too_broad"), result.Skipped.Select(s => s.ToString()));
    }

    [Fact]
    public void ExactlyThreeCardMatches_AreAllRequested()
    {
        string[] projects = ["project-c", "project-a", "project-b"];
        var table = Table(projects, projects, projects.Select(p => ("workflow", Workflow, p)).ToArray());

        var result = ProjectContextRouter.Resolve(new ProjectContextSignals(Workflow: Workflow), table);

        Assert.Equal(["project-a", "project-b", "project-c"], result.Requests.Select(r => r.Project));
    }

    [Fact]
    public void RouteTooBroad_IsLoggedAsAWarning()
    {
        string[] projects = ["project-a", "project-b", "project-c", "project-d"];
        var logger = new ConcurrentCapturingLogger<ProjectContextRouter>();
        var router = Router(Routing(projects, projects.Select(p => ("chat", ChatValue, p))), projects, logger);

        Assert.Empty(router.ResolveChat(Chat));
        Assert.Contains(logger.At(LogLevel.Warning), m => m.Contains("route_too_broad", StringComparison.Ordinal));
    }

    // ── case rules and assignment filter ────────────────────────────────────

    [Fact]
    public void RepoIsComparedLowerCase()
    {
        var table = Table(["project-a"], ["project-a"], ("repo", "Org/App", "project-a"));

        var result = ProjectContextRouter.Resolve(new ProjectContextSignals(Repo: "ORG/app"), table);

        Assert.Equal(["project-a"], result.Requests.Select(r => r.Project));
    }

    [Fact]
    public void ProjectNamesAreCaseInsensitive_AndRequestsUseTheAssignmentsOwnCasing()
    {
        // PromptBuilder reads projects/<assignment name>/, so the request must carry that casing
        // whatever the routing block wrote.
        var routing = Routing(["project-a"], [("chat", ChatValue, "PROJECT-A")],
            new Dictionary<string, string> { ["Project-A"] = "7" });
        Assert.True(ProjectContextRoutingTable.TryCreate(routing, ["Project-A"], out var table, out var error), error);

        var request = Assert.Single(ProjectContextRouter.Resolve(new ProjectContextSignals(Chat: Chat), table).Requests);

        Assert.Equal("Project-A", request.Project);
        Assert.Equal(7, request.FullVersion);
    }

    [Fact]
    public void RoutesForUnassignedProjects_AreIgnored_EvenWhenTheProjectIsListedAsACard()
    {
        // project-z is not assigned: its chat route is not a match, so the level has zero matches.
        var table = Table(["project-a"], ["project-a", "project-z"],
            ("chat", ChatValue, "project-z"), ("workflow", Workflow, "project-a"));

        Assert.Equal(ProjectContextRouteResult.NoMatch, ProjectContextRouter.Resolve(new ProjectContextSignals(Chat: Chat), table));
        Assert.Equal(["project-a"],
            ProjectContextRouter.Resolve(new ProjectContextSignals(Workflow: Workflow, Chat: Chat), table).Requests.Select(r => r.Project));
    }

    // ── relay signals ────────────────────────────────────────────────────────

    [Fact]
    public void Relay_UsesRepoThenLeadingWorkflowTag_AndHasNoChatSignal()
    {
        var router = Router(
            Routing(["project-a", "project-b", "project-c"],
                [("repo", Repo, "project-a"), ("workflow", Workflow, "project-b"), ("chat", ChatValue, "project-c")]),
            ["project-a", "project-b", "project-c"]);
        var directive = $"[fleet-wf:{Workflow}:{Workflow}-1]\nDo the step.";

        Assert.Equal(["project-a"], router.ResolveRelay(Repo, directive).Select(r => r.Project));
        Assert.Equal(["project-b"], router.ResolveRelay(null, directive).Select(r => r.Project));
        // No repo, no tag: nothing — the relay never offers its chat id, even though a chat route
        // exists for the chat such a directive would be posted to.
        Assert.Empty(router.ResolveRelay(null, "Do the step."));
    }

    [Fact]
    public void Relay_MalformedRepo_IsIgnoredWithAWarning_AndLowerLevelsStillApply()
    {
        var logger = new ConcurrentCapturingLogger<ProjectContextRouter>();
        var router = Router(Routing(["project-b"], [("workflow", Workflow, "project-b")]), ["project-b"], logger);

        var requests = router.ResolveRelay("not a repo", $"[fleet-wf:{Workflow}:1]\nx");

        Assert.Equal(["project-b"], requests.Select(r => r.Project));
        Assert.Contains(logger.At(LogLevel.Warning), m => m.Contains("malformed relay Repo", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("[fleet-wf:ExampleWorkflow:ExampleWorkflow-1]\ndo it", "ExampleWorkflow")]
    [InlineData("[fleet-wf:ExampleWorkflow:ExampleWorkflow-1]\r\ndo it", "ExampleWorkflow")]
    [InlineData("[fleet-wf:ExampleWorkflow:ExampleWorkflow-1]", "ExampleWorkflow")]
    [InlineData("do it\n[fleet-wf:ExampleWorkflow:ExampleWorkflow-1]", null)]
    [InlineData(" [fleet-wf:ExampleWorkflow:ExampleWorkflow-1]\ndo it", null)]
    [InlineData("[fleet-wf:ExampleWorkflow:ExampleWorkflow-1] do it", null)]
    [InlineData("[fleet-wf:ExampleWorkflow]\ndo it", null)]
    [InlineData("", null)]
    public void WorkflowSignal_IsOnlyALeadingTagLine(string text, string? expected) =>
        Assert.Equal(expected, ProjectContextRouter.ParseWorkflowType(text));

    // ── configuration ────────────────────────────────────────────────────────

    [Fact]
    public void AbsentBlock_DisablesTheRouterSilently()
    {
        var logger = new ConcurrentCapturingLogger<ProjectContextRouter>();
        var router = Router(null, ["project-a"], logger);

        Assert.False(router.IsEnabled);
        Assert.Empty(router.ResolveChat(Chat));
        Assert.Empty(router.ResolveRelay(Repo, $"[fleet-wf:{Workflow}:1]"));
        Assert.Empty(logger.Entries);
    }

    public static TheoryData<string, ProjectContextRoutingOptions> MalformedBlocks() => new()
    {
        { "unknown kind", Routing(["project-a"], [("branch", "main", "project-a")]) },
        { "blank project", Routing(["project-a"], [("chat", ChatValue, " ")]) },
        { "zero version", Routing(["project-a"], [], new Dictionary<string, string> { ["project-a"] = "0" }) },
        { "negative version", Routing(["project-a"], [], new Dictionary<string, string> { ["project-a"] = "-2" }) },
        { "non-numeric version", Routing(["project-a"], [], new Dictionary<string, string> { ["project-a"] = "three" }) },
        { "card without version", Routing(["project-a"], [], new Dictionary<string, string>()) },
        { "malformed repo", Routing(["project-a"], [("repo", "not a repo", "project-a")]) },
        { "malformed chat", Routing(["project-a"], [("chat", "general", "project-a")]) },
        { "path-like card project", Routing(["../project-a"], []) },
    };

    [Theory]
    [MemberData(nameof(MalformedBlocks))]
    public void MalformedBlock_DisablesTheRouter_WithExactlyOneWarning(string _, ProjectContextRoutingOptions routing)
    {
        var logger = new ConcurrentCapturingLogger<ProjectContextRouter>();
        var router = Router(routing, ["project-a"], logger);

        Assert.False(router.IsEnabled);
        Assert.Empty(router.ResolveChat(Chat));
        Assert.Single(logger.At(LogLevel.Warning));
        Assert.Contains("malformed", logger.At(LogLevel.Warning).Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PinnedJsonBlock_BindsThroughTheRegistrationGraph_AndRoutes()
    {
        // The exact key names the orchestrator writes (contract: cardProjects, fullVersions,
        // routes[{kind,value,project}]), bound by the real AddAgentCoreServices registration.
        var json = $$"""
            { "Agent": { "Name": "agent-a", "Role": "test", "WorkDir": "/tmp",
                "Projects": [ "project-a", "project-b" ],
                "ProjectContextRouting": {
                  "cardProjects": [ "project-a" ],
                  "fullVersions": { "project-a": 3 },
                  "routes": [
                    { "kind": "repo", "value": "{{Repo}}", "project": "project-b" },
                    { "kind": "chat", "value": "{{ChatValue}}", "project": "project-a" } ] } } }
            """;
        await using var provider = BuildCoreServices(json);

        var router = provider.GetRequiredService<ProjectContextRouter>();
        Assert.True(router.IsEnabled);
        var request = Assert.Single(router.ResolveChat(Chat));
        Assert.Equal(new ContextAttachmentRequest("project-a", 3, "chat"), request);
        Assert.Same(provider.GetRequiredService<ProjectContextAttacher>(), provider.GetRequiredService<ProjectContextAttacher>());
    }

    [Fact]
    public async Task NonNumericVersionInJson_DoesNotBreakOptionsBinding_ButDisablesTheRouter()
    {
        // A binder conversion failure would throw while AgentOptions is materialised and take the
        // whole agent down; the contract for a malformed block is "disabled + one Warning".
        var json = """
            { "Agent": { "Name": "agent-a", "Role": "test", "WorkDir": "/tmp", "Projects": [ "project-a" ],
                "ProjectContextRouting": { "cardProjects": [ "project-a" ], "fullVersions": { "project-a": "latest" }, "routes": [] } } }
            """;
        await using var provider = BuildCoreServices(json);

        Assert.Equal("agent-a", provider.GetRequiredService<IOptions<AgentOptions>>().Value.Name);
        Assert.False(provider.GetRequiredService<ProjectContextRouter>().IsEnabled);
    }

    [Fact]
    public async Task NoBlockInJson_LeavesTheOptionNull()
    {
        await using var provider = BuildCoreServices("""{ "Agent": { "Name": "agent-a", "Role": "test", "WorkDir": "/tmp" } }""");

        Assert.Null(provider.GetRequiredService<IOptions<AgentOptions>>().Value.ProjectContextRouting);
        Assert.False(provider.GetRequiredService<ProjectContextRouter>().IsEnabled);
    }

    private static ServiceProvider BuildCoreServices(string json)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentCoreServices(configuration);
        return services.BuildServiceProvider();
    }
}
