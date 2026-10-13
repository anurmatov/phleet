using System.Text.Json.Nodes;
using Fleet.Orchestrator.Services;

namespace Fleet.Orchestrator.Tests.EpicGrants;

public sealed class AutonomyScopeParserTests
{
    private static JsonObject Scope() => new()
    {
        ["driver"] = new JsonObject { ["namespace"] = "default", ["workflowId"] = "driver-example", ["runId"] = "driver-run" },
        ["targets"] = new JsonArray(new JsonObject { ["repo"] = "example-org/example-repo", ["issues"] = new JsonArray(12) }),
        ["gates"] = new JsonArray("design-approval", "merge-approval"),
        ["workflows"] = new JsonArray(new JsonObject { ["type"] = "UweDesignWorkflow", ["version"] = 17, ["sha256"] = new string('a', 64) }),
        ["expiresAt"] = "2026-11-01T00:00:00Z",
        ["mode"] = "autonomous", ["outcome"] = "Deliver the example outcome",
        ["children"] = new JsonObject { ["repos"] = new JsonArray(new JsonObject { ["repo"] = "example-org/example-repo", ["allowPublic"] = false }), ["maxIssues"] = 3 },
        ["limits"] = new JsonObject { ["maxDecisions"] = 20, ["maxDelegatedRuns"] = 12, ["maxBlockedRechecks"] = 3, ["maxTotalAcceptanceRounds"] = 10 },
        ["acceptanceIsolation"] = "isolated",
        ["plan"] = new JsonArray(Item("p1", 12, "UwePrImplementationWorkflow"), Item("p2", null, "UweDesignToPrWorkflow", "p1"))
    };
    private static JsonObject Item(string id, int? issue, string workflow, params string[] after) => new()
    {
        ["id"] = id, ["repo"] = "example-org/example-repo", ["issue"] = issue,
        ["workflow"] = workflow, ["gates"] = new JsonArray("merge-approval"),
        ["after"] = new JsonArray(after.Select(x => JsonValue.Create(x) as JsonNode).ToArray())
    };
    private static EpicScopeParse Parse(JsonObject scope, bool enabled = true) => EpicScopeParser.Parse(scope.ToJsonString(), enabled);

    [Fact]
    public void Charter_ParsesTypedLimitsAndOptionalEmptyLists()
    {
        var parsed = Parse(Scope()); Assert.Empty(parsed.Errors);
        var scope = Assert.IsType<EpicScope>(parsed.Scope);
        Assert.Equal("autonomous", scope.Mode);
        Assert.Equal(20, scope.Autonomy!.Limits.MaxDecisions);
        Assert.Equal(3, scope.Autonomy.Children.MaxIssues);
        Assert.Empty(scope.Autonomy.Adopt); Assert.Empty(scope.Autonomy.Exclusions);
        Assert.Equal(new[] { "p1", "p2" }, scope.Autonomy.Plan.Select(p => p.Id));
    }

    [Fact]
    public void Charter_DefaultParserIsFailClosedUntilServiceIntegration()
    {
        var parsed = EpicScopeParser.Parse(Scope().ToJsonString());
        Assert.Contains("autonomy is disabled", parsed.Errors); Assert.Null(parsed.Scope);
    }

    [Theory]
    [InlineData("maxDecisions", 0)] [InlineData("maxDecisions", 51)]
    [InlineData("maxDelegatedRuns", 0)] [InlineData("maxDelegatedRuns", 31)]
    [InlineData("maxBlockedRechecks", -1)] [InlineData("maxBlockedRechecks", 6)]
    [InlineData("maxTotalAcceptanceRounds", -1)] [InlineData("maxTotalAcceptanceRounds", 26)]
    public void Charter_InvalidBudgetRefuses(string key, int value)
    {
        var scope = Scope(); scope["limits"]![key] = value;
        Assert.NotEmpty(Parse(scope).Errors); Assert.Null(Parse(scope).Scope);
    }

