using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Tests.Services;
using Fleet.Orchestrator.Tools;

namespace Fleet.Orchestrator.Tests.Tools;

/// <summary>
/// The #346 <c>Size:</c> lines on <c>create_project_context</c>, <c>update_project_context</c> and
/// <c>rollback_project_context</c>. Today's full success output — the optional <c>Card:</c> line
/// included — is an exact prefix; errors get no <c>Size:</c> line.
/// </summary>
public sealed class ProjectContextToolsSizeTests : IDisposable
{
    private readonly SizeToolsDb _db = new();
    private readonly SizeLogCapture _logs = new();

    public void Dispose() => _db.Dispose();

    private ProjectContextTools Tools() => new(_db.ScopeFactory, new PromptSizePolicy(10_000, 10_000, _logs));

    [Fact]
    public async Task Create_appends_the_size_line()
    {
        var result = await Tools().CreateProjectContextAsync("row-a", "дд", "agent-a");

        Assert.Equal(
            "Project context 'row-a' created at v1." +
            "\nSize: 4 UTF-8 bytes; project-context soft limit 10,000 (PromptSizeWarnings:ProjectContextBytes).",
            result);
    }

    [Fact]
    public async Task Update_and_rollback_keep_their_output_as_a_prefix()
    {
        await Tools().CreateProjectContextAsync("row-a", "abc", "agent-a");

        var update = await Tools().UpdateProjectContextAsync("row-a", new string('д', 5_001), "edit", "agent-a");
        Assert.StartsWith("Project context 'row-a' updated to v2.\nSize: 3 → 10,002 UTF-8 bytes; project-context soft limit 10,000 (PromptSizeWarnings:ProjectContextBytes).\nSize warning: Project context 'row-a' is 10,002 UTF-8 bytes, 2 over", update);

        var rollback = await Tools().RollbackProjectContextAsync("row-a", 1);
        Assert.Equal(
            "Project context 'row-a' rolled back to v1 content — saved as v3." +
            "\nSize: 10,002 → 3 UTF-8 bytes; project-context soft limit 10,000 (PromptSizeWarnings:ProjectContextBytes).",
            rollback);
    }

    [Fact]
    public async Task Card_line_stays_before_the_size_lines()
    {
        await Tools().CreateProjectContextAsync("row-a", "Full v1", "agent-a");
        using (var db = _db.NewDb())
        {
            var ctx = db.ProjectContexts.Single(p => p.Name == "row-a");
            db.ProjectContextCardVersions.Add(new ProjectContextCardVersion
            {
                ProjectContextId = ctx.Id, VersionNumber = 1, Content = "Card v1", BasedOnFullVersion = 1, CreatedBy = "test",
            });
            ctx.CurrentCardVersion = 1;
            db.SaveChanges();
        }

        var result = await Tools().UpdateProjectContextAsync("row-a", new string('a', 10_001), "edit", "agent-a");

        var lines = result.Split('\n');
        Assert.Equal("Project context 'row-a' updated to v2.", lines[0]);
        Assert.StartsWith("Card: v1 for full v1 — stale (full is v2)", lines[1]);
        Assert.StartsWith("Size: 7 → 10,001 UTF-8 bytes;", lines[2]);
        Assert.StartsWith("Size warning: ", lines[3]);
        Assert.Equal(4, lines.Length);
    }

    [Fact]
    public async Task Errors_have_no_size_line()
    {
        Assert.Equal("Project context 'no-such-row' not found.", await Tools().UpdateProjectContextAsync("no-such-row", "abc", "edit", "agent-a"));
        Assert.Equal("name must contain only letters, digits, hyphens, or underscores.", await Tools().CreateProjectContextAsync("bad name", "abc", "agent-a"));

        await Tools().CreateProjectContextAsync("row-a", "abc", "agent-a");
        Assert.Equal("Project context 'row-a' already exists. Use update_project_context to add a new version.",
            await Tools().CreateProjectContextAsync("row-a", "abc", "agent-a"));
        Assert.Equal("Version 5 does not exist for project context 'row-a'.", await Tools().RollbackProjectContextAsync("row-a", 5));

        var invalidKeep = await Tools().UpdateProjectContextAsync("row-a", "Full <!-- keep:Bad -->", "edit", "agent-a");
        Assert.StartsWith("Project context rejected,", invalidKeep);
        Assert.DoesNotContain("Size:", invalidKeep);
    }
}
