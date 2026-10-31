using System.Text.Json;
using System.Threading.Channels;
using Fleet.Shared.Journal;
using Fleet.Shared.Security;
using Fleet.Telegram.Services;
using Fleet.Telegram.Tests.Tools;
using Fleet.Telegram.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using RabbitMQ.Client;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Fleet.Telegram.Tests;

/// <summary>
/// Managed stock tool-send receipts (#492 existing-role routing): a verified managed send goes over
/// the epoch control lane as the control principal to fleet.group/agent; ordinary sends are
/// unchanged; a stale or revoked context is dropped without retargeting. Synthetic values only.
/// </summary>
public sealed class ManagedToolSendReceiptTests : IDisposable
{
    private const string Agent = "example-agent";
    private const long ChatId = -1001234567890;
    private const string Session = "synthetic-session";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "managed-receipts-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _epoch = Guid.NewGuid();

    public ManagedToolSendReceiptTests()
    {
        Directory.CreateDirectory(_dir);
        SaveRecord("active", _epoch);
        File.WriteAllText(Path.Combine(_dir, Agent + ".control.password"), "synthetic-control");
        File.WriteAllText(Path.Combine(_dir, Agent + ".observer.password"), "synthetic-observer");
    }

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public async Task Managed_send_publishes_through_the_control_producer_to_the_agents_group_route()
    {
        await using var rig = new Rig(_dir);

        Assert.Contains("\"ok\":true", await rig.Send(pin: new ManagedToolSendPin(Agent, "sandbox-a", _epoch, Session)));

        var published = await rig.Lanes.NextAsync();
        Assert.Equal("fleet.group", published.Exchange);
        Assert.Equal(Agent, published.RoutingKey);
        var control = "stock-" + _epoch.ToString("N") + "-control";
        Assert.Equal(control, published.Properties.UserId);
        Assert.Equal(ToolSendReceipts.ManagedMessageType, published.Properties.Type);
        // Only the selected epoch's control credential is read, never observer/admin.
        Assert.Equal(control, rig.Lanes.Factory!.UserName);
        Assert.Equal("synthetic-control", rig.Lanes.Factory.Password);
        Assert.Equal("stock-" + _epoch.ToString("N"), rig.Lanes.Factory.VirtualHost);

        Assert.True(ToolSendReceipts.TryParse(published.Body, out var receipt, out var reason), reason);
        Assert.Equal(Agent, receipt!.Agent);
        Assert.Equal(new ToolSendManagedContext("sandbox-a", _epoch, Session, ChatId), receipt.Managed);
        await Eventually(() => rig.Publisher.Counters.Published == 1);
        Assert.False(rig.Ordinary.HasMore); // never the ordinary receipt exchange
    }

    [Fact]
    public async Task Ordinary_send_keeps_the_ordinary_publisher_unchanged()
    {
        await using var rig = new Rig(_dir);

        Assert.Contains("\"ok\":true", await rig.Send(pin: null));

        var (routingKey, body) = await rig.Ordinary.NextAsync();
        Assert.Equal(Agent, routingKey);
        Assert.DoesNotContain("managed", System.Text.Encoding.UTF8.GetString(body), StringComparison.OrdinalIgnoreCase);
        await Eventually(() => rig.Publisher.Counters.Published == 1);
        Assert.Equal(0, rig.Lanes.Opened);
    }

    [Fact]
    public async Task Stale_epoch_context_is_dropped_without_retargeting()
    {
        await using var rig = new Rig(_dir);
        SaveRecord("active", Guid.NewGuid()); // re-enrolled: the pinned epoch is no longer current

        Assert.Contains("\"ok\":true", await rig.Send(pin: new ManagedToolSendPin(Agent, "sandbox-a", _epoch, Session)));

        await Eventually(() => rig.Publisher.Counters.Dropped == 1);
        Assert.Equal(0, rig.Publisher.Counters.Published);
        Assert.Equal(0, rig.Lanes.Opened);
        Assert.False(rig.Ordinary.HasMore);
    }

    [Fact]
    public async Task Revoked_record_drops_the_receipt_and_closes_the_open_lane()
    {
        await using var rig = new Rig(_dir);
        var pin = new ManagedToolSendPin(Agent, "sandbox-a", _epoch, Session);
        await rig.Send(pin);
        await rig.Lanes.NextAsync();

        SaveRecord("revoked", _epoch);
        await rig.Send(pin);

        await Eventually(() => rig.Publisher.Counters.Dropped == 1);
        Assert.Equal(1, rig.Publisher.Counters.Published);
        Assert.True(rig.Lanes.Closed >= 1);
        Assert.False(rig.Ordinary.HasMore);
    }

