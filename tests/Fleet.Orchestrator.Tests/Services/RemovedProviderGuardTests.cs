using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Tests.Services;

public class RemovedProviderGuardTests
{
    private static OrchestratorDbContext CreateDb() => new(new DbContextOptionsBuilder<OrchestratorDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task EnsureSupportedProviders_PiRowsIncludingDisabled_NamesAgentsAndRefuses()
    {
        await using var db = CreateDb();
        db.Agents.AddRange(
            new Agent { DisplayName = "test", Role = "test", Model = "test", ContainerName = "test", Name = "agent-b", Provider = "pi", IsEnabled = false },
            new Agent { DisplayName = "test", Role = "test", Model = "test", ContainerName = "test", Name = "agent-a", Provider = "pi" },
            new Agent { DisplayName = "test", Role = "test", Model = "test", ContainerName = "test", Name = "agent-c", Provider = "codex" });
        await db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RemovedProviderGuard.EnsureAsync(db));
        Assert.Contains("agent-a, agent-b", error.Message);
        Assert.Contains("reprovision before upgrading", error.Message);
        Assert.DoesNotContain("agent-c", error.Message);
    }

    [Fact]
    public async Task EnsureSupportedProviders_NoPiRows_AllowsStartup()
    {
        await using var db = CreateDb();
        db.Agents.AddRange(new Agent { DisplayName = "test", Role = "test", Model = "test", ContainerName = "test", Name = "agent-a", Provider = "claude" },
            new Agent { DisplayName = "test", Role = "test", Model = "test", ContainerName = "test", Name = "agent-b", Provider = "codex" },
            new Agent { DisplayName = "test", Role = "test", Model = "test", ContainerName = "test", Name = "agent-c", Provider = "gemini" });
        await db.SaveChangesAsync();
        await RemovedProviderGuard.EnsureAsync(db);
    }

    [Fact]
    public async Task EnsureSupportedProviders_EmptyDatabase_AllowsStartup()
    {
        await using var db = CreateDb();
        await RemovedProviderGuard.EnsureAsync(db);
    }
}
