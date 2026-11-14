using System.Reflection;
using System.Text;
using System.Text.Json;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Fleet.Journal.Client;
using Fleet.WireFixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RabbitMQ.Client;

namespace Fleet.Agent.Tests;

/// <summary>
/// What the agent puts on the broker and what it accepts from it, held to the samples under
/// <c>tests/fixtures/wire/</c>. The other end of each pair is tested against the same files in
/// <c>Fleet.Temporal.Tests</c>, <c>Fleet.Orchestrator.Tests</c> and <c>Fleet.Bridge.Tests</c>.
/// </summary>
/// <remarks>
/// An agent is reprovisioned after the orchestrator and the bridges are deployed, never with them,
/// so for a while every pair here runs with one new end and one old end. A sample that changes is a
/// message one of the two can no longer read.
/// </remarks>
public sealed class WireContractTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    // ── what the agent publishes on the relay exchange ───────────────────────────────────────────

    /// <summary>The execution id of the one durable delegation the samples are about.</summary>
    private const string Execution = "e0e1e2e3e4e5e6e7e8e9eaebecedeeef";

    public static TheoryData<string, string, string, string?, string?, string?> Published => new()
    {
        // sample file, target, type, correlationId, taskId, executionId
        { "relay.agent.directive.json", "Peer-Agent", RelayMessageType.Directive, null, null, null },
        { "relay.agent.response.json", "temporal-bridge", RelayMessageType.Response, null, "wf-1/review", null },
        // An empty id is no id: the answer is the bytes it was before the field existed.
        { "relay.agent.response.json", "temporal-bridge", RelayMessageType.Response, null, "wf-1/review", "" },
        // The answer to a task whose directive named an execution names it too, last.
        { "relay.agent.response.execution.json", "temporal-bridge", RelayMessageType.Response, null, "wf-1/review", Execution },
        { "relay.agent.partial-response.json", "temporal-bridge", RelayMessageType.PartialResponse, null, "wf-1/review~2", null },
        { "relay.agent.partial-response.execution.json", "temporal-bridge", RelayMessageType.PartialResponse, null, "wf-1/review~2", Execution },
        { "relay.agent.bridge-response.json", "bridge", RelayMessageType.BridgeResponse, WireGolden.SampleId, null, null },
        { "relay.agent.status-response.json", "bridge", RelayMessageType.StatusResponse, null, null, null },
    };

    [Theory]
    [MemberData(nameof(Published))]
    public async Task A_relay_message_is_published_as_its_sample(
        string sample, string target, string type, string? correlationId, string? taskId, string? executionId)
    {
        var (relay, publish, _) = await ConnectedRelayAsync();
        await using var _ = relay;
        var sent = Capture(publish);

        Assert.True(await relay.TryPublishToAgentAsync(
            target, 42, "the text", type, correlationId, taskId, executionId: executionId));

        var message = Assert.Single(sent);
        Assert.Equal("fleet.group", message.Exchange);
        // The routing key is the target's lowercased short name: that is the binding every agent
        // queue and both bridge queues declare.
        Assert.Equal(target.ToLowerInvariant(), message.RoutingKey);
        Assert.Equal(DeliveryModes.Persistent, message.Properties.DeliveryMode);
        WireGolden.AssertMatches(sample, message.Body);
    }

    [Fact]
    public async Task A_workflow_signal_is_published_to_the_temporal_bridge_as_its_sample()
    {
        var (relay, publish, _) = await ConnectedRelayAsync();
        await using var _ = relay;
        var sent = Capture(publish);

        await relay.PublishWorkflowSignalAsync("wf-1", "human-review", """{"Decision":"approved"}""");

        var message = Assert.Single(sent);
        Assert.Equal("fleet.group", message.Exchange);
        Assert.Equal("temporal-bridge", message.RoutingKey);
        WireGolden.AssertMatches("relay.agent.workflow-signal.json", message.Body);
    }

    [Fact]
    public async Task An_access_request_is_published_to_the_orchestrator_as_its_sample()
    {
        var (relay, publish, _) = await ConnectedRelayAsync();
        await using var _ = relay;
        var sent = Capture(publish);

        await relay.PublishAccessRequestAsync(new AccessRequestPayload
        {
            RequestId = "req-1",
            TargetAgent = "agent1",
            UserId = 7001,
            Username = "someone",
            FirstName = "Some",
            MessageText = "may I?",
        });

        var message = Assert.Single(sent);
        Assert.Equal("fleet.orchestrator", message.Exchange);
        Assert.Equal("access-request.agent1", message.RoutingKey);
        Assert.Equal(DeliveryModes.Persistent, message.Properties.DeliveryMode);
        WireGolden.AssertMatches("orchestrator.access-request.json", message.Body);
    }

    // ── what the agent accepts from the relay ────────────────────────────────────────────────────

    public static TheoryData<string, string, string, string?, string?, string?, string?, string?> Received => new()
    {
        // sample file, sender, type, correlationId, taskId, workflowId, signalName, executionId
        { "relay.temporal.directive.json", "temporal-bridge", "directive", null, "wf-1/review", null, null, null },
        { "relay.temporal.directive.norepo.json", "temporal-bridge", "directive", null, "wf-1/review", null, null, null },
        // A durable delegation: the same directive, naming its execution.
        { "relay.temporal.directive.execution.json", "temporal-bridge", "directive", null, "wf-1/review", null, null, Execution },
        { "relay.temporal.token-update.json", "temporal-bridge", "token-update", null, null, null, null, null },
        { "relay.orchestrator.config-update.json", "orchestrator", "config.update", null, null, null, null, null },
        { "relay.orchestrator.access-request-directive.json", "orchestrator", "access.request", null, null, null, null, null },
        { "relay.bridge.bridge-request.json", "bridge:reader:docs", "bridge-request", WireGolden.SampleId, null, null, null, null },
        { "relay.bridge.status-check.json", "bridge", "status-check", null, null, null, null, null },
        { "relay.agent.directive.json", "agent1", "directive", null, null, null, null, null },
    };

    [Theory]
    [MemberData(nameof(Received))]
    public async Task A_relay_message_from_each_sender_reaches_the_agent_with_its_fields(
        string sample, string sender, string type, string? correlationId, string? taskId,
        string? workflowId, string? signalName, string? executionId)
    {
        // A different short name from the one in relay.agent.directive.json, which the agent would
        // otherwise drop as its own message.
        var (relay, _, consumer) = await ConnectedRelayAsync(shortName: "receiver");
        await using var _ = relay;
        var received = new List<(long ChatId, string Sender, string Text, string Type, string? CorrelationId,
            string? TaskId, string? WorkflowId, string? SignalName, string? ExecutionId)>();
        relay.MessageReceived += (chatId, from, text, kind, correlation, task, workflow, signal, execution) =>
            received.Add((chatId, from, text, kind, correlation, task, workflow, signal, execution));

        await DeliverAsync(consumer(), WireGolden.SampleBytes(sample));

        var message = Assert.Single(received);
        Assert.Equal(sender, message.Sender);
        Assert.Equal(type, message.Type);
        Assert.Equal(correlationId, message.CorrelationId);
        Assert.Equal(taskId, message.TaskId);
        Assert.Equal(workflowId, message.WorkflowId);
        Assert.Equal(signalName, message.SignalName);
        Assert.Equal(executionId, message.ExecutionId);

        using var document = JsonDocument.Parse(WireGolden.Sample(sample));
        Assert.Equal(document.RootElement.GetProperty("Text").GetString(), message.Text);
        Assert.Equal(document.RootElement.GetProperty("ChatId").GetInt64(), message.ChatId);
    }

    /// <summary>
    /// The text of a <c>config.update</c> is a second document. This is the call
    /// <c>GroupBehavior.OnRelayMessage</c> makes on it.
    /// </summary>
    [Fact]
    public void The_allowlist_change_inside_a_config_update_is_read_in_full()
    {
        using var envelope = JsonDocument.Parse(WireGolden.Sample("relay.orchestrator.config-update.json"));
        var text = envelope.RootElement.GetProperty("Text").GetString()!;

        var update = JsonSerializer.Deserialize<ConfigUpdateMessage>(
            text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Assert.Equal(1, update.SchemaVersion);
        Assert.Equal("Agent1", update.TargetAgent);
        var added = Assert.Single(update.AddedUsers);
        Assert.Equal(7001, added.UserId);
        Assert.Equal("someone", added.Username);
        Assert.Equal("Some", added.FirstName);
        Assert.Equal([7002L], update.RemovedUserIds);
        Assert.Equal([-1001L], update.AddedGroupIds);
        Assert.Equal([-1002L], update.RemovedGroupIds);
    }

    // ── the cancel route's answer ────────────────────────────────────────────────────────────────

    /// <summary>
    /// What <c>POST /cancel/{taskId}?executionId=…</c> answers with. The route returns the record
    /// through <c>Results.Ok</c>, which writes it with the web defaults used here. The orchestrator
    /// passes the answer back unread and the temporal bridge reads <c>executionId</c> and
    /// <c>outcome</c> from it.
    /// </summary>
    [Theory]
    [InlineData(BridgeCancelOutcome.Cancelled, "cancel.agent-answer.cancelled.json")]
    [InlineData(BridgeCancelOutcome.NotFound, "cancel.agent-answer.not-found.json")]
    [InlineData(BridgeCancelOutcome.OtherExecution, "cancel.agent-answer.other-execution.json")]
    [InlineData(BridgeCancelOutcome.NoExecutionId, "cancel.agent-answer.no-execution-id.json")]
    public void A_cancel_that_named_an_execution_is_answered_as_its_sample(BridgeCancelOutcome outcome, string sample)
    {
        var json = JsonSerializer.Serialize(
            BridgeCancelAnswer.From("wf-1/review", Execution, outcome), new JsonSerializerOptions(JsonSerializerDefaults.Web));

        WireGolden.AssertMatches(sample, json);
    }

    // ── the broker objects the agent declares ────────────────────────────────────────────────────

    [Fact]
    public async Task The_relay_declares_its_exchanges_queue_and_bindings()
    {
        var (relay, publish, _, consume) = await ConnectedRelayWithChannelsAsync();
        await using var _ = relay;

        await publish.Received(1).ExchangeDeclareAsync(
            "fleet.group", "direct", Arg.Is(true), Arg.Is(false), Arg.Any<IDictionary<string, object?>>(),
            Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await publish.Received(1).ExchangeDeclareAsync(
            "fleet.orchestrator", "topic", Arg.Is(true), Arg.Is(false), Arg.Any<IDictionary<string, object?>>(),
            Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await consume.Received(1).QueueDeclareAsync(
            "fleet.agent.agent1", Arg.Is(true), Arg.Is(false), Arg.Is(false), Arg.Any<IDictionary<string, object?>>(),
            Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await consume.Received(1).QueueBindAsync(
            "fleet.agent.agent1", "fleet.group", "agent1", Arg.Any<IDictionary<string, object?>>(),
            Arg.Any<bool>(), Arg.Any<CancellationToken>());

        // The token broadcast: a durable fanout every credential-holding agent binds its queue to.
        await consume.Received(1).ExchangeDeclareAsync(
            "fleet.relay", "fanout", Arg.Is(true), Arg.Is(false), Arg.Any<IDictionary<string, object?>>(),
            Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await consume.Received(1).QueueBindAsync(
            "fleet.agent.agent1", "fleet.relay", "", Arg.Any<IDictionary<string, object?>>(),
            Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The chat service declares the same queue with the same arguments. A queue declared twice with
    /// different arguments is a channel error on whichever side comes second.
    /// </summary>
    [Fact]
    public async Task The_conversation_consumer_declares_the_command_queue_the_chat_service_declares()
    {
        var channel = Substitute.For<IChannel>();
        var attached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.BasicConsumeAsync(default!, default, default!, default, default, default, default!, default)
            .ReturnsForAnyArgs(_ => { attached.TrySetResult(); return "tag"; });
        var connection = Substitute.For<IConnection>();
        connection.CreateChannelAsync(default, default).ReturnsForAnyArgs(channel);

        var options = Options.Create(new ConversationsOptions
        {
            SouthBaseUrl = "http://south.invalid",
            SouthBearerToken = "token",
        });
        var consumer = new ConversationSouthConsumer(
            options,
            Options.Create(new AgentOptions { Name = "example-agent", ShortName = "example", Role = "example", WorkDir = "/tmp" }),
            new FixedBrokerConnection(connection),
            new ConversationSouthClient(new HttpClient(), options, NullLogger<ConversationSouthClient>.Instance),
            new ConversationSouthHandoff(), new ConversationOrdinalAllocator(), null!,
            new ConversationSouthCounters(), NullLogger<ConversationSouthConsumer>.Instance);

        await consumer.StartAsync(CancellationToken.None);
        try
        {
            await attached.Task.WaitAsync(Wait);

            await channel.Received(1).ExchangeDeclareAsync(
            "fleet.conversations", "direct", Arg.Is(true), Arg.Is(false), Arg.Any<IDictionary<string, object?>>(),
                Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
            await channel.Received(1).QueueDeclareAsync(
            "fleet.conversations.inbound.example-agent", Arg.Is(true), Arg.Is(false), Arg.Is(false),
                Arg.Is<IDictionary<string, object?>>(a => a.Count == 1 && (string?)a["x-queue-type"] == "quorum"),
                Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
            // Bound under the agent's unique orchestrator name, not its short name.
            await channel.Received(1).QueueBindAsync(
                "fleet.conversations.inbound.example-agent", "fleet.conversations", "example-agent",
                Arg.Any<IDictionary<string, object?>>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            await consumer.StopAsync(CancellationToken.None);
        }
    }

    // ── registration and heartbeat ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("registration")]
    [InlineData("heartbeat")]
    public async Task An_idle_agent_without_a_journal_or_a_plan_reports_as_its_sample(string kind)
    {
        // gemini has no harness plan, so PlanSha256 is left out, as it is from an older image.
        var message = await HeartbeatAsync(kind, provider: "gemini", withJournal: false, queued: 0);

        Assert.Equal("fleet.orchestrator", message.Exchange);
        Assert.Equal($"{kind}.agent1", message.RoutingKey);
        Assert.Equal(DeliveryModes.Persistent, message.Properties.DeliveryMode);
        // A heartbeat nobody consumed within a minute describes a state that has passed.
        Assert.Equal("60000", message.Properties.Expiration);
        WireGolden.AssertMatches("orchestrator.heartbeat.idle.json", message.Body);
    }

    [Fact]
    public async Task A_busy_agent_with_a_journal_a_plan_and_a_queue_reports_as_its_sample()
    {
        var message = await HeartbeatAsync("heartbeat", provider: "claude", withJournal: true, queued: 2);

        WireGolden.AssertMatches("orchestrator.heartbeat.busy.json", message.Body);
    }

    // ── fixtures ─────────────────────────────────────────────────────────────────────────────────

    private sealed record Sent(string Exchange, string RoutingKey, BasicProperties Properties, byte[] Body);

    private sealed class FixedBrokerConnection(IConnection connection) : IAgentBrokerConnection
    {
        public IConnection? Connection => connection;
    }

    private static List<Sent> Capture(IChannel channel)
    {
        var sent = new List<Sent>();
        channel.BasicPublishAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<BasicProperties>(),
                Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                sent.Add(new Sent(call.ArgAt<string>(0), call.ArgAt<string>(1), call.ArgAt<BasicProperties>(3),
                    call.ArgAt<ReadOnlyMemory<byte>>(4).ToArray()));
                return ValueTask.CompletedTask;
            });
        return sent;
    }

    private static async Task<(GroupRelayService Relay, IChannel Publish, Func<IAsyncBasicConsumer> Consumer)>
        ConnectedRelayAsync(string shortName = "agent1")
    {
        var (relay, publish, consumer, _) = await ConnectedRelayWithChannelsAsync(shortName);
        return (relay, publish, consumer);
    }

    /// <summary>A relay started against substitute channels, through its real start-up path.</summary>
    private static async Task<(GroupRelayService Relay, IChannel Publish, Func<IAsyncBasicConsumer> Consumer, IChannel Consume)>
        ConnectedRelayWithChannelsAsync(string shortName = "agent1")
    {
        var connection = Substitute.For<IConnection>();
        var publish = Substitute.For<IChannel>();
        var consume = Substitute.For<IChannel>();
        connection.CreateChannelAsync(default, default).ReturnsForAnyArgs(publish, consume);
        IAsyncBasicConsumer? consumer = null;
        consume.BasicConsumeAsync(default!, default, default!, default, default, default, default!, default)
            .ReturnsForAnyArgs(call => { consumer = call.Arg<IAsyncBasicConsumer>(); return "tag"; });

        var relay = new GroupRelayService(
            Options.Create(new AgentOptions
            {
                Name = "fleet-" + shortName, ShortName = shortName, Role = "test", WorkDir = "/tmp", Provider = "claude",
            }),
            Options.Create(new RabbitMqOptions { Host = "broker" }),
            NullLogger<GroupRelayService>.Instance)
        {
            Connect = (_, _) => Task.FromResult(connection),
            RetryDelay = _ => TimeSpan.Zero,
        };

        await relay.InitializeAsync(CancellationToken.None).WaitAsync(Wait);
        using var cts = new CancellationTokenSource(Wait);
        while (relay.State != GroupRelayService.States.Connected)
            await Task.Delay(10, cts.Token);

        return (relay, publish, () => consumer!, consume);
    }

    private static Task DeliverAsync(IAsyncBasicConsumer consumer, byte[] body) =>
        consumer.HandleBasicDeliverAsync(
            "tag", 1, false, "fleet.group", "receiver", new BasicProperties(), body, CancellationToken.None);

    private static async Task<Sent> HeartbeatAsync(string kind, string provider, bool withJournal, int queued)
    {
        var options = new AgentOptions
        {
            Name = "fleet-agent1", ShortName = "Agent1", Role = "developer", WorkDir = "/tmp",
            Provider = provider, Model = "the-model", Projects = ["alpha", "beta"],
        };
        var harness = new AgentHarness(options, _ => null, NullLogger<AgentHarness>.Instance);
        await using var executor = new MidTurnIsolationTests.ControlledExecutor(MidTurnInjectionStatus.Injected);
        var manager = MidTurnIsolationTests.Manager(provider, executor);
        var channel = Substitute.For<IChannel>();
        var sent = Capture(channel);
        var connectionState = Substitute.For<IFleetConnectionState>();
        connectionState.TelegramConnected.Returns(true);

        using var spoolDirectory = new TempDirectory();
        var journal = withJournal
            ? new JournalDrainer(
                new JournalSpool(spoolDirectory.Path),
                new JournalHttpClient(new HttpClient { BaseAddress = new Uri("http://journal.invalid") }, "token"),
                new JournalCounters(), NullLogger<JournalDrainer>.Instance)
            : null;

        var heartbeat = new OrchestratorHeartbeatService(
            Options.Create(options), Options.Create(new RabbitMqOptions()), manager, connectionState,
            NullLogger<OrchestratorHeartbeatService>.Instance, journal, harness: harness);
        typeof(OrchestratorHeartbeatService).GetField("_channel", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(heartbeat, channel);
        try
        {
            if (queued > 0)
            {
                await manager.StartTask(1, "the running task", "the running task", true, TaskSource.Relay, taskId: "wf-1/review");
                await executor.WaitStarted(1);
                await manager.StartTask(100, "from the owner", "from the owner", true, priority: TaskPriority.PrimaryHuman);
                for (var i = 1; i < queued; i++)
                    await manager.StartTask(100 + i, "routine", "routine", true);
            }

            await (Task)typeof(OrchestratorHeartbeatService)
                .GetMethod("PublishAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(heartbeat, [kind, CancellationToken.None])!;

            return Assert.Single(sent);
        }
        finally
        {
            await manager.CancelAllAsync();
            await heartbeat.DisposeAsync();
            journal?.Dispose();
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } =
            Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wire-" + Guid.NewGuid().ToString("N"))).FullName;

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
        }
    }
}
