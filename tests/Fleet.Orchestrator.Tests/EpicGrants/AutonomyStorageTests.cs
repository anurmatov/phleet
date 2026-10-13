using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Fleet.Orchestrator.Tests.EpicGrants;

public sealed class AutonomyStorageTests
{
    [Fact]
    public void Charter_ModelHasLegacyDefaultAndProtectedEvidenceColumns()
    {
        using var db = new OrchestratorDbContextFactory().CreateDbContext([]);
        var grant = db.Model.FindEntityType(typeof(EpicGrant))!;
        Assert.Equal("delegated", grant.FindProperty(nameof(EpicGrant.Mode))!.GetDefaultValue());
        Assert.Equal("longtext", grant.FindProperty(nameof(EpicGrant.PreflightJson))!.GetColumnType());
        Assert.True(grant.FindProperty(nameof(EpicGrant.PreflightSha256))!.IsFixedLength());
        Assert.Equal(64, grant.FindProperty(nameof(EpicGrant.PreflightSha256))!.GetMaxLength());
    }

    [Theory]
    [InlineData(typeof(EpicGrantBinding), "epic_grant_bindings", "GrantId,Repo,Issue")]
    [InlineData(typeof(EpicGrantRun), "epic_grant_runs", "GrantId,Namespace,WorkflowId,RunId")]
    public void Charter_RecordsHaveUniqueIdentityAndRestrictGrantDeletion(Type type, string table, string identity)
    {
        using var db = new OrchestratorDbContextFactory().CreateDbContext([]);
        var entity = db.Model.FindEntityType(type)!;
        Assert.Equal(table, entity.GetTableName());
        Assert.Contains(entity.GetIndexes(), i => i.IsUnique && string.Join(',', i.Properties.Select(p => p.Name)) == identity);
        Assert.Equal(DeleteBehavior.Restrict, Assert.Single(entity.GetForeignKeys()).DeleteBehavior);
        Assert.Equal(64, entity.FindProperty("RunId")!.GetMaxLength());
        Assert.Equal("datetime(6)", entity.FindProperty(type == typeof(EpicGrantBinding) ? "CreatedAt" : "FirstReservedAt")!.GetColumnType());
    }
}
