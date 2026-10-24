using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Fleet.Orchestrator.Configuration;
using Fleet.Orchestrator.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Temporalio.Api.WorkflowService.V1;
using Temporalio.Client;

namespace Fleet.Orchestrator.Tests.WorkflowOutcomes;

/// <summary>
/// #476 source gap: a namespace that fails to connect must not be cached out for the process
/// lifetime, and a failure must be distinguishable from an empty result.
/// </summary>
public sealed class TemporalClientRegistryReconnectTests
{
    private static TemporalClientRegistry Registry(OutcomeClock clock, List<(string Ns, DateTime At)> attempts,
        Func<string, int, ITemporalClient> connect, params string[] namespaces)
    {
        var calls = new Dictionary<string, int>();
        return new TemporalClientRegistry(
            Options.Create(new TemporalOptions { Address = "temporal:7233", Namespaces = [.. namespaces] }),
            NullLogger<TemporalClientRegistry>.Instance)
        {
            Clock = clock,
            Connector = (ns, _) =>
            {
                attempts.Add((ns, clock.UtcNow));
                calls[ns] = calls.GetValueOrDefault(ns) + 1;
                return Task.FromResult(connect(ns, calls[ns]));
            },
        };
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "catchup")]
    public async Task GetClients_NamespaceDownAtStartup_RetriedWithBackoffAndNotCachedOut()
    {
        var clock = new OutcomeClock(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
        var attempts = new List<(string Ns, DateTime At)>();
        var a = Substitute.For<ITemporalClient>();
        var b = Substitute.For<ITemporalClient>();
        var registry = Registry(clock, attempts, (ns, n) => ns == "namespace-b" && n <= 2 ? throw new InvalidOperationException("down") : ns == "namespace-a" ? a : b,
            "namespace-a", "namespace-b");

        Assert.Equal(["namespace-a"], (await registry.GetClientsAsync()).Keys);
        Assert.Equal(["namespace-a"], (await registry.GetClientsAsync()).Keys);
        Assert.Single(attempts, x => x.Ns == "namespace-b"); // inside the 5 s backoff: no reconnect storm

        var failed = await registry.TryGetClientAsync("namespace-b");
        Assert.Null(failed.Client);
        Assert.Equal("connect_failed:InvalidOperationException", failed.Error);

        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Null(await registry.GetClientAsync("namespace-b")); // second failure, backoff now 10 s
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Null(await registry.GetClientAsync("namespace-b"));
        Assert.Equal(2, attempts.Count(x => x.Ns == "namespace-b"));

        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Same(b, await registry.GetClientAsync("namespace-b"));
        Assert.Equal(["namespace-a", "namespace-b"], (await registry.GetClientsAsync()).Keys.Order());
        Assert.Equal(["namespace-a", "namespace-b"], registry.GetNamespaces().Order());
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "catchup")]
    public async Task TryGetClient_ObserverOnlyNamespace_ConnectedOnDemandButNotAddedToDashboardFeeds()
    {
        var clock = new OutcomeClock(DateTimeOffset.UtcNow);
        var attempts = new List<(string Ns, DateTime At)>();
        var client = Substitute.For<ITemporalClient>();
        var registry = Registry(clock, attempts, (_, _) => client, "namespace-a");

        Assert.Null(await registry.GetClientAsync("namespace-z")); // unconfigured for dashboards: unchanged behavior
        Assert.DoesNotContain(attempts, x => x.Ns == "namespace-z");

        var observed = await registry.TryGetClientAsync("namespace-z");
        Assert.Same(client, observed.Client);
        Assert.Null(observed.Error);
        Assert.Equal(["namespace-a"], (await registry.GetClientsAsync()).Keys);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "failure-matrix")]
    public async Task TryGetClient_TemporalNotConfigured_ErrorNotEmpty()
    {
        var registry = new TemporalClientRegistry(Options.Create(new TemporalOptions()), NullLogger<TemporalClientRegistry>.Instance);

        var result = await registry.TryGetClientAsync("namespace-a");

        Assert.Null(result.Client);
        Assert.Equal("temporal_not_configured", result.Error);
        Assert.Empty(await registry.GetClientsAsync());
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "failure-matrix")]
    public async Task OutcomeRpc_UnreachableNamespace_UnavailableErrorNotEmptyList()
    {
        var clock = new OutcomeClock(DateTimeOffset.UtcNow);
        var registry = Registry(clock, [], (_, _) => throw new InvalidOperationException("refused"), "namespace-a");
        var rpc = new TemporalWorkflowOutcomeRpc(registry, Options.Create(new WorkflowOutcomeOptions()));

        var ex = await Assert.ThrowsAsync<WorkflowOutcomeReadException>(() =>
            rpc.ListAsync(new ListWorkflowExecutionsRequest { Namespace = "namespace-a", PageSize = 100 }, default));

        Assert.Equal(WorkflowOutcomeReadFailure.Unavailable, ex.Kind);
        Assert.Equal("connect_failed:InvalidOperationException", ex.Reason);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "failure-matrix")]
    public async Task OutcomeRpc_ServerNeverAnswers_BoundedByRpcTimeout()
    {
        // A listener that accepts TCP and never speaks gRPC: the call must end at the RPC bound.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accepted = new List<TcpClient>();
        _ = Task.Run(async () => { try { while (true) accepted.Add(await listener.AcceptTcpClientAsync()); } catch { } });
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var registry = new TemporalClientRegistry(
            Options.Create(new TemporalOptions { Address = "127.0.0.1:" + port, Namespaces = ["namespace-a"] }), NullLogger<TemporalClientRegistry>.Instance)
        {
            Connector = (ns, _) => Task.FromResult<ITemporalClient>(TemporalClient.CreateLazy(new TemporalClientConnectOptions("127.0.0.1:" + port) { Namespace = ns })),
        };
        var rpc = new TemporalWorkflowOutcomeRpc(registry, Options.Create(new WorkflowOutcomeOptions { RpcTimeoutSeconds = 1 }));
        var watch = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<WorkflowOutcomeReadException>(() =>
            rpc.DescribeNamespaceAsync(new DescribeNamespaceRequest { Namespace = "namespace-a" }, default));

        Assert.Equal(WorkflowOutcomeReadFailure.Unavailable, ex.Kind);
        Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5));
        listener.Stop();
        foreach (var c in accepted) c.Dispose();
    }
}
