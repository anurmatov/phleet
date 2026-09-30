using System.Text.RegularExpressions;
using System.Threading.Channels;
using Fleet.Shared.Journal;
using Fleet.Telegram.Services;
using Fleet.Telegram.Tests.Tools;
using Fleet.Telegram.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Fleet.Telegram.Tests;

/// <summary>
/// Tool-send receipts (#394 AC5 fleet-telegram part, AC6): what one tool call reports back to the
/// agent, what it never reports, and that the broker can never reach a tool result.
/// </summary>
public sealed class ToolSendReceiptTests
{
    private const string AgentName = "Example-Agent";
    private const long GroupChatId = -1001234567890;
    private const long DmChatId = 123456789;

    private static readonly DateTimeOffset T0 = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    // ── what one call reports (AC5) ──────────────────────────────────────────

    /// <summary>
    /// One receipt per call, every accepted chunk in send order with the text and parse mode it was
    /// sent with, and <c>requestedAt</c> taken before the first Bot API call.
    /// </summary>
    [Theory]
    [InlineData(ChatType.Supergroup, GroupChatId, "supergroup", "Example group")]
    [InlineData(ChatType.Private, DmChatId, "private", null)]
    public async Task Send_message_reports_every_accepted_chunk_as_one_receipt(
        ChatType chatType, long chatId, string wireType, string? title)
    {
        var clock = new ManualClock(T0);
        var sent = new List<SendMessageRequest>();
        await using var rig = new Rig(new FakeBotClient(req =>
        {
            var request = (SendMessageRequest)req;
            sent.Add(request);
            clock.Now = T0.AddSeconds(5); // every Bot API call happens after requestedAt
            return Accepted(request, 100 + sent.Count, chatId, chatType, title);
        }), clock: clock);

        var json = await rig.SendMessage().SendAsync(
            chatId.ToString(), "**first** " + new string('a', 4000) + "\nsecond " + new string('b', 100),
            reply_to_message_id: 77);

        Assert.True(Ok(json));
        Assert.True(sent.Count > 1, "the text must need more than one chunk");

        var (routingKey, receipt) = await rig.Transport.NextAsync();

        Assert.Equal("example-agent", routingKey);
        Assert.Equal("example-agent", receipt.Agent);
        Assert.Equal("send_message", receipt.Tool);
        Assert.Equal(T0, receipt.RequestedAt);
        Assert.Equal(1, receipt.BotId);
        Assert.Equal(new ToolSendChat(chatId, wireType, title), receipt.Chat);

        Assert.Equal(sent.Select((_, i) => (long)(101 + i)), receipt.Messages.Select(m => m.MessageId));
        Assert.Equal(sent.Select((_, i) => T0.UtcDateTime.AddSeconds(101 + i)), receipt.Messages.Select(m => m.Date.ToUniversalTime()));
        Assert.Equal(sent.Select(r => r.Text), receipt.Messages.Select(m => m.Text));
        Assert.All(receipt.Messages, m => Assert.Equal(ToolSendTextFormats.Html, m.TextFormat));

        // The reply target belongs to the first chunk only, as the Bot API calls do.
        Assert.Equal(77, receipt.Messages[0].ReplyToMessageId);
        Assert.All(receipt.Messages.Skip(1), m => Assert.Null(m.ReplyToMessageId));

        await rig.AssertNothingElsePublishedAsync();
    }

    [Fact]
    public async Task Send_to_ceo_reports_one_receipt_under_its_own_tool_name()
    {
        await using var rig = new Rig(new FakeBotClient(req =>
            Accepted((SendMessageRequest)req, 55, DmChatId, ChatType.Private)));

        Assert.True(Ok(await rig.SendToCeo(DmChatId).SendAsync("status: **green**")));

        var (_, receipt) = await rig.Transport.NextAsync();
        Assert.Equal("send_to_ceo", receipt.Tool);
        Assert.Equal(DmChatId, receipt.Chat.Id);
        Assert.Equal(55, Assert.Single(receipt.Messages).MessageId);

        await rig.AssertNothingElsePublishedAsync();
    }

    /// <summary><c>sendRichMessage</c> is recorded as <c>rich</c> with the Markdown it was rendered from.</summary>
    [Fact]
    public async Task A_rich_send_records_the_markdown_source()
    {
        const string markdown = "**Status**\n\n- one\n- two";
        await using var rig = new Rig(
            new FakeBotClient(req => Accepted(req, 9, GroupChatId, ChatType.Supergroup)), formattingMode: "2");

        Assert.True(Ok(await rig.SendMessage().SendAsync(GroupChatId.ToString(), markdown)));

        var (_, receipt) = await rig.Transport.NextAsync();
        var message = Assert.Single(receipt.Messages);
        Assert.Equal(markdown, message.Text);
        Assert.Equal(ToolSendTextFormats.Rich, message.TextFormat);
    }

