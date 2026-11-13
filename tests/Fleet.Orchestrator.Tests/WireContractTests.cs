using System.Reflection;
using System.Text;
using System.Text.Json;
using Fleet.Conversations.Journal;
using Fleet.Orchestrator.Configuration;
using Fleet.Orchestrator.Endpoints;
using Fleet.Orchestrator.Services;
using Fleet.WireFixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Fleet.Orchestrator.Tests;

/// <summary>
/// What the orchestrator puts on the broker and what it accepts from agents and from the temporal
/// bridge, held to the samples under <c>tests/fixtures/wire/</c>. The other end of each pair is
/// tested against the same files in <c>Fleet.Agent.Tests</c> and <c>Fleet.Temporal.Tests</c>.
/// </summary>
public sealed class WireContractTests
{
    // ── what the orchestrator publishes to an agent ──────────────────────────────────────────────

    [Fact]
    public async Task An_allowlist_change_is_published_to_the_agent_as_its_sample()
    {
        string? routingKey = null;
        byte[]? body = null;
        await using var publisher = new AgentConfigPublisherService(
            Options.Create(new RabbitMqOptions()), NullLogger<AgentConfigPublisherService>.Instance)
        {
            Send = (key, bytes, _) => { routingKey = key; body = bytes; return Task.CompletedTask; },
        };

        var delivered = await publisher.PublishAllowlistUpdateAsync(
            "Agent1",
            addedUsers: [new AddedUserInfo { UserId = 7001, Username = "someone", FirstName = "Some" }],
            removedUserIds: [7002],
            addedGroupIds: [-1001],
            removedGroupIds: [-1002]);

        Assert.True(delivered);
        Assert.Equal("agent1", routingKey);
        WireGolden.AssertMatches("relay.orchestrator.config-update.json", body);
    }

