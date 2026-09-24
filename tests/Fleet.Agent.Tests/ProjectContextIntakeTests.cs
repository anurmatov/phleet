using System.Text;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using static Fleet.Agent.Tests.ProjectContextTestSupport;

namespace Fleet.Agent.Tests;

/// <summary>
/// #347 intake call sites, end to end through the real <see cref="GroupBehavior"/>,
/// <see cref="MessageRouter"/> and <see cref="TaskManager"/>: which signal each site offers, and that
/// a relay's chat id is never one. The executor is never warm, so every routed delivery renders and
/// the executor input shows exactly what intake resolved.
/// </summary>
public sealed class ProjectContextIntakeTests : IDisposable
{
    private const long DmUser = 4242;

    private readonly ProjectContextRoot _root = new ProjectContextRoot()
        .WithFull(ProjectA, "A-FULL\n")
        .WithFull(ProjectB, "B-FULL\n");
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), $"pci-{Guid.NewGuid():N}");
    private readonly LedgerTestExecutor _executor = new() { Warm = false };

    public ProjectContextIntakeTests() => Directory.CreateDirectory(_workDir);

    public void Dispose()
    {
        _root.Dispose();
        try { Directory.Delete(_workDir, recursive: true); } catch { /* teardown only */ }
    }

    private sealed record Pipeline(GroupBehavior Behavior, MessageRouter Router, TaskManager Tasks, GroupRelayService Relay);

    private Pipeline Build(params (string Kind, string Value, string Project)[] routes)
    {
        var agentOpts = Options.Create(new AgentOptions
        {
            Name = "agent-a", Role = "test", WorkDir = _workDir, ShortName = "agent-a",
            Projects = [ProjectA, ProjectB],
            ProjectContextRouting = Routing([ProjectA, ProjectB], routes),
        });
        var telegramOpts = Options.Create(new TelegramOptions { AllowedUserIds = [DmUser], AllowedGroupIds = [Chat] });
        var router = new ProjectContextRouter(agentOpts, NullLogger<ProjectContextRouter>.Instance);
        Assert.True(router.IsEnabled);
        var attacher = new ProjectContextAttacher(_executor, NullLogger<ProjectContextAttacher>.Instance) { ContentRoot = _root.Path };
        var tasks = new TaskManager(agentOpts, _executor, new SessionManager(), NullLogger<TaskManager>.Instance, contextAttacher: attacher);
        var relay = new GroupRelayService(agentOpts, Options.Create(new RabbitMqOptions()), NullLogger<GroupRelayService>.Instance);
        var commands = new CommandDispatcher(tasks, _executor, agentOpts, NullLogger<CommandDispatcher>.Instance);
        var allowlist = new AllowlistHolder(telegramOpts);
        var behavior = new GroupBehavior(agentOpts, telegramOpts, allowlist, _executor, relay, tasks, commands,
            new PromptAssembler(_executor), NullLogger<GroupBehavior>.Instance, contextRouter: router);
        var messages = new MessageRouter(agentOpts, telegramOpts, allowlist, tasks, behavior, relay, commands,
            NullLogger<MessageRouter>.Instance, contextRouter: router);
        return new Pipeline(behavior, messages, tasks, relay);
    }

    private int _deliveries;

    /// <summary>
    /// Waits for the next executor call, releases it, and returns its input. The index is counted
    /// here rather than read from the executor, because the delivery may already have happened.
    /// </summary>
    private async Task<string> NextDeliveryAsync(Pipeline pipeline, long chatId)
    {
        var index = _deliveries++;
        await _executor.WaitForExecuteCountAsync(index + 1);
        _executor.ReleaseNextTurn();
        await WaitUntilAsync(() => !pipeline.Tasks.HasRunningTasks(chatId));
        return _executor.ExecutedTasks[index];
    }

    private static IncomingMessage GroupMessage(string text) => new()
    {
        ChatId = Chat, UserId = DmUser, Sender = "@someone", IsGroupChat = true,
        Text = text, StrippedText = text, IsBotMentioned = true,
    };

    private static IncomingMessage DirectMessage(string text) => new()
    {
        ChatId = DmUser, UserId = DmUser, Sender = "@someone", IsGroupChat = false,
        Text = text, StrippedText = text,
    };

    // ── relay / bridge ───────────────────────────────────────────────────────

    [Fact]
    public async Task RelayDirective_RoutesOnItsStructuredRepo()
    {
        var pipeline = Build(("repo", Repo, ProjectA), ("chat", ChatValue, ProjectB));

        pipeline.Behavior.OnRelayMessage(Chat, "temporal-bridge", "Do the step.", RelayMessageType.Directive,
            taskId: "task-1", repo: "Org/App");
        var input = await NextDeliveryAsync(pipeline, Chat);

        Assert.StartsWith(BlockHeader(ProjectA), input, StringComparison.Ordinal);
        Assert.DoesNotContain(BlockHeader(ProjectB), input, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RelayAndBridge_RouteOnALeadingWorkflowTag()
    {
        var pipeline = Build(("workflow", Workflow, ProjectA));
        var directive = $"[fleet-wf:{Workflow}:{Workflow}-1]\nDo the step.";

        pipeline.Behavior.OnRelayMessage(Chat, "temporal-bridge", directive, RelayMessageType.Directive, taskId: "task-1");
        Assert.StartsWith(BlockHeader(ProjectA), await NextDeliveryAsync(pipeline, Chat), StringComparison.Ordinal);

        pipeline.Behavior.OnRelayMessage(Chat, "bridge", directive, RelayMessageType.BridgeRequest,
            correlationId: "corr-1", taskId: "task-2");
        Assert.StartsWith(BlockHeader(ProjectA), await NextDeliveryAsync(pipeline, Chat), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RelayIgnoresItsChatId_WhileATelegramMessageInThatChatRoutesOnIt()
    {
        // The only route is a chat route for the very chat the relay posts into.
        var pipeline = Build(("chat", ChatValue, ProjectA));

        pipeline.Behavior.OnRelayMessage(Chat, "temporal-bridge", "Do the step.", RelayMessageType.Directive, taskId: "task-1");
        var relayInput = await NextDeliveryAsync(pipeline, Chat);
        Assert.DoesNotContain("[project context", relayInput, StringComparison.Ordinal);

        await pipeline.Router.HandleAsync(GroupMessage("hello"));
        Assert.StartsWith(BlockHeader(ProjectA), await NextDeliveryAsync(pipeline, Chat), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedRelayRepo_IsIgnored_AndTheWorkflowLevelStillApplies()
    {
        var pipeline = Build(("repo", Repo, ProjectB), ("workflow", Workflow, ProjectA));

        pipeline.Behavior.OnRelayMessage(Chat, "temporal-bridge", $"[fleet-wf:{Workflow}:1]\nDo it.",
            RelayMessageType.Directive, taskId: "task-1", repo: "org/app/extra");
        var input = await NextDeliveryAsync(pipeline, Chat);

        Assert.StartsWith(BlockHeader(ProjectA), input, StringComparison.Ordinal);
        Assert.DoesNotContain(BlockHeader(ProjectB), input, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RelayRepo_FlowsFromTheBrokerPayload_AndAnOldPayloadWithoutItStillDeserializes()
    {
        var pipeline = Build();
        var seen = new List<string?>();
        pipeline.Relay.MessageReceived += (_, _, _, _, _, _, _, _, repo) => seen.Add(repo);

        await pipeline.Relay.HandleDeliveryForTestsAsync(Delivery(
            """{"ChatId":-100000000001,"Sender":"temporal-bridge","Text":"x","Timestamp":"2026-01-01T00:00:00+00:00","Type":"directive","TaskId":"t","Repo":"org/app"}"""));
        await pipeline.Relay.HandleDeliveryForTestsAsync(Delivery(
            """{"ChatId":-100000000001,"Sender":"temporal-bridge","Text":"x","Timestamp":"2026-01-01T00:00:00+00:00","Type":"directive","TaskId":"t"}"""));

        Assert.Equal([Repo, null], seen);

        static BasicDeliverEventArgs Delivery(string json) => new(
            consumerTag: "ct", deliveryTag: 1, redelivered: false, exchange: "fleet.group", routingKey: "agent-a",
            properties: new BasicProperties(), body: Encoding.UTF8.GetBytes(json));
    }

    // ── Telegram and check-ins ───────────────────────────────────────────────

    [Fact]
    public async Task DirectMessageAndNewCommand_RouteOnTheMessageChat()
    {
        var pipeline = Build(("chat", DmUser.ToString(System.Globalization.CultureInfo.InvariantCulture), ProjectA));

        await pipeline.Router.HandleAsync(DirectMessage("hello"));
        Assert.StartsWith(BlockHeader(ProjectA), await NextDeliveryAsync(pipeline, DmUser), StringComparison.Ordinal);

        await pipeline.Router.HandleAsync(DirectMessage("/new write it up"));
        var input = await NextDeliveryAsync(pipeline, DmUser);
        Assert.StartsWith(BlockHeader(ProjectA), input, StringComparison.Ordinal);
        Assert.Contains("write it up", input, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMessageInAnUnroutedChat_CarriesNothing()
    {
        var pipeline = Build(("chat", ChatValue, ProjectA));

        await pipeline.Router.HandleAsync(DirectMessage("hello"));

        Assert.DoesNotContain("[project context", await NextDeliveryAsync(pipeline, DmUser), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckIns_RouteOnTheBufferChat()
    {
        var pipeline = Build(("chat", ChatValue, ProjectA));
        pipeline.Behavior.AddAndPersist(Chat, "@someone", "earlier message", replyTo: null);

        _ = pipeline.Behavior.TriggerDebouncedGroupBatchForTestAsync(Chat);
        Assert.StartsWith(BlockHeader(ProjectA), await NextDeliveryAsync(pipeline, Chat), StringComparison.Ordinal);

        pipeline.Behavior.StartGroupCheckIn(Chat, "Proactive check-in", "Anything pending? If not: IDLE", CancellationToken.None);
        Assert.StartsWith(BlockHeader(ProjectA), await NextDeliveryAsync(pipeline, Chat), StringComparison.Ordinal);
    }
}
