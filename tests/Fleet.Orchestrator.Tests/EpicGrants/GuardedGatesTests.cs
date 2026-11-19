using System.Text.Json.Nodes;
using Fleet.Orchestrator.Services;

namespace Fleet.Orchestrator.Tests.EpicGrants;

/// <summary>
/// #436 review round 1, finding 3: "delegation-capable" must mean every wait on the gate checks
/// the orchestrator's signal — marker <c>GrantId</c>, <c>VisitId</c> bound to the wait's own visit,
/// <c>ArtifactRef</c> bound to <c>review_ref</c> — not merely that some guard object is present.
/// Grant creation and D5 both use this one function.
///
/// <para>
/// The one other wait a delegable gate may have is one that refuses every delegated payload
/// (<c>delegatedGuard: {"marker": "GrantId", "refuse": true}</c>, no visit): the engine discards
/// a delegated approval there, so it cannot be consumed by anything but the guarded wait.
/// </para>
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

    // ── a wait that refuses every delegated payload ──────────────────────────

    /// <summary>
    /// Refusing waits beside the guarded one, before it or after it and nested anywhere, leave
    /// the gate delegable: none of them can consume a delegated approval.
    /// </summary>
    [Fact]
    public void Refusing_waits_beside_a_guarded_one_leave_the_gate_delegable()
    {
        var nested = new JsonObject
        {
            ["type"] = "loop",
            ["maxIterations"] = 3,
            ["steps"] = new JsonArray(Refusing("merge-approval")),
        };

        Assert.Equal(["merge-approval"],
            EpicJson.GuardedGates(Definition(Wait("merge-approval", "merge_visit"), Refusing("merge-approval"), nested)));
        Assert.Equal(["merge-approval"],
            EpicJson.GuardedGates(Definition(Refusing("merge-approval"), nested.DeepClone(), Wait("merge-approval", "merge_visit"))));
        // The signal name is matched as the gate's is: any casing.
        Assert.Equal(["merge-approval"],
            EpicJson.GuardedGates(Definition(Wait("merge-approval", "merge_visit"), Refusing("Merge-Approval"))));
    }

    /// <summary>Refusing every delegated approval everywhere is not guarding a gate: nothing could accept one.</summary>
    [Fact]
    public void Only_refusing_waits_guard_nothing()
    {
        Assert.Empty(EpicJson.GuardedGates(Definition(Refusing("merge-approval"))));
        Assert.Empty(EpicJson.GuardedGates(Definition(Refusing("merge-approval"), Refusing("merge-approval"))));
        // And they do not lend a guarded wait on another gate to their own.
        Assert.Equal(["design-approval"],
            EpicJson.GuardedGates(Definition(Refusing("merge-approval"), Wait("design-approval", "design_visit"))));
    }

    /// <summary>
    /// The refusing shape is exact. Anything near it is an unguarded wait and makes the gate not
    /// delegable, whichever side of the guarded wait it stands on.
    /// </summary>
    [Theory]
    [InlineData("""{"marker":"Grant","refuse":true}""", null)]
    [InlineData("""{"marker":"grantid","refuse":true}""", null)]
    [InlineData("""{"refuse":true}""", null)]
    [InlineData("""{"marker":"GrantId","refuse":false}""", null)]
    [InlineData("""{"marker":"GrantId","refuse":"true"}""", null)]
    [InlineData("""{"marker":"GrantId","refuse":1}""", null)]
    [InlineData("""{"marker":"GrantId","refuse":null}""", null)]
    [InlineData("""{"marker":"GrantId"}""", null)]
    [InlineData("""{"marker":"GrantId","refuse":true,"require":{"VisitId":"{{vars.v}}"}}""", null)]
    [InlineData("""{"marker":"GrantId","refuse":true,"require":{}}""", null)]
    [InlineData("""{"marker":"GrantId","refuse":true,"require":null}""", null)]
    [InlineData("""{"marker":"GrantId","refuse":true,"Require":{"ArtifactRef":"x"}}""", null)]
    [InlineData("""{"marker":"GrantId","refuse":true}""", "\"merge_visit\"")]
    [InlineData("""{"marker":"GrantId","refuse":true}""", "\"\"")]
    [InlineData("""{"marker":"GrantId","refuse":true}""", "null")]
    public void A_wait_that_is_not_exactly_the_refusing_shape_makes_the_gate_not_delegable(string guard, string? visitVar)
    {
        var wait = new JsonObject
        {
            ["type"] = "wait_for_signal",
            ["signalName"] = "merge-approval",
            ["delegatedGuard"] = JsonNode.Parse(guard),
        };
        if (visitVar is not null) wait["visitVar"] = JsonNode.Parse(visitVar);

        Assert.Empty(EpicJson.GuardedGates(Definition(Wait("merge-approval", "merge_visit"), wait)));
        Assert.Empty(EpicJson.GuardedGates(Definition(wait.DeepClone(), Wait("merge-approval", "merge_visit"))));
    }

    /// <summary>A visit variable under another casing is still a visit variable: not the refusing shape.</summary>
    [Fact]
    public void A_refusing_wait_with_a_visit_variable_in_any_casing_is_not_refusing()
    {
        var wait = Refusing("merge-approval");
        wait["VisitVar"] = "merge_visit";

        Assert.Empty(EpicJson.GuardedGates(Definition(Wait("merge-approval", "merge_visit"), wait)));
    }

    /// <summary>
    /// The shipped seed passes the strict rule, gate by gate. The PR definition reads
    /// <c>merge-approval</c> in four more places than its gate (late feedback in the merge hold,
    /// the stall of a held merge and at the merge entry); each of those refuses.
    /// </summary>
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

    /// <summary>
    /// What makes the shipped PR definition's merge gate delegable, wait by wait: one guarded gate
    /// and four refusing reads. Taking the refusing guard off any one of the four switches the
    /// gate's delegation off.
    /// </summary>
    [Fact]
    public void The_shipped_pr_definition_has_one_guarded_merge_gate_and_four_refusing_reads()
    {
        var definition = ShippedDefinition("UwePrImplementationWorkflow");
        var waits = Descendants(definition)
            .Where(o => o["type"]?.GetValue<string>() == "wait_for_signal" && o["signalName"]?.GetValue<string>() == "merge-approval")
            .ToList();

        Assert.Equal(5, waits.Count);
        var refusing = waits.Where(w => w["delegatedGuard"]?["refuse"] is not null).ToList();
        Assert.Equal(
            ["merge_entry_feedback_take", "merge_hold_feedback_take", "merge_hold_feedback_wait", "stalled_feedback_take"],
            refusing.Select(w => w["name"]!.GetValue<string>()).Order());
        Assert.All(refusing, w => Assert.Equal("""{"marker":"GrantId","refuse":true}""", w["delegatedGuard"]!.ToJsonString()));
        Assert.Equal("merge_approval_signal", Assert.Single(waits.Except(refusing))["name"]!.GetValue<string>());

        foreach (var read in refusing)
        {
            var guard = read["delegatedGuard"]!.DeepClone();
            read.Remove("delegatedGuard");
            Assert.Empty(EpicJson.GuardedGates(definition.ToJsonString()));
            read["delegatedGuard"] = guard;
        }
        Assert.Equal(["merge-approval"], EpicJson.GuardedGates(definition.ToJsonString()));
    }

    private static JsonNode ShippedDefinition(string workflow)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "seed.example.json")))
            dir = dir.Parent;
        Assert.NotNull(dir);

        var seed = JsonNode.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "seed.example.json")))!;
        return seed["workflowDefinitions"]!.AsArray()
            .Single(d => d!["name"]!.GetValue<string>() == workflow)!["definition"]!;
    }

    private static IEnumerable<JsonObject> Descendants(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                yield return obj;
                foreach (var property in obj)
                    foreach (var child in Descendants(property.Value)) yield return child;
                break;
            case JsonArray array:
                foreach (var item in array)
                    foreach (var child in Descendants(item)) yield return child;
                break;
        }
    }

    private static JsonObject Refusing(string signal) => new()
    {
        ["type"] = "wait_for_signal",
        ["signalName"] = signal,
        ["timeoutMinutes"] = 0,
        ["autoCompleteOnTimeout"] = true,
        ["delegatedGuard"] = new JsonObject { ["marker"] = "GrantId", ["refuse"] = true },
    };

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
