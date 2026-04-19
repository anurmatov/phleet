using Fleet.Orchestrator.Services;

namespace Fleet.Orchestrator.Tests;

/// <summary>
/// Verifies that FLEET_CTO_AGENT is non-editable in the shipped registry.
/// The PUT /api/credentials/{key} endpoint checks entry.Editable before writing;
/// when false it immediately returns 403 { error: "not_editable" }.
/// </summary>
public class FleetCtoAgentEditabilityTests
{
    [Fact]
    public void ShippedRegistry_FleetCtoAgent_IsNotEditable()
    {
        // Load the actual credentials-registry.json copied to the test output directory.
        var registryPath = Path.Combine(AppContext.BaseDirectory, "credentials-registry.json");
        var registry = CredentialsService.LoadRegistry(registryPath);

        Assert.True(
            registry.TryGet("FLEET_CTO_AGENT", out var entry) && entry is not null,
            "FLEET_CTO_AGENT must exist in credentials-registry.json");

        Assert.False(
            entry!.Editable,
            "FLEET_CTO_AGENT is system-managed (written by WriteCtoAgentAsync) — " +
            "setting editable:false ensures PUT /api/credentials/FLEET_CTO_AGENT returns 403 not_editable");
    }
}
