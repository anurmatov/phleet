using System.Net.Http.Json;
using Fleet.Orchestrator.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Orchestrator.Tests.EpicGrants;

public sealed partial class AutonomyPreflightTests
{
    public static IEnumerable<object[]> IntegrityCases() =>
        from reader in new[] { "dry_run", "state", "admission" }
        from fault in new[] { "null_json", "null_sha", "byte", "whitespace", "non_json", "missing_visibility", "bad_value" }
        select new object[] { reader, fault };

    [Theory]
    [MemberData(nameof(IntegrityCases))]
    public async Task Integrity_PerReaderMatrixFailsClosedAndRealDecisionStillReadsLive(string reader, string fault)
    {
        const string original = "{\"visibility\":{\"example-org/example-repo\":\"private\"}}";
        string? json = original, hash = EpicGrantWorld.Sha256(original);
        string expected;
        switch (fault)
        {
            case "null_json": json = null; expected = "missing"; break;
            case "null_sha": hash = null; expected = "missing"; break;
            case "byte": json = original.Replace("private", "public"); expected = "mismatch"; break;
            case "whitespace": json = " " + original + "\n"; expected = "mismatch"; break;
            case "non_json": json = "not JSON"; hash = EpicGrantWorld.Sha256(json); expected = "malformed"; break;
            case "missing_visibility": json = "{}"; hash = EpicGrantWorld.Sha256(json); expected = "malformed"; break;
            default: json = original.Replace("private", "internal"); hash = EpicGrantWorld.Sha256(json); expected = "malformed"; break;
        }
        var run = _world.PrRunRecord;
        run.Type = EpicGrantWorld.DesignType; run.DefinitionName = run.Type; run.DefinitionVersion = 16;
        run.Input!["ExistingIssueNumber"] = 12; run.GateVisit = "design-approval:1";
        _runs.Values.Add(new(run.WorkflowId, run.RunId, run.Type, EpicGrantWorld.Ns, null, "Running", run.StartTime));
        await _world.ExecAsync(async db => {
            var grant = await db.EpicGrants.SingleAsync(); grant.Mode = "autonomous";
            grant.ScopeJson = AutonomyDecisionTests.Charter(_world).ToJsonString(); grant.ScopeSha256 = EpicGrantWorld.Sha256(grant.ScopeJson);
            grant.PreflightJson = json; grant.PreflightSha256 = hash; await db.SaveChangesAsync();
        });
        _world.GitHub.Calls.Clear(); _world.Log.Lines.Clear();
        await using var db = _world.NewDb();
        var request = EpicGrantWorld.Request(gate: "design-approval", visitId: "design-approval:1");
        if (reader == "dry_run") Assert.Equal("unknown", (await Service(db).DryRunDecideAsync(_world.GrantId, request, default)).Reason);
        else if (reader == "admission")
        {
            await using var app = await ReaderAppAsync();
            var result = (await app.GetTestClient().GetFromJsonAsync<EpicGrantAdmission>(
                $"/api/epic-grants/{_world.GrantId}/admission?workflowType={EpicGrantWorld.PrType}&repo={EpicGrantWorld.PrivateRepo}&issue=12&planItem=p1"))!;
            Assert.Equal("deny", result.Result); Assert.Equal("unknown", result.Reason);
        }
        else
        {
            await using var app = await ReaderAppAsync();
            var result = (await app.GetTestClient().GetFromJsonAsync<EpicDriverState>(
                $"/api/epic-drivers/{EpicGrantWorld.DriverId}/state?namespace={EpicGrantWorld.Ns}"))!;
            Assert.Equal("human_decide", Assert.Single(result.Children).NextAction);
            Assert.Equal("unknown", Assert.Single(result.Children).Eligibility);
        }
        Assert.Empty(await db.EpicGrantDecisions.ToListAsync()); Assert.Empty(await db.EpicGrantBindings.ToListAsync());
        Assert.Empty(await db.EpicGrantRuns.ToListAsync()); Assert.Empty(_world.Temporal.Signals); Assert.Empty(_world.GitHub.Calls);
        Assert.Equal($"EpicGrant preflight_integrity grant={_world.GrantId} reader={(reader == "state" ? "dry_run" : reader)} result={expected}",
            Assert.Single(_world.Log.Lines, l => l.StartsWith("EpicGrant preflight_integrity", StringComparison.Ordinal)));
        Assert.Equal("sent", (await _world.DecideAsync(request)).Result);
        Assert.Single(_world.GitHub.Calls);
    }