    [Theory]
    [InlineData("outcome")] [InlineData("children")] [InlineData("limits")]
    [InlineData("acceptanceIsolation")] [InlineData("plan")]
    public void Charter_RequiredFieldsCannotBeOmitted(string key)
    {
        var scope = Scope(); scope.Remove(key); Assert.NotEmpty(Parse(scope).Errors);
    }

    [Theory]
    [InlineData("unknown_dependency")] [InlineData("cycle")] [InlineData("self")]
    [InlineData("duplicate")] [InlineData("create_pr")] [InlineData("create_repo")]
    [InlineData("plan_gate")] [InlineData("workflow")] [InlineData("id_newline")]
    [InlineData("plan_empty")] [InlineData("plan_51")]
    public void Charter_InvalidPlanRefuses(string problem)
    {
        var scope = Scope(); var plan = scope["plan"]!.AsArray();
        switch (problem)
        {
            case "unknown_dependency": plan[1]!["after"] = new JsonArray("missing"); break;
            case "cycle": plan[0]!["after"] = new JsonArray("p2"); break;
            case "self": plan[0]!["after"] = new JsonArray("p1"); break;
            case "duplicate": plan[1]!["id"] = "p1"; break;
            case "create_pr": plan[0]!["issue"] = null; break;
            case "create_repo": plan[1]!["repo"] = "example-org/other-repo"; break;
            case "plan_gate": plan[0]!["gates"] = new JsonArray("doc-review"); break;
            case "workflow": plan[0]!["workflow"] = "OtherWorkflow"; break;
            case "id_newline": plan[0]!["id"] = "p1\n"; break;
            case "plan_empty": scope["plan"] = new JsonArray(); break;
            case "plan_51": scope["plan"] = new JsonArray(Enumerable.Range(0, 51).Select(i => (JsonNode)Item("p" + i, 12, "UwePrImplementationWorkflow")).ToArray()); break;
        }
        var parsed = Parse(scope); Assert.NotEmpty(parsed.Errors); Assert.Null(parsed.Scope);
        if (problem == "unknown_dependency") Assert.Contains("plan_unknown_dependency", parsed.Errors);
        if (problem is "cycle" or "self") Assert.Contains("plan_cycle", parsed.Errors);
    }

    [Theory]
    [InlineData("production")] [InlineData("actions")] [InlineData("acceptance_gate")]
    [InlineData("exclusions_51")] [InlineData("adopt_outside_targets")]
    public void Charter_InvalidAdditionalAuthorityRefuses(string problem)
    {
        var scope = Scope();
        switch (problem)
        {
            case "production": scope["acceptanceIsolation"] = "production"; break;
            case "actions": scope["actions"] = new JsonArray("deploy"); break;
            case "acceptance_gate": scope["gates"] = new JsonArray("acceptance-decision"); break;
            case "exclusions_51": scope["exclusions"] = new JsonArray(Enumerable.Range(0,51).Select(i => JsonValue.Create("p" + i) as JsonNode).ToArray()); break;
            case "adopt_outside_targets": scope["adopt"] = new JsonArray(new JsonObject { ["namespace"]="default",["workflowId"]="child-example",["runId"]="child-run",["repo"]="example-org/other-repo",["issue"]=12 }); break;
        }
        Assert.NotEmpty(Parse(scope).Errors);
    }

    [Fact]
    public void Charter_DeniedChildRepoRefusesEvenWithPublicOptIn()
    {
        var scope = Scope(); scope["children"]!["repos"]![0]!["allowPublic"] = true;
        var parsed = EpicScopeParser.Parse(scope.ToJsonString(), true, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "EXAMPLE-ORG/EXAMPLE-REPO" });
        Assert.NotEmpty(parsed.Errors); Assert.Null(parsed.Scope);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Legacy_AbsentOrExplicitDelegatedModeKeepsItsContract(bool explicitMode)
    {
        var scope = Scope(); foreach (var key in new[] { "mode","outcome","children","limits","acceptanceIsolation","plan" }) scope.Remove(key);
        if (explicitMode) scope["mode"] = "delegated";
        Assert.Equal("delegated", Parse(scope).Scope!.Mode);
        scope["plan"] = new JsonArray(); Assert.NotEmpty(Parse(scope).Errors);
    }
}
