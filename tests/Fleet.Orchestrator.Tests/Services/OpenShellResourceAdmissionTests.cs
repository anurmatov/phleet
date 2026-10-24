using System.Text.Json;
using Fleet.Orchestrator.Services;

namespace Fleet.Orchestrator.Tests.Services;

public sealed class OpenShellResourceAdmissionTests
{
    [Theory]
    [InlineData("default", true)]
    [InlineData("custom", true)]
    [InlineData("custom_missing_label", false)]
    [InlineData("custom_wrong_workspace", false)]
    [InlineData("missing_current_policy", false)]
    [InlineData("missing_approval", false)]
    [InlineData("missing_label", false)]
    [InlineData("wrong_workspace", false)]
    [InlineData("policy_drift", false)]
    [InlineData("disabled", false)]
    [InlineData("driver_json_disabled", false)]
    [InlineData("reserved", false)]
    [InlineData("invalid_policy_key", false)]
    [InlineData("invalid_extra_key", false)]
    public async Task ApprovedPlanLabelsReachActualVolumeRequestOrRefuseBeforeAnyMutation(string fault, bool accepted)
    {
        var required = new SortedDictionary<string, string> { ["openshell.ai/sandbox-attachable"] = "true", ["openshell.ai/sandbox-attachable-workspace"] = "${workspace}" };
        if (fault.StartsWith("custom")) required = new() { ["example.invalid/approved"] = "yes", ["example.invalid/workspace"] = "${workspace}" };
        if (fault == "invalid_policy_key") required[".invalid/approved"] = "yes";
        var labels = new SortedDictionary<string, string>(required.ToDictionary(kv => kv.Key, kv => kv.Value == "${workspace}" ? "workspace1" : kv.Value));
        if (fault.EndsWith("missing_label")) labels.Remove(labels.Keys.First());
        if (fault.EndsWith("wrong_workspace")) labels[required.Single(p => p.Value == "${workspace}").Key] = "other";
        if (fault == "invalid_extra_key") labels["-invalid"] = "x";
        if (fault == "reserved") labels["openshell.ai/managed-by"] = "forged";
        var policy = "v1:" + JsonSerializer.Serialize(new { allow_driver_config = fault != "driver_json_disabled", resource_admission = new { enabled = fault != "disabled", required_labels = required } });
        var approved = new OpenShellResourceAdmission(fault == "missing_approval" ? "" : "owned-approval1", policy, JsonSerializer.Serialize(labels));
        using var wire = new OpenShellBootstrapArtifactTests.OwnedEngine();
        using var engine = new HttpClient(wire) { BaseAddress = new("http://owned-engine.invalid") };
        var artifact = new OpenShellBootstrapArtifacts(engine, wire.Custody, fault == "missing_current_policy" ? null : fault == "policy_drift" ? policy.Replace("\"true\"", "\"different\"") : policy);
        var original = OpenShellPolicyCompilerTests.Plan();
        async Task Run()
        {
            var plan = new OpenShellPolicyCompiler().Compile("gateway1", "workspace1", original.ReadSpec(), original.InputHash,
                original.ReadExpectedProviders(), original.BootstrapIngress, original.BootstrapCapabilityFile, approved);
            Assert.NotEqual(original.Hash, (plan with { ResourceAdmissionJson = "" }).Hash);
            await artifact.DeliverAsync(plan, Guid.NewGuid(), Guid.NewGuid(), default);
            foreach (var (key, value) in labels) Assert.Equal(value, wire.VolumeLabels[key]);
        }
        if (accepted) { await Run(); Assert.Equal(1, wire.HelperCreates); }
        else { await Assert.ThrowsAnyAsync<Exception>(Run); Assert.Equal(0, wire.HelperCreates); Assert.Equal(0, wire.VolumeCreates); }
    }
}
