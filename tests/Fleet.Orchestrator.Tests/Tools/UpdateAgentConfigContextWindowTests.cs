using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Tools;
using Fleet.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleet.Orchestrator.Tests.Tools;

/// <summary>
/// #367 MCP patch path: in-range values persist, out-of-range ones are refused without saving,
/// 0 clears, omission keeps the current value, and a fresh agent row has no value.
/// </summary>
public class UpdateAgentConfigContextWindowTests
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
    [InlineData(4_096)]
    [InlineData(131_072)]
    [InlineData(1_048_576)]
    public async Task InRange_Persists(int tokens)
    {
        var name = $"ctx-accept-{tokens}";
        Seed(name);
        var tool = CreateTool(name);

        var result = await tool.UpdateAgentConfigAsync("agent1", context_window: tokens);

        Assert.Contains($"context_window: (none) → {tokens}", result);
        Assert.Equal(tokens, Current(name).ContextWindow);
    }

    [Theory]
    [InlineData(4_095)]
    [InlineData(1_048_577)]
    public async Task OutOfRange_Refused_AndNothingSaved(int tokens)
    {
        var name = $"ctx-reject-{tokens}";
        Seed(name);
        var tool = CreateTool(name);

        var result = await tool.UpdateAgentConfigAsync("agent1", context_window: tokens);

        Assert.Equal(ContextWindow.DescribeFault(tokens), result);
        Assert.Null(Current(name).ContextWindow);
    }

    [Fact]
    public async Task Zero_Clears()
    {
        var name = "ctx-clear";
        Seed(name);
        var tool = CreateTool(name);
        await tool.UpdateAgentConfigAsync("agent1", context_window: 65_536);

        var result = await tool.UpdateAgentConfigAsync("agent1", context_window: 0);

        Assert.Contains("context_window: 65536 → (none)", result);
        Assert.Null(Current(name).ContextWindow);
    }

    [Fact]
    public async Task Omitted_KeepsTheCurrentValue()
    {
        var name = "ctx-omit";
        Seed(name);
        var tool = CreateTool(name);
        await tool.UpdateAgentConfigAsync("agent1", context_window: 65_536);

        var result = await tool.UpdateAgentConfigAsync("agent1", memory_limit_mb: 2048);

        Assert.DoesNotContain("context_window", result);
        Assert.Equal(65_536, Current(name).ContextWindow);
    }

    [Fact]
    public void NewAgentRow_HasNoValue()
    {
        var name = "ctx-default";
        Seed(name);

        Assert.Null(Current(name).ContextWindow);
    }

    private static void Seed(string name)
    {
        using var db = CreateDb(name);
        db.Agents.Add(new Agent
        {
            Name = "agent1",
            DisplayName = "agent1",
            Role = "test",
            Model = "qwen3.8:27b-agent",
            Provider = "claude",
            AnthropicBaseUrl = "http://inference-host:11434",
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
