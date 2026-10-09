using System.Collections.Concurrent;
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
/// The agent's tool-send receipt consumer (#394): what it drops at once, what it defers, when it
/// decides, what the capture writes, and that the ack follows the decision. No broker — deliveries
/// go through the consumer's own receive path and acks through its test hook.
/// </summary>
public sealed class ToolSendReceiptConsumerTests : IDisposable
{
    private readonly ReceiptRig _rig = new();

    public void Dispose() => _rig.Dispose();

    // ── a human send ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_human_send_is_decided_after_its_window_and_journaled_as_agent_tool()
    {
        _rig.At(90);
        var human = _rig.OpenTurn(OutboundOrigin.Human);

        _rig.At(100.1);
        var tag = await _rig.DeliverAsync(ReceiptRig.Receipt(100,
            new ToolSendMessage(501, ReceiptRig.Date(100), ReplyToMessageId: 77, "<b>first</b>", ToolSendTextFormats.Html),
            new ToolSendMessage(502, ReceiptRig.Date(100), ReplyToMessageId: null, "second", ToolSendTextFormats.Plain)));

        // Not before requestedAt + 2 s + 0.25 s, and never acked before its decision.
        _rig.At(102.2);
        Assert.Equal(0, await _rig.DecideAsync());
        Assert.Empty(_rig.Acked);
        Assert.Equal(0, _rig.Rows);

        _rig.At(102.25);
        Assert.Equal(1, await _rig.DecideAsync());
        Assert.Equal([tag], _rig.Acked);
        Assert.Equal(1, _rig.Counters.Get("tool_send_captured"));

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

        human.Close();
    }

    [Fact]
    public async Task A_rich_send_keeps_its_markdown_source_and_format()
    {
        _rig.At(90);
        _rig.OpenTurn(OutboundOrigin.Human);

        _rig.At(100.1);
        await _rig.DeliverAsync(ReceiptRig.Receipt(100,
            new ToolSendMessage(601, ReceiptRig.Date(100), null, "**bold**", ToolSendTextFormats.Rich)));
        _rig.At(102.25);
        await _rig.DecideAsync();

        var record = Assert.Single(_rig.Records());
        Assert.Equal("rich", record["textFormat"]!.GetValue<string>());
        Assert.Equal("**bold**", record["text"]!.GetValue<string>());
        Assert.Null(record["sendGroup"]);
    }

    // ── dropped on arrival ───────────────────────────────────────────────────

    public static TheoryData<string, byte[]> InvalidBodies() => new()
    {
        { "not json", "not json"u8.ToArray() },
        { "no version", """{"agent":"a","tool":"send_message"}"""u8.ToArray() },
        { "wrong version", Encoding.UTF8.GetBytes(ReceiptRig.Json(ReceiptRig.Receipt(100)).Replace("\"v\":1", "\"v\":2")) },
        { "no messages", Encoding.UTF8.GetBytes(ReceiptRig.Json(ReceiptRig.Receipt(100)).Replace("\"messages\":[", "\"ignored\":[")) },
        { "over 1 MiB", Encoding.UTF8.GetBytes(ReceiptRig.Json(ReceiptRig.Receipt(100,
            new ToolSendMessage(1, ReceiptRig.Date(100), null, new string('x', ToolSendReceipts.MaxBytes), ToolSendTextFormats.Plain)))) },
    };

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task An_invalid_receipt_is_acked_counted_and_never_deferred(string name, byte[] body)
    {
        _rig.At(90);
        _rig.OpenTurn(OutboundOrigin.Human);
        _rig.At(100.1);

        var tag = await _rig.DeliverRawAsync(body);

        Assert.Equal([tag], _rig.Acked);
        Assert.Equal(1, _rig.Counters.Get("receipt_invalid"));
        Assert.Equal(0, _rig.Consumer.DeferredCountForTests);
        _rig.At(110);
        await _rig.DecideAsync();
        Assert.True(_rig.Rows == 0, name);
    }