    /// <summary>A refused send is not a message: only the fallback that was accepted is recorded.</summary>
    [Fact]
    public async Task A_refused_rich_send_is_not_recorded_and_its_html_fallback_is()
    {
        SendMessageRequest? html = null;
        await using var rig = new Rig(new FakeBotClient(req =>
        {
            if (req is SendRichMessageRequest) throw new ApiRequestException("Bad Request: rich messages are not available");
            html = (SendMessageRequest)req;
            return Accepted(html, 10, GroupChatId, ChatType.Supergroup);
        }), formattingMode: "2");

        Assert.True(Ok(await rig.SendMessage().SendAsync(GroupChatId.ToString(), "**bold** move")));

        var (_, receipt) = await rig.Transport.NextAsync();
        var message = Assert.Single(receipt.Messages);
        Assert.Equal(html!.Text, message.Text);
        Assert.Equal(ToolSendTextFormats.Html, message.TextFormat);
    }

    [Fact]
    public async Task A_format_fallback_records_the_plain_resend_as_plain()
    {
        SendMessageRequest? plain = null;
        await using var rig = new Rig(new FakeBotClient(req =>
        {
            var request = (SendMessageRequest)req;
            if (request.ParseMode == ParseMode.Html)
                throw new ApiRequestException("Bad Request: can't parse entities in the message");
            plain = request;
            return Accepted(request, 12, GroupChatId, ChatType.Supergroup);
        }));

        Assert.True(Ok(await rig.SendMessage().SendAsync(GroupChatId.ToString(), "**verdict**: success")));

        var (_, receipt) = await rig.Transport.NextAsync();
        var message = Assert.Single(receipt.Messages);
        Assert.Equal(plain!.Text, message.Text);
        Assert.Equal(ToolSendTextFormats.Plain, message.TextFormat);
    }

    /// <summary>The <c>PLAIN</c> opt-out is sent HTML-escaped with HTML parse mode, so it is <c>html</c>.</summary>
    [Fact]
    public async Task The_plain_opt_out_is_recorded_as_the_escaped_html_it_was_sent_as()
    {
        await using var rig = new Rig(new FakeBotClient(req =>
            Accepted((SendMessageRequest)req, 13, GroupChatId, ChatType.Supergroup)));

        Assert.True(Ok(await rig.SendMessage().SendAsync(GroupChatId.ToString(), "a < b", parse_mode: "PLAIN")));

        var (_, receipt) = await rig.Transport.NextAsync();
        var message = Assert.Single(receipt.Messages);
        Assert.Equal("a &lt; b", message.Text);
        Assert.Equal(ToolSendTextFormats.Html, message.TextFormat);
    }

    /// <summary>What was delivered is reported even when a later chunk failed the call.</summary>
    [Fact]
    public async Task A_partly_delivered_call_reports_what_was_accepted()
    {
        var calls = 0;
        await using var rig = new Rig(new FakeBotClient(req =>
        {
            if (++calls == 2) throw new ApiRequestException("Bad Request: something else went wrong");
            return Accepted((SendMessageRequest)req, 20 + calls, GroupChatId, ChatType.Supergroup);
        }));

        var json = await rig.SendMessage().SendAsync(
            GroupChatId.ToString(), new string('a', 4000) + "\n" + new string('b', 4000));

        Assert.False(Ok(json));
        var (_, receipt) = await rig.Transport.NextAsync();
        Assert.Equal(21, Assert.Single(receipt.Messages).MessageId);
    }

    // ── what is never reported (AC6, MUST NOT 3) ─────────────────────────────

    /// <summary>The notifier bot is not in the agent's conversation: its send publishes nothing.</summary>
    [Fact]
    public async Task A_fallback_bot_send_publishes_no_receipt()
    {
        var notifier = new FakeBotClient(req => Accepted((SendMessageRequest)req, 55, GroupChatId, ChatType.Supergroup));
        await using var rig = new Rig(
            new FakeBotClient(_ => throw new ApiRequestException("Forbidden: bot was kicked from the group chat", 403)),
            notifier, start: false);

        var json = await rig.SendMessage().SendAsync(GroupChatId.ToString(), "plain message");

        Assert.True(Ok(json));
        Assert.True(Json(json).GetProperty("fallback").GetBoolean());
        Assert.Equal(0, rig.Publisher.Pending);
        Assert.Equal(0, rig.Publisher.Counters.Dropped);
    }

