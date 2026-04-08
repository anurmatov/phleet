using System.Runtime.CompilerServices;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Fleet.Agent.Tests;

public class GroupBehaviorBufferTests
{
    private readonly GroupBehavior _behavior;
    private const long ChatId = 12345;

    public GroupBehaviorBufferTests()
    {
        var agentOpts = Options.Create(new AgentOptions
        {
            Name = "test-agent",
            Role = "test",
            WorkDir = Path.GetTempPath(),
            GroupDebounceSeconds = 30,
            ShortName = "test"
        });
        var telegramOpts = Options.Create(new TelegramOptions());
        var rabbitOpts = Options.Create(new RabbitMqOptions());

        var executor = Substitute.For<IAgentExecutor>();
        executor.IsProcessWarm.Returns(true);

        var relay = new GroupRelayService(agentOpts, rabbitOpts,
            NullLogger<GroupRelayService>.Instance);
        var sessions = new SessionManager();
        var taskManager = new TaskManager(agentOpts, executor, sessions,
            NullLogger<TaskManager>.Instance);
        // CommandDispatcher has deep sealed deps (ClaudeTokenRefreshService → ClaudeExecutor).
        // It's only used in OnRelayMessage when text starts with '/' — our tests don't trigger that.
        var commands = (CommandDispatcher)RuntimeHelpers.GetUninitializedObject(typeof(CommandDispatcher));
        var prompts = new PromptAssembler(executor);

        _behavior = new GroupBehavior(agentOpts, telegramOpts, executor, relay,
            taskManager, commands, prompts, NullLogger<GroupBehavior>.Instance);
    }

    [Fact]
    public void OnRelayMessage_Directive_DoesNotBuffer()
    {
        var bufferBefore = _behavior.GetGroupBuffer(ChatId).GetEntries().Count;

        _behavior.OnRelayMessage(ChatId, "temporal-bridge", "do the task",
            RelayMessageType.Directive, taskId: "task-1");

        var bufferAfter = _behavior.GetGroupBuffer(ChatId).GetEntries().Count;
        Assert.Equal(bufferBefore, bufferAfter);
    }

    [Fact]
    public void OnRelayMessage_BridgeRequest_DoesNotBuffer()
    {
        var bufferBefore = _behavior.GetGroupBuffer(ChatId).GetEntries().Count;

        _behavior.OnRelayMessage(ChatId, "bridge", "bridge task",
            RelayMessageType.BridgeRequest, correlationId: "corr-1", taskId: "task-2");

        var bufferAfter = _behavior.GetGroupBuffer(ChatId).GetEntries().Count;
        Assert.Equal(bufferBefore, bufferAfter);
    }

    [Fact]
    public void OnRelayMessage_Response_DoesBuffer()
    {
        var bufferBefore = _behavior.GetGroupBuffer(ChatId).GetEntries().Count;

        _behavior.OnRelayMessage(ChatId, "cto", "here is my response",
            RelayMessageType.Response);

        var bufferAfter = _behavior.GetGroupBuffer(ChatId).GetEntries().Count;
        Assert.Equal(bufferBefore + 1, bufferAfter);
    }
}
