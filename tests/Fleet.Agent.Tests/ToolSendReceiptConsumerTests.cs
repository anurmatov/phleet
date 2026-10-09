using System.Text;
using System.Text.Json.Nodes;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Services;
using Fleet.Journal.Client;
using Fleet.Shared.Journal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Fleet.Agent.Tests;

/// <summary>
/// The agent's tool-send receipt consumer (#394, #439): delivery is the admission rule. A valid
/// own-bot receipt is captured on arrival whatever turn made the call, or none; the classifier still
/// decides where; the ack follows the spool write. No broker — deliveries go through the consumer's
/// own handler and acks through its test hook.
/// </summary>
public sealed class ToolSendReceiptConsumerTests : IDisposable
{
    private readonly ReceiptRig _rig = new();

    public void Dispose() => _rig.Dispose();

    // ── captured ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_valid_receipt_with_no_turn_anywhere_is_captured_as_agent_tool()
    {
        var tag = await _rig.DeliverAsync(ReceiptRig.Receipt(
            new ToolSendMessage(501, ReceiptRig.Date, ReplyToMessageId: 77, "<b>first</b>", ToolSendTextFormats.Html),
            new ToolSendMessage(502, ReceiptRig.Date, ReplyToMessageId: null, "second", ToolSendTextFormats.Plain)));

        Assert.Equal([tag], _rig.Acked);
        Assert.Equal(1, _rig.Counters.Get("tool_send_captured"));
        Assert.Equal("result=captured, reason=-, parts=2, kept=2, spooled=2", _rig.Logs.Handled());

        var records = _rig.Records();
        Assert.Equal(2, records.Count);
        var group = records[0]["sendGroup"]!["id"]!.GetValue<string>();
        foreach (var (record, part) in records.Select((r, i) => (r, i + 1)))
        {
            Assert.Equal("agent_tool", record["origin"]!.GetValue<string>());
            Assert.Equal("outbound", record["direction"]!.GetValue<string>());
            Assert.Equal("agent", record["sender"]!["kind"]!.GetValue<string>());
            Assert.Equal(ReceiptRig.BotId.ToString(), record["sender"]!["id"]!.GetValue<string>());
            Assert.Equal(ReceiptRig.BotId, record["telegram"]!["botId"]!.GetValue<long>());
            Assert.Equal(ReceiptRig.HumanDm, record["telegram"]!["chatId"]!.GetValue<long>());
            Assert.Equal("private", record["telegram"]!["chatKind"]!.GetValue<string>());
            Assert.Equal(ReceiptRig.Date, DateTimeOffset.Parse(record["sentAt"]!.GetValue<string>()).UtcDateTime);
            Assert.Equal(group, record["sendGroup"]!["id"]!.GetValue<string>());
            Assert.Equal(part, record["sendGroup"]!["part"]!.GetValue<int>());
            Assert.Equal(2, record["sendGroup"]!["parts"]!.GetValue<int>());
        }

        var first = records.Single(r => r["telegram"]!["messageId"]!.GetValue<long>() == 501);
        Assert.Equal("<b>first</b>", first["text"]!.GetValue<string>());
        Assert.Equal("html", first["textFormat"]!.GetValue<string>());
        Assert.Equal(77, first["telegram"]!["replyToMessageId"]!.GetValue<long>());

        var second = records.Single(r => r["telegram"]!["messageId"]!.GetValue<long>() == 502);
        Assert.Equal("plain", second["textFormat"]!.GetValue<string>());
        Assert.Null(second["telegram"]!["replyToMessageId"]);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(65, false)]
    public async Task A_send_group_is_shared_only_by_two_to_sixty_four_messages(int parts, bool grouped)
    {
        await _rig.DeliverAsync(ReceiptRig.Receipt(Enumerable.Range(1, parts)
            .Select(i => new ToolSendMessage(1_000 + i, ReceiptRig.Date, null, "m", ToolSendTextFormats.Plain)).ToArray()));

        var records = _rig.Records();
        Assert.Equal(parts, records.Count);
        if (grouped)
            Assert.Single(records.Select(r => r["sendGroup"]!["id"]!.GetValue<string>()).Distinct());
        else
            Assert.All(records, r => Assert.Null(r["sendGroup"]));
        Assert.Equal($"result=captured, reason=-, parts={parts}, kept={parts}, spooled={parts}", _rig.Logs.Handled());
    }

