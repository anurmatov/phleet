using System.Text.Json.Nodes;
using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Tests.EpicGrants;

public sealed partial class AutonomyDecisionTests
{
    private void ConfigureCreate()
    {
        var run = _world.PrRunRecord;
        run.Type = EpicGrantWorld.DesignType;
        run.DefinitionName = run.Type;
        run.DefinitionVersion = 16;
        run.Input!.Remove("IssueNumber");
        run.Input["ExistingIssueNumber"] = "";
        run.Input["AutonomyGrantId"] = _world.GrantId;
        run.IssueNumber = 14;
        run.GateVisit = "design-approval:1";
        run.ReviewScrub = " scope-fit";
    }
    private static Fleet.Orchestrator.Services.EpicGrantDecisionRequest DesignRequest(string visit = "design-approval:1") =>
        EpicGrantWorld.Request(gate: "design-approval", visitId: visit);

    [Fact]
    public async Task CreateBinding_EmptyExistingIssueNumberBindsThenReusesAndCoversPr()
    {
        await StoreCharterAsync(); ConfigureCreate();
        Assert.Equal("sent", (await _world.DecideAsync(DesignRequest())).Result);
        _world.PrRunRecord.GateVisit = "design-approval:2";
        Assert.Equal("sent", (await _world.DecideAsync(DesignRequest("design-approval:2"))).Result);
        var pr = _world.Temporal.Add(_world.PrRunFor("bound-pr", "bound-pr-run", issue: 14));
        Assert.Equal("acceptance_missing", (await _world.DecideAsync(EpicGrantWorld.Request(workflowId: pr.WorkflowId))).Reason);
        await using var db = _world.NewDb();
        Assert.Single(await db.EpicGrantBindings.ToListAsync());
        Assert.Single(await db.EpicGrantRuns.ToListAsync());
        Assert.Equal(2, await db.EpicGrantDecisions.CountAsync());
        Assert.Equal(2, _world.Temporal.SignalAttempts);
    }

    [Theory]
    [InlineData("scope_fit")]
    [InlineData("grant_absent")]
    [InlineData("grant_other")]
    [InlineData("repo")]
    [InlineData("existing")]
    [InlineData("adopted")]
    [InlineData("legacy")]
    [InlineData("pr")]
    [InlineData("prose")]
    public async Task CreateBinding_DenialMatrixNeverWidensScope(string reason)
    {
        var scope = Charter(_world);
        if (reason == "adopted") scope["adopt"] = new JsonArray(new JsonObject {
            ["namespace"] = EpicGrantWorld.Ns, ["workflowId"] = EpicGrantWorld.PrId,
            ["runId"] = EpicGrantWorld.PrRun, ["repo"] = EpicGrantWorld.PrivateRepo, ["issue"] = 12 });
        if (reason != "legacy") await StoreCharterAsync(scope);
        ConfigureCreate(); var run = _world.PrRunRecord;
        switch (reason)
        {
            case "scope_fit": run.ReviewScrub = "scope-fit-extra"; break;
            case "grant_absent": run.Input!.Remove("AutonomyGrantId"); break;
            case "grant_other": run.Input!["AutonomyGrantId"] = Guid.NewGuid().ToString("D"); break;
            case "repo": run.Input!["Repo"] = EpicGrantWorld.PublicRepo; break;
            case "existing": run.Input!["ExistingIssueNumber"] = 14; break;
            case "adopted": run.StartTime = _world.GrantCreatedAt.AddHours(-1); break;
            case "legacy": break;
            case "pr": run.Type = EpicGrantWorld.PrType; run.DefinitionName = run.Type; run.DefinitionVersion = 21;
                run.Input!["IssueNumber"] = 14; run.GateVisit = "merge-approval:2"; break;
            case "prose": run.Type = EpicGrantWorld.PrType; run.DefinitionName = run.Type; run.DefinitionVersion = 21;
                run.Input!["IssueNumber"] = 14; run.Input["Description"] = "The epic mentions issue 14"; run.GateVisit = "merge-approval:2"; break;
        }
        var result = await _world.DecideAsync(reason is "pr" or "prose" ? EpicGrantWorld.Request() : DesignRequest());
        Assert.Equal("target_out_of_scope", result.Reason); await AssertNoWritesAsync();
    }

    [Theory]
    [InlineData("pass scope-fit", "sent")]
    [InlineData("pass", "sent")]
    [InlineData(" scope-fit", "scrub_missing")]
    public async Task PublicDesign_WholePassTokenIsRequired(string scrub, string expected)
    {
        await StoreCharterAsync(); ConfigureCreate(); var run = _world.PrRunRecord;
        run.Input!["Repo"] = EpicGrantWorld.PublicRepo; run.Input["ExistingIssueNumber"] = 7;
        run.IssueNumber = 7; run.ReviewScrub = scrub;
        var result = await _world.DecideAsync(DesignRequest());
        Assert.Equal(expected, result.Reason ?? result.Result);
        if (expected != "sent") await AssertNoWritesAsync();
    }
    [Fact]
    public async Task SecondCreateBinding_AtOneChildLimitRollsBackNewRunAndDecision()
    {
        var scope = Charter(_world); scope["children"]!["maxIssues"] = 1;
        await StoreCharterAsync(scope); ConfigureCreate();
        Assert.Equal("sent", (await _world.DecideAsync(DesignRequest())).Result);
        var next = _world.Temporal.Add(_world.PrRunFor("second-create", "second-create-run"));
        next.Type = EpicGrantWorld.DesignType; next.DefinitionName = next.Type; next.DefinitionVersion = 16;
        next.Input!.Remove("IssueNumber"); next.Input["ExistingIssueNumber"] = ""; next.Input["AutonomyGrantId"] = _world.GrantId;
        next.IssueNumber = 15; next.GateVisit = "design-approval:1"; next.ReviewScrub = " scope-fit";
        var result = await _world.DecideAsync(EpicGrantWorld.Request(workflowId: next.WorkflowId, gate: "design-approval", visitId: "design-approval:1"));
        Assert.Equal("budget_exhausted", result.Reason);
        await using var db = _world.NewDb(); Assert.Single(await db.EpicGrantBindings.ToListAsync());
        Assert.Single(await db.EpicGrantRuns.ToListAsync()); Assert.Single(await db.EpicGrantDecisions.ToListAsync());
        Assert.Equal(1, _world.Temporal.SignalAttempts);
        Assert.Contains(_world.Log.Lines, line => line.Contains("budget=maxIssues", StringComparison.Ordinal));
    }

}