    [Fact]
    public async Task Managed_send_without_a_session_is_dropped_not_sent_ordinary()
    {
        await using var rig = new Rig(_dir);

        await rig.Send(pin: new ManagedToolSendPin(Agent, "sandbox-a", _epoch, ""));

        await Eventually(() => rig.Publisher.Counters.Dropped == 1);
        Assert.Equal(0, rig.Lanes.Opened);
        Assert.False(rig.Ordinary.HasMore);
    }

    [Fact]
    public async Task Foreign_pinned_agent_is_dropped()
    {
        await using var rig = new Rig(_dir);

        await rig.Send(pin: new ManagedToolSendPin("other-agent", "sandbox-a", _epoch, Session));

        await Eventually(() => rig.Publisher.Counters.Dropped == 1);
        Assert.Equal(0, rig.Lanes.Opened);
        Assert.False(rig.Ordinary.HasMore);
    }

    [Fact]
    public async Task Receiver_without_an_authority_directory_logs_and_counts_a_named_drop()
    {
        var logger = new CapturingLogger<StockControlReceiptTransport>();
        await using var rig = new Rig("", logger);

        Assert.Contains("\"ok\":true", await rig.Send(pin: new ManagedToolSendPin(Agent, "sandbox-a", _epoch, Session)));

        await Eventually(() => rig.Publisher.Counters.Dropped == 1);
        Assert.Equal(1, rig.Transport.Misconfigured[StockControlReceiptTransport.AuthorityDirectoryUnset]);
        Assert.Equal(1, rig.Transport.Misconfigured.Total);
        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("reason=authority_directory_unset", warning);
        AssertContentFree(warning);
        Assert.Equal(0, rig.Publisher.Counters.Published);
        Assert.Equal(0, rig.Lanes.Opened);
        Assert.False(rig.Ordinary.HasMore);
    }

    [Fact]
    public async Task Missing_control_credential_logs_and_counts_a_named_drop_without_opening_a_lane()
    {
        File.Delete(Path.Combine(_dir, Agent + ".control.password"));
        var logger = new CapturingLogger<StockControlReceiptTransport>();
        await using var rig = new Rig(_dir, logger);
        var pin = new ManagedToolSendPin(Agent, "sandbox-a", _epoch, Session);

        await rig.Send(pin);
        await rig.Send(pin);

        await Eventually(() => rig.Publisher.Counters.Dropped == 2);
        Assert.Equal(2, rig.Transport.Misconfigured[StockControlReceiptTransport.ControlCredentialUnavailable]);
        // Warned once per reason per interval, but every drop is counted.
        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("reason=control_credential_unavailable", warning);
        AssertContentFree(warning);
        Assert.Equal(0, rig.Publisher.Counters.Published);
        Assert.Equal(0, rig.Lanes.Opened);
        Assert.False(rig.Ordinary.HasMore);
    }

    [Fact]
    public async Task Unreadable_authority_directory_logs_the_identity_reason_code()
    {
        var logger = new CapturingLogger<StockControlReceiptTransport>();
        await using var rig = new Rig(Path.Combine(_dir, "missing"), logger);

        await rig.Send(pin: new ManagedToolSendPin(Agent, "sandbox-a", _epoch, Session));

        await Eventually(() => rig.Publisher.Counters.Dropped == 1);
        Assert.Equal(1, rig.Transport.Misconfigured["authority_unavailable"]);
        AssertContentFree(Assert.Single(logger.Warnings));
        Assert.Contains("reason=authority_unavailable", logger.Warnings[0]);
    }

    [Fact]
    public async Task Stale_context_is_not_counted_as_a_misconfiguration()
    {
        var logger = new CapturingLogger<StockControlReceiptTransport>();
        await using var rig = new Rig(_dir, logger);
        SaveRecord("active", Guid.NewGuid());

        await rig.Send(pin: new ManagedToolSendPin(Agent, "sandbox-a", _epoch, Session));

        await Eventually(() => rig.Publisher.Counters.Dropped == 1);
        Assert.Equal(0, rig.Transport.Misconfigured.Total);
        Assert.Empty(logger.Warnings);
    }

    private void AssertContentFree(string warning)
    {
        foreach (var secret in new[]
                 {
                     "synthetic text", ChatId.ToString(), Session, Agent, "sandbox-a", _epoch.ToString(), _epoch.ToString("N"),
                     "synthetic-control", "tok:agent", _dir,
                 })
            Assert.DoesNotContain(secret, warning, StringComparison.OrdinalIgnoreCase);
    }