    [Fact]
    public async Task A_rich_send_keeps_its_markdown_source_and_format()
    {
        await _rig.DeliverAsync(ReceiptRig.Receipt(
            new ToolSendMessage(601, ReceiptRig.Date, null, "**bold**", ToolSendTextFormats.Rich)));

        var record = Assert.Single(_rig.Records());
        Assert.Equal("rich", record["textFormat"]!.GetValue<string>());
        Assert.Equal("**bold**", record["text"]!.GetValue<string>());
        Assert.Null(record["sendGroup"]);
    }

    [Theory]
    [InlineData(3600)]
    [InlineData(-3600)]
    public async Task Admission_does_not_depend_on_the_clock(int offsetSeconds)
    {
        // Stamped an hour in the future, or an hour ago (a receipt that waited across a restart).
        await _rig.DeliverAsync(ReceiptRig.Receipt() with { RequestedAt = _rig.Now.AddSeconds(offsetSeconds) });

        Assert.Single(_rig.Records());
        Assert.Equal(1, _rig.Counters.Get("tool_send_captured"));
        Assert.Single(_rig.Acked);
    }

    [Fact]
    public async Task A_partial_delivery_journals_only_the_accepted_message()
    {
        // fleet-telegram reports accepted chunks only: the first of three, then the Bot API failed.
        await _rig.DeliverAsync(ReceiptRig.Receipt(
            new ToolSendMessage(701, ReceiptRig.Date, null, "chunk 1 of 3", ToolSendTextFormats.Plain)));

        var record = Assert.Single(_rig.Records());
        Assert.Equal(701, record["telegram"]!["messageId"]!.GetValue<long>());
        Assert.Null(record["sendGroup"]);
    }

    [Fact]
    public async Task The_same_receipt_twice_writes_the_same_natural_key_again_for_comms_to_deduplicate()
    {
        var receipt = ReceiptRig.Receipt();
        await _rig.DeliverAsync(receipt);
        await _rig.DeliverAsync(receipt);

        // Idempotency is Comms' natural key (conversation, tg:<messageId>) with an unchanged
        // fingerprint, so both records must agree on every fingerprinted field.
        var records = _rig.Records();
        Assert.Equal(2, records.Count);
        foreach (var field in new[] { "telegram", "direction", "sender", "sentAt", "text", "origin" })
            Assert.Equal(records[0][field]!.ToJsonString(), records[1][field]!.ToJsonString());
        Assert.Equal(2, _rig.Acked.Count);
    }

    // ── the ack follows the spool write ──────────────────────────────────────

    [Fact]
    public async Task The_ack_runs_only_after_the_record_file_exists_in_pending()
    {
        var pendingAtAck = -1;
        _rig.Consumer.AckHook = _ =>
        {
            pendingAtAck = Directory.GetFiles(_rig.Spool.PendingDir).Length;
            return Task.CompletedTask;
        };

        await _rig.DeliverAsync(ReceiptRig.Receipt());

        Assert.Equal(1, pendingAtAck);
    }

    [Fact]
    public async Task A_full_spool_drops_the_record_counts_it_and_still_acks()
    {
        using var rig = new ReceiptRig(maxSpoolRecords: 0);

        var tag = await rig.DeliverAsync(ReceiptRig.Receipt());

        Assert.Equal([tag], rig.Acked);
        Assert.Equal(0, rig.Rows);
        Assert.Equal(1, rig.Counters.Get("journal_spool_dropped{reason=full}"));
        Assert.Equal("result=captured, reason=-, parts=1, kept=1, spooled=0", rig.Logs.Handled());
    }

    // ── dropped and counted ──────────────────────────────────────────────────

