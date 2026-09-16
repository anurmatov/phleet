using System.Reflection;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Interfaces;
using Fleet.Agent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Telegram.Bot;

namespace Fleet.Agent.Tests;

/// <summary>
/// #277 §4.2 S1–S2 and MUST NOT 17 — an absent or malformed bot token must be survivable.
///
/// The whitespace check in front of the client construction accepts any non-blank string, and
/// Telegram.Bot validates token FORMAT in its constructor. So before this change a typo in the
/// token threw out of <c>AgentTransport</c>'s constructor, which the host reports as a startup
/// failure — taking relay publication, workflow completion and the first-party adapter down with
/// it. That is the same total-outage shape #277 exists to remove, arriving through configuration
/// instead of registration.
///
/// The negative control for <see cref="MalformedToken_LeavesTheBotNullInsteadOfThrowing"/> is to
/// remove the try/catch at the construction site: the test then fails because the constructor
/// throws.
/// </summary>
public class TelegramTokenIsolationTests
{
    private static AgentTransport BuildTransport(string botToken, SinkSuppressionCounter counter)
    {
        var agentOpts = Options.Create(new AgentOptions
        {
            Name = "test", Role = "test", WorkDir = Path.GetTempPath(), Provider = "claude",
        });
        var telegramOpts = Options.Create(new TelegramOptions { BotToken = botToken });
        var rabbitOpts = Options.Create(new RabbitMqOptions());
        var whisperOpts = Options.Create(new WhisperOptions());
        var ttsOpts = Options.Create(new TtsOptions());

        var executor = Substitute.For<IAgentExecutor>();
        var httpFact = Substitute.For<IHttpClientFactory>();
        var connState = Substitute.For<IFleetConnectionState>();
        var allowlist = new AllowlistHolder(telegramOpts);
        var relay = new GroupRelayService(agentOpts, rabbitOpts, NullLogger<GroupRelayService>.Instance);
        var holder = new MessageSinkHolder(counter);
        var manager = new TaskManager(agentOpts, executor, new SessionManager(),
            NullLogger<TaskManager>.Instance, sink: holder);
        var commands = new CommandDispatcher(manager, executor, agentOpts,
            NullLogger<CommandDispatcher>.Instance, sink: holder);
        var behavior = new GroupBehavior(agentOpts, telegramOpts, allowlist, executor, relay,
            manager, commands, new PromptAssembler(executor),
            NullLogger<GroupBehavior>.Instance, sink: holder);
        var router = new MessageRouter(agentOpts, telegramOpts, allowlist, manager, behavior,
            relay, commands, NullLogger<MessageRouter>.Instance, sink: holder);
        var voice = new VoiceTranscriptionService(httpFact, whisperOpts,
            NullLogger<VoiceTranscriptionService>.Instance);
        var tts = new TtsService(httpFact, ttsOpts, NullLogger<TtsService>.Instance);

        return new AgentTransport(
            agentOpts, telegramOpts, allowlist, relay, manager, behavior, router, commands,
            voice, tts, connState, NullLogger<AgentTransport>.Instance, holder,
            sinkCounter: counter);
    }

