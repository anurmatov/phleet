using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Tools;
using Fleet.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleet.Orchestrator.Tests.Tools;

/// <summary>
/// #357 MCP patch path: accepted values persist, 9/601 are refused without saving, omission keeps
/// the current value, and a fresh agent row defaults to 60.
/// </summary>
public class UpdateAgentConfigWarmupTimeoutTests
{
    private static OrchestratorDbContext CreateDb(string name) =>
        new(new DbContextOptionsBuilder<OrchestratorDbContext>().UseInMemoryDatabase(name).Options);

    // A fresh context per scope over the same in-memory store: the tool disposes the scope it
    // creates, so handing it one shared context would dispose the one the assertions read.
    private static IServiceScopeFactory BuildScopeFactory(string name)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => CreateDb(name));
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static UpdateAgentConfigTool CreateTool(string name) =>
        new(BuildScopeFactory(name), new NoOpAclChangeNotifier());

    private static Agent Current(string name) =>
        CreateDb(name).Agents.AsNoTracking().Single(a => a.Name == "agent1");

    [Theory]
    [InlineData(10)]
    [InlineData(180)]
    [InlineData(600)]
    public async Task InRange_Persists(int seconds)
    {
        var name = $"accept-{seconds}";
        Seed(name);
        var tool = CreateTool(name);

        var result = await tool.UpdateAgentConfigAsync("agent1", warmup_timeout_seconds: seconds);

        Assert.Contains($"warmup_timeout_seconds: 60 → {seconds}", result);
        Assert.Equal(seconds, Current(name).WarmupTimeoutSeconds);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(601)]
    public async Task OutOfRange_Refused_AndNothingSaved(int seconds)
    {
        var name = $"reject-{seconds}";
        Seed(name);
        var tool = CreateTool(name);

        var result = await tool.UpdateAgentConfigAsync("agent1", warmup_timeout_seconds: seconds);

        Assert.Equal(WarmupTimeout.DescribeFault(seconds), result);
        Assert.Equal(WarmupTimeout.DefaultSeconds, Current(name).WarmupTimeoutSeconds);
    }

    [Fact]
    public async Task Omitted_KeepsTheCurrentValue()
    {
        var name = "omit";
        Seed(name);
        var tool = CreateTool(name);
        await tool.UpdateAgentConfigAsync("agent1", warmup_timeout_seconds: 180);

        var result = await tool.UpdateAgentConfigAsync("agent1", model: "claude-opus-4-8");

        Assert.DoesNotContain("warmup_timeout_seconds", result);
        Assert.Equal(180, Current(name).WarmupTimeoutSeconds);
    }

    [Fact]
    public void NewAgentRow_DefaultsToSixty()
    {
        var name = "default";
        Seed(name);

        Assert.Equal(WarmupTimeout.DefaultSeconds, Current(name).WarmupTimeoutSeconds);
    }

    private static void Seed(string name)
    {
        using var db = CreateDb(name);
        db.Agents.Add(new Agent
        {
            Name = "agent1",
            DisplayName = "agent1",
            Role = "test",
            Model = "claude-sonnet-4-6",
            Provider = "claude",
            MemoryLimitMb = 1024,
            ContainerName = "agent1",
        });
        db.SaveChanges();
    }

    private sealed class NoOpAclChangeNotifier : IAclChangeNotifier
    {
        public Task PublishAclChangedAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