    public static TheoryData<string, byte[]> InvalidBodies() => new()
    {
        { "malformed", "not json"u8.ToArray() },
        { "malformed", """{"agent":"a","tool":"send_message"}"""u8.ToArray() },
        { "malformed", Encoding.UTF8.GetBytes(ReceiptRig.Json(ReceiptRig.Receipt()).Replace("\"messages\":[", "\"ignored\":[")) },
        { "version", Encoding.UTF8.GetBytes(ReceiptRig.Json(ReceiptRig.Receipt()).Replace("\"v\":1", "\"v\":2")) },
        { "too_large", Encoding.UTF8.GetBytes(ReceiptRig.Json(ReceiptRig.Receipt(
            new ToolSendMessage(1, ReceiptRig.Date, null, new string('x', ToolSendReceipts.MaxBytes), ToolSendTextFormats.Plain)))) },
        { "agent", ToolSendReceipts.Serialize(ReceiptRig.Receipt() with { Agent = "fleet-agent2" }) },
        { "agent", ToolSendReceipts.Serialize(ReceiptRig.Receipt() with { Agent = "Fleet-Agent1" }) },
        { "message_id", ToolSendReceipts.Serialize(ReceiptRig.Receipt(
            new ToolSendMessage(801, ReceiptRig.Date, null, "ok", ToolSendTextFormats.Plain),
            new ToolSendMessage(0, ReceiptRig.Date, null, "zero", ToolSendTextFormats.Plain))) },
        { "message_id", ToolSendReceipts.Serialize(ReceiptRig.Receipt(
            new ToolSendMessage(-5, ReceiptRig.Date, null, "negative", ToolSendTextFormats.Plain))) },
    };

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task An_invalid_receipt_is_acked_counted_with_its_reason_and_writes_nothing(string reason, byte[] body)
    {
        var tag = await _rig.DeliverRawAsync(body);

        Assert.Equal([tag], _rig.Acked);
        Assert.Equal(1, _rig.Counters.Get("receipt_invalid"));
        Assert.Equal(0, _rig.Counters.Get("tool_send_captured"));
        Assert.Equal(0, _rig.Rows);
        Assert.StartsWith($"result=invalid, reason={reason}, parts=", _rig.Logs.Handled());
        Assert.EndsWith(", kept=0, spooled=0", _rig.Logs.Handled());
    }

    [Fact]
    public async Task A_receipt_from_another_bot_is_dropped()
    {
        var tag = await _rig.DeliverAsync(ReceiptRig.Receipt() with { BotId = ReceiptRig.BotId + 1 });

        Assert.Equal([tag], _rig.Acked);
        Assert.Equal(1, _rig.Counters.Get("tool_send_foreign_bot"));
        Assert.Equal(0, _rig.Rows);
        Assert.Equal("result=foreign_bot, reason=-, parts=1, kept=0, spooled=0", _rig.Logs.Handled());
    }

    [Fact]
    public async Task Without_a_known_own_bot_every_receipt_is_foreign()
    {
        using var rig = new ReceiptRig(ownBotId: null);

        var tag = await rig.DeliverAsync(ReceiptRig.Receipt());

        Assert.Equal([tag], rig.Acked);
        Assert.Equal(1, rig.Counters.Get("tool_send_foreign_bot"));
        Assert.Equal(0, rig.Rows);
    }

    // ── the classifier still decides where ───────────────────────────────────

    [Theory]
    [InlineData(ReceiptRig.Operational, "supergroup", "operational_chat")]
    [InlineData(ReceiptRig.Stranger, "private", "unauthorized_chat")]
    [InlineData(0L, "private", "no_chat")]
    [InlineData(ReceiptRig.Group, "channel", "unsupported_chat")]
    public async Task A_send_to_a_chat_the_policy_excludes_writes_nothing(long chatId, string chatType, string reason)
    {
        var tag = await _rig.DeliverAsync(ReceiptRig.Receipt() with { Chat = new ToolSendChat(chatId, chatType, null) });

        Assert.Equal(0, _rig.Rows);
        Assert.Equal(1, _rig.Counters.Get($"journal_excluded{{reason={reason}}}"));
        Assert.Equal([tag], _rig.Acked);
        Assert.Equal("result=captured, reason=-, parts=1, kept=0, spooled=0", _rig.Logs.Handled());
    }

    // ── the log line ─────────────────────────────────────────────────────────