    private static ITelegramBotClient? BotOf(AgentTransport transport) =>
        (ITelegramBotClient?)typeof(AgentTransport)
            .GetField("_bot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(transport);

    /// <summary>
    /// Positive control for the whole file. If <c>TelegramBotClient</c> ever stopped validating
    /// token format, every test below would pass while guarding nothing — the try/catch would be
    /// dead code and the "malformed token is survivable" claim would be unevidenced.
    ///
    /// This asserts the premise directly, so the day the library changes, the reason these tests
    /// became vacuous is named rather than inferred.
    /// </summary>
    [Fact]
    public void Premise_TheClientRejectsAMalformedTokenAtConstruction() =>
        Assert.ThrowsAny<Exception>(() => new TelegramBotClient("this-is-not-a-valid-bot-token"));

    /// <summary>S2 / T13. The headline case: construction succeeds and the bot is simply absent.</summary>
    [Fact]
    public void MalformedToken_LeavesTheBotNullInsteadOfThrowing()
    {
        var counter = new SinkSuppressionCounter();

        var transport = BuildTransport("this-is-not-a-valid-bot-token", counter);

        Assert.Null(BotOf(transport));
        Assert.Equal(SinkSuppressionCounter.TelegramMalformed, counter.StartupTelegramState);
    }

    /// <summary>
    /// S1. An absent token is reported as <c>absent</c>, NOT <c>malformed</c>. Collapsing the two
    /// would make a configuration typo indistinguishable from an intentionally Telegram-free
    /// deployment, which is the distinction <c>startup_telegram_state</c> exists to draw.
    /// </summary>
    [Fact]
    public void AbsentToken_IsReportedDistinctlyFromMalformed()
    {
        var counter = new SinkSuppressionCounter();

        var transport = BuildTransport("", counter);

        Assert.Null(BotOf(transport));
        Assert.Equal(SinkSuppressionCounter.TelegramAbsent, counter.StartupTelegramState);
    }

    /// <summary>A well-formed token still produces a client — the change must not break S3.</summary>
    [Fact]
    public void WellFormedToken_StillConstructsTheClient()
    {
        var counter = new SinkSuppressionCounter();

        var transport = BuildTransport("123456:AAFakeTokenForTestsOnly_0000000000000", counter);

        Assert.NotNull(BotOf(transport));
        Assert.Equal(SinkSuppressionCounter.TelegramConfigured, counter.StartupTelegramState);
    }

    /// <summary>
    /// #277 D-1: whatever the token state, the transport attaches itself as the sink. A malformed
    /// token disables the poller, not the render path — reserved-key suppression and the
    /// <c>_bot is null</c> checks already handle the rest.
    /// </summary>
    [Fact]
    public void TransportAttachesItselfAsTheSink_EvenWithAMalformedToken()
    {
        var counter = new SinkSuppressionCounter();
        var holder = new MessageSinkHolder(counter);

        Assert.False(holder.IsAttached);

        var agentOpts = Options.Create(new AgentOptions
        {
            Name = "test", Role = "test", WorkDir = Path.GetTempPath(), Provider = "claude",
        });
        var telegramOpts = Options.Create(new TelegramOptions { BotToken = "bad-token" });
        var rabbitOpts = Options.Create(new RabbitMqOptions());
        var executor = Substitute.For<IAgentExecutor>();
        var httpFact = Substitute.For<IHttpClientFactory>();
        var allowlist = new AllowlistHolder(telegramOpts);
        var relay = new GroupRelayService(agentOpts, rabbitOpts, NullLogger<GroupRelayService>.Instance);
        var manager = new TaskManager(agentOpts, executor, new SessionManager(),
            NullLogger<TaskManager>.Instance, sink: holder);
        var commands = new CommandDispatcher(manager, executor, agentOpts,
            NullLogger<CommandDispatcher>.Instance, sink: holder);
        var behavior = new GroupBehavior(agentOpts, telegramOpts, allowlist, executor, relay,
            manager, commands, new PromptAssembler(executor),
            NullLogger<GroupBehavior>.Instance, sink: holder);
        var router = new MessageRouter(agentOpts, telegramOpts, allowlist, manager, behavior,
            relay, commands, NullLogger<MessageRouter>.Instance, sink: holder);

        _ = new AgentTransport(
            agentOpts, telegramOpts, allowlist, relay, manager, behavior, router, commands,
            new VoiceTranscriptionService(httpFact, Options.Create(new WhisperOptions()),
                NullLogger<VoiceTranscriptionService>.Instance),
            new TtsService(httpFact, Options.Create(new TtsOptions()), NullLogger<TtsService>.Instance),
            Substitute.For<IFleetConnectionState>(), NullLogger<AgentTransport>.Instance, holder,
            sinkCounter: counter);

        Assert.True(holder.IsAttached);
    }
}