    [Fact]
    public async Task A_receipt_from_another_bot_is_dropped()
    {
        _rig.At(90);
        _rig.OpenTurn(OutboundOrigin.Human);
        _rig.At(100.1);

        var tag = await _rig.DeliverAsync(ReceiptRig.Receipt(100) with { BotId = ReceiptRig.BotId + 1 });

        Assert.Equal([tag], _rig.Acked);
        Assert.Equal(1, _rig.Counters.Get("tool_send_foreign_bot"));
        _rig.At(110);
        await _rig.DecideAsync();
        Assert.Equal(0, _rig.Rows);
    }

    [Fact]
    public async Task Without_a_known_own_bot_every_receipt_is_foreign()
    {
        using var rig = new ReceiptRig(ownBotId: null);
        rig.At(90);
        rig.OpenTurn(OutboundOrigin.Human);
        rig.At(100.1);

        await rig.DeliverAsync(ReceiptRig.Receipt(100));

        Assert.Equal(1, rig.Counters.Get("tool_send_foreign_bot"));
        rig.At(110);
        await rig.DecideAsync();
        Assert.Equal(0, rig.Rows);
    }

    [Fact]
    public async Task A_receipt_stamped_after_its_arrival_is_clock_skew()
    {
        _rig.At(90);
        _rig.OpenTurn(OutboundOrigin.Human);
        _rig.At(100.1);

        var tag = await _rig.DeliverAsync(ReceiptRig.Receipt(100.2));

        Assert.Equal([tag], _rig.Acked);
        Assert.Equal(1, _rig.Counters.Get("receipt_clock_skew"));
        _rig.At(110);
        await _rig.DecideAsync();
        Assert.Equal(0, _rig.Rows);
    }

    [Fact]
    public async Task The_deferred_list_is_bounded_and_an_overflow_is_acked_and_excluded()
    {
        _rig.At(90);
        _rig.OpenTurn(OutboundOrigin.Human);
        _rig.At(100.1);

        for (var i = 0; i < ToolSendReceiptConsumer.MaxDeferred; i++)
            await _rig.DeliverAsync(ReceiptRig.Receipt(100, new ToolSendMessage(1_000 + i, ReceiptRig.Date(100), null, "m", ToolSendTextFormats.Plain)));

        Assert.Empty(_rig.Acked);
        Assert.Equal(ToolSendReceiptConsumer.MaxDeferred, _rig.Consumer.DeferredCountForTests);

        var overflow = await _rig.DeliverAsync(ReceiptRig.Receipt(100));
        Assert.Equal([overflow], _rig.Acked);
        Assert.Equal(1, _rig.Counters.Get("receipt_deferred_overflow"));

        _rig.At(102.25);
        Assert.Equal(ToolSendReceiptConsumer.MaxDeferred, await _rig.DecideAsync());
        Assert.Equal(ToolSendReceiptConsumer.MaxDeferred + 1, _rig.Acked.Count);
        Assert.Equal(ToolSendReceiptConsumer.MaxDeferred, _rig.Rows);
    }

    // ── what the ledger decides ──────────────────────────────────────────────