    [Fact]
    public async Task An_agent_without_its_own_bot_sends_through_the_notifier_and_publishes_no_receipt()
    {
        var notifier = new FakeBotClient(req => Accepted((SendMessageRequest)req, 56, DmChatId, ChatType.Private));
        await using var rig = new Rig(new FakeBotClient(), notifier, agentHasOwnBot: false, start: false);

        Assert.True(Ok(await rig.SendMessage().SendAsync(DmChatId.ToString(), "hello")));
        Assert.True(Ok(await rig.SendToCeo(DmChatId).SendAsync("hello")));

        Assert.Equal(0, rig.Publisher.Pending);
    }

    [Fact]
    public async Task A_call_with_nothing_accepted_publishes_no_receipt()
    {
        await using var rig = new Rig(
            new FakeBotClient(_ => throw new ApiRequestException("Bad Request: chat not found")), start: false);

        Assert.False(Ok(await rig.SendMessage().SendAsync(GroupChatId.ToString(), "hello")));
        Assert.Equal(0, rig.Publisher.Pending);
    }

    // ── the broker never reaches a tool result (AC6, MUST NOT 2) ─────────────

    [Fact]
    public async Task With_the_broker_down_send_message_still_returns_ok_and_the_drop_is_counted()
    {
        var down = new RecordingTransport { OnPublish = _ => throw new IOException("broker unreachable") };
        await using var rig = new Rig(
            new FakeBotClient(req => Accepted((SendMessageRequest)req, 1, GroupChatId, ChatType.Supergroup)),
            transport: down);

        var json = await rig.SendMessage().SendAsync(GroupChatId.ToString(), "hello");

        Assert.True(Ok(json));
        Assert.Equal(1, Json(json).GetProperty("message_id").GetInt32());
        await Eventually(() => rig.Publisher.Counters.Dropped == 1);
        Assert.Equal(0, rig.Publisher.Counters.Published);
    }

    [Fact]
    public async Task A_stuck_broker_never_holds_a_tool_result()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stuck = new RecordingTransport { OnPublish = _ => release.Task };
        await using var rig = new Rig(
            new FakeBotClient(req => Accepted((SendMessageRequest)req, 1, GroupChatId, ChatType.Supergroup)),
            transport: stuck);

        var calls = Task.WhenAll(
            rig.SendMessage().SendAsync(GroupChatId.ToString(), "one"),
            rig.SendMessage().SendAsync(GroupChatId.ToString(), "two"));

        Assert.Same(calls, await Task.WhenAny(calls, Task.Delay(TimeSpan.FromSeconds(10))));
        Assert.All(await calls, json => Assert.True(Ok(json)));

