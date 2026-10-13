using System.Text.Json.Nodes;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Models;
using Fleet.Orchestrator.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Orchestrator.Tests.EpicGrants;

public sealed partial class AutonomyPreflightTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private EpicGrantWorld _world = null!;
    private readonly Runs _runs = new();
    private sealed class Runs : IEpicDriverTemporal
    {
        public bool IsConfigured => true;
        public List<WorkflowSummary> Values { get; } = [];
        public bool Complete { get; set; } = true;
        public Task<EpicVisibilityResult> ListAsync(string ns,string query,int? limit,CancellationToken ct) => Task.FromResult(new EpicVisibilityResult(Values,Complete,Complete ? null : "candidates_incomplete"));
        public Task<string> SignalWorkflowAsync(string ns,string id,string run,string signal,object payload,CancellationToken ct) => throw new InvalidOperationException("Preflight must not signal");
    }
    public async Task InitializeAsync()
    {
        _connection = new("Data Source=:memory:"); await _connection.OpenAsync();
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>().UseSqlite(_connection).Options;
        await using var db = new OrchestratorDbContext(options); await db.Database.EnsureCreatedAsync();
        _world = new(() => new OrchestratorDbContext(options)); await _world.InitializeAsync();
        _world.Options.AutonomyEnabled=true;
    }
    public async Task DisposeAsync() => await _connection.DisposeAsync();
    private EpicGrantService Service(OrchestratorDbContext db) => _world.Service(db,new(_world.Temporal,_runs));

    [Fact]
    public async Task Create_ComputesAndStoresCanonicalServerPreflightAndExactHash()
    {
        await using var db = _world.NewDb(); var scope = AutonomyDecisionTests.Charter(_world);
        var result = await Service(db).CreateAsync(scope.ToJsonString(),default);
        Assert.True(result.Report.Valid,string.Join(';',result.Report.Errors));
        var grant = await db.EpicGrants.SingleAsync(g => g.Id == Guid.Parse(result.Grant!.Id));
        Assert.Equal("autonomous",grant.Mode);
        Assert.Equal(EpicGrantWorld.Sha256(grant.PreflightJson!),grant.PreflightSha256);
        Assert.Equal(EpicGrantPreflight.CanonicalJson(result.Report.Preflight),grant.PreflightJson);
        Assert.Equal(0,result.Report.Preflight!.Unresolved);
        Assert.Equal("ok",Assert.Single(result.Report.Preflight.Items)["verdict"]!.GetValue<string>());
        Assert.Empty(_world.Temporal.Signals); Assert.Empty(await db.EpicGrantRuns.ToListAsync());
    }
    [Fact]
    public async Task CurrentDefinitionDrift_IsUnresolvedButExcludedPlanWins()
    {
        var scope=AutonomyDecisionTests.Charter(_world);
        await using var db=_world.NewDb();
        var definition=await db.WorkflowDefinitions.SingleAsync(d=>d.Name==EpicGrantWorld.PrType);
        db.WorkflowDefinitionVersions.Add(new() { WorkflowDefinitionId=definition.Id,Version=definition.Version,Definition=definition.Definition });
        definition.Version++; await db.SaveChangesAsync();
        var report=await Service(db).ValidateAsync(scope.ToJsonString(),default);
        Assert.False(report.Valid); Assert.Equal("definition_unpinned",Assert.Single(report.Preflight!.Items)["verdict"]!.GetValue<string>());
        scope["exclusions"]=new JsonArray("p1");
        report=await Service(db).ValidateAsync(scope.ToJsonString(),default);
        Assert.Equal("excluded",Assert.Single(report.Preflight!.Items)["verdict"]!.GetValue<string>());
        Assert.Equal(0,report.Preflight.Unresolved);
    }
    [Fact]
    public async Task LinkedRun_RequiresExactAdoptionAndIncompletePagingNeverCreates()
    {
        var run=_world.PrRunRecord;
        _runs.Values.Add(new(EpicGrantWorld.PrId,EpicGrantWorld.PrRun,EpicGrantWorld.PrType,EpicGrantWorld.Ns,null,"Running",run.StartTime));
        var scope=AutonomyDecisionTests.Charter(_world); await using var db=_world.NewDb();
        var report=await Service(db).ValidateAsync(scope.ToJsonString(),default);
        Assert.Equal("needs_adoption",report.Preflight!.Items.Single(i=>i["source"]!.GetValue<string>()=="linked_run")["verdict"]!.GetValue<string>());
        scope["adopt"]=new JsonArray(new JsonObject { ["namespace"]=EpicGrantWorld.Ns,["workflowId"]=EpicGrantWorld.PrId,
            ["runId"]=EpicGrantWorld.PrRun,["repo"]=EpicGrantWorld.PrivateRepo,["issue"]=12 });
        report=await Service(db).ValidateAsync(scope.ToJsonString(),default);
        Assert.True(report.Valid,string.Join(';',report.Errors));
        _runs.Complete=false;
        var result=await Service(db).CreateAsync(scope.ToJsonString(),default);
        Assert.Null(result.Grant); Assert.Contains("candidates_incomplete",result.Report.Errors);
        Assert.Equal(1,await db.EpicGrants.CountAsync());
    }
    [Fact]
    public async Task AdoptionAuthorIsDecider_RefusesEvenWhenNotLinked()
    {
        var scope=AutonomyDecisionTests.Charter(_world);
        scope["adopt"]=new JsonArray(new JsonObject { ["namespace"]=EpicGrantWorld.Ns,["workflowId"]=EpicGrantWorld.PrId,
            ["runId"]=EpicGrantWorld.PrRun,["repo"]=EpicGrantWorld.PrivateRepo,["issue"]=12 });
        _world.PrRunRecord.Input!["TargetAgent"]=EpicGrantWorld.Cto;
        await using var db=_world.NewDb();
        Assert.Contains("adopt_author_is_decider",(await Service(db).ValidateAsync(scope.ToJsonString(),default)).Errors);
    }
    [Fact]
    public async Task Admission_CachedAllowNeverBypassesFreshIntegrityAndRecordsFullQuery()
    {
        await using var db=_world.NewDb(); var service=Service(db);
        var created=await service.CreateAsync(AutonomyDecisionTests.Charter(_world).ToJsonString(),default);
        Assert.True(created.Report.Valid,string.Join(';',created.Report.Errors));
        _world.GitHub.Calls.Clear();
        var first=await service.AdmissionAsync(created.Grant!.Id,EpicGrantWorld.PrType,EpicGrantWorld.PrivateRepo,12,"p1",default);
        Assert.Equal("admit",first.Result);
        _world.Clock.Advance(TimeSpan.FromSeconds(1));
        var second=await service.AdmissionAsync(created.Grant.Id,EpicGrantWorld.PrType,EpicGrantWorld.PrivateRepo,12,"p1",default);
        Assert.Equal(first.CachedAt,second.CachedAt);
        Assert.Equal("target_out_of_scope",(await service.AdmissionAsync(created.Grant.Id,EpicGrantWorld.PrType,EpicGrantWorld.PrivateRepo,12,"unknown",default)).Reason);
        var row=await db.EpicGrants.SingleAsync(g=>g.Id==Guid.Parse(created.Grant.Id));
        row.PreflightJson+=" "; await db.SaveChangesAsync();
        Assert.Equal("unknown",(await service.AdmissionAsync(created.Grant.Id,EpicGrantWorld.PrType,EpicGrantWorld.PrivateRepo,12,"p1",default)).Reason);
        Assert.Empty(_world.GitHub.Calls); Assert.Empty(await db.EpicGrantDecisions.ToListAsync());
    }
    [Fact]
    public async Task CreatePlanNeedsBindingButAnOldRunningCreateCannotBind()
    {
        var scope=AutonomyDecisionTests.Charter(_world);
        scope["plan"]![0]!["issue"]=null; scope["plan"]![0]!["workflow"]=EpicGrantWorld.DesignType;
        scope["plan"]![0]!["gates"]=new JsonArray("design-approval");
        await using var db=_world.NewDb(); var service=Service(db);
        var report=await service.ValidateAsync(scope.ToJsonString(),default);
        Assert.Equal("needs_binding",Assert.Single(report.Preflight!.Items)["verdict"]!.GetValue<string>());
        var run=_world.PrRunRecord; run.Type=EpicGrantWorld.DesignType; run.DefinitionName=run.Type; run.DefinitionVersion=16;
        run.Input!.Remove("IssueNumber"); run.IssueNumber=14;
        _runs.Values.Add(new(EpicGrantWorld.PrId,EpicGrantWorld.PrRun,run.Type,EpicGrantWorld.Ns,null,"Running",run.StartTime));
        report=await service.ValidateAsync(scope.ToJsonString(),default);
        Assert.Equal("create_unbound",report.Preflight!.Items.Single(i=>i["source"]!.GetValue<string>()=="linked_run")["verdict"]!.GetValue<string>());
        Assert.False(report.Valid);
    }

    [Fact]
    public async Task MoreThanTwoHundredLinkedRuns_RefusesRatherThanOmittingTail()
    {
        for(var i=0;i<201;i++)
        {
            var run=_world.Temporal.Add(_world.PrRunFor("candidate-"+i,"run-"+i));
            _runs.Values.Add(new(run.WorkflowId,run.RunId,run.Type,EpicGrantWorld.Ns,null,"Running",run.StartTime));
        }
        await using var db=_world.NewDb();
        var result=await Service(db).CreateAsync(AutonomyDecisionTests.Charter(_world).ToJsonString(),default);
        Assert.Null(result.Grant); Assert.Contains("too_many_linked_runs",result.Report.Errors);
        Assert.Equal(1,await db.EpicGrants.CountAsync());
    }

    [Fact]
    public async Task State_RechecksAutonomousIntegrityInsteadOfReturningCachedCtoEligibility()
    {
        var run=_world.PrRunRecord; run.Type=EpicGrantWorld.DesignType; run.DefinitionName=run.Type; run.DefinitionVersion=16;
        run.Input!["ExistingIssueNumber"]=12; run.GateVisit="design-approval:1";
        _runs.Values.Add(new(run.WorkflowId,run.RunId,run.Type,EpicGrantWorld.Ns,null,"Running",run.StartTime));
        await _world.ExecAsync(async db=>
        {
            var grant=await db.EpicGrants.SingleAsync(); grant.Mode="autonomous";
            grant.ScopeJson=AutonomyDecisionTests.Charter(_world).ToJsonString(); grant.ScopeSha256=EpicGrantWorld.Sha256(grant.ScopeJson);
            grant.PreflightJson="{\"visibility\":{\"example-org/example-repo\":\"private\"}}";
            grant.PreflightSha256=EpicGrantWorld.Sha256(grant.PreflightJson); await db.SaveChangesAsync();
        });
        var links=new EpicRunLinkage(_world.Temporal,_runs);
        using var services=new ServiceCollection().AddScoped<OrchestratorDbContext>(_=>_world.NewDb())
            .AddScoped<EpicGrantService>(sp=>_world.Service(sp.GetRequiredService<OrchestratorDbContext>(),links)).BuildServiceProvider();
        var state=new EpicDriverStateService(services.GetRequiredService<IServiceScopeFactory>(),_world.Temporal,links,
            _world.Clock,_world.Configuration,NullLogger<EpicDriverStateService>.Instance);
        var first=await state.ReadAsync(EpicGrantWorld.Ns,EpicGrantWorld.DriverId,default);
        Assert.Equal("cto_decide",Assert.Single(first.Children).NextAction);
        await _world.ExecAsync(async db=> { var grant=await db.EpicGrants.SingleAsync(); grant.PreflightJson+=" "; await db.SaveChangesAsync(); });
        var second=await state.ReadAsync(EpicGrantWorld.Ns,EpicGrantWorld.DriverId,default);
        Assert.False(second.Cached); Assert.Equal("human_decide",Assert.Single(second.Children).NextAction);
        Assert.Equal("unknown",Assert.Single(second.Children).Eligibility); Assert.NotEqual(first.Fingerprint,second.Fingerprint);
        Assert.Empty(await _world.DecisionRowsAsync()); Assert.Empty(_world.Temporal.Signals);
    }

}
