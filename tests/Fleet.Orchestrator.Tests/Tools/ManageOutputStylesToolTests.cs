using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleet.Orchestrator.Tests.Tools;

/// <summary>
/// <c>manage_output_styles</c> — action dispatch, the guards, and what it says (#317).
/// </summary>
/// <remarks>
/// <para>
/// The REST surface has its own tests, and both go through the same validator and the same usage
/// query, so what is worth asserting here is everything the tool decides for itself: which action
/// ran, which parameters each action requires, and the text it returns. That text is the whole
/// interface — an agent acts on the sentence, not on a status code, so a delete refusal that does
/// not name the agents, or an update that does not say a reprovision is needed, is a functional
/// defect rather than a wording preference.
/// </para>
/// <para>
/// SQLite rather than the InMemory provider: the tool writes and reads rows back through a
/// relational store, and the delete guard's meaning depends on how strings compare.
/// </para>
/// </remarks>
public class ManageOutputStylesToolTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly ManageOutputStylesTool _tool;

    public ManageOutputStylesToolTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<OrchestratorDbContext>(o => o.UseSqlite(_connection), ServiceLifetime.Scoped);
        _provider = services.BuildServiceProvider();

        using (var scope = _provider.CreateScope())
            scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Database.EnsureCreated();

        _tool = new ManageOutputStylesTool(_provider.GetRequiredService<IServiceScopeFactory>());
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private static string StyleFile(string name, string description, string body = "Write it plainly.") =>
        $"---\nname: {name}\ndescription: {description}\n---\n\n{body}\n";

    private void Seed(Action<OrchestratorDbContext> seed)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        seed(db);
        db.SaveChanges();
    }

    private void SeedStyle(string name, string body) => Seed(db => db.OutputStyles.Add(new OutputStyle
    {
        Name = name,
        Body = body,
        Description = OutputStyleRenderer.ReadDescription(body),
    }));

    private void SeedAgent(string name, string? style) => Seed(db => db.Agents.Add(new Agent
    {
        Name          = name,
        DisplayName   = name,
        Role          = "developer",
        Model         = "claude-sonnet-5",
        ContainerName = $"fleet-{name}",
        OutputStyle   = style,
    }));

    private OutputStyle? Read(string name)
    {
        using var scope = _provider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>()
            .OutputStyles.AsNoTracking().FirstOrDefault(s => s.Name == name);
    }

    // ── Dispatch ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task AMissingAction_IsReported(string? action)
    {
        var result = await _tool.ManageOutputStylesAsync(action!);
        Assert.Contains("missing required parameter 'action'", result);
    }

    [Fact]
    public async Task AnUnknownAction_ListsTheValidOnes()
    {
        var result = await _tool.ManageOutputStylesAsync("rename");
        Assert.Contains("Unknown action 'rename'", result);
        Assert.Contains("list, get, create, update, delete", result);
    }

    [Theory]
    [InlineData("LIST")]
    [InlineData("  list  ")]
    public async Task TheActionIsTrimmedAndCaseInsensitive(string action)
    {
        SeedStyle("alpha", StyleFile("alpha", "first style"));
        var result = await _tool.ManageOutputStylesAsync(action);
        Assert.Contains("alpha", result);
    }

    [Theory]
    [InlineData("get")]
    [InlineData("update")]
    [InlineData("delete")]
    public async Task ActionsThatNeedAName_SayWhichOneIsMissing(string action)
    {
        var result = await _tool.ManageOutputStylesAsync(action, name: "  ");
        Assert.Contains($"'{action}' action requires 'name' parameter", result);
    }

    // ── list ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_OnAnEmptyTable_SaysSo()
    {
        Assert.Equal("No output styles exist.", await _tool.ManageOutputStylesAsync("list"));
    }

    [Fact]
    public async Task List_MarksEachStyleUsedOrUnused()
    {
        SeedStyle("alpha", StyleFile("alpha", "first style"));
        SeedStyle("beta", StyleFile("beta", "second style"));
        SeedAgent("agent-one", "alpha");

        var result = await _tool.ManageOutputStylesAsync("list");

        Assert.Contains("alpha — first style [used by agent-one]", result);
        Assert.Contains("beta — second style [unused]", result);
    }

    // ── get ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_ReturnsTheBodyVerbatim_WithTheAgentsOnIt()
    {
        var body = StyleFile("alpha", "first style", "Lead with the answer.\nThen stop.");
        SeedStyle("alpha", body);
        SeedAgent("agent-one", "alpha");

        var result = await _tool.ManageOutputStylesAsync("get", "alpha");

        Assert.Contains("Used by: agent-one", result);
        Assert.Contains(body, result);
    }

    [Fact]
    public async Task Get_ForAnUnknownStyle_SaysNotFound()
    {
        Assert.Equal("Output style 'nope' not found.", await _tool.ManageOutputStylesAsync("get", "nope"));
    }

    // ── create ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_StoresTheBodyVerbatim_AndDerivesTheDescription()
    {
        var body = StyleFile("alpha", "keep it terse", "Write «plainly».\r\nSecond line.");

        var result = await _tool.ManageOutputStylesAsync("create", "alpha", body);

        Assert.Contains("Created output style 'alpha'", result);
        // The reprovision hint is not decoration: a created style reaches no agent until then.
        Assert.Contains("reprovision", result, StringComparison.OrdinalIgnoreCase);

        var stored = Read("alpha");
        Assert.NotNull(stored);
        Assert.Equal(body, stored!.Body);
        Assert.Equal("keep it terse", stored.Description);
    }

    [Fact]
    public async Task Create_TrimsTheName_SoTheRowMatchesTheFrontmatter()
    {
        var result = await _tool.ManageOutputStylesAsync("create", "  alpha  ", StyleFile("alpha", "first style"));

        Assert.Contains("Created output style 'alpha'", result);
        Assert.NotNull(Read("alpha"));
    }

    [Fact]
    public async Task Create_OverAnExistingName_RefusesAndPointsAtUpdate()
    {
        var original = StyleFile("alpha", "first style", "Original body.");
        SeedStyle("alpha", original);

        var result = await _tool.ManageOutputStylesAsync(
            "create", "alpha", StyleFile("alpha", "second style", "Replacement body."));

        Assert.Contains("already exists", result);
        Assert.Contains("action 'update'", result);
        Assert.Equal(original, Read("alpha")!.Body);
    }

    [Fact]
    public async Task Create_ValidatesTheNameAsWellAsTheBody()
    {
        // ValidateBody would accept this; Validate is what also rejects the name. The split is
        // the point: update cannot revalidate the name, so create must.
        var result = await _tool.ManageOutputStylesAsync("create", "../escape", StyleFile("../escape", "x"));

        Assert.StartsWith("manage_output_styles:", result);
        Assert.Null(Read("../escape"));
    }

    [Fact]
    public async Task Create_WithNoBody_IsRefused()
    {
        var result = await _tool.ManageOutputStylesAsync("create", "alpha");

        Assert.StartsWith("manage_output_styles:", result);
        Assert.Null(Read("alpha"));
    }

    [Fact]
    public async Task Create_WithAFrontmatterNameThatDisagrees_IsRefused()
    {
        // Claude Code matches on the frontmatter name, so this stores a style that silently does
        // not load while every artifact reports it as present.
        var result = await _tool.ManageOutputStylesAsync("create", "alpha", StyleFile("not-alpha", "x"));

        Assert.StartsWith("manage_output_styles:", result);
        Assert.Null(Read("alpha"));
    }

    // ── update ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_ReplacesTheBody_AndNamesTheAgentsToReprovision()
    {
        SeedStyle("alpha", StyleFile("alpha", "first style", "Original body."));
        SeedAgent("agent-one", "alpha");
        SeedAgent("agent-two", "alpha");

        var edited = StyleFile("alpha", "edited description", "Edited body.");
        var result = await _tool.ManageOutputStylesAsync("update", "alpha", edited);

        Assert.Contains("provision time", result);
        Assert.Contains("agent-one", result);
        Assert.Contains("agent-two", result);

        var stored = Read("alpha")!;
        Assert.Equal(edited, stored.Body);
        Assert.Equal("edited description", stored.Description);
    }

    [Fact]
    public async Task Update_OfAnUnassignedStyle_SaysNoAgentIsOnIt()
    {
        SeedStyle("alpha", StyleFile("alpha", "first style"));

        var result = await _tool.ManageOutputStylesAsync("update", "alpha", StyleFile("alpha", "edited"));

        Assert.Contains("No agent is assigned to it", result);
    }

    [Fact]
    public async Task Update_ValidatesAgainstTheStoredName_NotTheSuppliedFrontmatter()
    {
        // The name is not editable, so a body whose frontmatter renames the style is a style that
        // would stop loading — refuse it rather than storing a row that disagrees with itself.
        SeedStyle("alpha", StyleFile("alpha", "first style", "Original body."));

        var result = await _tool.ManageOutputStylesAsync("update", "alpha", StyleFile("renamed", "x"));

        Assert.StartsWith("manage_output_styles:", result);
        Assert.Contains("Original body.", Read("alpha")!.Body);
    }

    [Fact]
    public async Task Update_OfAnUnknownStyle_PointsAtCreate()
    {
        var result = await _tool.ManageOutputStylesAsync("update", "nope", StyleFile("nope", "x"));

        Assert.Contains("not found", result);
        Assert.Contains("action 'create'", result);
    }

    // ── delete ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_AnUnreferencedStyle_RemovesIt()
    {
        SeedStyle("alpha", StyleFile("alpha", "first style"));

        Assert.Equal("Deleted output style 'alpha'.", await _tool.ManageOutputStylesAsync("delete", "alpha"));
        Assert.Null(Read("alpha"));
    }

    [Fact]
    public async Task Delete_AReferencedStyle_RefusesAndNamesEveryAgent()
    {
        SeedStyle("alpha", StyleFile("alpha", "first style"));
        SeedAgent("agent-one", "alpha");
        SeedAgent("agent-two", "alpha");

        var result = await _tool.ManageOutputStylesAsync("delete", "alpha");

        Assert.Contains("Refusing to delete", result);
        Assert.Contains("2 agent(s)", result);
        Assert.Contains("agent-one", result);
        Assert.Contains("agent-two", result);

        // The refusal is worthless if the row went anyway.
        Assert.NotNull(Read("alpha"));
    }

    [Fact]
    public async Task Delete_ByACaseVariantOfAnAssignedName_IsStillRefused()
    {
        // Case sensitivity is the store's to decide, so a guard written as a WHERE predicate would
        // refuse on MySQL and allow here. Grouping in OutputStyleUsage is what makes both agree.
        SeedStyle("alpha", StyleFile("alpha", "first style"));
        SeedAgent("agent-one", "ALPHA");

        Assert.Contains("Refusing to delete", await _tool.ManageOutputStylesAsync("delete", "alpha"));
        Assert.NotNull(Read("alpha"));
    }

    [Fact]
    public async Task Delete_OfAnUnknownStyle_SaysNotFound()
    {
        Assert.Equal("Output style 'nope' not found.", await _tool.ManageOutputStylesAsync("delete", "nope"));
    }
}
