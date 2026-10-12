using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Tests.EpicGrants;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Tests.EpicDrivers;

public class EpicGrantDryRunTests
{
    [Fact]
    public async Task DryRun_D1ThroughD5Pass_RefusesBeforeGitHubReservationOrSignal()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>().UseSqlite(connection).Options;
        await using var db = new OrchestratorDbContext(options); await db.Database.EnsureCreatedAsync();
        var world = new EpicGrantWorld(() => new OrchestratorDbContext(options)); await world.InitializeAsync();
        world.GitHub.Calls.Clear();
        var result = await world.Service(db).DryRunDecideAsync(world.GrantId, EpicGrantWorld.Request(), default);
        Assert.Equal("visibility_unknown", result.Reason); Assert.Empty(world.GitHub.Calls);
        Assert.Empty(world.Temporal.Signals); Assert.Empty(await db.EpicGrantDecisions.ToListAsync());
        world.Options.Enabled = false;
        Assert.Equal("disabled", (await world.Service(db).DryRunDecideAsync(world.GrantId, EpicGrantWorld.Request(), default)).Reason);
    }
}
