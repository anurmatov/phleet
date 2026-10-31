using Fleet.Agent.Configuration;
using Fleet.Agent.Services;
using Fleet.Journal.Client;
using Fleet.Shared.Journal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Fleet.Agent.Tests;

/// <summary>
/// Managed stock tool-send receipts on the existing control lane (#492): the workload admits a typed
/// receipt from its own queue only from the epoch control principal on fleet.group routed to it, with
/// its own pinned agent/sandbox/epoch, a session and a matching destination; it is captured once,
/// spooled before the ack, and anything else is refused. Synthetic values only.
/// </summary>
public sealed class ManagedStockAgentReceiptTests : IDisposable
{
    private const long Chat = -1001000000001;
    private const long BotId = 4242;
    private readonly string _spoolRoot = Path.Combine(Path.GetTempPath(), "managed-receipt-spool-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_spoolRoot)) Directory.Delete(_spoolRoot, true);
    }

    [Fact]
    public async Task ManagedStockAgent_admitted_receipt_is_captured_once_and_spooled_before_ack()
    {
        using var fixture = new StockBrokerTransportTests.Fixture();
        await using var rig = await Rig.StartAsync(fixture, _spoolRoot);
        var body = Receipt(fixture);

        await rig.Deliver(rig.ControlProps(), body, tag: 1);

        var entry = Assert.Single(rig.Spool.Pending());
        Assert.Equal("agent_tool", entry.Record["origin"]!.GetValue<string>());
        Assert.Equal(Chat, entry.Record["telegram"]!["chatId"]!.GetValue<long>());
        Assert.Equal([(1UL, 1)], rig.Acks); // acked once, after the spool write
        Assert.Equal(1, rig.Counters.Get("tool_send_captured"));

        await rig.Deliver(rig.ControlProps(), body, tag: 2); // redelivered duplicate
        Assert.Single(rig.Spool.Pending());
        Assert.Equal(1, rig.Counters.Get("tool_send_captured"));
        Assert.Equal(2, rig.Acks.Count);
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("session")]
    [InlineData("epoch")]
    [InlineData("sandbox")]
    [InlineData("destination")]
    [InlineData("user")]
    [InlineData("workload_user")]
    [InlineData("routing")]
    [InlineData("exchange")]
    [InlineData("revoked")]
    [InlineData("unpinned")]
    public async Task ManagedStockAgent_wrong_context_or_sender_is_rejected_and_never_captured(string fault)
    {
        using var fixture = new StockBrokerTransportTests.Fixture();
        await using var rig = await Rig.StartAsync(fixture, _spoolRoot);
        var props = rig.ControlProps();
        var routing = "agent1";
        var exchange = "fleet.group";
        var body = fault switch
        {
            "agent" => Receipt(fixture, agent: "agent2"),
            "session" => Receipt(fixture, session: ""),
            "epoch" => Receipt(fixture, epoch: Guid.NewGuid()),
            "sandbox" => Receipt(fixture, sandbox: "other-sandbox"),
            "destination" => Receipt(fixture, destination: Chat - 1),
            "unpinned" => Receipt(fixture, pinned: false),
            _ => Receipt(fixture),
        };
        switch (fault)
        {
            case "user": props.UserId = "foreign"; break;
            case "workload_user": props.UserId = fixture.Binding.Principal("workload"); break;
            case "routing": routing = "agent2"; break;
            case "exchange": exchange = "fleet.journal.tool-sends"; break;
            case "revoked": fixture.Save("revoked"); break;
        }

        await rig.Deliver(props, body, tag: 7, routing: routing, exchange: exchange);

        Assert.Empty(rig.Spool.Pending());
        Assert.Equal(0, rig.Counters.Get("tool_send_captured"));
        Assert.Equal([(7UL, 0)], rig.Acks); // refused without requeue or retargeting
    }

