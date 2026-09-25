using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using static Fleet.Orchestrator.Tests.ProjectCardTestSupport;

namespace Fleet.Orchestrator.Tests.Tools;

/// <summary>
/// The #347 MCP admin surface: <c>get_project_card</c>, <c>update_project_card</c>,
/// <c>rollback_project_card</c>, <c>manage_project_routes</c>, and the keep-marker and card-line
/// additions to the full-context tools. The rules are shared with REST (ProjectCardService), so
/// these assert the tool wiring and text, not every rule again.
/// </summary>
public sealed class ProjectCardToolsTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private ProjectCardTools CardTools() => new(_db.ScopeFactory, _db.Logs.CreateLogger<ProjectCardTools>());
    private ProjectContextTools ContextTools() => new(_db.ScopeFactory, new PromptSizePolicy(10_000, 10_000, NullLogger.Instance));
    private ManageProjectRoutesTool RouteTool() => new(_db.ScopeFactory);

    private int Seed(string name, params string[] fullVersions)
    {
        using var db = _db.NewDb();
        return SeedContext(db, name, fullVersions).Id;
    }

    private int? CurrentCardVersion(string name)
    {
        using var db = _db.NewDb();
        return db.ProjectContexts.Single(p => p.Name == name).CurrentCardVersion;
    }

    // ── update_project_card ───────────────────────────────────────────────────

    [Fact]
    public async Task UpdateCard_MissingAKeep_IsAnErrorNamingIt_AndSavesNothing()
    {
        Seed("project-a", "Full.\n<!-- keep:x -->\n<!-- keep:y -->");

        var result = await CardTools().UpdateProjectCardAsync("project-a", "Card <!-- keep:y -->", 1, "draft", "agent-a");

        Assert.Contains("missing keep marker", result);
        Assert.Contains("x", result);
        Assert.Null(CurrentCardVersion("project-a"));
    }

    [Fact]
    public async Task UpdateCard_InvalidCandidate_IsAnErrorNamingItAndTheGrammar()
    {
        Seed("project-a", "Full.");

        var result = await CardTools().UpdateProjectCardAsync("project-a", "Card <!-- keep:x extra -->", 1, "draft", "agent-a");

        Assert.Contains("'<!-- keep:x extra -->'", result);
        Assert.Contains("<!-- keep:slug -->", result);
        Assert.Null(CurrentCardVersion("project-a"));
    }

    [Fact]
    public async Task UpdateCard_BasedOnOutOfRange_IsAnError()
    {
        Seed("project-a", "Full.");

        var result = await CardTools().UpdateProjectCardAsync("project-a", "Card.", 2, "draft", "agent-a");

        Assert.Contains("between 1 and 1", result);
        Assert.Null(CurrentCardVersion("project-a"));
    }

    [Fact]
    public async Task UpdateCard_ThenGet_ShowsVersionBasedOnAndStaleness()
    {
        Seed("project-a", "Full v1.", "Full v2 <!-- keep:x -->");

        var saved = await CardTools().UpdateProjectCardAsync("project-a", "Card body <!-- keep:x -->", 1, "first card", "agent-a");
        Assert.Contains("saved as v1", saved);
        Assert.Contains("stale", saved);

        var card = await CardTools().GetProjectCardAsync("PROJECT-A");
        Assert.Contains("## Project Card: project-a", card);
        Assert.Contains("Card version: 1", card);
        Assert.Contains("Written for full version: 1 (full is v2, stale)", card);
        Assert.Contains("Missing keeps: (none)", card);
        Assert.Contains("Card body <!-- keep:x -->", card);
        Assert.Contains("- v1 (current) for full v1", card);
        Assert.Contains("by agent-a — first card", card);
    }

    [Fact]
    public async Task GetCard_WithoutACard_SaysSo()
    {
        Seed("project-a", "Full.");

        Assert.Contains("has no card", await CardTools().GetProjectCardAsync("project-a"));
        Assert.Contains("not found", await CardTools().GetProjectCardAsync("project-b"));
    }

    // ── rollback_project_card ─────────────────────────────────────────────────

    [Fact]
    public async Task RollbackCard_ToInvalidContent_IsAllowed_AndWarns()
    {
        var id = Seed("project-a", "Full.");
        using (var db = _db.NewDb())
        {
            SeedCard(db, id, 1, "Old <!-- keep:Bad -->", 1, current: false);
            SeedCard(db, id, 2, "New.", 1);
        }

        var result = await CardTools().RollbackProjectCardAsync("project-a", 1);

        Assert.Contains("saved as v3", result);
        Assert.Contains("<!-- keep:Bad -->", result);
        Assert.Equal(3, CurrentCardVersion("project-a"));
        Assert.Contains(_db.Logs.Warnings, w => w.Contains("<!-- keep:Bad -->"));
        Assert.Contains("not found", await CardTools().RollbackProjectCardAsync("project-a", 7));
    }

    // ── manage_project_routes ─────────────────────────────────────────────────

    [Fact]
    public async Task Routes_AddListRemove()
    {
        Seed("project-a", "Full.");
        Seed("project-b", "Full.");
        var tool = RouteTool();

        Assert.Contains("added", await tool.ManageProjectRoutesAsync("add", "project-a", kind: "repo", value: "Org/App"));
        Assert.Contains("already exists", await tool.ManageProjectRoutesAsync("add", "project-a", kind: "repo", value: "org/app"));
        Assert.Contains("chat value must be", await tool.ManageProjectRoutesAsync("add", "project-a", kind: "chat", value: "abc"));
        Assert.Contains("added", await tool.ManageProjectRoutesAsync("add", "project-b", kind: "workflow", value: "ExampleWorkflow"));

        var list = await tool.ManageProjectRoutesAsync("list", "project-a");
        Assert.Contains("repo=org/app", list);
        Assert.DoesNotContain("ExampleWorkflow", list);

        int otherId;
        using (var db = _db.NewDb())
            otherId = db.ProjectContextRoutes.Single(r => r.SignalValue == "ExampleWorkflow").Id;

        Assert.Contains("not found", await tool.ManageProjectRoutesAsync("remove", "project-a", id: otherId));
        Assert.Contains("removed", await tool.ManageProjectRoutesAsync("remove", "project-b", id: otherId));
        Assert.Contains("has no routes", await tool.ManageProjectRoutesAsync("list", "project-b"));
        Assert.Contains("Unknown action", await tool.ManageProjectRoutesAsync("rename", "project-a"));
    }

    // ── Full-context tools ────────────────────────────────────────────────────

    [Fact]
    public async Task CreateContext_WithAnInvalidKeep_IsAnError_AndCreatesNothing()
    {
        var result = await ContextTools().CreateProjectContextAsync("project-a", "Full <!-- KEEP:x -->", "agent-a");

        Assert.Contains("'<!-- KEEP:x -->'", result);
        using var db = _db.NewDb();
        Assert.False(db.ProjectContexts.Any());
    }

    [Fact]
    public async Task UpdateContext_WithAnInvalidKeep_IsAnError_AndSavesNothing()
    {
        Seed("project-a", "Full v1.");

        var result = await ContextTools().UpdateProjectContextAsync("project-a", "Full v2 <!-- keep:Bad -->", "edit", "agent-a");

        Assert.Contains("'<!-- keep:Bad -->'", result);
        Assert.Contains("[a-z0-9][a-z0-9-]{0,63}", result);
        using var db = _db.NewDb();
        Assert.Equal(1, db.ProjectContexts.Single().CurrentVersion);
    }

    [Fact]
    public async Task UpdateContext_WithACard_AddsOneCardLine_WithoutACard_None()
    {
        var id = Seed("project-a", "Full v1.");
        Seed("project-b", "Full v1.");
        using (var db = _db.NewDb())
            SeedCard(db, id, 1, "Card.", 1);

        var withCard = await ContextTools().UpdateProjectContextAsync("project-a", "Full v2 <!-- keep:y -->", "edit", "agent-a");
        var withoutCard = await ContextTools().UpdateProjectContextAsync("project-b", "Full v2.", "edit", "agent-a");

        var cardLines = withCard.Split('\n').Where(l => l.StartsWith("Card:")).ToList();
        var line = Assert.Single(cardLines);
        Assert.Contains("v1 for full v1", line);
        Assert.Contains("stale (full is v2)", line);
        Assert.Contains("missing keeps: y", line);
        Assert.DoesNotContain("Card:", withoutCard);
    }

    [Fact]
    public async Task RollbackContext_ToInvalidContent_IsAllowed_AndWarns()
    {
        Seed("project-a", "Old <!-- keep:Bad -->", "Current.");

        var result = await ContextTools().RollbackProjectContextAsync("project-a", 1);

        Assert.Contains("saved as v3", result);
        Assert.Contains(_db.Logs.Warnings, w => w.Contains("<!-- keep:Bad -->") && w.Contains("project-a"));
    }

    [Fact]
    public async Task ListContexts_ShowsCardVersionStaleness_AndCardHolders()
    {
        var id = Seed("project-a", "Full v1.", "Full v2.");
        Seed("project-b", "Full v1.");
        using (var db = _db.NewDb())
        {
            SeedCard(db, id, 1, "Card.", 1);
            SeedAgent(db, "agent-a", ("project-a", ProjectContextMode.Card));
            SeedAgent(db, "agent-b", ("project-a", ProjectContextMode.Full), ("project-b", ProjectContextMode.Full));
        }

        var list = await ContextTools().ListProjectContextsAsync();

        var a = list.Split('\n').Single(l => l.Contains("**project-a**"));
        Assert.Contains("card v1 for full v1 (stale)", a);
        Assert.Contains("agents: agent-a (card), agent-b", a);
        var b = list.Split('\n').Single(l => l.Contains("**project-b**"));
        Assert.DoesNotContain("card", b);
    }
}
