using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Interfaces;
using Fleet.Agent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Fleet.Agent.Tests;

/// <summary>
/// #340 D3: a local-model claude agent holds no Claude credential, so a claude token broadcast
/// must not create <c>.credentials.json</c> or restart the process — and a normal claude agent
/// still applies it.
/// </summary>
public sealed class GroupBehaviorClaudeLocalTokenUpdateTests : IAsyncDisposable
{
    private const string ClaudeTokenBroadcast =
        """{"provider":"claude","accessToken":"access-token-value","refreshToken":"refresh-token-value","expiresAt":4102444800000}""";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"claude-token-{Guid.NewGuid():N}");
    private readonly List<IAsyncDisposable> _disposables = [];

    public GroupBehaviorClaudeLocalTokenUpdateTests() => Directory.CreateDirectory(_dir);

    private string CredentialsPath => Path.Combine(_dir, "claude", ".credentials.json");

    private (GroupBehavior Behavior, IAgentExecutor Executor) Build(string? anthropicBaseUrl)
    {
        var agentOptions = Options.Create(new AgentOptions
        {
            Name = "test-agent",
            Role = "test",
            WorkDir = _dir,
            ShortName = "test",
            Provider = "claude",
            Model = anthropicBaseUrl is null ? "claude-sonnet-5" : "qwen3.8:27b-agent",
            AnthropicBaseUrl = anthropicBaseUrl,
        });
        var telegramOptions = Options.Create(new TelegramOptions());
        var rabbitOptions = Options.Create(new RabbitMqOptions { Exchange = "fleet.tasks" });
        var executor = Substitute.For<IAgentExecutor>();
        executor.TryStopProcessAsync().Returns(false);
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
            ClaudeCredentialsPath = CredentialsPath,
        };
        return (behavior, executor);
    }

    [Fact]
    public async Task LocalModelAgent_IgnoresClaudeTokenUpdate_WritesNothing_RestartsNothing()
    {
        var (behavior, executor) = Build("http://inference-host:11434");

        await behavior.ApplyTokenUpdateForTestsAsync(ClaudeTokenBroadcast);

        Assert.False(File.Exists(CredentialsPath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(CredentialsPath)));
        await executor.DidNotReceive().TryStopProcessAsync();
        executor.DidNotReceive().RequestRestart();
    }

    [Fact]
    public async Task NormalClaudeAgent_StillAppliesClaudeTokenUpdate_AndRestarts()
    {
        var (behavior, executor) = Build(anthropicBaseUrl: null);

        await behavior.ApplyTokenUpdateForTestsAsync(ClaudeTokenBroadcast);

        Assert.True(File.Exists(CredentialsPath));
        Assert.Contains("access-token-value", await File.ReadAllTextAsync(CredentialsPath));
        await executor.Received(1).TryStopProcessAsync();
        executor.Received(1).RequestRestart();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var disposable in _disposables)
            await disposable.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch { /* teardown only */ }
    }
}
