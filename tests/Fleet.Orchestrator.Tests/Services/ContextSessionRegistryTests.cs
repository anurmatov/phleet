using Fleet.Orchestrator.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;

namespace Fleet.Orchestrator.Tests.Services;

/// <summary>The binding table's lifetime follows the transport's own limits (#347 D6).</summary>
public class ContextSessionRegistryTests
{
    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (ContextSessionRegistry Registry, ManualTime Time) Create(Action<HttpServerTransportOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.Configure<HttpServerTransportOptions>(o => configure?.Invoke(o));
        var monitor = services.BuildServiceProvider().GetRequiredService<IOptionsMonitor<HttpServerTransportOptions>>();
        var time = new ManualTime();
        return (new ContextSessionRegistry(monitor, time), time);
    }

    [Fact]
    public void Bind_ThenLookup_ReturnsTheAgent()
    {
        var (registry, _) = Create();

        registry.Bind("s1", "agent-a");

        Assert.True(registry.TryGetAgent("s1", out var agent));
        Assert.Equal("agent-a", agent);
        Assert.False(registry.TryGetAgent("s2", out _));
    }

    [Fact]
    public void Remove_DropsTheBinding()
    {
        var (registry, _) = Create();
        registry.Bind("s1", "agent-a");

        Assert.True(registry.Remove("s1"));
        Assert.False(registry.Contains("s1"));
    }

    [Fact]
    public void Prune_DropsEntriesIdleLongerThanTheTransportIdleTimeout()
    {
        var (registry, time) = Create(); // SDK default: 2 h
        registry.Bind("old", "agent-a");
        time.Now += TimeSpan.FromMinutes(90);
        registry.Bind("young", "agent-a");
        time.Now += TimeSpan.FromMinutes(31); // old: 121 min idle, young: 31 min

        Assert.Equal(1, registry.Prune());
        Assert.False(registry.Contains("old"));
        Assert.True(registry.Contains("young"));
    }

    [Fact]
    public void Touch_RefreshesLastSeen_SoAnActiveSessionIsNotPruned()
    {
        var (registry, time) = Create(o => o.IdleTimeout = TimeSpan.FromMinutes(10));
        registry.Bind("s1", "agent-a");
        time.Now += TimeSpan.FromMinutes(9);
        registry.Touch("s1");
        time.Now += TimeSpan.FromMinutes(9);

        Assert.Equal(0, registry.Prune());
        Assert.True(registry.Contains("s1"));
    }

    [Fact]
    public void Prune_WithAnInfiniteIdleTimeout_KeepsEverything()
    {
        var (registry, time) = Create(o => o.IdleTimeout = Timeout.InfiniteTimeSpan);
        registry.Bind("s1", "agent-a");
        time.Now += TimeSpan.FromDays(30);

        Assert.Equal(0, registry.Prune());
    }

    [Fact]
    public void Bind_BeyondMaxIdleSessionCount_EvictsTheOldestFirst()
    {
        var (registry, time) = Create(o => o.MaxIdleSessionCount = 2);
        registry.Bind("s1", "agent-a");
        time.Now += TimeSpan.FromSeconds(1);
        registry.Bind("s2", "agent-a");
        time.Now += TimeSpan.FromSeconds(1);
        registry.Touch("s1"); // s2 is now the least recently seen
        time.Now += TimeSpan.FromSeconds(1);
        registry.Bind("s3", "agent-b");

        Assert.Equal(2, registry.Count);
        Assert.False(registry.Contains("s2"));
        Assert.True(registry.Contains("s1"));
        Assert.True(registry.Contains("s3"));
    }

    [Fact]
    public void Defaults_AreReadFromTheSdkOptions()
    {
        var options = new HttpServerTransportOptions();

        // The registry has no limits of its own; these are the SDK's, and the spec's numbers.
        Assert.Equal(TimeSpan.FromHours(2), options.IdleTimeout);
        Assert.Equal(10_000, options.MaxIdleSessionCount);
    }
}