    private async Task<WebApplication> ReaderAppAsync()
    {
        var links = new EpicRunLinkage(_world.Temporal, _runs);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing", Args = [] });
        builder.WebHost.UseTestServer(); builder.Logging.ClearProviders();
        builder.Services.AddScoped<OrchestratorDbContext>(_ => _world.NewDb())
            .AddScoped<EpicGrantService>(sp => _world.Service(sp.GetRequiredService<OrchestratorDbContext>(), links))
            .AddSingleton(sp => new EpicDriverStateService(sp.GetRequiredService<IServiceScopeFactory>(), _world.Temporal, links,
                _world.Clock, _world.Configuration, NullLogger<EpicDriverStateService>.Instance));
        var app = builder.Build(); app.MapEpicDriverEndpoints(); app.MapEpicGrantEndpoints(); await app.StartAsync();
        return app;
    }

    [Theory]
    [InlineData("create", "admit")]
    [InlineData("definition", "definition_unpinned")]
    [InlineData("runs", "delegated_runs_exhausted")]
    [InlineData("excluded", "excluded")]
    [InlineData("temporal", "unknown")]
    [InlineData("legacy", "not_autonomous")]
    [InlineData("revoked", "grant_inactive")]
    public async Task Admission_MissingAcceptanceCasesStayReadOnly(string scenario, string expected)
    {
        await using var db = _world.NewDb(); var scope = AutonomyDecisionTests.Charter(_world);
        if (scenario == "create") { scope["plan"]![0]!["issue"] = null; scope["plan"]![0]!["workflow"] = EpicGrantWorld.DesignType; scope["plan"]![0]!["gates"] = new System.Text.Json.Nodes.JsonArray("design-approval"); }
        if (scenario == "excluded") scope["exclusions"] = new System.Text.Json.Nodes.JsonArray("p1");
        var created = await Service(db).CreateAsync(scope.ToJsonString(), default);
        Assert.True(created.Report.Valid, string.Join(';', created.Report.Errors)); var id = created.Grant!.Id;
        if (scenario == "definition") { var d = await db.WorkflowDefinitions.SingleAsync(d => d.Name == EpicGrantWorld.PrType); d.Version++; await db.SaveChangesAsync(); }
        if (scenario == "runs")
        {
            for (var i = 0; i < 2; i++) db.EpicGrantRuns.Add(new() { GrantId = Guid.Parse(id), Namespace = EpicGrantWorld.Ns,
                WorkflowId = "recorded-" + i, RunId = "run-" + i, WorkflowType = EpicGrantWorld.PrType, Repo = EpicGrantWorld.PrivateRepo, FirstReservedAt = _world.Clock.Now.UtcDateTime });
            await db.SaveChangesAsync();
        }
        if (scenario == "temporal") _world.Temporal.FailWorkflowId = EpicGrantWorld.DriverId;
        if (scenario == "legacy") id = _world.GrantId;
        if (scenario == "revoked") await Service(db).RevokeAsync(id, "test revoke", default);
        _world.GitHub.Calls.Clear(); var rowsBefore = await db.EpicGrantRuns.CountAsync();
        var result = await Service(db).AdmissionAsync(id, scenario == "create" ? EpicGrantWorld.DesignType : EpicGrantWorld.PrType,
            EpicGrantWorld.PrivateRepo, scenario == "create" ? null : 12, "p1", default);
        Assert.Equal(expected, result.Reason ?? result.Result);
        Assert.Equal(scenario == "create", result.NeedsBinding);
        Assert.Empty(await db.EpicGrantDecisions.ToListAsync()); Assert.Empty(await db.EpicGrantBindings.ToListAsync());
        Assert.Equal(rowsBefore, await db.EpicGrantRuns.CountAsync()); Assert.Empty(_world.GitHub.Calls); Assert.Empty(_world.Temporal.Signals);
    }
}
