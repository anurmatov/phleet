using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Tests.Services;

/// <summary>
/// The provenance rules for <c>agent_project_access</c> (#311).
/// </summary>
/// <remarks>
/// <para>
/// Assigning a project to an agent is what grants it memory read access, and unassigning it is what
/// revokes the grant — but only the grant the assignment hook itself created. The whole reason the
/// <c>Source</c> column exists is that revoking an operator's hand-made row is a worse failure than
/// the stale row it would fix, so most of what is asserted here is what the hook must NOT touch.
/// </para>
/// <para>
/// SQLite rather than the InMemory provider, because these tests care about rows actually being
/// written and read back through a relational store with the column's default applied.
/// </para>
/// </remarks>
public class AgentProjectAccessSyncTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<OrchestratorDbContext> _options;

    public AgentProjectAccessSyncTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var ctx = new OrchestratorDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private OrchestratorDbContext NewContext() => new(_options);

    private void Seed(params AgentProjectAccess[] rows)
    {
        using var ctx = NewContext();
        ctx.AgentProjectAccess.AddRange(rows);
        ctx.SaveChanges();
    }

    private static AgentProjectAccess Row(string agent, string project, string source) =>
        new() { AgentName = agent, Project = project, Source = source };

    private List<AgentProjectAccess> RowsFor(string agent)
    {
        using var ctx = NewContext();
        return ctx.AgentProjectAccess
            .Where(x => x.AgentName == agent)
            .OrderBy(x => x.Project)
            .ToList();
    }

    /// <summary>Counts <c>PublishAclChangedAsync</c> calls so "the broadcast fired" is assertable.</summary>
    private sealed class CountingNotifier : IAclChangeNotifier
    {
        public int Calls { get; private set; }

        public Task PublishAclChangedAsync(CancellationToken ct = default)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    // ── Grant on assignment ──────────────────────────────────────────────────

    /// <summary>Assignment alone is the grant — nothing else has to be remembered.</summary>
    [Fact]
    public async Task Assigning_A_Project_Writes_An_Assignment_Sourced_Row()
    {
        await using (var db = NewContext())
        {
            Assert.True(await AgentProjectAccessSync.StageAssignmentsAsync(db, "adev", ["fleet"]));
            await db.SaveChangesAsync();
        }

        var row = Assert.Single(RowsFor("adev"));
        Assert.Equal("fleet", row.Project);
        Assert.Equal(AgentProjectAccessSource.Assignment, row.Source);
    }

    /// <summary>Names are stored lowercase and trimmed, matching every other writer of this table.</summary>
    [Fact]
    public async Task Assignments_Are_Normalized_To_Lowercase_And_Trimmed()
    {
        await StageAndSave("  ADev ", ["  Fleet  ", "MML"]);

        Assert.Equal(["fleet", "mml"], RowsFor("adev").Select(r => r.Project));
    }

    /// <summary>Re-running with an unchanged assignment list stages nothing, so nothing is broadcast.</summary>
    [Fact]
    public async Task Unchanged_Assignments_Stage_Nothing()
    {
        await StageAndSave("adev", ["fleet"]);

        await using var db = NewContext();
        Assert.False(await AgentProjectAccessSync.StageAssignmentsAsync(db, "adev", ["fleet"]));
    }

    // ── Revoke on unassignment ───────────────────────────────────────────────

    /// <summary>The row the hook created is the row the hook may remove.</summary>
    [Fact]
    public async Task Unassigning_Removes_The_Assignment_Sourced_Row()
    {
        await StageAndSave("adev", ["fleet", "mml"]);

        await StageAndSave("adev", ["fleet"]);

        var row = Assert.Single(RowsFor("adev"));
        Assert.Equal("fleet", row.Project);
    }

    /// <summary>
    /// The failure this provenance exists to prevent: an operator's hand-added grant surviving an
    /// unassignment of the same (agent, project) pair.
    /// </summary>
    [Fact]
    public async Task Unassigning_Leaves_A_Manual_Row_For_The_Same_Pair_In_Place()
    {
        Seed(Row("adev", "fuddyduddy", AgentProjectAccessSource.Manual));

        await StageAndSave("adev", ["fuddyduddy"]);   // assignment added on top of the manual row
        await StageAndSave("adev", []);               // then unassigned

        var row = Assert.Single(RowsFor("adev"));
        Assert.Equal("fuddyduddy", row.Project);
        Assert.Equal(AgentProjectAccessSource.Manual, row.Source);
    }

    /// <summary>
    /// A manual row is never rewritten to <c>assignment</c>. If it were, the next unassignment
    /// would revoke it — the same failure, arriving one step later.
    /// </summary>
    [Fact]
    public async Task Assigning_A_Project_That_Already_Has_A_Manual_Row_Does_Not_Downgrade_It()
    {
        Seed(Row("adev", "fleet", AgentProjectAccessSource.Manual));

        await using var db = NewContext();
        Assert.False(await AgentProjectAccessSync.StageAssignmentsAsync(db, "adev", ["fleet"]));

        Assert.Equal(AgentProjectAccessSource.Manual, Assert.Single(RowsFor("adev")).Source);
    }

    /// <summary>The wildcard is an operator grant with no assignment behind it.</summary>
    [Fact]
    public async Task Wildcard_Rows_Are_Never_Removed()
    {
        Seed(Row("acto", "*", AgentProjectAccessSource.Manual));
        await StageAndSave("acto", ["fleet"]);

        await StageAndSave("acto", []);

        var row = Assert.Single(RowsFor("acto"));
        Assert.Equal("*", row.Project);
    }

    /// <summary>
    /// A wildcard row that somehow carries <c>assignment</c> provenance is still off limits — the
    /// guard is on the project token, not only on the source, because a cross-project grant is the
    /// most expensive row in the table to lose or to keep by accident.
    /// </summary>
    [Fact]
    public async Task A_Wildcard_Row_Is_Skipped_Even_When_Its_Source_Says_Assignment()
    {
        Seed(Row("acto", "*", AgentProjectAccessSource.Assignment));

        await using var db = NewContext();
        Assert.False(await AgentProjectAccessSync.StageAssignmentsAsync(db, "acto", []));

        Assert.Single(RowsFor("acto"));
    }

    /// <summary>A project literally named <c>*</c> must not manufacture a cross-project grant.</summary>
    [Fact]
    public async Task An_Assignment_Named_Wildcard_Is_Dropped_Rather_Than_Granted()
    {
        await using var db = NewContext();
        Assert.False(await AgentProjectAccessSync.StageAssignmentsAsync(db, "adev", ["*"]));

        Assert.Empty(RowsFor("adev"));
    }

    /// <summary>One agent's assignments never reach another agent's rows.</summary>
    [Fact]
    public async Task Sync_Only_Touches_The_Named_Agent()
    {
        await StageAndSave("aops", ["fleet"]);

        await StageAndSave("adev", ["mml"]);

        Assert.Equal("fleet", Assert.Single(RowsFor("aops")).Project);
        Assert.Equal("mml", Assert.Single(RowsFor("adev")).Project);
    }

    // ── Broadcast ────────────────────────────────────────────────────────────

    /// <summary>
    /// A grant broadcasts. Without it fleet-memory keeps serving its cached ACL for up to five
    /// minutes, and a freshly assigned agent reads 403 the whole time — indistinguishable at the
    /// agent from the missing row this feature exists to stop producing.
    /// </summary>
    [Fact]
    public async Task A_Grant_Broadcasts_Config_Changed()
    {
        var notifier = new CountingNotifier();

        await using var db = NewContext();
        Assert.True(await AgentProjectAccessSync.SyncAndBroadcastAsync(db, notifier, "adev", ["fleet"]));

        Assert.Equal(1, notifier.Calls);
    }

    /// <summary>A revoke broadcasts for the same reason a grant does.</summary>
    [Fact]
    public async Task A_Revoke_Broadcasts_Config_Changed()
    {
        await StageAndSave("adev", ["fleet"]);
        var notifier = new CountingNotifier();

        await using var db = NewContext();
        Assert.True(await AgentProjectAccessSync.SyncAndBroadcastAsync(db, notifier, "adev", []));

        Assert.Equal(1, notifier.Calls);
        Assert.Empty(RowsFor("adev"));
    }

    /// <summary>
    /// A no-op write publishes nothing. Every agent config save passes through here, so publishing
    /// unconditionally would make fleet-memory refetch the whole ACL on saves that changed no row.
    /// </summary>
    [Fact]
    public async Task A_No_Op_Sync_Does_Not_Broadcast()
    {
        await StageAndSave("adev", ["fleet"]);
        var notifier = new CountingNotifier();

        await using var db = NewContext();
        Assert.False(await AgentProjectAccessSync.SyncAndBroadcastAsync(db, notifier, "adev", ["fleet"]));

        Assert.Equal(0, notifier.Calls);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task StageAndSave(string agent, string[] projects)
    {
        await using var db = NewContext();
        if (await AgentProjectAccessSync.StageAssignmentsAsync(db, agent, projects))
            await db.SaveChangesAsync();
    }
}
