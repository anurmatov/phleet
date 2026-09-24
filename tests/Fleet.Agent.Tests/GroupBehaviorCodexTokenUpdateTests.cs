using Fleet.Agent.Configuration;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Interfaces;
using Fleet.Agent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Fleet.Agent.Tests;

/// <summary>
/// #335 D6 / AC9: a hosted-provider agent holds no OpenAI credential, so a codex token broadcast
/// must not recreate <c>auth.json</c> — and a frontier codex agent still applies it.
/// </summary>
public sealed class GroupBehaviorCodexTokenUpdateTests : IAsyncDisposable
{
    private const string CodexTokenBroadcast =
        """{"provider":"codex","accessToken":"access-token-value","refreshToken":"refresh-token-value","expiresAt":4102444800000}""";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"codex-token-{Guid.NewGuid():N}");
    private readonly List<IAsyncDisposable> _disposables = [];

    public GroupBehaviorCodexTokenUpdateTests() => Directory.CreateDirectory(_dir);

    private string AuthPath => Path.Combine(_dir, "codex", "auth.json");

    private (GroupBehavior Behavior, IAgentExecutor Executor) Build(string model, bool hosted)
    {
        var agentOptions = Options.Create(new AgentOptions
        {
            Name = "test-agent",
            Role = "test",
            WorkDir = _dir,
            ShortName = "test",
            Provider = "codex",
            Model = model,
            HostedProvider = hosted,
            HostedProviderKeyEnv = hosted ? "ZAI_CODING_PLAN_API_KEY" : null,
        });
        var telegramOptions = Options.Create(new TelegramOptions());
        var rabbitOptions = Options.Create(new RabbitMqOptions { Exchange = "fleet.tasks" });
        var executor = Substitute.For<IAgentExecutor>();
        executor.TryStopProcessAsync().Returns(true);
        var sink = Substitute.For<IMessageSink>();
        var relay = new GroupRelayService(agentOptions, rabbitOptions, NullLogger<GroupRelayService>.Instance);
        _disposables.Add(relay);
        var taskManager = new TaskManager(agentOptions, executor, new SessionManager(),
            NullLogger<TaskManager>.Instance, sink: sink);
        var commands = new CommandDispatcher(taskManager, executor, agentOptions,
            NullLogger<CommandDispatcher>.Instance, sink: sink);
        var behavior = new GroupBehavior(agentOptions, telegramOptions, new AllowlistHolder(telegramOptions),
            executor, relay, taskManager, commands, new PromptAssembler(executor),
            NullLogger<GroupBehavior>.Instance, sink: sink)
        {
            CodexAuthPath = AuthPath,
        };
        return (behavior, executor);
    }

    [Fact]
    public async Task HostedAgent_IgnoresCodexTokenUpdate_AndCreatesNoAuthJson()
    {
        var (behavior, executor) = Build("zai/glm-5.3", hosted: true);

        await behavior.ApplyTokenUpdateForTestsAsync(CodexTokenBroadcast);

        Assert.False(File.Exists(AuthPath));
        await executor.DidNotReceive().TryStopProcessAsync();
        executor.DidNotReceive().RequestRestart();
    }

    [Fact]
    public async Task FrontierCodexAgent_StillAppliesCodexTokenUpdate()
    {
        var (behavior, executor) = Build("gpt-5", hosted: false);

        await behavior.ApplyTokenUpdateForTestsAsync(CodexTokenBroadcast);

        Assert.True(File.Exists(AuthPath));
        Assert.Contains("access-token-value", await File.ReadAllTextAsync(AuthPath));
        await executor.Received(1).TryStopProcessAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var disposable in _disposables)
            await disposable.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch { /* teardown only */ }
    }
}