    [Fact]
    public async Task The_log_line_renders_only_fixed_codes_and_counts()
    {
        await _rig.DeliverAsync(ReceiptRig.Receipt(
            new ToolSendMessage(4_242_001, ReceiptRig.Date, 4_242_002, "nonce-text-secret", ToolSendTextFormats.Plain)) with
        {
            Chat = new ToolSendChat(ReceiptRig.HumanDm, "private", "chat-title-secret"),
        });
        await _rig.DeliverAsync(ReceiptRig.Receipt() with { BotId = ReceiptRig.BotId + 1 });
        await _rig.DeliverRawAsync("not json"u8.ToArray());

        var entries = _rig.Logs.Entries.Where(e => e.Message.StartsWith("tool-send receipt handled", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, entries.Count);
        foreach (var entry in entries)
        {
            Assert.Equal(LogLevel.Information, entry.Level);
            Assert.Equal(["Kept", "Parts", "Reason", "Result", "Spooled", "{OriginalFormat}"], entry.Keys.Order(StringComparer.Ordinal));
            Assert.Matches(
                "^tool-send receipt handled \\(result=(captured|invalid|foreign_bot), reason=[a-z_-]+, parts=\\d+, kept=\\d+, spooled=\\d+\\)$",
                entry.Message);
        }

        var all = string.Join("\n", _rig.Logs.Entries.Select(e => e.Message));
        foreach (var secret in new[] { "nonce-text-secret", "chat-title-secret", "4242001", "4242002",
                     ReceiptRig.HumanDm.ToString(), ReceiptRig.BotId.ToString(), "fleet-agent1" })
            Assert.DoesNotContain(secret, all, StringComparison.Ordinal);
    }

    // ── registration ─────────────────────────────────────────────────────────

    [Fact]
    public void With_the_journal_on_the_consumer_is_registered()
    {
        using var host = BuildHost(ReceiptRig.Token);

        var consumer = Assert.Single(host.Services.GetServices<IHostedService>().OfType<ToolSendReceiptConsumer>());
        Assert.Same(consumer, host.Services.GetRequiredService<ToolSendReceiptConsumer>());
        Assert.DoesNotContain(host.Services.GetServices<IHostedService>(), s => s is ToolSendReceiptQueueCleanup);
    }

    [Fact]
    public void With_the_journal_off_there_is_no_consumer_only_the_queue_cleanup()
    {
        using var host = BuildHost(token: null);

        Assert.Null(host.Services.GetService<ToolSendReceiptConsumer>());
        Assert.DoesNotContain(host.Services.GetServices<IHostedService>(), s => s is ToolSendReceiptConsumer);
        Assert.Single(host.Services.GetServices<IHostedService>().OfType<ToolSendReceiptQueueCleanup>());

        // Journal off: the registration method itself still adds nothing.
        var services = new ServiceCollection();
        AgentHostRegistration.AddJournal(services, new ConfigurationBuilder().Build());
        Assert.Empty(services);
    }

    [Fact]
    public async Task With_the_journal_off_and_no_broker_the_cleanup_does_nothing_and_ends()
    {
        var relay = new GroupRelayService(
            Options.Create(new AgentOptions { Name = "fleet-agent1", Role = "r", WorkDir = "/tmp" }),
            Options.Create(new RabbitMqOptions()), NullLogger<GroupRelayService>.Instance);
        var cleanup = new ToolSendReceiptQueueCleanup(relay,
            Options.Create(new AgentOptions { Name = "fleet-agent1", Role = "r", WorkDir = "/tmp" }),
            NullLogger<ToolSendReceiptQueueCleanup>.Instance);

        await cleanup.StartAsync(CancellationToken.None);
        await cleanup.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(cleanup.ExecuteTask.IsCompletedSuccessfully);
    }

    private IHost BuildHost(string? token)
    {
        Directory.CreateDirectory(_rig.Root);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agent:Name"] = "fleet-agent1",
            ["Agent:Role"] = "generic-role",
            ["Agent:WorkDir"] = _rig.Root,
            ["Agent:Provider"] = "claude",
            ["Telegram:BotToken"] = "123456:AAHfakefakefakefakefakefakefakefakeff",
            ["Journal:IngestToken"] = token,
        });
        builder.Services.AddLogging();
        builder.Services.AddAgentCoreServices(builder.Configuration);
        builder.Services.AddAgentDaemonServices(builder.Configuration);
        return builder.Build();
    }
}