    [Fact]
    public async Task A_send_during_a_relay_turn_is_captured_as_relay()
    {
        _rig.At(90);
        _rig.OpenTurn(OutboundOrigin.Relay);
        _rig.At(100.1);
        await _rig.DeliverAsync(ReceiptRig.Receipt(100));
        _rig.At(102.25);
        await _rig.DecideAsync();

        Assert.Equal(["relay/covered"], _rig.Decisions);
        Assert.Equal(1, _rig.Counters.Get("tool_send_captured"));
        Assert.Equal(1, _rig.Counters.Get("tool_send_captured_relay"));
        Assert.Single(_rig.Acked);
        Assert.Equal("agent_tool", Assert.Single(_rig.Records())["origin"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_send_during_a_bridge_turn_is_excluded_origin()
    {
        _rig.At(90);
        _rig.OpenTurn(OutboundOrigin.Bridge);
        _rig.At(100.1);
        await _rig.DeliverAsync(ReceiptRig.Receipt(100));
        _rig.At(102.25);
        await _rig.DecideAsync();

        Assert.Equal(["excluded_origin/bridge_or_unknown"], _rig.Decisions);
        Assert.Equal(1, _rig.Counters.Get("tool_send_excluded_origin"));
        Assert.Equal(0, _rig.Counters.Get("tool_send_captured"));
        Assert.Single(_rig.Acked);
        Assert.Equal(0, _rig.Rows);
    }

    [Fact]
    public async Task A_send_with_no_interval_is_unattributed()
    {
        _rig.At(100.1);
        await _rig.DeliverAsync(ReceiptRig.Receipt(100));
        _rig.At(102.25);
        await _rig.DecideAsync();

        Assert.Equal(1, _rig.Counters.Get("tool_send_unattributed"));
        Assert.Single(_rig.Acked);
        Assert.Equal(0, _rig.Rows);
    }

    // ── the S2 capture policy still applies ──────────────────────────────────

    [Theory]
    [InlineData(ReceiptRig.Operational, "supergroup", "operational_chat")]
    [InlineData(ReceiptRig.Stranger, "private", "unauthorized_chat")]
    public async Task A_human_send_to_a_chat_the_policy_excludes_writes_nothing(long chatId, string chatType, string reason)
    {
        _rig.At(90);
        _rig.OpenTurn(OutboundOrigin.Human);
        _rig.At(100.1);
        await _rig.DeliverAsync(ReceiptRig.Receipt(100) with { Chat = new ToolSendChat(chatId, chatType, null) });
        _rig.At(102.25);
        await _rig.DecideAsync();

        Assert.Equal(0, _rig.Rows);
        Assert.Equal(1, _rig.Counters.Get($"journal_excluded{{reason={reason}}}"));
        Assert.Single(_rig.Acked);
    }

    [Fact]
    public async Task A_redelivered_receipt_writes_the_same_natural_key_again_for_comms_to_deduplicate()
    {
        _rig.At(90);
        _rig.OpenTurn(OutboundOrigin.Human);
        _rig.At(100.1);
        var receipt = ReceiptRig.Receipt(100);
        await _rig.DeliverAsync(receipt);
        await _rig.DeliverAsync(receipt);
        _rig.At(102.25);
        await _rig.DecideAsync();

        // Idempotency is Comms' natural key (conversation, tg:<messageId>) with an unchanged
        // fingerprint, so both records must agree on every fingerprinted field.
        var records = _rig.Records();
        Assert.Equal(2, records.Count);
        foreach (var field in new[] { "telegram", "direction", "sender", "sentAt", "text" })
            Assert.Equal(records[0][field]!.ToJsonString(), records[1][field]!.ToJsonString());
    }

    // ── a relay (workflow) send (#439) ───────────────────────────────────────

    [Fact]
    public async Task A_two_message_relay_send_is_journaled_as_agent_tool_with_one_send_group()
    {
        _rig.At(90);
        var relay = _rig.OpenTurn(OutboundOrigin.Relay);

        _rig.At(100.1);
        await _rig.DeliverAsync(ReceiptRig.Receipt(100,
            new ToolSendMessage(701, ReceiptRig.Date(100), ReplyToMessageId: 55, "*status*", ToolSendTextFormats.Rich),
            new ToolSendMessage(702, ReceiptRig.Date(100), ReplyToMessageId: null, "continued", ToolSendTextFormats.Plain)));
        _rig.At(102.25);
        await _rig.DecideAsync();

        var records = _rig.Records();
        Assert.Equal(2, records.Count);
        var group = records[0]["sendGroup"]!["id"]!.GetValue<string>();
        foreach (var (record, part) in records.Select((r, i) => (r, i + 1)))
        {
            Assert.Equal("agent_tool", record["origin"]!.GetValue<string>());
            Assert.Equal("outbound", record["direction"]!.GetValue<string>());
            Assert.Equal("agent", record["sender"]!["kind"]!.GetValue<string>());
            Assert.Equal(ReceiptRig.BotId, record["telegram"]!["botId"]!.GetValue<long>());
            Assert.Equal(ReceiptRig.HumanDm, record["telegram"]!["chatId"]!.GetValue<long>());
            Assert.Equal(group, record["sendGroup"]!["id"]!.GetValue<string>());
            Assert.Equal(part, record["sendGroup"]!["part"]!.GetValue<int>());
            Assert.Equal(2, record["sendGroup"]!["parts"]!.GetValue<int>());
        }

        Assert.Equal((701L, 55L, "*status*", "rich"), (
            records[0]["telegram"]!["messageId"]!.GetValue<long>(), records[0]["telegram"]!["replyToMessageId"]!.GetValue<long>(),
            records[0]["text"]!.GetValue<string>(), records[0]["textFormat"]!.GetValue<string>()));
        Assert.Equal((702L, "continued", "plain"), (
            records[1]["telegram"]!["messageId"]!.GetValue<long>(), records[1]["text"]!.GetValue<string>(),
            records[1]["textFormat"]!.GetValue<string>()));
        Assert.Null(records[1]["telegram"]!["replyToMessageId"]);

        Assert.Equal(1, _rig.Counters.Get("tool_send_captured"));
        Assert.Equal(1, _rig.Counters.Get("tool_send_captured_relay"));
        Assert.Equal("2", Assert.Single(_rig.DecisionEvents).Value("Kept"));
        relay.Close();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(65)]
    public async Task A_relay_receipt_spools_one_record_per_message_and_groups_only_two_to_64(int count)
    {
        _rig.At(90);
        _rig.OpenTurn(OutboundOrigin.Relay);
        _rig.At(100.1);
        var messages = Enumerable.Range(1, count)
            .Select(i => new ToolSendMessage(2_000 + i, ReceiptRig.Date(100), null, $"part {i}", ToolSendTextFormats.Plain))
            .ToArray();
        await _rig.DeliverAsync(ReceiptRig.Receipt(100, messages));
        _rig.At(102.25);
        await _rig.DecideAsync();

        var records = _rig.Records();
        Assert.Equal(count, records.Count);
        Assert.All(records, r => Assert.Equal("agent_tool", r["origin"]!.GetValue<string>()));
        Assert.Equal(Enumerable.Range(1, count).Select(i => 2_000L + i), records.Select(r => r["telegram"]!["messageId"]!.GetValue<long>()));
        if (count is > 1 and <= 64)
            Assert.Single(records.Select(r => r["sendGroup"]!["id"]!.GetValue<string>()).Distinct());
        else
            Assert.All(records, r => Assert.Null(r["sendGroup"]));

        Assert.Equal((1, 1, count), (_rig.Counters.Get("tool_send_captured"), _rig.Counters.Get("tool_send_captured_relay"),
            _rig.Counters.Get("journal_captured{direction=outbound}")));
        var decision = Assert.Single(_rig.DecisionEvents);
        Assert.Equal((count.ToString(), count.ToString()), (decision.Value("Parts"), decision.Value("Kept")));
    }

    [Theory]
    [InlineData(ReceiptRig.Operational, "supergroup", "operational_chat")]
    [InlineData(ReceiptRig.Stranger, "private", "unauthorized_chat")]
    [InlineData(0L, "private", "no_chat")]
    [InlineData(-1002000000055L, "channel", "unsupported_chat")]
    public async Task A_relay_send_to_a_chat_the_policy_excludes_writes_nothing(long chatId, string chatType, string reason)
    {
        _rig.At(90);
        _rig.OpenTurn(OutboundOrigin.Relay);
        _rig.At(100.1);
        await _rig.DeliverAsync(ReceiptRig.Receipt(100) with { Chat = new ToolSendChat(chatId, chatType, null) });
        _rig.At(102.25);
        await _rig.DecideAsync();

        Assert.Equal(0, _rig.Rows);
        Assert.Equal(1, _rig.Counters.Get($"journal_excluded{{reason={reason}}}"));
        Assert.Equal(1, _rig.Counters.Get("tool_send_captured_relay"));
        Assert.Equal("0", Assert.Single(_rig.DecisionEvents).Value("Kept"));
        Assert.Single(_rig.Acked);
    }

    [Fact]
    public async Task A_redelivered_relay_receipt_writes_the_same_fingerprinted_fields_again()
    {
        _rig.At(90);
        _rig.OpenTurn(OutboundOrigin.Relay);
        _rig.At(100.1);
        var receipt = ReceiptRig.Receipt(100);
        await _rig.DeliverAsync(receipt);
        await _rig.DeliverAsync(receipt);
        _rig.At(102.25);
        await _rig.DecideAsync();

        var records = _rig.Records();
        Assert.Equal(2, records.Count);
        foreach (var field in new[] { "telegram", "direction", "sender", "sentAt", "text" })
            Assert.Equal(records[0][field]!.ToJsonString(), records[1][field]!.ToJsonString());
        Assert.Equal(2, _rig.Counters.Get("tool_send_captured_relay"));
    }

    [Fact]
    public async Task A_partial_relay_receipt_journals_only_the_accepted_message_with_no_group()
    {
        // A three-chunk send of which Telegram accepted only the first: the receipt carries one message.
        _rig.At(90);
        _rig.OpenTurn(OutboundOrigin.Relay);
        _rig.At(100.1);
        await _rig.DeliverAsync(ReceiptRig.Receipt(100,
            new ToolSendMessage(801, ReceiptRig.Date(100), null, "chunk 1 of 3", ToolSendTextFormats.Plain)));
        _rig.At(102.25);
        await _rig.DecideAsync();

        var record = Assert.Single(_rig.Records());
        Assert.Equal(801, record["telegram"]!["messageId"]!.GetValue<long>());
        Assert.Null(record["sendGroup"]);
    }

    [Fact]
    public async Task The_decision_log_carries_codes_and_counts_only()
    {
        const long chat = ReceiptRig.Group;
        const long messageId = 98_765_431;
        const string title = "Project Lantern";
        const string text = "nonce-kestrel-7731";

        ToolSendReceipt Receipt(double at) => ReceiptRig.Receipt(at,
            new ToolSendMessage(messageId + (long)at, ReceiptRig.Date(at), ReplyToMessageId: 4_444_441, text, ToolSendTextFormats.Plain))
            with { Chat = new ToolSendChat(chat, "supergroup", title) };

        // captured relay, captured human, excluded origin, unattributed
        _rig.At(90);
        var relay = _rig.OpenTurn(OutboundOrigin.Relay);
        _rig.At(100.1);
        await _rig.DeliverAsync(Receipt(100));
        _rig.At(102.25);
        await _rig.DecideAsync();
        relay.Close();

        _rig.At(110);
        var human = _rig.OpenTurn(OutboundOrigin.Human);
        _rig.At(120.1);
        await _rig.DeliverAsync(Receipt(120));
        _rig.At(122.25);
        await _rig.DecideAsync();
        human.Close();

        _rig.At(130);
        var bridge = _rig.OpenTurn(OutboundOrigin.Bridge);
        _rig.At(140.1);
        await _rig.DeliverAsync(Receipt(140));
        _rig.At(142.25);
        await _rig.DecideAsync();
        bridge.Close();

        _rig.At(170.1);
        await _rig.DeliverAsync(Receipt(170));
        _rig.At(172.25);
        await _rig.DecideAsync();

        Assert.Equal(["relay/covered", "human/covered", "excluded_origin/bridge_or_unknown", "unattributed/no_interval"], _rig.Decisions);
        var pattern = new System.Text.RegularExpressions.Regex(
            @"^tool-send receipt decided \(attribution=(human|relay|excluded_origin|unattributed), "
            + @"reason=(covered|idle_edge|bridge_or_unknown|no_interval|abnormal_close|horizon), "
            + @"relay_touched=(true|false), parts=\d+, kept=\d+\)$");
        foreach (var decision in _rig.DecisionEvents)
        {
            Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Information, decision.Level);
            Assert.Matches(pattern, decision.Message);
            Assert.Equal(["Attribution", "Reason", "RelayTouched", "Parts", "Kept"], decision.Names);
        }

        foreach (var logged in _rig.ConsumerLog.Events)
        {
            var ids = new[] { 100, 120, 140, 170 }.Select(at => (messageId + at).ToString());
            foreach (var secret in ids.Concat([chat.ToString(), "4444441", ReceiptRig.BotId.ToString(), title, text]))
                Assert.DoesNotContain(secret, logged.Message, StringComparison.Ordinal);
        }

        Assert.Equal(2, _rig.Rows);
    }

    // ── registration ─────────────────────────────────────────────────────────

    [Fact]
    public void With_the_journal_on_the_ledger_and_the_consumer_are_registered()
    {
        using var host = BuildHost(ReceiptRig.Token);

        var consumer = Assert.Single(host.Services.GetServices<IHostedService>().OfType<ToolSendReceiptConsumer>());
        Assert.Same(consumer, host.Services.GetRequiredService<ToolSendReceiptConsumer>());
        Assert.NotNull(host.Services.GetService<TurnOriginLedger>());
        Assert.DoesNotContain(host.Services.GetServices<IHostedService>(), s => s is ToolSendReceiptQueueCleanup);
    }

    [Fact]
    public void With_the_journal_off_there_is_no_ledger_and_no_consumer_only_the_queue_cleanup()
    {
        using var host = BuildHost(token: null);

        Assert.Null(host.Services.GetService<TurnOriginLedger>());
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

/// <summary>Keeps every log event, rendered and with its structured values.</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<Event> _events = new();

    public IReadOnlyList<Event> Events => _events.ToArray();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var values = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
        _events.Enqueue(new Event(logLevel, formatter(state, exception), values.ToList()));
    }

    public sealed record Event(LogLevel Level, string Message, IReadOnlyList<KeyValuePair<string, object?>> Values)
    {
        public string? Value(string name) => Values.FirstOrDefault(v => v.Key == name).Value?.ToString();

        /// <summary>The template's named values, without the template itself.</summary>
        public IReadOnlyList<string> Names => Values.Select(v => v.Key).Where(k => k != "{OriginalFormat}").ToList();
    }
}

/// <summary>A clock the test moves by hand.</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private long _ticks = start.UtcTicks;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

    public void Set(DateTimeOffset value) => Interlocked.Exchange(ref _ticks, value.UtcTicks);
}

/// <summary>
/// A ledger, a real capture over a temp spool and a consumer, all on one <see cref="ManualClock"/>.
/// Times are seconds after <see cref="Base"/>. Ids are made up.
/// </summary>
internal sealed class ReceiptRig : IDisposable
{
    public static readonly DateTimeOffset Base = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public const long BotId = 7001;
    public const long HumanDm = 4242;
    public const long Stranger = 4343;
    public const long Group = -1002000000001;
    public const long Operational = -1002000000009;
    public const string Token = "cj1.ingest.agent1.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    private ulong _nextTag;

