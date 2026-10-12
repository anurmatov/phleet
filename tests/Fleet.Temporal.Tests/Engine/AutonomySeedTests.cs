using System.Text.Json.Nodes;
using Fleet.Temporal.Engine;

namespace Fleet.Temporal.Tests.Engine;

public sealed class AutonomySeedTests
{
    private static JsonObject Definition(string name)
    {
        for (var path=new DirectoryInfo(AppContext.BaseDirectory);path is not null;path=path.Parent)
            if (File.Exists(Path.Combine(path.FullName,"seed.example.json")))
            {
                var seed=JsonNode.Parse(File.ReadAllText(Path.Combine(path.FullName,"seed.example.json")))!;
                return seed["workflowDefinitions"]!.AsArray().Single(w=>w!["name"]!.GetValue<string>()==name)!["definition"]!.AsObject();
            }
        throw new InvalidOperationException("Seed not found");
    }
    private static IEnumerable<JsonObject> Walk(JsonNode? node)
    {
        if (node is JsonObject obj)
        { yield return obj; foreach(var value in obj.Select(p=>p.Value)) foreach(var child in Walk(value)) yield return child; }
        else if (node is JsonArray array) foreach(var item in array) foreach(var child in Walk(item)) yield return child;
    }
    [Theory]
    [InlineData("UweDesignWorkflow")]
    [InlineData("UweDesignToPrWorkflow")]
    [InlineData("UweCarteBlancheWorkflow")]
    public void RevisedSeeds_ValidateWithExistingEngine(string name) => SeedHarness.Parse(name);

    [Fact]
    public void Design_ScopeFitBranchIsAdjacentSiblingAndBothBooleanCasesOnlyAppendToken()
    {
        var definition=Definition("UweDesignWorkflow");
        var parent=Walk(definition).Single(n=>n["steps"] is JsonArray children && children.Any(c=>c?["name"]?.GetValue<string>()=="design_publish_scope_fit"));
        var steps=parent["steps"]!.AsArray(); var index=steps.ToList().FindIndex(n=>n?["name"]?.GetValue<string>()=="design_publish_scope_fit");
        Assert.Equal("design_publish_review_attrs",steps[index+1]!["name"]!.GetValue<string>());
        var branch=steps[index]!;
        Assert.Equal("{{vars.consensus_out.ScopeFitAttested | default: 'false'}}",branch["on"]!.GetValue<string>());
        Assert.Equal(new[] { "True","true" },branch["cases"]!.AsObject().Select(p=>p.Key));
        foreach(var boolean in new[] { "True","true" })
            Assert.Equal("{{vars.review_scrub}} scope-fit",branch["cases"]![boolean]!["vars"]!["review_scrub"]!.GetValue<string>());
        foreach(var name in new[] { "consensus_review","ceo_consensus_review" })
        {
            var prompt=Walk(definition).Single(n=>n["name"]?.GetValue<string>()==name)["args"]!["ReviewPrompt"]!.GetValue<string>();
            Assert.Contains("AutonomyGrantId",prompt); Assert.Contains("SCOPE_FIT: fail",prompt); Assert.Contains("SCOPE_FIT: n/a",prompt);
        }
    }
    [Fact]
    public void DesignToPr_ForwardsOptionalCharterAndAcceptanceWithoutAddingSteps()
    {
        var nodes=Walk(Definition("UweDesignToPrWorkflow")).ToArray();
        Assert.Equal("{{input.AutonomyGrantId | default: 'none'}}",nodes.Single(n=>n["name"]?.GetValue<string>()=="design_phase")["args"]!["AutonomyGrantId"]!.GetValue<string>());
        Assert.Equal("{{input.RequireAcceptance | default: 'false'}}",nodes.Single(n=>n["name"]?.GetValue<string>()=="start_implementation")["args"]!["RequireAcceptance"]!.GetValue<string>());
    }
    [Fact]
    public void Driver_AllFiveNonRetryReasonsAndAdmissionArePresentInExistingTickOnly()
    {
        var nodes=Walk(Definition("UweCarteBlancheWorkflow")).ToArray();
        var prompt=nodes.Single(n=>n["name"]?.GetValue<string>()=="epic_tick")["instruction"]!.GetValue<string>();
        foreach(var reason in new[] { "attempt_canceled","attempt_terminated","attempt_completed_not_done","attempt_status_unknown","attempts_exhausted" }) Assert.Contains(reason,prompt);
        foreach(var required in new[] { "id_reuse_policy=reject_duplicate","at most TWO","Failed or TimedOut","/admission?","RequireAcceptance=true","AutonomyGrantId=<grant-id>" }) Assert.Contains(required,prompt);
        Assert.DoesNotContain("belong to the later charter stage",prompt);
    }
}
