using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Fleet.Orchestrator.Tests.Migrations;

/// <summary>
/// <c>OrchestratorDbContextModelSnapshot</c> must equal the latest migration's <c>BuildTargetModel</c>.
/// A snapshot left behind (it still declared <c>agent_projects.ContextMode</c> after
/// <c>RemoveProjectContextCards</c> dropped it, #346) makes the next <c>dotnet ef migrations add</c>
/// scaffold a spurious drop of a column that no longer exists. Needs no database.
/// </summary>
public sealed class ModelSnapshotTests
{
    [Fact]
    public void Snapshot_matches_latest_migration_target_model()
    {
        using var db = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseMySql("Server=127.0.0.1", new MySqlServerVersion(new Version(8, 0, 0)))
            .Options);
        var assembly = db.GetService<IMigrationsAssembly>();
        var latest = assembly.Migrations.Last();
        var target = assembly.CreateMigration(latest.Value, db.Database.ProviderName!).TargetModel;

        var differences = db.GetService<IMigrationsModelDiffer>().GetDifferences(
            Finalize(db, assembly.ModelSnapshot!.Model).GetRelationalModel(),
            Finalize(db, target!).GetRelationalModel());

        Assert.True(differences.Count == 0,
            $"Snapshot differs from {latest.Key}: " +
            string.Join(", ", differences.Select(d => d.GetType().Name)));
    }

    // The same finalisation EF's Migrator applies before it diffs a snapshot.
    private static IModel Finalize(DbContext db, IModel model)
    {
        if (model is IMutableModel mutable) model = mutable.FinalizeModel();
        return db.GetService<IModelRuntimeInitializer>().Initialize(model);
    }
}