    [Fact]
    public async Task ManagedStockAgent_ordinary_relay_admission_is_unchanged()
    {
        using var fixture = new StockBrokerTransportTests.Fixture();
        await using var rig = await Rig.StartAsync(fixture, _spoolRoot);
        var received = 0;
        rig.Relay.MessageReceived += (_, _, _, _, _, _, _, _) => received++;
        var request = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            ChatId = 0, Sender = "bridge:operator", Text = "synthetic task", Type = "bridge-request",
            CorrelationId = Guid.NewGuid().ToString("N"),
        });

        await rig.Deliver(new BasicProperties { UserId = fixture.Binding.Principal("control") }, request, tag: 3);
        // A receipt body without the typed marker is not a receipt and is not admitted as a relay message.
        await rig.Deliver(new BasicProperties { UserId = fixture.Binding.Principal("control") }, Receipt(fixture), tag: 4);

        Assert.Equal(1, received);
        Assert.Empty(rig.Spool.Pending());
        Assert.Equal([(3UL, 0), (4UL, 0)], rig.Acks);
    }

    private static byte[] Receipt(StockBrokerTransportTests.Fixture fixture, string agent = "agent1", string session = "synthetic-session",
        Guid? epoch = null, string sandbox = "synthetic-sandbox", long destination = Chat, bool pinned = true)
        => ToolSendReceipts.Serialize(new ToolSendReceipt
        {
            Agent = agent,
            Tool = "send_message",
            RequestedAt = DateTimeOffset.UtcNow,
            BotId = BotId,
            Chat = new ToolSendChat(Chat, "supergroup", null),
            Messages = [new ToolSendMessage(9001, DateTime.UtcNow, null, "sent by a tool", ToolSendTextFormats.Plain)],
            Managed = pinned ? new ToolSendManagedContext(sandbox, epoch ?? fixture.Epoch, session, destination) : null,
        });

    private sealed class Rig : IAsyncDisposable
    {
        private AsyncEventingBasicConsumer _consumer = null!;
        private StockBrokerTransportTests.Fixture _fixture = null!;

        public GroupRelayService Relay { get; private set; } = null!;
        public JournalSpool Spool { get; private set; } = null!;
        public JournalCounters Counters { get; } = new();
        public List<(ulong Tag, int Spooled)> Acks { get; } = [];

        public static async Task<Rig> StartAsync(StockBrokerTransportTests.Fixture fixture, string spoolRoot)
        {
            var rig = new Rig { _fixture = fixture, Relay = fixture.Relay(), Spool = new JournalSpool(spoolRoot) };
            var capture = new JournalCapture(rig.Spool, rig.Counters,
                new AllowlistHolder(Options.Create(new TelegramOptions { AllowedUserIds = [1001], AllowedGroupIds = [Chat] })),
                Options.Create(new JournalOptions { IngestToken = "cj1.ingest.agent1.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" }),
                NullLogger<JournalCapture>.Instance);
            var receipts = new ToolSendReceiptConsumer(
                Options.Create(new AgentOptions { Name = "agent1", ShortName = "agent1", Role = "synthetic", WorkDir = spoolRoot }),
                rig.Relay, capture, rig.Counters, () => BotId, NullLogger<ToolSendReceiptConsumer>.Instance);
            receipts.AttachManaged(rig.Relay);
            rig.Relay.StockAckHook = tag => { rig.Acks.Add((tag, rig.Spool.Pending().Count)); return Task.CompletedTask; };
            await rig.Relay.InitializeAsync(default);
            rig._consumer = fixture.Channel.ReceivedCalls().SelectMany(c => c.GetArguments()).OfType<AsyncEventingBasicConsumer>().Single();
            Assert.Contains(fixture.Channel.ReceivedCalls(), c => c.GetMethodInfo().Name == "BasicConsumeAsync"
                && c.GetArguments().OfType<bool>().FirstOrDefault() == false); // manual ack in stock mode
            return rig;
        }

        public BasicProperties ControlProps() => new()
        {
            UserId = _fixture.Binding.Principal("control"),
            Type = ToolSendReceipts.ManagedMessageType,
            ContentType = "application/json",
        };

        public Task Deliver(BasicProperties props, byte[] body, ulong tag, string routing = "agent1", string exchange = "fleet.group")
            => _consumer.HandleBasicDeliverAsync("owned", tag, false, exchange, routing, props, body);

        public ValueTask DisposeAsync() => Relay.DisposeAsync();
    }
}
