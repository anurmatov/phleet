using Fleet.Temporal.Activities;
using Fleet.Temporal.Configuration;
using Fleet.Temporal.Mcp;
using Fleet.Temporal.Models;
using Fleet.Temporal.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RabbitMQ.Client;
using Temporalio.Activities;
using Temporalio.Testing;

namespace Fleet.Temporal.Tests.Services.DurableDelegation;

/// <summary>
/// The switch, at the two places that read it: <see cref="DelegateToAgentActivity"/> and the relay
/// listener's reply routing. Off (and absent, which is what every existing test constructs) must be
/// the path that was always there; on must hand over to the durable service and nothing else.
/// </summary>
public sealed class DurableDelegationSwitchTests
{
    private const string Agent = "reviewer-one";

    private static (DelegateToAgentActivity Activity, TaskCompletionRegistry Registry, List<string> Published) Activity(
        DurableDelegationService? durable, int agentTimeoutSeconds = 30)
    {
        var published = new List<string>();
        var channel = Substitute.For<IChannel>();
        channel.BasicPublishAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(),
                Arg.Any<BasicProperties>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                lock (published) published.Add(System.Text.Encoding.UTF8.GetString(call.ArgAt<ReadOnlyMemory<byte>>(4).Span));
                return ValueTask.CompletedTask;
            });
        var connection = Substitute.For<IConnection>();
        connection.CreateChannelAsync(Arg.Any<CreateChannelOptions?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(channel));
        var connectionFactory = Substitute.For<IConnectionFactory>();
        connectionFactory.CreateConnectionAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(connection));

        var registry = new TaskCompletionRegistry(NullLogger<TaskCompletionRegistry>.Instance);
        var activity = new DelegateToAgentActivity(
            Options.Create(new RabbitMqOptions()),
            Options.Create(new TemporalBridgeOptions { AgentTimeoutSeconds = agentTimeoutSeconds }),
            registry, connectionFactory, Substitute.For<IHttpClientFactory>(),
            NullLogger<DelegateToAgentActivity>.Instance, durable);
        return (activity, registry, published);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 10_000;
        while (!condition() && Environment.TickCount64 < deadline) await Task.Delay(10);
        Assert.True(condition(), "the activity never got that far");
    }

    // ── the activity ──────────────────────────────────────────────────────────

    /// <summary>
    /// Switched off, with the service present as it is in the running bridge: the activity
    /// registers in the in-memory registry, publishes itself, waits, and returns the agent's
    /// answer. The durable service is never asked for anything and writes no file.
    /// </summary>
    [Fact]
    public async Task SwitchedOff_TheActivityWaitsForTheAgentItself_AsItAlwaysDid()
    {
        using var h = new DurableHarness(enabled: false);
        var (activity, registry, published) = Activity(h.Service);

        var run = new ActivityEnvironment().RunAsync(() => activity.DelegateToAgentAsync(Agent, "do the thing", "wf/task-1"));
        await WaitForAsync(() => published.Count == 1);

        Assert.Equal(1, registry.PendingCount);
        Assert.True(registry.TryComplete("wf/task-1", new AgentTaskResult("all done", "completed")));
        Assert.Equal(new AgentTaskResult("all done", "completed"), await run);

        Assert.Empty(h.Relay.Published);
        Assert.Empty(h.Gateway.Calls);
        Assert.Empty(await h.PendingAsync());
        Assert.False(File.Exists(h.StateFile));
    }

    /// <summary>
    /// Switched on: the delegation is recorded with everything needed to finish it later, the
    /// directive goes out through the durable relay, and the activity tells Temporal it completes
    /// asynchronously. Nothing is registered in memory.
    /// </summary>
    [Fact]
    public async Task SwitchedOn_TheActivityRecordsTheDelegation_AndCompletesLater()
    {
        using var h = new DurableHarness();
        var (activity, registry, published) = Activity(h.Service, agentTimeoutSeconds: 1800);
        var env = new ActivityEnvironment();

        await Assert.ThrowsAsync<CompleteAsyncException>(() => env.RunAsync(() =>
            activity.DelegateToAgentAsync(Agent, "do the thing", "wf/task-1", retryOnIncomplete: false, maxIncompleteRetries: 5, repo: " owner/name ")));

        Assert.Equal(0, registry.PendingCount);
        Assert.Empty(published);
        Assert.Equal((Agent, "wf/task-1", "do the thing", "owner/name"), Assert.Single(h.Relay.Published));

        var pending = Assert.Single(await h.PendingAsync());
        Assert.Equal(env.Info.WorkflowId, pending.WorkflowId);
        Assert.Equal(env.Info.WorkflowRunId, pending.RunId);
        Assert.Equal(env.Info.WorkflowNamespace, pending.Namespace);
        Assert.Equal(env.Info.ActivityId, pending.ActivityId);
        Assert.Equal(pending.WaitStartedAt.AddSeconds(1800), pending.Deadline);

        var stored = File.ReadAllText(h.StateFile);
        Assert.Contains($"\"TaskToken\": \"{Convert.ToBase64String(env.Info.TaskToken)}\"", stored);
        Assert.Contains("\"RetryOnIncomplete\": false", stored);
        Assert.Contains("\"MaxIncompleteRetries\": 5", stored);
        Assert.Contains($"\"WorkflowType\": \"{env.Info.WorkflowType}\"", stored);
    }

    /// <summary>An explicit budget is the record's budget, and the timer-ordering guard still runs first.</summary>
    [Fact]
    public async Task SwitchedOn_AnExplicitBudgetIsRecorded_AndAMisorderedOneIsStillRefused()
    {
        using var h = new DurableHarness();
        var (activity, _, _) = Activity(h.Service);

        var roomy = new ActivityEnvironment { Info = ActivityEnvironment.DefaultInfo with { StartToCloseTimeout = TimeSpan.FromMinutes(10) } };
        await Assert.ThrowsAsync<CompleteAsyncException>(() => roomy.RunAsync(() =>
            activity.DelegateToAgentAsync(Agent, "do it", "wf/roomy", agentBudgetSeconds: 120)));
        var pending = Assert.Single(await h.PendingAsync());
        Assert.Equal(pending.WaitStartedAt.AddSeconds(120), pending.Deadline);

        var tight = new ActivityEnvironment { Info = ActivityEnvironment.DefaultInfo with { StartToCloseTimeout = TimeSpan.FromMinutes(1) } };
        var refused = await Assert.ThrowsAsync<Temporalio.Exceptions.ApplicationFailureException>(() => tight.RunAsync(() =>
            activity.DelegateToAgentAsync(Agent, "do it", "wf/tight", agentBudgetSeconds: 120)));
        Assert.Equal("MisorderedAgentTimeouts", refused.ErrorType);
        Assert.Single(await h.PendingAsync());
    }

    [Fact]
    public async Task SwitchedOn_APublishFailureFailsTheActivity_AsItDidBefore()
    {
        using var h = new DurableHarness();
        h.Relay.PublishFailure = new InvalidOperationException("broker away");
        var (activity, _, _) = Activity(h.Service);

        await Assert.ThrowsAsync<InvalidOperationException>(() => new ActivityEnvironment().RunAsync(() =>
            activity.DelegateToAgentAsync(Agent, "do it", "wf/task-1")));

        Assert.Empty(await h.PendingAsync());
    }

    /// <summary>A local activity cannot be completed later, so it keeps the in-process wait.</summary>
    [Fact]
    public async Task SwitchedOn_ALocalActivity_StillWaitsInProcess()
    {
        using var h = new DurableHarness();
        var (activity, registry, published) = Activity(h.Service);
        var env = new ActivityEnvironment { Info = ActivityEnvironment.DefaultInfo with { IsLocal = true } };

        var run = env.RunAsync(() => activity.DelegateToAgentAsync(Agent, "do it", "wf/local"));
        await WaitForAsync(() => published.Count == 1);
        Assert.True(registry.TryComplete("wf/local", new AgentTaskResult("ok", "completed")));
        await run;

        Assert.Empty(await h.PendingAsync());
    }

    // ── the listener ──────────────────────────────────────────────────────────

    private static TemporalRelayListener Listener(TaskCompletionRegistry registry, DurableDelegationService? durable) => new(
        Options.Create(new RabbitMqOptions()), registry, Substitute.For<ITemporalClientFactory>(),
        NullLogger<TemporalRelayListener>.Instance, durable);

    private static RelayMessage Reply(string taskId, string text = "[status: completed] all done", string type = RelayMessageType.Response) =>
        new(0, Agent, text, DateTimeOffset.UtcNow, type, TaskId: taskId);

    [Fact]
    public async Task SwitchedOff_AReplyGoesToTheRegistry_AndAnUnknownOneIsStillAnOrphan()
    {
        using var h = new DurableHarness(enabled: false);
        var registry = new TaskCompletionRegistry(NullLogger<TaskCompletionRegistry>.Instance);
        var listener = Listener(registry, h.Service);
        var waiting = registry.Register("wf/task-1");

        Assert.True(await listener.TryRouteReplyAsync(Reply("wf/task-1")));
        Assert.Equal(new AgentTaskResult("all done", "completed"), await waiting.Task);
        Assert.False(await listener.TryRouteReplyAsync(Reply("wf/unknown")));
        Assert.Empty(h.Gateway.Calls);
    }

    [Fact]
    public async Task SwitchedOn_AReplyForADurableDelegationCompletesIt_WithTheParsedStatus()
    {
        using var h = new DurableHarness();
        await h.StartAsync(DurableHarness.Start("wf/task-1"));
        var registry = new TaskCompletionRegistry(NullLogger<TaskCompletionRegistry>.Instance);
        var listener = Listener(registry, h.Service);

        Assert.True(await listener.TryRouteReplyAsync(Reply("wf/task-1", "[status: failed] could not do it")));

        Assert.Equal(("wf/task-1", new AgentTaskResult("could not do it", "failed")), Assert.Single(h.Gateway.Completed));
        // The second copy belongs to nothing any more: an orphan, nacked as before.
        Assert.False(await listener.TryRouteReplyAsync(Reply("wf/task-1", "[status: failed] could not do it")));
    }

    /// <summary>A partial response without a status prefix is "incomplete", and is continued, not completed.</summary>
    [Fact]
    public async Task SwitchedOn_APartialResponseIsContinued_AndTheMessageIsSettled()
    {
        using var h = new DurableHarness();
        await h.StartAsync(DurableHarness.Start("wf/task-1"));
        var listener = Listener(new TaskCompletionRegistry(NullLogger<TaskCompletionRegistry>.Instance), h.Service);

        Assert.True(await listener.TryRouteReplyAsync(Reply("wf/task-1", "half of it", RelayMessageType.PartialResponse)));
        await h.SettleAsync();

        Assert.Empty(h.Gateway.Completed);
        Assert.Equal("wf/task-1/incomplete-retry-1", h.Relay.Published[1].TaskId);
    }

    /// <summary>
    /// A delegation that was started before the switch was turned on is still in the registry, and
    /// its reply still reaches it: the durable path only takes what it has a record for.
    /// </summary>
    [Fact]
    public async Task SwitchedOn_AReplyForAnInMemoryRegistration_StillReachesIt()
    {
        using var h = new DurableHarness();
        var registry = new TaskCompletionRegistry(NullLogger<TaskCompletionRegistry>.Instance);
        var listener = Listener(registry, h.Service);
        var waiting = registry.Register("wf/in-memory");

        Assert.True(await listener.TryRouteReplyAsync(Reply("wf/in-memory")));
        Assert.True(waiting.Task.IsCompletedSuccessfully);
        Assert.Empty(h.Gateway.Calls);
    }

    // ── install surfaces ──────────────────────────────────────────────────────

    [Fact]
    public void TheOptInKeyAndStateVolume_AreOnEveryInstallSurface()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Fleet.sln"))) root = root.Parent;
        string Read(string path) => File.ReadAllText(Path.Combine(root!.FullName, path));

        Assert.Contains("FLEET_DURABLE_DELEGATION_ENABLED=false", Read(".env.example"));
        var compose = Read("docker-compose.example.yml");
        Assert.Contains("DurableDelegation__Enabled=${FLEET_DURABLE_DELEGATION_ENABLED:-false}", compose);
        Assert.Contains("${FLEET_BASE_DIR}/temporal-bridge:/var/lib/fleet-temporal-bridge", compose);
        Assert.Contains("FLEET_DURABLE_DELEGATION_ENABLED=false", Read("setup.sh"));
        var upgrade = Read("upgrade.sh");
        Assert.Contains("add_missing_env_var FLEET_DURABLE_DELEGATION_ENABLED false", upgrade);
        // .env is the record of the decision: a stale shell export must not override it.
        Assert.Matches(@"(?m)^unset .*\bFLEET_DURABLE_DELEGATION_ENABLED\b", upgrade);
        Assert.Matches("\"DurableDelegation\": \\{\\s+\"Enabled\": false", Read("src/Fleet.Temporal/appsettings.json"));
        Assert.False(new DurableDelegationOptions().Enabled);
    }
}