    /// <param name="startAt">When the agent — and so its ledger — started, in seconds after <see cref="Base"/>.</param>
    public ReceiptRig(long? ownBotId = BotId, double startAt = 0)
    {
        Directory.CreateDirectory(Root);
        Clock = new ManualClock(T(startAt));
        Ledger = new TurnOriginLedger(NullLogger<TurnOriginLedger>.Instance, Clock);
        Spool = new JournalSpool(Path.Combine(Root, "spool"));

        var telegram = Options.Create(new TelegramOptions { AllowedUserIds = [HumanDm], AllowedGroupIds = [Group, Operational] });
        var capture = new JournalCapture(Spool, Counters, new AllowlistHolder(telegram),
            Options.Create(new JournalOptions { IngestToken = Token, ExcludedChatIds = Operational.ToString() }),
            NullLogger<JournalCapture>.Instance, Clock);

        Consumer = new ToolSendReceiptConsumer(
            Options.Create(new AgentOptions { Name = "fleet-agent1", Role = "generic-role", WorkDir = Root }),
            new NoBroker(), Ledger, capture, Counters, () => ownBotId,
            ConsumerLog, Clock)
        {
            AckHook = tag =>
            {
                lock (Acked) Acked.Add(tag);
                return Task.CompletedTask;
            },
        };
    }

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "tool-send-" + Guid.NewGuid().ToString("N"));
    public ManualClock Clock { get; }
    public TurnOriginLedger Ledger { get; }
    public JournalSpool Spool { get; }
    public JournalCounters Counters { get; } = new();
    public ToolSendReceiptConsumer Consumer { get; }
    public List<ulong> Acked { get; } = [];
    public RecordingLogger<ToolSendReceiptConsumer> ConsumerLog { get; } = new();