    private void SaveRecord(string state, Guid epoch) => File.WriteAllText(Path.Combine(_dir, Agent + ".json"),
        JsonSerializer.Serialize(new StockEnrollmentAuthority
        {
            State = state, Agent = Agent, SandboxId = "sandbox-a", Epoch = epoch, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            GatewayIssuer = "issuer", GatewayAudience = "audience", GatewayJwksPath = Path.Combine(_dir, "jwks"),
            AssertionPrivateKeyPath = Path.Combine(_dir, "private"), AssertionPublicKeyPath = Path.Combine(_dir, "public"),
            MemoryHost = "memory.example.test", BrokerHost = "broker.example.test",
        }));

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not met within 10 s");
            await Task.Delay(10);
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly BotClientFactory _factory;

        public Rig(string dir, ILogger<StockControlReceiptTransport>? logger = null)
        {
            Ordinary = new OrdinaryTransport();
            Lanes = new LaneRecorder();
            Transport = new StockControlReceiptTransport(dir, logger ?? NullLogger<StockControlReceiptTransport>.Instance,
                Lanes.OpenAsync);
            Publisher = new ToolSendReceiptPublisher(Ordinary, NullLogger<ToolSendReceiptPublisher>.Instance,
                managed: Transport);
            _factory = new BotClientFactory(NullLogger<BotClientFactory>.Instance, _ => new FakeBotClient(req => new Message
            {
                Id = 41,
                Date = DateTime.UtcNow,
                Chat = new Chat { Id = ChatId, Type = ChatType.Supergroup, Title = "Example group" },
            }));
            _factory.ApplyAgentTokens(new Dictionary<string, string> { [Agent] = "tok:agent" });
            Publisher.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        public OrdinaryTransport Ordinary { get; }
        public LaneRecorder Lanes { get; }
        public StockControlReceiptTransport Transport { get; }
        public ToolSendReceiptPublisher Publisher { get; }

        public Task<string> Send(ManagedToolSendPin? pin)
        {
            var context = new DefaultHttpContext();
            context.Request.QueryString = new QueryString($"?agent={Agent}");
            if (pin is not null) context.Items[ManagedToolSendPin.ItemKey] = pin;
            var accessor = Substitute.For<IHttpContextAccessor>();
            accessor.HttpContext.Returns(context);
            var sender = new TelegramSender(Publisher, http: accessor);
            return new SendMessageTool(_factory, accessor, sender, NullLogger<SendMessageTool>.Instance)
                .SendAsync(ChatId.ToString(), "synthetic text");
        }

        public async ValueTask DisposeAsync()
        {
            await Publisher.StopAsync(CancellationToken.None);
            Publisher.Dispose();
        }
    }

    private sealed class OrdinaryTransport : IToolSendReceiptTransport
    {
        private readonly Channel<(string, byte[])> _published = Channel.CreateUnbounded<(string, byte[])>();
        public bool HasMore => _published.Reader.Count > 0;

        public Task PublishAsync(string routingKey, ReadOnlyMemory<byte> body, CancellationToken ct)
        {
            _published.Writer.TryWrite((routingKey, body.ToArray()));
            return Task.CompletedTask;
        }

        public async Task<(string RoutingKey, byte[] Body)> NextAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return await _published.Reader.ReadAsync(timeout.Token);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<string> _warnings = [];

        public List<string> Warnings { get { lock (_warnings) return [.. _warnings]; } }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) lock (_warnings) _warnings.Add(formatter(state, exception));
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    }

    internal sealed record Published(string Exchange, string RoutingKey, BasicProperties Properties, byte[] Body);

    internal sealed class LaneRecorder
    {
        private readonly Channel<Published> _published = Channel.CreateUnbounded<Published>();
        public ConnectionFactory? Factory { get; private set; }
        public int Opened;
        public int Closed;

        public Task<IManagedReceiptLane> OpenAsync(ConnectionFactory factory, CancellationToken ct)
        {
            Factory = factory;
            Interlocked.Increment(ref Opened);
            return Task.FromResult<IManagedReceiptLane>(new Lane(this));
        }

        public async Task<Published> NextAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return await _published.Reader.ReadAsync(timeout.Token);
        }

        private sealed class Lane(LaneRecorder owner) : IManagedReceiptLane
        {
            public bool IsOpen { get; private set; } = true;

            public Task PublishAsync(string exchange, string routingKey, BasicProperties properties, ReadOnlyMemory<byte> body,
                CancellationToken ct)
            {
                owner._published.Writer.TryWrite(new(exchange, routingKey, properties, body.ToArray()));
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                IsOpen = false;
                Interlocked.Increment(ref owner.Closed);
                return ValueTask.CompletedTask;
            }
        }
    }
}
