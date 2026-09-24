using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Tools;
using Microsoft.EntityFrameworkCore;
using static Fleet.Orchestrator.Tests.ProjectCardTestSupport;

namespace Fleet.Orchestrator.Tests.Tools;

/// <summary>
/// Per-assignment context modes (#347) on both write surfaces: <c>update_agent_config</c>, and
/// <see cref="AgentProjectModes.UpdateAssignmentsAsync"/> — the exact step
/// <c>PUT /api/agents/{name}/config</c> runs (its 400 is that method's error, returned before
/// SaveChanges). Plus the <c>get_agent_config</c> display and the ACL non-effect of a mode change.
/// </summary>
public sealed class UpdateAgentConfigProjectModesTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private sealed class CountingNotifier : IAclChangeNotifier
    {
        public int Calls { get; private set; }

        public Task PublishAclChangedAsync(CancellationToken ct = default)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    /// <summary>project-a has a card (v2, for full v1); project-b and project-c have none.</summary>
    private void SeedProjects()
    {
        using var db = _db.NewDb();
        var a = SeedContext(db, "project-a", "Full v1.", "Full v2.");
        SeedCard(db, a.Id, 1, "Card v1.", 1, current: false);
        SeedCard(db, a.Id, 2, "Card v2.", 1);
        SeedContext(db, "project-b", "Full v1.");
        SeedContext(db, "project-c", "Full v1.");
    }

    private void SeedAgentWithAccess(string name, params (string Project, string Mode)[] projects)
    {
        using var db = _db.NewDb();
        SeedAgent(db, name, projects);
        foreach (var (project, _) in projects)
            db.AgentProjectAccess.Add(new AgentProjectAccess { AgentName = name, Project = project, Source = AgentProjectAccessSource.Assignment });
        db.SaveChanges();
    }

    private Dictionary<string, string> Modes(string agent)
    {
        using var db = _db.NewDb();
        return db.AgentProjects
            .Where(p => p.Agent.Name == agent)
            .ToDictionary(p => p.ProjectName, p => p.ContextMode);
    }

    private List<string> AccessSnapshot()
    {
        using var db = _db.NewDb();
        return db.AgentProjectAccess
            .AsEnumerable()
            .Select(x => $"{x.AgentName}|{x.Project}|{x.Source}")
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    // ── update_agent_config (MCP) ─────────────────────────────────────────────

    [Fact]
    public async Task Mcp_ProjectsReplace_PreservesModes_IncludingACaseVariantName()
    {
        SeedProjects();
        SeedAgentWithAccess("agent-a", ("project-a", ProjectContextMode.Card), ("project-b", ProjectContextMode.Full));
        var tool = new UpdateAgentConfigTool(_db.ScopeFactory, new CountingNotifier());

        var result = await tool.UpdateAgentConfigAsync("agent-a", projects: "PROJECT-A,project-b,project-c");

        Assert.Contains("projects replaced (3 projects)", result);
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["PROJECT-A"] = ProjectContextMode.Card,
                ["project-b"] = ProjectContextMode.Full,
                ["project-c"] = ProjectContextMode.Full,
            },
            Modes("agent-a"));
    }

    [Fact]
    public async Task Mcp_CardWithoutACard_IsAnError_AndNothingIsSaved()
    {
        SeedProjects();
        SeedAgentWithAccess("agent-a", ("project-b", ProjectContextMode.Full));
        var notifier = new CountingNotifier();
        var tool = new UpdateAgentConfigTool(_db.ScopeFactory, notifier);

        var result = await tool.UpdateAgentConfigAsync("agent-a", model: "claude-opus-5", projects: "project-b,project-c", project_modes: "project-b=card");

        Assert.Contains("Invalid project_modes", result);
        Assert.Contains("has no card", result);
        Assert.Contains("Nothing was saved", result);
        Assert.Equal(new Dictionary<string, string> { ["project-b"] = ProjectContextMode.Full }, Modes("agent-a"));
        using var db = _db.NewDb();
        Assert.Equal("claude-sonnet-5", db.Agents.Single(a => a.Name == "agent-a").Model);
        Assert.Equal(0, notifier.Calls);
    }

    [Theory]
    [InlineData("project-z=card", "not assigned")]
    [InlineData("project-a=partial", "must be 'full' or 'card'")]
    [InlineData("project-a", "must be name=mode")]
    [InlineData("project-a=card,PROJECT-A=full", "more than once")]
    public async Task Mcp_InvalidProjectModes_AreErrors(string modes, string expected)
    {
        SeedProjects();
        SeedAgentWithAccess("agent-a", ("project-a", ProjectContextMode.Full));
        var tool = new UpdateAgentConfigTool(_db.ScopeFactory, new CountingNotifier());

        var result = await tool.UpdateAgentConfigAsync("agent-a", project_modes: modes);

        Assert.Contains(expected, result);
        Assert.Equal(ProjectContextMode.Full, Modes("agent-a")["project-a"]);
    }

    [Fact]
    public async Task Mcp_ModesApplyToTheResultingAssignments()
    {
        SeedProjects();
        SeedAgentWithAccess("agent-a", ("project-b", ProjectContextMode.Full));
        var tool = new UpdateAgentConfigTool(_db.ScopeFactory, new CountingNotifier());

        var result = await tool.UpdateAgentConfigAsync("agent-a", projects: "project-b,project-a", project_modes: "Project-A=CARD");

        Assert.Contains("project mode project-a: full → card", result);
        Assert.Contains("reprovision", result);
        Assert.Equal(ProjectContextMode.Card, Modes("agent-a")["project-a"]);
    }

    /// <summary>
    /// AC 14. A mode change is not an ACL change: with project_modes alone the hook is not called,
    /// and with the same projects list it is called with the same name set — which, per
    /// <see cref="AgentProjectAccessSync.SyncAndBroadcastAsync"/>, stages nothing and so saves and
    /// publishes nothing.
    /// </summary>
    [Fact]
    public async Task Mcp_ModeOnlyChange_LeavesAccessRowsAlone_AndPublishesNothing()
    {
        SeedProjects();
        SeedAgentWithAccess("agent-a", ("project-a", ProjectContextMode.Full), ("project-b", ProjectContextMode.Full));
        var before = AccessSnapshot();
        var notifier = new CountingNotifier();
        var tool = new UpdateAgentConfigTool(_db.ScopeFactory, notifier);

        var alone = await tool.UpdateAgentConfigAsync("agent-a", project_modes: "project-a=card");
        var withSameList = await tool.UpdateAgentConfigAsync("agent-a", projects: "project-a,project-b", project_modes: "project-a=full");

        Assert.Contains("full → card", alone);
        Assert.Contains("card → full", withSameList);
        Assert.Equal(before, AccessSnapshot());
        Assert.Equal(0, notifier.Calls);
    }

    // ── The REST PUT step ─────────────────────────────────────────────────────

    [Fact]
    public async Task Rest_ProjectsReplace_PreservesModes_IncludingACaseVariantName()
    {
        SeedProjects();
        SeedAgentWithAccess("agent-a", ("project-a", ProjectContextMode.Card), ("project-b", ProjectContextMode.Full));

        using (var db = _db.NewDb())
        {
            var agent = await db.Agents.Include(a => a.Projects).SingleAsync(a => a.Name == "agent-a");
            var error = await AgentProjectModes.UpdateAssignmentsAsync(db, agent, ["Project-A", "project-b", "project-c"], null);
            Assert.Null(error);
            await db.SaveChangesAsync();
        }

        Assert.Equal(
            new Dictionary<string, string>
            {
                ["Project-A"] = ProjectContextMode.Card,
                ["project-b"] = ProjectContextMode.Full,
                ["project-c"] = ProjectContextMode.Full,
            },
            Modes("agent-a"));
    }

    [Fact]
    public async Task Rest_CardWithoutACard_IsAnError_AndTheRequestSavesNothing()
    {
        SeedProjects();
        SeedAgentWithAccess("agent-a", ("project-a", ProjectContextMode.Full));

        using (var db = _db.NewDb())
        {
            var agent = await db.Agents.Include(a => a.Projects).SingleAsync(a => a.Name == "agent-a");
            var error = await AgentProjectModes.UpdateAssignmentsAsync(
                db, agent, ["project-a", "project-c"], new Dictionary<string, string> { ["project-c"] = "card" });

            // The handler returns 400 { error } here, before SaveChanges.
            Assert.NotNull(error);
            Assert.Contains("project-c", error);
            Assert.Contains("has no card", error);
        }

        Assert.Equal(new Dictionary<string, string> { ["project-a"] = ProjectContextMode.Full }, Modes("agent-a"));
    }

    [Fact]
    public async Task Rest_ModeKeyOutsideTheResultingAssignments_IsAnError()
    {
        SeedProjects();
        SeedAgentWithAccess("agent-a", ("project-a", ProjectContextMode.Full));

        using var db = _db.NewDb();
        var agent = await db.Agents.Include(a => a.Projects).SingleAsync(a => a.Name == "agent-a");

        // project-a is being dropped by the same request, so it is not in the RESULTING assignments.
        var error = await AgentProjectModes.UpdateAssignmentsAsync(
            db, agent, ["project-b"], new Dictionary<string, string> { ["project-a"] = "card" });

        Assert.NotNull(error);
        Assert.Contains("not assigned", error);
    }

    [Fact]
    public async Task Rest_ModeOnlyChange_ThenTheSameNameSetSync_StagesNothing_AndPublishesNothing()
    {
        SeedProjects();
        SeedAgentWithAccess("agent-a", ("project-a", ProjectContextMode.Full), ("project-b", ProjectContextMode.Full));
        var before = AccessSnapshot();
        var notifier = new CountingNotifier();

        using (var db = _db.NewDb())
        {
            var agent = await db.Agents.Include(a => a.Projects).SingleAsync(a => a.Name == "agent-a");
            Assert.Null(await AgentProjectModes.UpdateAssignmentsAsync(
                db, agent, ["project-a", "project-b"], new Dictionary<string, string> { ["project-a"] = "card" }));
            await db.SaveChangesAsync();

            var changed = await AgentProjectAccessSync.SyncAndBroadcastAsync(
                db, notifier, agent.Name, agent.Projects.Select(p => p.ProjectName));
            Assert.False(changed);
        }

        Assert.Equal(ProjectContextMode.Card, Modes("agent-a")["project-a"]);
        Assert.Equal(before, AccessSnapshot());
        Assert.Equal(0, notifier.Calls);
    }

    // ── get_agent_config ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetAgentConfig_PrintsFullRowsAsBefore_AndCardRowsWithVersions()
    {
        SeedProjects();
        SeedAgentWithAccess("agent-a", ("project-a", ProjectContextMode.Card), ("project-b", ProjectContextMode.Full));

        var text = await new GetAgentConfigTool(_db.ScopeFactory).GetAgentConfigAsync("agent-a");

        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        Assert.Contains("- project-a (card v2, for full v1)", lines);
        Assert.Contains("- project-b", lines);
    }
}
