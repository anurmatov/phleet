using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Fleet.Agent.Tests;

public sealed class PrimaryHumanRouterTests
{
    [Theory]
    [InlineData("/new synthetic", 42, TaskSource.NewCommand, TaskPriority.PrimaryHuman)]
    [InlineData("synthetic", 42, TaskSource.UserMessage, TaskPriority.PrimaryHuman)]
    [InlineData("synthetic", 43, TaskSource.UserMessage, TaskPriority.Routine)]
    public async Task RouterPassesTrustedPriorityAndMessageId(string text, long user, TaskSource source, TaskPriority priority)
    {
        await Run(async (router, manager, executor) =>
        {
            var message = new IncomingMessage { ChatId = user, UserId = user, Text = text, StrippedText = text, IsGroupChat = false, Sender = "synthetic", TelegramMessageId = 7 };
            await router.HandleAsync(message); await router.HandleAsync(message);
            var queued = Assert.Single(manager.GetQueueSnapshot());
            Assert.Equal(source, queued.Source); Assert.Equal(priority, queued.Priority);
            Assert.Equal(7, queued.FirstPart.TelegramMessageId); Assert.Equal(user, queued.UserId);
            Assert.Equal(priority == TaskPriority.PrimaryHuman ? 1 : 2, queued.PartCount);
            Assert.Equal(0, executor.InjectionAttempts);
        });
    }

    [Theory]
    [InlineData("mention", true, false, true)]
    [InlineData("mention", false, true, false)]
    [InlineData("all", false, true, true)]
    public async Task Q9_PriorityNeverWidensExistingGroupGate(string mode, bool mention, bool nameMention, bool dispatched)
    {
        await Run(async (router, manager, _) =>
        {
            await router.HandleAsync(new IncomingMessage { ChatId = -101, UserId = 42, Text = "synthetic", StrippedText = "synthetic", Sender = "synthetic",
                IsGroupChat = true, IsBotMentioned = mention, IsNameMentioned = nameMention, TelegramMessageId = 7 });
            Assert.Equal(dispatched ? 1 : 0, manager.GetQueueSnapshot().Count);
            if (dispatched) Assert.Equal(TaskPriority.PrimaryHuman, manager.GetQueueSnapshot()[0].Priority);
            await router.HandleAsync(new IncomingMessage { ChatId = -202, UserId = 42, Text = "synthetic", StrippedText = "synthetic", Sender = "synthetic",
                IsGroupChat = true, IsBotMentioned = true, TelegramMessageId = 8 });
            Assert.DoesNotContain(manager.GetQueueSnapshot(), e => e.ChatId == -202);
        }, mode);
    }

    [Theory]
    [InlineData("/stop")]
    [InlineData("/cancel")]
    public async Task Q11_NonPrimaryAllowedUsersCommandsActBeforeTaskQueue(string command)
    {
        await Run(async (router, manager, executor) =>
        {
            await router.HandleAsync(new IncomingMessage { ChatId = 1, UserId = 43, Text = command, StrippedText = command, IsGroupChat = false, Sender = "synthetic", TelegramMessageId = 7 });
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (manager.HasRunningTasks(1)) await Task.Delay(10, deadline.Token);
            Assert.Empty(manager.GetQueueSnapshot()); Assert.Single(executor.Tasks);
        });
    }

    private static async Task Run(Func<MessageRouter, TaskManager, MidTurnIsolationTests.ControlledExecutor, Task> action, string mode = "mention")
    {
        var directory = Path.Combine(Path.GetTempPath(), $"primary-router-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        await using var executor = new MidTurnIsolationTests.ControlledExecutor(MidTurnInjectionStatus.Injected);
        var agent = Options.Create(new AgentOptions { Name = "agent1", Role = "test", WorkDir = directory, GroupListenMode = mode });
        var telegram = Options.Create(new TelegramOptions { PrimaryHumanUserId = 42, AllowedUserIds = [42, 43], AllowedGroupIds = [-101] });
        var allowlist = new AllowlistHolder(telegram);
        var sink = Substitute.For<IMessageSink>();
        var manager = new TaskManager(agent, executor, new SessionManager(), NullLogger<TaskManager>.Instance, sink: sink);
        await using var relay = new GroupRelayService(agent, Options.Create(new RabbitMqOptions()), NullLogger<GroupRelayService>.Instance);
        var commands = new CommandDispatcher(manager, executor, agent, NullLogger<CommandDispatcher>.Instance, sink);
        var groups = new GroupBehavior(agent, telegram, allowlist, executor, relay, manager, commands, new PromptAssembler(executor), NullLogger<GroupBehavior>.Instance);
        var router = new MessageRouter(agent, telegram, allowlist, manager, groups, relay, commands, NullLogger<MessageRouter>.Instance, sink);
        try
        {
            await manager.StartTask(1, "relay", "relay", true, TaskSource.Relay); await executor.WaitStarted(1);
            await action(router, manager, executor);
        }
        finally { await manager.CancelAllAsync(); Directory.Delete(directory, true); }
    }
}
