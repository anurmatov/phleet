using System.Text.Json.Nodes;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Tests.EpicGrants;

public sealed partial class AutonomyDecisionTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private EpicGrantWorld _world = null!;
    public async Task InitializeAsync()
    {
        _connection = new("Data Source=:memory:"); await _connection.OpenAsync();
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>().UseSqlite(_connection).Options;
        await using var db = new OrchestratorDbContext(options); await db.Database.EnsureCreatedAsync();
        _world = new(() => new OrchestratorDbContext(options)); await _world.InitializeAsync();
        _world.Options.AutonomyEnabled = true;
    }
    public async Task DisposeAsync() => await _connection.DisposeAsync();

    internal static JsonObject Charter(EpicGrantWorld world)
    {
        var scope = world.Scope(); scope["mode"] = "autonomous"; scope["outcome"] = "Deliver the example outcome";
        scope["children"] = new JsonObject { ["repos"] = new JsonArray(new JsonObject { ["repo"] = EpicGrantWorld.PrivateRepo }), ["maxIssues"] = 2 };
        scope["limits"] = new JsonObject { ["maxDecisions"] = 3, ["maxDelegatedRuns"] = 2, ["maxBlockedRechecks"] = 0, ["maxTotalAcceptanceRounds"] = 0 };
        scope["acceptanceIsolation"] = "isolated";
        scope["plan"] = new JsonArray(new JsonObject { ["id"]="p1", ["repo"]=EpicGrantWorld.PrivateRepo, ["issue"]=12, ["workflow"]=EpicGrantWorld.PrType, ["gates"]=new JsonArray("merge-approval") });
        return scope;
    }
    private async Task StoreCharterAsync(JsonObject? scope = null, string? preflight = "{\"visibility\":{\"example-org/example-repo\":\"private\"}}", string? hash = null)
    {
        await _world.ExecAsync(async db =>
        {
            var grant = await db.EpicGrants.SingleAsync(); grant.Mode = "autonomous";
            grant.ScopeJson = (scope ?? Charter(_world)).ToJsonString(); grant.ScopeSha256 = EpicGrantWorld.Sha256(grant.ScopeJson);
            grant.PreflightJson = preflight; grant.PreflightSha256 = hash ?? (preflight is null ? null : EpicGrantWorld.Sha256(preflight));
            await db.SaveChangesAsync();
        });
        _world.GitHub.Calls.Clear();
    }
    private async Task AssertNoWritesAsync()
    {
        Assert.Empty(await _world.DecisionRowsAsync()); Assert.Empty(_world.Temporal.Signals);
        await using var db = _world.NewDb(); Assert.Empty(await db.EpicGrantRuns.ToListAsync()); Assert.Empty(await db.EpicGrantBindings.ToListAsync());
    }

    [Fact]
    public async Task DisabledAutonomy_StoredScopeReachesD1aNotScopeTampered()
    {
        await StoreCharterAsync(); _world.Options.AutonomyEnabled = false;
        var result = await _world.DecideAsync(); Assert.Equal("autonomy_disabled", result.Reason);
        Assert.Empty(_world.GitHub.Calls); await AssertNoWritesAsync();
    }
    [Fact]
    public async Task AutonomousMerge_RefusesAfterArtifactCheckWithoutReservation()
    {
        await StoreCharterAsync();
        Assert.Equal("stale_artifact", (await _world.DecideAsync(EpicGrantWorld.Request(artifactRef: "other"))).Reason);
        Assert.Equal("acceptance_missing", (await _world.DecideAsync()).Reason); await AssertNoWritesAsync();
    }
    [Theory]
    [InlineData(null, null, "missing")]
    [InlineData("{}", "bad", "mismatch")]
    [InlineData("{}", null, "malformed")]
    public async Task DryRun_VerifiesPreflightEveryRequestBeforeTemporalOrGitHub(string? json, string? hash, string failure)
    {
        await StoreCharterAsync(preflight: json, hash: hash);
        _world.Temporal.FailWorkflowId = EpicGrantWorld.DriverId;
        await using var db = _world.NewDb();
        var result = await _world.Service(db).DryRunDecideAsync(_world.GrantId, EpicGrantWorld.Request(), default);
        Assert.Equal("unknown", result.Reason);
        Assert.Contains(_world.Log.Lines, s => s == $"EpicGrant preflight_integrity grant={_world.GrantId} reader=dry_run result={failure}");
        Assert.Empty(_world.GitHub.Calls); await AssertNoWritesAsync();
    }
    [Fact]
    public async Task DryRun_UsesRecordedVisibilityNotGitHubAndStillStopsMerge()
    {
        await StoreCharterAsync(); await using var db = _world.NewDb();
        Assert.Equal("acceptance_missing", (await _world.Service(db).DryRunDecideAsync(_world.GrantId, EpicGrantWorld.Request(), default)).Reason);
        Assert.Empty(_world.GitHub.Calls); await AssertNoWritesAsync();
    }
    [Fact]
    public async Task Validation_UsesFlagAndDeniedChildrenAtParserCallSite()
    {
        var scope = Charter(_world); _world.Options.AutonomyEnabled = false;
        await using var db = _world.NewDb();
        Assert.Contains("autonomy is disabled", (await _world.Service(db).ValidateAsync(scope.ToJsonString(), default)).Errors);
        _world.Options.AutonomyEnabled = true; _world.Options.DeniedRepos = EpicGrantWorld.PrivateRepo;
        var report = await _world.Service(db).ValidateAsync(scope.ToJsonString(), default);
        Assert.DoesNotContain("autonomy is disabled", report.Errors);
        Assert.Contains(report.Errors, e => e.Contains("child repo") && e.Contains("denied"));
    }
    [Theory]
    [InlineData(null,null,"missing")]
    [InlineData("{}","bad","mismatch")]
    [InlineData("{}",null,"malformed")]
    public async Task Admission_IntegrityRefusesWithoutRowsSignalsOrGitHub(string? json,string? hash,string failure)
    {
        await StoreCharterAsync(preflight:json,hash:hash);
        await using var db = _world.NewDb();
        var result = await _world.Service(db).AdmissionAsync(_world.GrantId,EpicGrantWorld.PrType,EpicGrantWorld.PrivateRepo,12,"p1",default);
        Assert.Equal("deny",result.Result); Assert.Equal("unknown",result.Reason);
        Assert.Contains(_world.Log.Lines,s => s == $"EpicGrant preflight_integrity grant={_world.GrantId} reader=admission result={failure}");
        Assert.Empty(_world.GitHub.Calls); await AssertNoWritesAsync();
    }

    [Fact]
    public async Task ExcludedRun_RefusesBeforeStageTwoMergeStop()
    {
        var scope = Charter(_world); scope["exclusions"] = new JsonArray(EpicGrantWorld.PrId);
        await StoreCharterAsync(scope);
        Assert.Equal("excluded",(await _world.DecideAsync()).Reason); await AssertNoWritesAsync();
    }

    [Fact]
    public async Task Adoption_ExactOldRunIsAllowedButAnotherRunAndArtifactAreNot()
    {
        var scope = Charter(_world);
        scope["adopt"] = new JsonArray(new JsonObject { ["namespace"]=EpicGrantWorld.Ns,["workflowId"]=EpicGrantWorld.PrId,
            ["runId"]=EpicGrantWorld.PrRun,["repo"]=EpicGrantWorld.PrivateRepo,["issue"]=12,["artifactRef"]="pinned-other" });
        await StoreCharterAsync(scope);
        _world.PrRunRecord.StartTime = _world.GrantCreatedAt.AddHours(-1);
        _world.PrRunRecord.Input!.Remove("WaiterWorkflowId");
        Assert.Equal("stale_artifact",(await _world.DecideAsync()).Reason);
        _world.Temporal.Add(_world.PrRunFor(EpicGrantWorld.PrId,"replacement-run"));
        var replacement = _world.PrRunFor(EpicGrantWorld.PrId,"replacement-run-2");
        replacement.StartTime = _world.GrantCreatedAt.AddHours(-1); _world.Temporal.Add(replacement);
        Assert.Equal("not_linked",(await _world.DecideAsync()).Reason); await AssertNoWritesAsync();
    }

    [Theory]
    [InlineData("maxDecisions")]
    [InlineData("maxDelegatedRuns")]
    [InlineData("maxIssues")]
    public async Task Reservation_ExhaustedBudgetsRollbackEveryNewRecord(string exhausted)
    {
        var scope = Charter(_world);
        if (exhausted == "maxIssues") scope["children"]!["maxIssues"] = 0;
        else scope["limits"]![exhausted] = 1;
        await StoreCharterAsync(scope);
        await using var db = _world.NewDb();
        var service = _world.Service(db);
        if (exhausted != "maxIssues")
        {
            var first = await service.ReserveAsync(Guid.Parse(_world.GrantId),EpicGrantWorld.Ns,"first","run-1","design-approval",
                "design-approval:1","ref",EpicGrantWorld.Evidence,EpicGrantWorld.Cto,default,
                new(EpicGrantWorld.DesignType,EpicGrantWorld.PrivateRepo,12,false));
            Assert.NotNull(first.DecisionId);
        }
        var second = await service.ReserveAsync(Guid.Parse(_world.GrantId),EpicGrantWorld.Ns,"second","run-2","design-approval",
            "design-approval:2","ref",EpicGrantWorld.Evidence,EpicGrantWorld.Cto,default,
            new(EpicGrantWorld.DesignType,EpicGrantWorld.PrivateRepo,14,true));
        Assert.Equal("budget_exhausted",second.Refusal);
        Assert.Equal(exhausted == "maxIssues" ? 0 : 1,await db.EpicGrantDecisions.CountAsync());
        Assert.Equal(exhausted == "maxIssues" ? 0 : 1,await db.EpicGrantRuns.CountAsync());
        Assert.Empty(await db.EpicGrantBindings.ToListAsync());
    }

    [Fact]
    public async Task CreateBinding_RequiresScopeFitAndCharterIdentityAndPersistsOnSendFailure()
    {
        await StoreCharterAsync();
        var run = _world.PrRunRecord;
        run.Type = EpicGrantWorld.DesignType; run.DefinitionName=run.Type; run.DefinitionVersion=16;
        run.Input!.Remove("IssueNumber"); run.Input["AutonomyGrantId"]=_world.GrantId;
        run.GateVisit="design-approval:1"; run.ReviewScrub="pass";
        // FakeRun's description needs the same attribute source as the production port.
        run.IssueNumber=14;
        var request = EpicGrantWorld.Request(gate:"design-approval",visitId:"design-approval:1");
        Assert.Equal("target_out_of_scope",(await _world.DecideAsync(request)).Reason);
        run.ReviewScrub="pass scope-fit";
        _world.Temporal.SignalError=new TimeoutException("send failed");
        Assert.Equal("send_failed",(await _world.DecideAsync(request)).Result);
        await using var db = _world.NewDb();
        var binding=Assert.Single(await db.EpicGrantBindings.ToListAsync());
        Assert.Equal(14,binding.Issue); Assert.Equal(EpicGrantWorld.PrivateRepo,binding.Repo);
        Assert.Single(await db.EpicGrantRuns.ToListAsync()); Assert.Single(await db.EpicGrantDecisions.ToListAsync());
    }

}