    public int Rows => Spool.Pending().Count;

    /// <summary>Every decision log line so far, as <c>attribution/reason</c>, in order (#439).</summary>
    public List<string> Decisions =>
        DecisionEvents.Select(e => $"{e.Value("Attribution")}/{e.Value("Reason")}").ToList();

    public List<RecordingLogger<ToolSendReceiptConsumer>.Event> DecisionEvents =>
        ConsumerLog.Events.Where(e => e.Message.StartsWith("tool-send receipt decided", StringComparison.Ordinal)).ToList();

    public List<JsonObject> Records() =>
        Spool.Pending().Select(e => e.Record).OrderBy(r => r["telegram"]!["messageId"]!.GetValue<long>()).ToList();

    public static DateTimeOffset T(double seconds) => Base + TimeSpan.FromSeconds(seconds);

    public static DateTime Date(double seconds) => T(seconds).UtcDateTime;

    public void At(double seconds) => Clock.Set(T(seconds));

    /// <summary>A lock-held interval opened now under <paramref name="origin"/>, as an executor would.</summary>
    public TurnOriginLedger.LedgerInterval OpenTurn(OutboundOrigin origin)
    {
        using var _ = Ledger.Pending(origin);
        return Ledger.OpenTurn();
    }

    public static ToolSendReceipt Receipt(double requestedAt, params ToolSendMessage[] messages) =>
        Receipt(requestedAt, messageId: 0, messages);

