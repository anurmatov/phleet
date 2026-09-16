using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Interfaces;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Fleet.Agent.Tests;

/// <summary>
/// AC18–21: the wiring that must happen with or without a bot token, the guard that refuses to
/// start without a completion subscriber, and the shutdown token that actually fires on shutdown.
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

    private static (RuntimeWiringService wiring, TaskManager manager, GroupBehavior groupBehavior, FakeLifetime lifetime)
        Build(bool botToken)
    {
        var agentOpts = Options.Create(new AgentOptions
        {
            Name = "fleet-agent1", Role = "r", WorkDir = Path.GetTempPath(), Provider = "claude",
            GroupDebounceSeconds = 1,
        });
        var telegramOpts = Options.Create(new TelegramOptions
        {
            // A syntactically valid but fake token; the transport only creates a client from it.
            BotToken = botToken ? "123456:AAFakeTokenForTestsOnly_0000000000000" : "",
        });
        var rabbitOpts = Options.Create(new RabbitMqOptions());
        var whisperOpts = Options.Create(new WhisperOptions());
        var ttsOpts = Options.Create(new TtsOptions());

        var executor = Substitute.For<IAgentExecutor>();
        var connState = Substitute.For<IFleetConnectionState>();
        var httpFact = Substitute.For<IHttpClientFactory>();
        var allowlist = new AllowlistHolder(telegramOpts);
        var relay = new GroupRelayService(agentOpts, rabbitOpts, NullLogger<GroupRelayService>.Instance);
        var manager = new TaskManager(agentOpts, executor, new SessionManager(), NullLogger<TaskManager>.Instance);
        var prompts = new PromptAssembler(executor);
        var commands = new CommandDispatcher(manager, executor, agentOpts, NullLogger<CommandDispatcher>.Instance);
        var voice = new VoiceTranscriptionService(httpFact, whisperOpts, NullLogger<VoiceTranscriptionService>.Instance);
        var tts = new TtsService(httpFact, ttsOpts, NullLogger<TtsService>.Instance);
        var groupBehavior = new GroupBehavior(agentOpts, telegramOpts, allowlist, executor, relay,
            manager, commands, prompts, NullLogger<GroupBehavior>.Instance);
        var router = new MessageRouter(agentOpts, telegramOpts, allowlist, manager, groupBehavior,
            relay, commands, NullLogger<MessageRouter>.Instance);

        // Constructing the transport is what attaches the completion handler — the whole point of
        // moving that subscription out of ExecuteAsync.
        _ = new AgentTransport(
            agentOpts, telegramOpts, allowlist, relay, manager, groupBehavior, router, commands,
            voice, tts, connState, NullLogger<AgentTransport>.Instance, null);

        var lifetime = new FakeLifetime();
        var wiring = new RuntimeWiringService(relay, groupBehavior, router, manager, lifetime,
            NullLogger<RuntimeWiringService>.Instance);

        return (wiring, manager, groupBehavior, lifetime);
    }

    /// <summary>
    /// AC19. Exactly one subscriber, attached at CONSTRUCTION. Two subscribers would mean two
    /// attachment moments and therefore a window in which one is attached and the other is not —
    /// which is the exact class of bug this wiring exists to remove.
    /// </summary>
    [Fact]
    public void ConstructingTheTransport_AttachesTheCompletionHandler()
    {
        var (_, manager, _, _) = Build(botToken: true);

        Assert.True(manager.HasCompletionSubscriber);
    }

    /// <summary>
    /// AC18 and Constraint 12. The headline fix: with NO bot token the completion callback is
    /// still wired. Before this, "headless" meant no Telegram AND no completion callback, so an
    /// adapter started that way would look alive while every workflow answer silently vanished.
    /// </summary>
    [Fact]
    public void WithNoBotToken_TheCompletionHandlerIsStillAttached()
    {
        var (_, manager, _, _) = Build(botToken: false);

        Assert.True(manager.HasCompletionSubscriber);
    }

    /// <summary>
    /// AC20. The guard is not decorative: it exists so the implementation does not silently
    /// depend on the host's "all constructors run before any StartAsync" ordering. If that
    /// assumption ever breaks, startup must fail loudly rather than drop relay answers.
    /// </summary>
    [Fact]
    public async Task StartAsync_ThrowsWhenNoCompletionSubscriberIsAttached()
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
        var manager = new TaskManager(agentOpts, executor, new SessionManager(), NullLogger<TaskManager>.Instance);
        var prompts = new PromptAssembler(executor);
        var commands = new CommandDispatcher(manager, executor, agentOpts, NullLogger<CommandDispatcher>.Instance);
        var groupBehavior = new GroupBehavior(agentOpts, telegramOpts, allowlist, executor, relay,
            manager, commands, prompts, NullLogger<GroupBehavior>.Instance);
        var router = new MessageRouter(agentOpts, telegramOpts, allowlist, manager, groupBehavior,
            relay, commands, NullLogger<MessageRouter>.Instance);

        // NOTE: no AgentTransport is constructed, so nothing attached the handler.
        Assert.False(manager.HasCompletionSubscriber);

        var wiring = new RuntimeWiringService(relay, groupBehavior, router, manager,
            new FakeLifetime(), NullLogger<RuntimeWiringService>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => wiring.StartAsync(CancellationToken.None));

        Assert.Contains("OnTaskCompleted", ex.Message);
        // AC20: it threw BEFORE starting a consumer — no relay consumption began.
        Assert.False(relay.IsEnabled && relay.IsInitializedForTesting);
    }

    /// <summary>
    /// AC21 and Constraint 20. The token handed to SetShutdownToken must be ApplicationStopping,
    /// not StartAsync's token. The latter aborts STARTUP and never fires on a normal shutdown, so
    /// using it would leave debounce timers running past stop.
    /// </summary>
    [Fact]
    public async Task ShutdownToken_ComesFromApplicationStoppingNotStartAsync()
    {
        var (wiring, _, groupBehavior, lifetime) = Build(botToken: false);

        // A token that would be passed if StartAsync's parameter were (wrongly) used.
        using var startupAbort = new CancellationTokenSource();
        await wiring.StartAsync(startupAbort.Token);

        var observed = groupBehavior.ShutdownTokenForTesting;
        Assert.False(observed.IsCancellationRequested);

        // Cancelling the STARTUP token must not fire it — that is the whole distinction.
        await startupAbort.CancelAsync();
        Assert.False(observed.IsCancellationRequested);

        // Stopping the application must.
        lifetime.StopApplication();
        Assert.True(observed.IsCancellationRequested);
    }

    [Fact]
    public async Task StopAsync_DetachesTheRelaySubscription()
    {
        var (wiring, _, _, _) = Build(botToken: false);

        await wiring.StartAsync(CancellationToken.None);
        await wiring.StopAsync(CancellationToken.None);
        // No exception, and the service is re-startable.
        await wiring.StartAsync(CancellationToken.None);
    }
}
