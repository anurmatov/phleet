using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RabbitMQ.Client;

namespace Fleet.Agent.Tests;

/// <summary>
/// #386 §4.6 / MUST NOT 3: <c>fleet.relay</c> carries only OAuth token broadcasts and a pi agent
/// holds none. It calls the unbind and never binds the exchange (A15), and a broadcast that reaches
/// it anyway writes no credential file and restarts nothing — whichever provider it names.
/// </summary>
public sealed class PiTokenBroadcastIsolationTests : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pi-relay-{Guid.NewGuid():N}");
    private readonly List<IAsyncDisposable> _disposables = [];

    public PiTokenBroadcastIsolationTests() => Directory.CreateDirectory(_dir);

    private static AgentOptions Agent(string provider, string? anthropicBaseUrl = null) => new()
    {
        Name = "test-agent",
        Role = "test",
        WorkDir = "/workspace",
        ShortName = "Pi-Agent",
        Provider = provider,
        Model = provider == "pi" ? "qwen3.8-flash-next" : "claude-sonnet-5",
        PiBaseUrl = provider == "pi" ? "http://inference-host:8000" : null,
        ContextWindow = provider == "pi" ? 131072 : null,
        AnthropicBaseUrl = anthropicBaseUrl,
    };

    /// <summary>Runs the real InitializeAsync against a recording connection; returns every channel it opened.</summary>
    private async Task<List<IChannel>> InitializeAsync(AgentOptions agent)
    {
        var channels = new List<IChannel>();
        var connection = Substitute.For<IConnection>();
        connection.CreateChannelAsync(Arg.Any<CreateChannelOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var channel = Substitute.For<IChannel>();
                lock (channels)
                    channels.Add(channel);
                return Task.FromResult(channel);
            });

        var relay = new GroupRelayService(
            Options.Create(agent), Options.Create(new RabbitMqOptions { Host = "broker" }),
            NullLogger<GroupRelayService>.Instance)
        {
            Connect = (_, _) => Task.FromResult(connection),
        };
        _disposables.Add(relay);

        await relay.InitializeAsync(CancellationToken.None);
        Assert.True(relay.IsInitializedForTesting);
        return channels;
    }

    private static bool BoundToRelay(IEnumerable<IChannel> channels) =>
        channels.Any(c => c.ReceivedCalls().Any(call =>
            call.GetMethodInfo().Name == nameof(IChannel.QueueBindAsync) && (string?)call.GetArguments()[1] == "fleet.relay"));

    private static bool UnboundFromRelay(IEnumerable<IChannel> channels, string queue) =>
        channels.Any(c => c.ReceivedCalls().Any(call =>
            call.GetMethodInfo().Name == nameof(IChannel.QueueUnbindAsync)
            && (string?)call.GetArguments()[0] == queue
            && (string?)call.GetArguments()[1] == "fleet.relay"));

    [Fact]
    public async Task A15_APiAgentCallsTheUnbindAndNeverBindsFleetRelay()
    {
        var channels = await InitializeAsync(Agent("pi"));

        Assert.True(UnboundFromRelay(channels, "fleet.agent.pi-agent"));
        Assert.False(BoundToRelay(channels));
    }

    [Fact]
    public async Task A15_Control_ACloudClaudeAgentIsStillBound()
    {
        var channels = await InitializeAsync(Agent("claude"));

        Assert.True(BoundToRelay(channels));
        Assert.False(UnboundFromRelay(channels, "fleet.agent.pi-agent"));
    }

    [Theory]
    [InlineData("pi", null, false)]
    [InlineData("claude", "http://inference-host:11434", false)]
    [InlineData("claude", null, true)]
    [InlineData("codex", null, true)]
    [InlineData("gemini", null, true)]
    public void BindsTokenBroadcasts_IsFalseOnlyForAgentsWithoutAnOAuthCredential(
        string provider, string? anthropicBaseUrl, bool binds) =>
        Assert.Equal(binds, GroupRelayService.BindsTokenBroadcasts(Agent(provider, anthropicBaseUrl)));

    [Theory]
    [InlineData("pi")]
    [InlineData("claude")]
    [InlineData("codex")]
    public async Task APiAgent_AppliesNoTokenBroadcast_WritesNothing_RestartsNothing(string named)
    {
        var options = Options.Create(Agent("pi"));
        var telegramOptions = Options.Create(new TelegramOptions());
        var executor = Substitute.For<IAgentExecutor>();
        var sink = Substitute.For<IMessageSink>();
        var relay = new GroupRelayService(options, Options.Create(new RabbitMqOptions()), NullLogger<GroupRelayService>.Instance);
        _disposables.Add(relay);
        var taskManager = new TaskManager(options, executor, new SessionManager(), NullLogger<TaskManager>.Instance, sink: sink);
        var commands = new CommandDispatcher(taskManager, executor, options, NullLogger<CommandDispatcher>.Instance, sink: sink);
        var behavior = new GroupBehavior(options, telegramOptions, new AllowlistHolder(telegramOptions),
            executor, relay, taskManager, commands, new PromptAssembler(executor),
            NullLogger<GroupBehavior>.Instance, sink: sink)
        {
            ClaudeCredentialsPath = Path.Combine(_dir, "claude", ".credentials.json"),
            CodexAuthPath = Path.Combine(_dir, "codex", "auth.json"),
        };

        await behavior.ApplyTokenUpdateForTestsAsync(
            $$"""{"provider":"{{named}}","accessToken":"access-token-value","refreshToken":"refresh-token-value","expiresAt":4102444800000}""");

        Assert.Empty(Directory.GetFileSystemEntries(_dir));
        await executor.DidNotReceive().TryStopProcessAsync();
        executor.DidNotReceive().RequestRestart();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var disposable in _disposables)
            await disposable.DisposeAsync();
        Directory.Delete(_dir, recursive: true);
    }
}