    /// <param name="messageId">The single message's id when none are given; 0 derives one from the stamp.</param>
    public static ToolSendReceipt Receipt(double requestedAt, long messageId, params ToolSendMessage[] messages) => new()
    {
        Agent = "fleet-agent1",
        Tool = "send_message",
        RequestedAt = T(requestedAt),
        BotId = BotId,
        Chat = new ToolSendChat(HumanDm, "private", null),
        Messages = messages.Length > 0
            ? messages
            : [new ToolSendMessage(messageId != 0 ? messageId : 900 + (long)(requestedAt * 10), Date(requestedAt), null, "hello", ToolSendTextFormats.Plain)],
    };

    public static string Json(ToolSendReceipt receipt) => Encoding.UTF8.GetString(ToolSendReceipts.Serialize(receipt));

    public Task<ulong> DeliverAsync(ToolSendReceipt receipt) => DeliverRawAsync(ToolSendReceipts.Serialize(receipt));

    public async Task<ulong> DeliverRawAsync(byte[] body)
    {
        var tag = Interlocked.Increment(ref _nextTag);
        await Consumer.ReceiveAsync(body, tag);
        return tag;
    }

    public Task<int> DecideAsync() => Consumer.DecideDueAsync();

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }

    private sealed class NoBroker : IAgentBrokerConnection
    {
        public IConnection? Connection => null;
    }
}
