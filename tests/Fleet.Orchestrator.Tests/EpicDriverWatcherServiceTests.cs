using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using Fleet.Orchestrator.Tests.EpicDrivers;

namespace Fleet.Orchestrator.Tests;

public class EpicDriverWatcherServiceTests
{
    [Fact]
    public async Task Scan_SeveralTriggers_OneExactRunWakeAndNoDuplicateOnOverlap()
    {
        using var f = new DriverFixture(); await f.SeedDefinitionAsync();
        f.Temporal.Add("failed", "UwePrImplementationWorkflow", "Failed");
        f.Temporal.Add("gate", "UwePrImplementationWorkflow", gate: "merge-approval:2");
        var watcher = f.Watcher();
        Assert.True(await watcher.ScanAsync(default));
        var wake = Assert.Single(f.Temporal.Signals); Assert.Equal("driver-run", wake.Run); Assert.Equal("blocker-resolved", wake.Signal);
        var json = JsonSerializer.SerializeToElement(wake.Payload);
        Assert.Equal(["decision", "blockerRef"], json.EnumerateObject().Select(p => p.Name));
        Assert.Equal("wake", json.GetProperty("decision").GetString());
        f.Clock.Advance(TimeSpan.FromSeconds(300));
        Assert.True(await watcher.ScanAsync(default)); Assert.Single(f.Temporal.Signals);
    }

    [Fact]
    public async Task Scan_GrantCreationOverlapAndNotFound_AreDeduplicatedWithoutSignalsToChildren()
    {
        using var f = new DriverFixture(); await f.SeedDefinitionAsync();
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Fleet.Orchestrator.Data.OrchestratorDbContext>();
            db.EpicGrants.Add(new() { Id = Guid.NewGuid(), DriverNamespace = "fleet", DriverWorkflowId = "driver", DriverRunId = "driver-run",
                Status = "active", ScopeJson = "{}", ScopeSha256 = new string('a', 64), CtoAgent = "agent1",
                CreatedAt = f.Clock.Now.AddSeconds(-10).UtcDateTime, ExpiresAt = f.Clock.Now.AddHours(1).UtcDateTime });
            await db.SaveChangesAsync();
        }
        var watcher = f.Watcher(); f.Temporal.SendResult = "not_found";
        Assert.True(await watcher.ScanAsync(default)); var wake = Assert.Single(f.Temporal.Signals);
        Assert.Contains("epic-watcher:grant-created:", JsonSerializer.Serialize(wake.Payload)); Assert.Equal("driver", wake.Id);
        f.Clock.Advance(TimeSpan.FromSeconds(20)); Assert.True(await watcher.ScanAsync(default)); Assert.Single(f.Temporal.Signals);
    }

    [Theory]
    [InlineData(false, 300, true)]
    [InlineData(true, 30, true)]
    [InlineData(true, 300, false)]
    public async Task Scan_DisabledMisconfiguredOrOldDefinition_NeverSignals(bool enabled, int interval, bool marker)
    {
        using var f = new DriverFixture(); await f.SeedDefinitionAsync(marker);
        f.Temporal.Add("failed", "UweDesignWorkflow", "Terminated");
        await f.Watcher(enabled, interval).ScanAsync(default); Assert.Empty(f.Temporal.Signals);
    }

    [Fact]
    public async Task Scan_FailedVisibilityAndFailedSend_DoNotLoseTheTrigger()
    {
        using var f = new DriverFixture(); await f.SeedDefinitionAsync();
        f.Temporal.Add("failed", "UwePrImplementationWorkflow", "Failed");
        var watcher = f.Watcher(); f.Temporal.VisibilityError = "page_failed";
        Assert.False(await watcher.ScanAsync(default)); Assert.Empty(f.Temporal.Signals);
        f.Temporal.VisibilityError = null; f.Temporal.SendResult = "failed";
        Assert.False(await watcher.ScanAsync(default)); Assert.Single(f.Temporal.Signals);
        f.Temporal.SendResult = "sent"; Assert.True(await watcher.ScanAsync(default)); Assert.Equal(2, f.Temporal.Signals.Count);
        Assert.True(await watcher.ScanAsync(default)); Assert.Equal(2, f.Temporal.Signals.Count);
    }

    [Fact]
    public async Task Scan_CompletedChild_IsSkipped()
    {
        using var f = new DriverFixture(); await f.SeedDefinitionAsync();
        f.Temporal.Add("completed", "UweDesignWorkflow", "Completed");
        Assert.True(await f.Watcher().ScanAsync(default)); Assert.Empty(f.Temporal.Signals);
    }
}
