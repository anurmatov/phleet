using System.Text.Json.Nodes;
using Fleet.Orchestrator.Services;

namespace Fleet.Orchestrator.Tests.EpicGrants;

/// <summary>
/// #436 review round 1, finding 3: "delegation-capable" must mean every wait on the gate checks
/// the orchestrator's signal — marker <c>GrantId</c>, <c>VisitId</c> bound to the wait's own visit,
/// <c>ArtifactRef</c> bound to <c>review_ref</c> — not merely that some guard object is present.
/// Grant creation and D5 both use this one function.
/// </summary>
public sealed class GuardedGatesTests
{
    [Fact]
    public void A_fully_guarded_wait_guards_its_gate()
    {
        Assert.Equal(["merge-approval"], EpicJson.GuardedGates(Definition(Wait("merge-approval", "merge_visit"))));
    }

    [Theory]
    [InlineData("marker", "Grant")]
    [InlineData("marker", "grantid")]
    [InlineData("VisitId", "{{vars.other_visit}}")]
    [InlineData("VisitId", "merge-approval:1")]
    [InlineData("ArtifactRef", "{{vars.head_sha}}")]
    [InlineData("ArtifactRef", "")]
    public void A_guard_that_checks_something_else_guards_nothing(string field, string value)
    {
        var wait = Wait("merge-approval", "merge_visit");
        if (field == "marker") wait["delegatedGuard"]!["marker"] = value;
        else wait["delegatedGuard"]!["require"]![field] = value;

        Assert.Empty(EpicJson.GuardedGates(Definition(wait)));
    }

    [Theory]
    [InlineData("VisitId")]
    [InlineData("ArtifactRef")]
    public void A_guard_missing_a_required_field_guards_nothing(string field)
    {
        var wait = Wait("merge-approval", "merge_visit");
        wait["delegatedGuard"]!["require"]!.AsObject().Remove(field);

        Assert.Empty(EpicJson.GuardedGates(Definition(wait)));
    }

    [Fact]
    public void A_second_unguarded_wait_on_the_same_gate_makes_the_gate_not_delegable()
    {
        var unguarded = Wait("merge-approval", null);
        unguarded.Remove("delegatedGuard");

        Assert.Empty(EpicJson.GuardedGates(Definition(Wait("merge-approval", "merge_visit"), unguarded)));
        Assert.Empty(EpicJson.GuardedGates(Definition(unguarded.DeepClone(), Wait("merge-approval", "merge_visit"))));
    }

    [Fact]
    public void An_unguarded_wait_on_another_gate_does_not_affect_a_guarded_one()
    {
        var advisory = Wait("advisory-review", null);
        advisory.Remove("delegatedGuard");
        var design = Wait("design-approval", null);
        design.Remove("delegatedGuard");

        Assert.Equal(
            ["merge-approval"],
            EpicJson.GuardedGates(Definition(Wait("merge-approval", "merge_visit"), advisory, design)));
    }

    [Fact]
    public void A_templated_signal_name_anywhere_makes_the_definition_guard_nothing()
    {
        var templated = Wait("{{vars.gate}}", null);
        templated.Remove("delegatedGuard");

        Assert.Empty(EpicJson.GuardedGates(Definition(Wait("merge-approval", "merge_visit"), templated)));
    }

    [Fact]
    public void A_templated_visit_var_guards_nothing()
    {
        var wait = Wait("merge-approval", "merge_visit");
        wait["visitVar"] = "{{vars.name}}";
        wait["delegatedGuard"]!["require"]!["VisitId"] = "{{vars.{{vars.name}}}}";

        Assert.Empty(EpicJson.GuardedGates(Definition(wait)));
    }

    [Fact]
    public void A_wait_nested_in_a_branch_counts_like_any_other()
    {
        var unguarded = Wait("doc-review", null);
        unguarded.Remove("delegatedGuard");
        var nested = new JsonObject
        {
            ["type"] = "branch",
            ["on"] = "{{vars.x}}",
            ["cases"] = new JsonObject { ["a"] = unguarded },
        };

        Assert.Empty(EpicJson.GuardedGates(Definition(Wait("doc-review", "doc_visit"), nested)));
    }

    /// <summary>The shipped seed passes the strict rule, gate by gate.</summary>
    [Theory]
    [InlineData("UwePrImplementationWorkflow", "merge-approval")]
    [InlineData("UweDesignWorkflow", "design-approval")]
    [InlineData("UweDocMaintenanceWorkflow", "doc-review")]
    public void Every_shipped_delegation_capable_definition_fully_guards_its_gate(string workflow, string gate)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "seed.example.json")))
            dir = dir.Parent;
        Assert.NotNull(dir);

        var seed = JsonNode.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "seed.example.json")))!;
        var definition = seed["workflowDefinitions"]!.AsArray()
            .Single(d => d!["name"]!.GetValue<string>() == workflow)!["definition"]!.ToJsonString();

        Assert.Equal([gate], EpicJson.GuardedGates(definition));
    }

    private static JsonObject Wait(string signal, string? visitVar) => new()
    {
        ["type"] = "wait_for_signal",
        ["signalName"] = signal,
        ["visitVar"] = visitVar,
        ["delegatedGuard"] = new JsonObject
        {
            ["marker"] = "GrantId",
            ["require"] = new JsonObject
            {
                ["VisitId"] = $"{{{{vars.{visitVar}}}}}",
                ["ArtifactRef"] = "{{vars.review_ref}}",
            },
        },
    };

    private static string Definition(params JsonNode[] steps) =>
        new JsonObject { ["type"] = "sequence", ["steps"] = new JsonArray(steps) }.ToJsonString();
}
