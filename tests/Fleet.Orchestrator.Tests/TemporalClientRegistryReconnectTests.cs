using Fleet.Orchestrator.Configuration;
using Fleet.Orchestrator.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Temporalio.Client;

namespace Fleet.Orchestrator.Tests;

/// <summary>
/// A Temporal namespace that was unreachable is tried again, not remembered as absent for the life
/// of the process, and the poller that depends on it recovers with it.
/// </summary>
public class TemporalClientRegistryReconnectTests
{
    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Temporal
    {
        private readonly object _lock = new();
        private readonly HashSet<string> _down = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _attempts = [];

        public ManualTime Time { get; } = new();
        public TemporalClientRegistry Registry { get; }

        public Temporal(params string[] namespaces)
        {
            Registry = new TemporalClientRegistry(
                Options.Create(new TemporalOptions { Address = "temporal.example:7233", Namespaces = [.. namespaces], PollIntervalSeconds = 1 }),
                NullLogger<TemporalClientRegistry>.Instance)
            {
                Time = Time,
                Connect = (_, ns) =>
                {
                    lock (_lock)
                    {
                        _attempts.Add(ns);
                        if (_down.Contains(ns))
                            return Task.FromException<ITemporalClient>(new InvalidOperationException("connection refused"));
                    }
                    return Task.FromResult(Substitute.For<ITemporalClient>());
                },
            };
        }

        public IReadOnlyList<string> Attempts { get { lock (_lock) return _attempts.ToList(); } }
        public void Down(params string[] namespaces) { lock (_lock) _down.UnionWith(namespaces); }
        public void Up(params string[] namespaces) { lock (_lock) _down.ExceptWith(namespaces); }
        public void PassCooldown() => Time.Now += TemporalClientRegistry.RetryCooldown;
    }

    [Fact]
    public async Task ANamespaceThatWasDown_IsConnectedByALaterCall()
    {
        var temporal = new Temporal("fleet");
        temporal.Down("fleet");

        Assert.Empty(await temporal.Registry.GetClientsAsync());
        Assert.Null(await temporal.Registry.GetClientAsync("fleet"));

        temporal.Up("fleet");
        temporal.PassCooldown();

        Assert.NotNull(await temporal.Registry.GetClientAsync("fleet"));
        Assert.Equal(["fleet"], temporal.Registry.GetNamespaces());
    }

    [Fact]
    public async Task ADownNamespace_IsNotTriedAgainBeforeTheCooldown()
    {
        var temporal = new Temporal("fleet");
        temporal.Down("fleet");

        for (var i = 0; i < 20; i++)
            await temporal.Registry.GetClientsAsync();
        Assert.Single(temporal.Attempts);

        temporal.Time.Now += TemporalClientRegistry.RetryCooldown - TimeSpan.FromSeconds(1);
        await temporal.Registry.GetClientsAsync();
        Assert.Single(temporal.Attempts);

        temporal.Time.Now += TimeSpan.FromSeconds(1);
        await temporal.Registry.GetClientsAsync();
        Assert.Equal(2, temporal.Attempts.Count);
    }

    [Fact]
    public async Task AConnectedNamespace_IsKept_WhileTheOtherIsRetried()
    {
        var temporal = new Temporal("fleet", "research");
        temporal.Down("research");

        var first = await temporal.Registry.GetClientsAsync();
        var fleet = Assert.Single(first).Value;

        temporal.Up("research");
        temporal.PassCooldown();
        var second = await temporal.Registry.GetClientsAsync();

        Assert.Equal(2, second.Count);
        Assert.Same(fleet, second["fleet"]);
        // fleet once; research once failed, once connected.
        Assert.Equal(["fleet", "research", "research"], temporal.Attempts);
        // The dictionary an earlier caller holds is not changed under it.
        Assert.Single(first);
    }

    [Fact]
    public async Task OnceEveryNamespaceIsConnected_NothingIsTriedAgain()
    {
        var temporal = new Temporal("fleet", "research");

        var first = await temporal.Registry.GetClientsAsync();
        temporal.PassCooldown();
        temporal.PassCooldown();
        var second = await temporal.Registry.GetClientsAsync();

        Assert.Same(first, second);
        Assert.Equal(2, temporal.Attempts.Count);
    }

    [Fact]
    public async Task ThePoller_StartedWhileTemporalIsDown_PicksItUpWhenItReturns()
    {
        var temporal = new Temporal("fleet");
        temporal.Down("fleet");
        var store = new WorkflowStore();
        var agents = new AgentRegistry(new TaskHistoryStore(), NullLogger<AgentRegistry>.Instance);
        var poller = new TemporalPollerService(
            Options.Create(new TemporalOptions { Address = "temporal.example:7233", Namespaces = ["fleet"], PollIntervalSeconds = 1 }),
            store, agents, temporal.Registry, NullLogger<TemporalPollerService>.Instance);

        await poller.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => temporal.Attempts.Count >= 1);
            Assert.Empty(store.Snapshot().Namespaces);

            temporal.Up("fleet");
            temporal.PassCooldown();

            await WaitUntilAsync(() => store.Snapshot().Namespaces.Contains("fleet"));
        }
        finally
        {
            await poller.StopAsync(CancellationToken.None);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition was not reached in time");
            await Task.Delay(50);
        }
    }
}
