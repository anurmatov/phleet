using System.Text.Json;
using Fleet.Orchestrator.Data;
using Fleet.Shared;

namespace Fleet.Orchestrator.Tests.Services;

/// <summary>
/// #357: the persisted <c>WarmupTimeoutSeconds</c> is the single timeout source — provisioning
/// writes it into the agent's generated <c>appsettings.json</c>, with 60 as the default for rows
/// that never set one.
/// </summary>
public class WarmupTimeoutProvisioningTests
{
    [Theory]
    [InlineData(null, 60)]
    [InlineData(180, 180)]
    public async Task GeneratedAppsettings_CarryThePersistedValue(int? configured, int expected)
    {
        await using var harness = ProvisioningHarness.Create();
        await harness.SeedAsync(db =>
        {
            var agent = new Agent
            {
                Name = "agent-warm",
                DisplayName = "Agent Warm",
                Role = "developer",
                Model = "model-x",
                Provider = "claude",
                ContainerName = "fleet-agent-warm",
                MemoryLimitMb = 512,
            };
            if (configured is not null)
                agent.WarmupTimeoutSeconds = configured.Value;
            db.Agents.Add(agent);
        });

        var result = await harness.Service.ProvisionAsync("agent-warm");
        Assert.True(result.Success, result.Message);

        var appsettings = await File.ReadAllTextAsync(
            Path.Combine(harness.GeneratedDir("fleet-agent-warm"), "appsettings.json"));
        using var doc = JsonDocument.Parse(appsettings);

        var actual = doc.RootElement.GetProperty("Agent").GetProperty("WarmupTimeoutSeconds").GetInt32();
        Assert.Equal(expected, actual);
        Assert.Equal(WarmupTimeout.DefaultSeconds, 60);
    }
}
