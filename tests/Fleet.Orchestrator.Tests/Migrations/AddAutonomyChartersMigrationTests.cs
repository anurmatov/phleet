using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Tests.EpicGrants;
using Fleet.Orchestrator.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MySqlConnector;

namespace Fleet.Orchestrator.Tests.Migrations;

public sealed class AddAutonomyChartersMigrationTests : IAsyncLifetime
{
    private string _admin = "";
    private string _database = "";
    private string _connection = "";
    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable(AddEpicGrantsMigrationTests.ConnectionVariable);
        if (string.IsNullOrWhiteSpace(configured)) throw new InvalidOperationException("Migration connection is required; migration tests never skip");
        _admin = new MySqlConnectionStringBuilder(configured) { Database="" }.ConnectionString;
        _database="orch_charter_"+Guid.NewGuid().ToString("N")[..16];
        await SqlAsync(_admin,$"CREATE DATABASE `{_database}` CHARACTER SET utf8mb4");
        _connection=new MySqlConnectionStringBuilder(configured) { Database=_database }.ConnectionString;
    }
    public async Task DisposeAsync()
    { if (_database.Length != 0) await SqlAsync(_admin,$"DROP DATABASE IF EXISTS `{_database}`"); }
    private OrchestratorDbContext Db() => new(new DbContextOptionsBuilder<OrchestratorDbContext>().UseMySql(_connection,new MySqlServerVersion(new Version(8,0,0))).Options);
    private static async Task<object?> SqlAsync(string connection,string sql)
    {
        await using var c=new MySqlConnection(connection); await c.OpenAsync();
        await using var cmd=new MySqlCommand(sql,c); return await cmd.ExecuteScalarAsync();
    }
    [Fact]
    public async Task UpPreservesLegacyDefaultExactEvidenceAndPermanentUniqueRecords_DownRestoresOldModel()
    {
        await using var db=Db(); var migrator=db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261008135432_AddEpicGrants");
        await SqlAsync(_connection,"""
            INSERT INTO epic_grants (Id,Status,ScopeJson,ScopeSha256,DriverNamespace,DriverWorkflowId,DriverRunId,CtoAgent,CreatedAt,ExpiresAt)
            VALUES ('00000000-0000-4000-8000-000000000001','active','{}',REPEAT('a',64),'default','driver','run','agent1',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6))
            """);
        await SqlAsync(_connection,"""
            INSERT INTO epic_grant_decisions (GrantId,Namespace,WorkflowId,RunId,Gate,VisitId,ArtifactRef,Evidence,Caller,Status,CreatedAt,UpdatedAt)
            VALUES ('00000000-0000-4000-8000-000000000001','default','child','run','design-approval','design-approval:1','ref','https://example.com/review','agent1','sent',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6))
            """);
        await db.Database.MigrateAsync();
        Assert.Equal(1L,Convert.ToInt64(await SqlAsync(_connection,"SELECT COUNT(*) FROM epic_grant_decisions WHERE Status='sent' AND VisitId='design-approval:1' AND ArtifactRef='ref'")));
        Assert.Equal("delegated",await SqlAsync(_connection,"SELECT Mode FROM epic_grants"));
        const string evidence=" {\"visibility\":{\"example-org/example-repo\":\"private\"}} ";
        var grant=await db.EpicGrants.SingleAsync(); grant.PreflightJson=evidence;
        grant.PreflightSha256=Fleet.Orchestrator.Services.EpicGrantService.Sha256Hex(evidence); await db.SaveChangesAsync();
        db.ChangeTracker.Clear(); grant=await db.EpicGrants.SingleAsync();
        Assert.Equal(evidence,grant.PreflightJson); Assert.Equal(Fleet.Orchestrator.Services.EpicGrantService.Sha256Hex(evidence),grant.PreflightSha256);
        foreach (var table in new[] { "epic_grant_bindings","epic_grant_runs" })
        {
            Assert.Equal("RESTRICT",await SqlAsync(_connection,$"SELECT DELETE_RULE FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA=DATABASE() AND TABLE_NAME='{table}'"));
            Assert.Equal(1L,Convert.ToInt64(await SqlAsync(_connection,$"SELECT COUNT(DISTINCT INDEX_NAME) FROM information_schema.STATISTICS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='{table}' AND NON_UNIQUE=0 AND INDEX_NAME!='PRIMARY'")));
        }
        await migrator.MigrateAsync("20261008135432_AddEpicGrants");
        Assert.Equal(0L,Convert.ToInt64(await SqlAsync(_connection,"SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME IN ('epic_grant_bindings','epic_grant_runs')")));
        Assert.Equal(1L,Convert.ToInt64(await SqlAsync(_connection,"SELECT COUNT(*) FROM epic_grants")));
        Assert.Equal(1L,Convert.ToInt64(await SqlAsync(_connection,"SELECT COUNT(*) FROM epic_grant_decisions")));
    }
    [Fact]
    public async Task ConcurrentAutonomousDecisions_ReserveOnlyOneAtDecisionBudgetOne()
    {
        await using (var db=Db()) await db.Database.MigrateAsync();
        var world=new EpicGrantWorld(Db); await world.InitializeAsync(); world.Options.AutonomyEnabled=true;
        var scope=AutonomyDecisionTests.Charter(world); scope["limits"]!["maxDecisions"]=1;
        await world.ExecAsync(async db=>
        {
            var grant=await db.EpicGrants.SingleAsync(); grant.Mode="autonomous";
            grant.ScopeJson=scope.ToJsonString(); grant.ScopeSha256=EpicGrantWorld.Sha256(grant.ScopeJson); await db.SaveChangesAsync();
        });
        var second=world.Temporal.Add(world.PrRunFor("design-second","run-second"));
        foreach(var run in new[] { world.PrRunRecord,second })
        {
            run.Type=EpicGrantWorld.DesignType; run.DefinitionName=run.Type; run.DefinitionVersion=16;
            run.Input!["ExistingIssueNumber"]=12; run.GateVisit="design-approval:1";
        }
        var results=await Task.WhenAll(new[] { world.PrRunRecord,second }.Select(run=>world.DecideAsync(
            EpicGrantWorld.Request(workflowId:run.WorkflowId,gate:"design-approval",visitId:"design-approval:1"))));
        Assert.Single(results,r=>r.Result=="sent"); Assert.Single(results,r=>r.Reason=="budget_exhausted");
        Assert.Single(await world.DecisionRowsAsync()); Assert.Single(world.Temporal.Signals);
        await using var verify=Db(); Assert.Single(await verify.EpicGrantRuns.ToListAsync());
        Assert.Empty(await verify.EpicGrantBindings.ToListAsync());
    }

}