/// <summary>
/// A real capture over a temp spool and a consumer, with a recording logger. Ids are made up.
/// </summary>
internal sealed class ReceiptRig : IDisposable
{
    public const long BotId = 7001;
    public const long HumanDm = 4242;
    public const long Stranger = 4343;
    public const long Group = -1002000000001;
    public const long Operational = -1002000000009;
    public const string Token = "cj1.ingest.agent1.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    public static readonly DateTime Date = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private ulong _nextTag;

    public ReceiptRig(long? ownBotId = BotId, int? maxSpoolRecords = null)
    {
        Directory.CreateDirectory(Root);
        Spool = maxSpoolRecords is { } max
            ? new JournalSpool(Path.Combine(Root, "spool")) { MaxRecords = max }
            : new JournalSpool(Path.Combine(Root, "spool"));

        var telegram = Options.Create(new TelegramOptions { AllowedUserIds = [HumanDm], AllowedGroupIds = [Group, Operational] });
        var capture = new JournalCapture(Spool, Counters, new AllowlistHolder(telegram),
            Options.Create(new JournalOptions { IngestToken = Token, ExcludedChatIds = Operational.ToString() }),
            NullLogger<JournalCapture>.Instance);

        Consumer = new ToolSendReceiptConsumer(
            Options.Create(new AgentOptions { Name = "fleet-agent1", Role = "generic-role", WorkDir = Root }),
            new NoBroker(), capture, Counters, () => ownBotId, Logs)
        {
            AckHook = tag =>
            {
                lock (Acked) Acked.Add(tag);
                return Task.CompletedTask;
            },
        };
    }

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "tool-send-" + Guid.NewGuid().ToString("N"));
    public JournalSpool Spool { get; }
    public JournalCounters Counters { get; } = new();
    public RecordingLogger<ToolSendReceiptConsumer> Logs { get; } = new();
    public ToolSendReceiptConsumer Consumer { get; }
    public List<ulong> Acked { get; } = [];
    public DateTimeOffset Now => DateTimeOffset.UtcNow;

    public int Rows => Spool.Pending().Count;

    public List<JsonObject> Records() =>
        Spool.Pending().Select(e => e.Record).OrderBy(r => r["telegram"]!["messageId"]!.GetValue<long>()).ToList();

    /// <summary>One receipt from this agent's own bot to the allowlisted DM; one message unless given.</summary>
    public static ToolSendReceipt Receipt(params ToolSendMessage[] messages) => new()
    {
        Agent = "fleet-agent1",
        Tool = "send_message",
        RequestedAt = Date,
        BotId = BotId,
        Chat = new ToolSendChat(HumanDm, "private", null),
        Messages = messages.Length > 0
            ? messages
            : [new ToolSendMessage(901, Date, null, "hello", ToolSendTextFormats.Plain)],
    };

    public static string Json(ToolSendReceipt receipt) => Encoding.UTF8.GetString(ToolSendReceipts.Serialize(receipt));

    public Task<ulong> DeliverAsync(ToolSendReceipt receipt) => DeliverRawAsync(ToolSendReceipts.Serialize(receipt));

    public async Task<ulong> DeliverRawAsync(byte[] body)
    {
        var tag = Interlocked.Increment(ref _nextTag);
        await Consumer.HandleAsync(body, tag);
        return tag;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }

    private sealed class NoBroker : IAgentBrokerConnection
    {
        public IConnection? Connection => null;
    }
}

/// <summary>Keeps every rendered message with its level and structured keys.</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message, IReadOnlyList<string> Keys)> Entries { get; } = [];

    /// <summary>The fields of the one <c>tool-send receipt handled</c> line, as rendered.</summary>
    public string Handled()
    {
        const string prefix = "tool-send receipt handled (";
        var line = Assert.Single(Entries, e => e.Message.StartsWith(prefix, StringComparison.Ordinal)).Message;
        return line[prefix.Length..^1];
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var keys = state is IReadOnlyList<KeyValuePair<string, object?>> pairs ? pairs.Select(p => p.Key).ToList() : [];
        lock (Entries) Entries.Add((logLevel, formatter(state, exception), keys));
    }
}