    /// <summary>
    /// An access request arrives from one agent and leaves as a directive to the CTO agent. Both
    /// ends of the orchestrator's part are held here: the request it reads and the directive it
    /// writes.
    /// </summary>
    [Fact]
    public async Task An_access_request_is_read_and_forwarded_to_the_cto_agent_as_its_sample()
    {
        var channel = Substitute.For<IChannel>();
        var sent = new List<(string Exchange, string RoutingKey, BasicProperties Properties, byte[] Body)>();
        channel.BasicPublishAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<BasicProperties>(),
                Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                sent.Add((call.ArgAt<string>(0), call.ArgAt<string>(1), call.ArgAt<BasicProperties>(3),
                    call.ArgAt<ReadOnlyMemory<byte>>(4).ToArray()));
                return ValueTask.CompletedTask;
            });
        await using var publisher = new AgentConfigPublisherService(
            Options.Create(new RabbitMqOptions { Host = "broker" }), NullLogger<AgentConfigPublisherService>.Instance);
        SetField(publisher, "_channel", channel);
        SetField(publisher, "_initialized", true);

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Provisioning:EnvFilePath"] = Path.Combine(Path.GetTempPath(), "no-such-env-" + Guid.NewGuid().ToString("N")),
            ["FLEET_CTO_AGENT"] = "Co-Cto",
        }).Build();
        await using var consumer = new HeartbeatConsumerService(
            Options.Create(new RabbitMqOptions()),
            new AgentRegistry(new TaskHistoryStore(), NullLogger<AgentRegistry>.Instance),
            publisher, configuration, NullLogger<HeartbeatConsumerService>.Instance);

        await (Task)typeof(HeartbeatConsumerService)
            .GetMethod("OnMessageReceived", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(consumer, [this, Delivery("access-request.agent1", WireGolden.SampleBytes("orchestrator.access-request.json"))])!;

        var message = Assert.Single(sent);
        Assert.Equal("fleet.group", message.Exchange);
        Assert.Equal("co-cto", message.RoutingKey);
        Assert.Equal(DeliveryModes.Persistent, message.Properties.DeliveryMode);
        WireGolden.AssertMatches("relay.orchestrator.access-request-directive.json", message.Body);

        // Every field of the request is in the directive's text, so each was read under its name.
        using var envelope = JsonDocument.Parse(message.Body);
        var text = envelope.RootElement.GetProperty("Text").GetString()!;
        Assert.Contains("bot 'agent1'", text);
        Assert.Contains("@someone (id=7001)", text);
        Assert.Contains("Message: may I?", text);
        Assert.Contains("Request ID: req-1", text);
    }

    [Fact]
    public async Task A_configuration_change_is_published_to_the_peers_as_its_sample()
    {
        var channel = Substitute.For<IChannel>();
        channel.IsOpen.Returns(true);
        (string Exchange, string RoutingKey, BasicProperties Properties, byte[] Body)? sent = null;
        channel.BasicPublishAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<BasicProperties>(),
                Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                sent = (call.ArgAt<string>(0), call.ArgAt<string>(1), call.ArgAt<BasicProperties>(3),
                    call.ArgAt<ReadOnlyMemory<byte>>(4).ToArray());
                return ValueTask.CompletedTask;
            });
        var service = new ConfigService(
            new ConfigurationBuilder().Build(),
            Options.Create(new RabbitMqOptions { Host = "broker" }),
            Substitute.For<IServiceScopeFactory>(),
            NullLogger<ConfigService>.Instance);
        SetField(service, "_publishChannel", channel);

        Assert.True(await service.TryPublishAclChangedAsync());

        Assert.NotNull(sent);
        Assert.Equal("fleet.orchestrator", sent.Value.Exchange);
        Assert.Equal("config.changed", sent.Value.RoutingKey);
        Assert.Equal("application/json", sent.Value.Properties.ContentType);
        WireGolden.AssertMatches("orchestrator.config-changed.json", sent.Value.Body);
    }

    // ── what the orchestrator accepts ────────────────────────────────────────────────────────────

    [Fact]
    public void An_idle_agents_heartbeat_is_read_with_its_fields()
    {
        var heartbeat = HeartbeatConsumerService.DeserializeHeartbeat(
            WireGolden.Sample("orchestrator.heartbeat.idle.json"))!;

        Assert.Equal("agent1", heartbeat.AgentName);
        Assert.Equal("idle", heartbeat.Status);
        Assert.Equal(DateTimeOffset.Parse(WireGolden.SampleTimestamp), heartbeat.Timestamp);
        Assert.Null(heartbeat.CurrentTask);
        Assert.Null(heartbeat.CurrentTaskId);
        Assert.Equal("<env>", heartbeat.Version);
        Assert.Equal("<env>", heartbeat.ContainerName);
        Assert.Equal("developer", heartbeat.Role);
        Assert.Equal("the-model", heartbeat.Model);
        Assert.Equal(0, heartbeat.QueuedCount);
        Assert.Empty(heartbeat.QueuedMessages!);
        Assert.Null(heartbeat.BackgroundTasks);
        Assert.Equal(0, heartbeat.PriorityQueuedCount);
        // Left out by an agent without a journal or a harness plan, as by an older image.
        Assert.Null(heartbeat.Journal);
        Assert.Null(heartbeat.PlanSha256);
    }

    [Fact]
    public void A_busy_agents_heartbeat_is_read_with_its_queue_journal_and_plan()
    {
        var heartbeat = HeartbeatConsumerService.DeserializeHeartbeat(
            WireGolden.Sample("orchestrator.heartbeat.busy.json"))!;

        Assert.Equal("busy", heartbeat.Status);
        Assert.Equal("the running task", heartbeat.CurrentTask);
        Assert.Equal("wf-1/review", heartbeat.CurrentTaskId);
        Assert.Equal(2, heartbeat.QueuedCount);
        Assert.Equal(1, heartbeat.PriorityQueuedCount);
        Assert.Equal(WireGolden.SampleSha, heartbeat.PlanSha256);

        Assert.Equal(2, heartbeat.QueuedMessages!.Length);
        var first = heartbeat.QueuedMessages[0];
        Assert.Equal("from the owner", first.Preview);
        Assert.True(first.Priority);
        Assert.Equal(DateTimeOffset.Parse(WireGolden.SampleTimestamp), first.QueuedAt);
        Assert.False(heartbeat.QueuedMessages[1].Priority);

        // The journal block is camelCase inside a PascalCase message: read case-insensitively.
        Assert.NotNull(heartbeat.Journal);
        Assert.True(heartbeat.Journal.Enabled);
        Assert.Equal(0, heartbeat.Journal.SpoolDepth);
        Assert.Equal(0, heartbeat.Journal.OldestAgeSeconds);
        Assert.Equal(0, heartbeat.Journal.Dropped);
        Assert.Equal(0, heartbeat.Journal.Dead);
        Assert.Equal(0, heartbeat.Journal.AuthFailed);
        Assert.Equal(0, heartbeat.Journal.BindingFailed);
    }

    [Fact]
    public void A_delegated_approval_from_the_bridge_is_read_with_its_fields()
    {
        // The options EpicGrantEndpoints reads the body with.
        var options = (JsonSerializerOptions)typeof(EpicGrantEndpoints)
            .GetField("DecisionJson", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        var request = JsonSerializer.Deserialize<EpicGrantDecisionRequest>(
            WireGolden.Sample("epic-grant.decision-request.json"), options)!;

        Assert.Equal("fleet", request.Namespace);
        Assert.Equal("wf-1", request.WorkflowId);
        Assert.Equal("merge-approval", request.Gate);
        Assert.Equal("approved", request.Decision);
        Assert.Equal("merge-approval:1", request.VisitId);
        Assert.Equal("org/app#12@abc123", request.ArtifactRef);
        Assert.Equal("consensus", request.Evidence);
        Assert.Equal("co-cto", request.Caller);
    }

    // ── names the orchestrator shares with other services ────────────────────────────────────────

    [Fact]
    public void The_exchange_agents_report_to_is_fleet_orchestrator()
    {
        Assert.Equal("fleet.orchestrator", new RabbitMqOptions().Exchange);
    }

    /// <summary>
    /// The signals the dashboard can send, by name. The temporal bridge reserves the approver-only
    /// ones under the same names (<c>CeoGateSignals</c>).
    /// </summary>
    [Fact]
    public void The_signals_the_dashboard_offers_are_exactly_these()
    {
        var names = SignalRegistry.All.Values.SelectMany(signals => signals).Select(signal => signal.Name)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);

        Assert.Equal(
            ["acceptance-decision", "advisory-review", "design-approval", "doc-review", "escalation-decision",
             "human-review", "merge-approval", "request-input"],
            names);
    }

    // ── the journal token, minted here and verified by the chat service ──────────────────────────

    private const string KeyOne = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8";
    private const string KeyTwo = "ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8";

    /// <summary>
    /// A known answer. Both implementations must produce it for this key, purpose and subject, so a
    /// token minted by either before a change is still accepted after it.
    /// </summary>
    [Fact]
    public void The_token_for_a_known_key_purpose_and_subject_is_this_one()
    {
        const string expected = "cj1.ingest.agent-1.ldUeRGsdmHCBmcq4C9k5Hhp8s2LlI0A0o1PwgzU7JhI";

        Assert.Equal(expected, Tokens(KeyOne).Mint("ingest", "agent-1"));
        Assert.Equal(expected, JournalTokens.Mint(JournalTokens.ParseKeys(KeyOne)[0], "ingest", "agent-1"));
    }

    public static TheoryData<string> Purposes => new()
    {
        JournalTokenService.PurposeIngest, JournalTokenService.PurposeRead, JournalTokenService.PurposeStatus,
        JournalTokenService.PurposeReadCrossChat, JournalTokenService.PurposeCrossChatAuthz,
        // Purposes the chat service mints for itself: the south surface and the sign-in check.
        "south", "auth-check",
    };

    [Theory]
    [MemberData(nameof(Purposes))]
    public void A_token_minted_by_either_side_is_verified_by_the_other(string purpose)
    {
        // Two keys on each side, the minting one first: rotation keeps the old key verifying.
        var orchestrator = Tokens($"{KeyOne},{KeyTwo}");
        var comms = JournalTokens.ParseKeys($"{KeyOne}, {KeyTwo}");

        var fromOrchestrator = orchestrator.Mint(purpose, "agent_1-x");
        Assert.True(JournalTokens.TryVerify(fromOrchestrator, purpose, comms, out var subject));
        Assert.Equal("agent_1-x", subject);

        var fromComms = JournalTokens.Mint(comms[0], purpose, "agent_1-x");
        Assert.Equal(fromOrchestrator, fromComms);
        Assert.True(orchestrator.TryVerify(fromComms, purpose, out subject));
        Assert.Equal("agent_1-x", subject);

        // Minted with the second key, as by a peer that has not rotated yet.
        var fromOldKey = JournalTokens.Mint(comms[1], purpose, "agent_1-x");
        Assert.True(orchestrator.TryVerify(fromOldKey, purpose, out _));
        Assert.True(JournalTokens.TryVerify(Tokens($"{KeyTwo},{KeyOne}").Mint(purpose, "agent_1-x"), purpose, comms, out _));

        // Neither accepts it for another purpose, or with a key it does not hold.
        Assert.False(orchestrator.TryVerify(fromComms, "other", out _));
        Assert.False(JournalTokens.TryVerify(fromOrchestrator, "other", comms, out _));
        Assert.False(Tokens(KeyTwo).TryVerify(fromComms, purpose, out _));
        Assert.False(JournalTokens.TryVerify(fromOrchestrator, purpose, JournalTokens.ParseKeys(KeyTwo), out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64url!")]
    [InlineData("c2hvcnQ")]
    public void A_key_one_side_refuses_the_other_refuses_too(string key)
    {
        Assert.NotNull(Tokens(key).DescribeKeyFault());
        Assert.Throws<FormatException>(() => JournalTokens.ParseKeys(key));
    }

    // ── fixtures ─────────────────────────────────────────────────────────────────────────────────

    private static JournalTokenService Tokens(string keys) => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Journal:TokenKey"] = keys }).Build());

    private static void SetField(object instance, string name, object value) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);

    private static BasicDeliverEventArgs Delivery(string routingKey, byte[] body) => new(
        consumerTag: "tag", deliveryTag: 1, redelivered: false, exchange: "fleet.orchestrator",
        routingKey: routingKey, properties: new BasicProperties(), body: body);
}