        release.SetResult();
        await rig.Transport.NextAsync();
        await rig.Transport.NextAsync();
        await Eventually(() => rig.Publisher.Counters.Published == 2);
    }

    [Fact]
    public void A_full_queue_drops_and_counts_without_waiting()
    {
        var publisher = new ToolSendReceiptPublisher(new RecordingTransport(), NullLogger<ToolSendReceiptPublisher>.Instance);

        for (var i = 0; i < ToolSendReceiptPublisher.Capacity + 3; i++)
            publisher.Enqueue(Receipt("hello"));

        Assert.Equal(ToolSendReceiptPublisher.Capacity, publisher.Pending);
        Assert.Equal(3, publisher.Counters.Dropped);
    }

    [Fact]
    public void Drops_warn_at_most_once_a_minute_and_never_log_text_or_chat_ids()
    {
        var clock = new ManualClock(T0);
        var logger = new CapturingLogger<ToolSendReceiptPublisher>();
        var publisher = new ToolSendReceiptPublisher(new RecordingTransport(), logger, clock);

        for (var i = 0; i < ToolSendReceiptPublisher.Capacity + 5; i++)
            publisher.Enqueue(Receipt("private words"));
        Assert.Single(logger.Warnings);

        clock.Now = T0 + ToolSendReceiptPublisher.WarningInterval + TimeSpan.FromSeconds(1);
        publisher.Enqueue(Receipt("private words"));

        Assert.Equal(2, logger.Warnings.Count);
        Assert.Contains("journal_receipts_dropped=6", logger.Warnings[1]);
        Assert.All(logger.Warnings, line =>
        {
            Assert.DoesNotContain("private words", line);
            Assert.DoesNotContain(GroupChatId.ToString(), line);
        });
    }

    // ── off by default (MUST NOT 11) ─────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("not-a-boolean")]
    public async Task Receipts_off_registers_no_transport_and_no_loop_and_publishes_nothing(string? value)
    {
        var services = Services(value);

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IToolSendReceiptTransport));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IHostedService));

        await using var provider = services.BuildServiceProvider();
        var publisher = provider.GetRequiredService<ToolSendReceiptPublisher>();
        Assert.False(publisher.Enabled);

        var factory = AgentFactory(new FakeBotClient(req => Accepted((SendMessageRequest)req, 1, DmChatId, ChatType.Private)));
        var tool = new SendMessageTool(factory, Accessor(), provider.GetRequiredService<TelegramSender>(),
            NullLogger<SendMessageTool>.Instance);

        Assert.True(Ok(await tool.SendAsync(DmChatId.ToString(), "hello")));
        Assert.Equal(0, publisher.Pending);
        Assert.Equal(0, publisher.Counters.Dropped);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    public async Task Receipts_on_registers_the_transport_and_the_loop(string value)
    {
        var services = Services(value);

        Assert.Contains(services, d => d.ServiceType == typeof(IToolSendReceiptTransport));
        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService));

        // Resolving opens nothing: the connection waits for the first receipt.
        await using var provider = services.BuildServiceProvider();
        Assert.True(provider.GetRequiredService<ToolSendReceiptPublisher>().Enabled);
        Assert.Same(provider.GetRequiredService<ToolSendReceiptPublisher>(), provider.GetServices<IHostedService>().Single());
    }

    // ── one sender (the source scan) ─────────────────────────────────────────

    private static readonly Regex BotApiSend = new(
        @"\b(SendMessage|SendRichMessage|SendPhoto|SendDocument|SendMediaGroup|CopyMessages?|ForwardMessages?)\s*\("
        + @"|\bnew\s+(SendMessage|SendRichMessage|SendPhoto|SendDocument|SendMediaGroup|CopyMessages?|ForwardMessages?)Request\b",
        RegexOptions.Compiled);

    /// <summary>
    /// A Bot API send anywhere in <c>src/Fleet.Telegram</c> outside <c>TelegramSender</c> fails, so
    /// any future send tool, photos included, produces receipts.
    /// </summary>
    [Fact]
    public void Every_bot_api_send_in_fleet_telegram_goes_through_TelegramSender()
    {
        var root = Path.Combine(RepoRoot(), "src", "Fleet.Telegram");
        var sources = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .ToList();

        var wrapper = Assert.Single(sources, f => Path.GetFileName(f) == "TelegramSender.cs");

        // The scan is not vacuous: it does see the calls the wrapper makes.
        Assert.Contains(File.ReadLines(wrapper), line => BotApiSend.IsMatch(line));

        var offenders = sources
            .Where(f => f != wrapper)
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (File: f, Line: i + 1, Text: line)))
            .Where(l => BotApiSend.IsMatch(l.Text))
            .Select(l => $"{Path.GetRelativePath(root, l.File)}:{l.Line}: {l.Text.Trim()}")
            .ToList();

        Assert.True(offenders.Count == 0,
            "Bot API sends must go through TelegramSender so the tool call's receipt records them:\n"
            + string.Join("\n", offenders));
    }

    // ── support ──────────────────────────────────────────────────────────────

    private static Message Accepted(object request, int id, long chatId, ChatType type, string? title = "Example group")
    {
        var replyTo = request switch
        {
            SendMessageRequest { ReplyParameters: { } r } => r.MessageId,
            SendRichMessageRequest { ReplyParameters: { } r } => r.MessageId,
            _ => (int?)null,
        };

        return new Message
        {
            Id = id,
            Date = T0.UtcDateTime.AddSeconds(id),
            Chat = new Chat { Id = chatId, Type = type, Title = type == ChatType.Private ? null : title },
            ReplyToMessage = replyTo is { } target ? new Message { Id = target } : null,
        };
    }

    private static ToolSendReceipt Receipt(string text) => new()
    {
        Agent = "example-agent",
        Tool = "send_message",
        RequestedAt = T0,
        BotId = 1,
        Chat = new ToolSendChat(GroupChatId, "supergroup", "Example group"),
        Messages = [new ToolSendMessage(1, T0.UtcDateTime, null, text, ToolSendTextFormats.Plain)],
    };

    private static bool Ok(string json) => Json(json).GetProperty("ok").GetBoolean();

    private static System.Text.Json.JsonElement Json(string json) => System.Text.Json.JsonDocument.Parse(json).RootElement;

    private static IServiceCollection Services(string? enabled)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ToolSendReceiptRegistration.EnabledKey] = enabled,
                [ToolSendReceiptRegistration.BrokerUrlKey] = "amqp://broker.invalid:5672/",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddToolSendReceipts(configuration);
        return services;
    }

    private static BotClientFactory AgentFactory(FakeBotClient agentBot, FakeBotClient? notifier = null, bool agentHasOwnBot = true)
    {
        var factory = new BotClientFactory(NullLogger<BotClientFactory>.Instance, _ => agentBot);
        if (agentHasOwnBot)
            factory.ApplyAgentTokens(new Dictionary<string, string> { [AgentName] = "tok:agent" });

        if (notifier is not null)
        {
            typeof(BotClientFactory)
                .GetField("_notifierClient", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(factory, notifier);
        }

        return factory;
    }

    private static IHttpContextAccessor Accessor(string? formattingMode = null)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString(
            $"?agent={AgentName}" + (formattingMode is null ? "" : $"&formatting_mode={formattingMode}"));
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(httpContext);
        return accessor;
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not met within 10 s");
            await Task.Delay(10);
        }
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Fleet.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate the repository root (no Fleet.sln above '{AppContext.BaseDirectory}').");
    }

    /// <summary>The tools, the sender and a running publisher over a recording transport.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly BotClientFactory _factory;
        private readonly IHttpContextAccessor _accessor;
        private readonly TelegramSender _sender;
        private readonly bool _started;

        public Rig(
            FakeBotClient agentBot, FakeBotClient? notifier = null, bool agentHasOwnBot = true,
            string? formattingMode = null, RecordingTransport? transport = null, bool start = true,
            ManualClock? clock = null)
        {
            clock ??= new ManualClock(T0);
            Transport = transport ?? new RecordingTransport();
            Publisher = new ToolSendReceiptPublisher(Transport, NullLogger<ToolSendReceiptPublisher>.Instance, clock);
            _sender = new TelegramSender(Publisher, clock);
            _factory = AgentFactory(agentBot, notifier, agentHasOwnBot);
            _accessor = Accessor(formattingMode);

            if (start)
            {
                Publisher.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
                _started = true;
            }
        }

        public RecordingTransport Transport { get; }

        public ToolSendReceiptPublisher Publisher { get; }

        public SendMessageTool SendMessage() =>
            new(_factory, _accessor, _sender, NullLogger<SendMessageTool>.Instance);

        public SendToCeoTool SendToCeo(long ceoChatId)
        {
            var ceo = new CeoConfigService();
            ceo.Apply(ceoChatId.ToString());
            return new SendToCeoTool(_factory, _accessor, ceo, _sender, NullLogger<SendToCeoTool>.Instance);
        }

        public async Task AssertNothingElsePublishedAsync()
        {
            await Eventually(() => Publisher.Counters.Published == 1);
            Assert.Equal(0, Publisher.Pending);
            Assert.False(Transport.HasMore);
        }

        public async ValueTask DisposeAsync()
        {
            if (_started) await Publisher.StopAsync(CancellationToken.None);
            Publisher.Dispose();
        }
    }

    private sealed class RecordingTransport : IToolSendReceiptTransport
    {
        private readonly Channel<(string RoutingKey, byte[] Body)> _published = Channel.CreateUnbounded<(string, byte[])>();

        public Func<CancellationToken, Task>? OnPublish { get; init; }

        public bool HasMore => _published.Reader.Count > 0;

        public async Task PublishAsync(string routingKey, ReadOnlyMemory<byte> body, CancellationToken ct)
        {
            if (OnPublish is not null) await OnPublish(ct);
            _published.Writer.TryWrite((routingKey, body.ToArray()));
        }

        /// <summary>The next published receipt, read back through the agent's own parser.</summary>
        public async Task<(string RoutingKey, ToolSendReceipt Receipt)> NextAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var (routingKey, body) = await _published.Reader.ReadAsync(timeout.Token);

            Assert.True(ToolSendReceipts.TryParse(body, out var receipt, out var reason), reason);
            return (routingKey, receipt!);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = [];

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    }
}
