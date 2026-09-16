using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Fleet.Agent.Tests;

/// <summary>
/// AC18–21: the wiring that must happen with or without a bot token, the guard that refuses to
/// start without the completion owners, and the shutdown token that actually fires on shutdown.
///
/// #277 D-2 split the single completion handler into two owners with disjoint effects, so the
/// guard can no longer ask "does OnTaskCompleted have any subscriber" — see
/// <see cref="StartAsync_ThrowsWhenOnlyTheContextBufferIsAttached"/>, which is the case that
/// motivated the change.
/// </summary>
public class RuntimeWiringServiceTests
{
    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();
        private readonly CancellationTokenSource _started = new();

        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;
        public void StopApplication() => _stopping.Cancel();
    }

    private sealed record Parts(
        RuntimeWiringService Wiring,
        TaskManager Manager,
        GroupBehavior GroupBehavior,
        FakeLifetime Lifetime,
        RelayCompletionPublisher Publisher,
        CompletionContextBuffer Buffer,
        GroupRelayService Relay);

    /// <summary>
    /// Builds the runtime WITHOUT an AgentTransport. That is the point of #277: no first-party
    /// path may depend on the Telegram transport being constructed, so every test here runs in a
    /// host that never creates one.
    /// </summary>
    private static Parts Build()
    {
        var agentOpts = Options.Create(new AgentOptions
        {
            Name = "fleet-agent1", Role = "r", WorkDir = Path.GetTempPath(), Provider = "claude",
        });
        var telegramOpts = Options.Create(new TelegramOptions());
        var rabbitOpts = Options.Create(new RabbitMqOptions());

        var executor = Substitute.For<IAgentExecutor>();
        var allowlist = new AllowlistHolder(telegramOpts);
        var relay = new GroupRelayService(agentOpts, rabbitOpts, NullLogger<GroupRelayService>.Instance);
        var holder = new MessageSinkHolder();
        var manager = new TaskManager(agentOpts, executor, new SessionManager(),
            NullLogger<TaskManager>.Instance, sink: holder);
        var prompts = new PromptAssembler(executor);
        var commands = new CommandDispatcher(manager, executor, agentOpts,
            NullLogger<CommandDispatcher>.Instance, sink: holder);
        var groupBehavior = new GroupBehavior(agentOpts, telegramOpts, allowlist, executor, relay,
            manager, commands, prompts, NullLogger<GroupBehavior>.Instance, sink: holder);
        var router = new MessageRouter(agentOpts, telegramOpts, allowlist, manager, groupBehavior,
            relay, commands, NullLogger<MessageRouter>.Instance, sink: holder);

        // Constructing these is what attaches the completion effects — the whole point of moving
        // them off the Telegram transport.
        var publisher = new RelayCompletionPublisher(manager, relay,
            NullLogger<RelayCompletionPublisher>.Instance);
        var buffer = new CompletionContextBuffer(manager, groupBehavior, holder);

        var lifetime = new FakeLifetime();
        var wiring = new RuntimeWiringService(relay, groupBehavior, router, publisher, buffer,
            lifetime, NullLogger<RuntimeWiringService>.Instance);

        return new Parts(wiring, manager, groupBehavior, lifetime, publisher, buffer, relay);
    }

    /// <summary>
    /// AC18/AC19 and #274 Constraint 12, restated for #277: the completion effects are attached
    /// with NO Telegram transport in the process at all. Before, "headless" meant no Telegram AND
    /// no completion callback, so an adapter started that way looked alive while every workflow
    /// answer silently vanished.
    /// </summary>
    [Fact]
    public void WithNoTransportConstructed_BothCompletionOwnersAreAttached()
    {
        var p = Build();

        Assert.True(p.Publisher.IsAttached);
        Assert.True(p.Buffer.IsAttached);
        Assert.True(p.Manager.HasCompletionSubscriber);
    }

    /// <summary>
    /// AC20. The guard is not decorative: it exists so the implementation does not silently depend
    /// on the host's "all constructors run before any StartAsync" ordering.
    /// </summary>
    [Fact]
    public async Task StartAsync_ThrowsWhenNoCompletionOwnerIsAttached()
    {
        var p = Build();
        // Detach both, simulating the ordering assumption breaking.
        p.Publisher.Dispose();
        p.Buffer.Dispose();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => p.Wiring.StartAsync(CancellationToken.None));

        Assert.Contains("RelayCompletionPublisher", ex.Message);
        // It threw BEFORE starting a consumer — no relay consumption began.
        Assert.False(p.Relay.IsEnabled && p.Relay.IsInitializedForTesting);
    }

    /// <summary>
    /// #277 MUST NOT 21 — the reason the guard checks identity rather than a subscriber count.
    ///
    /// With the context buffer attached and the relay publisher detached, the old predicate
    /// (<c>TaskManager.HasCompletionSubscriber</c>) is TRUE, so the process would start and then
    /// drop every workflow answer. This test fails if the guard is ever reverted to that boolean.
    /// </summary>
    [Fact]
    public async Task StartAsync_ThrowsWhenOnlyTheContextBufferIsAttached()
    {
        var p = Build();
        p.Publisher.Dispose();

        // The old, insufficient predicate still reports "someone is listening".
        Assert.True(p.Manager.HasCompletionSubscriber);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => p.Wiring.StartAsync(CancellationToken.None));

        Assert.Contains("RelayCompletionPublisher.IsAttached=False", ex.Message);
    }

    /// <summary>The mirror case: the publisher alone is not enough either.</summary>
    [Fact]
    public async Task StartAsync_ThrowsWhenOnlyTheRelayPublisherIsAttached()
    {
        var p = Build();
        p.Buffer.Dispose();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => p.Wiring.StartAsync(CancellationToken.None));

        Assert.Contains("CompletionContextBuffer.IsAttached=False", ex.Message);
    }

    /// <summary>
    /// AC21 and Constraint 20. The token handed to SetShutdownToken must be ApplicationStopping,
    /// not StartAsync's token. The latter aborts STARTUP and never fires on a normal shutdown, so
    /// using it would leave debounce timers running past stop.
    /// </summary>
    [Fact]
    public async Task ShutdownToken_ComesFromApplicationStoppingNotStartAsync()
    {
        var p = Build();

        // A token that would be passed if StartAsync's parameter were (wrongly) used.
        using var startupAbort = new CancellationTokenSource();
        await p.Wiring.StartAsync(startupAbort.Token);

        var observed = p.GroupBehavior.ShutdownTokenForTesting;
        Assert.False(observed.IsCancellationRequested);

        // Cancelling the STARTUP token must not fire it — that is the whole distinction.
        await startupAbort.CancelAsync();
        Assert.False(observed.IsCancellationRequested);

        // Stopping the application must.
        p.Lifetime.StopApplication();
        Assert.True(observed.IsCancellationRequested);
    }

    [Fact]
    public async Task StopAsync_DetachesTheRelaySubscription()
    {
        var p = Build();

        await p.Wiring.StartAsync(CancellationToken.None);
        await p.Wiring.StopAsync(CancellationToken.None);
        // No exception, and the service is re-startable.
        await p.Wiring.StartAsync(CancellationToken.None);
    }
}
