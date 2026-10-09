using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Interfaces;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Fleet.Journal.Client;
using Fleet.Shared;
using Fleet.Shared.Journal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Fleet.Agent.Tests;

/// <summary>
/// The agent side of the conversation journal (#377): what the Telegram transport captures, what it
/// never captures, and that an agent without a token registers nothing (AC2–AC8, AC4).
/// </summary>
public sealed class JournalCaptureTests : IDisposable
{
    internal const long User = 111;
    internal const long Stranger = 222;
    internal const long Group = -1001000000001;
    internal const long Operational = -1001000000003;
    internal const string Token = "cj1.ingest.agent1.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "journal-capture-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // ── AC2: a human DM with text and a photo ────────────────────────────────

    [Fact]
    public async Task A_human_dm_with_a_photo_is_one_record_delivered_once_after_a_restart()
    {
        var rig = Rig.Build(_root);

        await rig.Transport.OnMessage(new Message
        {
            Id = 10,
            Chat = new Chat { Id = User, Type = ChatType.Private, FirstName = "Ann" },
            From = new User { Id = User, FirstName = "Ann", Username = "ann" },
            Date = Rig.Now,
            Caption = "look at this",
            Photo = [new PhotoSize { FileId = "f1", FileUniqueId = "uq-1", FileSize = 3, Width = 1, Height = 1 }],
        }, UpdateType.Message);

        var entry = Assert.Single(rig.Spool.Pending());
        var record = entry.Record;
        Assert.Equal("inbound", record["direction"]!.GetValue<string>());
        Assert.Equal("look at this", record["text"]!.GetValue<string>());
        Assert.Equal("plain", record["textFormat"]!.GetValue<string>());
        Assert.Equal("private", record["telegram"]!["chatKind"]!.GetValue<string>());
        Assert.Equal(10, record["telegram"]!["messageId"]!.GetValue<long>());
        Assert.Equal("human", record["sender"]!["kind"]!.GetValue<string>());
        Assert.Equal(User.ToString(), record["sender"]!["id"]!.GetValue<string>());

        var attachment = Assert.Single(record["attachments"]!.AsArray())!;
        Assert.Equal("photo", attachment["kind"]!.GetValue<string>());
        Assert.Equal("uq-1", attachment["fileUniqueId"]!.GetValue<string>());
        Assert.Equal("media_disabled", attachment["notArchivedReason"]!.GetValue<string>());
        Assert.True(File.Exists(rig.Spool.MediaPath(entry.Id, 0)));
        Assert.Equal(1, rig.Counters.Get("journal_captured{direction=inbound}"));

        // Comms down: the record stays.
        var down = new ScriptedListener(_ => throw new HttpRequestException("refused"));
        await Drainer(rig.Spool, down, rig.Counters).RunOnceAsync(default);
        Assert.Single(rig.Spool.PendingIds());

        // Restart: a new spool over the same directory, Comms up, delivered exactly once.
        var restarted = new JournalSpool(rig.SpoolRoot);
        var up = new ScriptedListener(_ => ScriptedListener.Json(201, "{\"result\":\"created\"}"));
        var drainer = Drainer(restarted, up, rig.Counters);
        await Task.Delay(1100); // past the one-second backoff the refused attempt set
        for (var i = 0; i < 3; i++) await drainer.RunOnceAsync(default);

        Assert.Empty(restarted.PendingIds());
        Assert.Single(up.Posted);
        Assert.Equal(1, rig.Counters.Get("journal_delivered"));
        Assert.Equal("media_disabled", up.Posted[0]["attachments"]![0]!["notArchivedReason"]!.GetValue<string>());
    }

    // ── AC3: relay output to the group writes nothing ───────────────────────

    [Fact]
    public async Task A_relay_tasks_reply_to_the_group_writes_no_spool_file()
    {
        var rig = Rig.Build(_root);
        var manager = rig.ManagerAnswering("the answer");

        var done = new TaskCompletionSource();
        manager.OnTaskCompleted += (_, _, _, _, _, _, _, _) => done.TrySetResult();
        _ = manager.StartTask(chatId: Group, task: "status?", displayText: "status?", isSessionTask: false,
            source: TaskSource.Relay, relaySender: "agent2", taskId: "t-1");
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains(rig.Bot.Texts, t => t.Contains("the answer", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFiles(rig.Spool.PendingDir));
        Assert.Equal(0, rig.Counters.Get("journal_captured{direction=outbound}"));
        Assert.True(rig.Counters.Get("journal_excluded{reason=system_origin}") >= 1);
    }

    [Fact]
    public async Task A_bridge_tasks_reply_to_the_group_writes_no_spool_file()
    {
        var rig = Rig.Build(_root);
        var manager = rig.ManagerAnswering("the answer");

        var done = new TaskCompletionSource();
        manager.OnTaskCompleted += (_, _, _, _, _, _, _, _) => done.TrySetResult();
        _ = manager.StartTask(chatId: Group, task: "status?", displayText: "status?", isSessionTask: false,
            source: TaskSource.Bridge, taskId: "t-1");
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains(rig.Bot.Texts, t => t.Contains("the answer", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFiles(rig.Spool.PendingDir));
        Assert.Equal(0, rig.Counters.Get("journal_captured{direction=outbound}"));
        Assert.True(rig.Counters.Get("journal_excluded{reason=system_origin}") >= 1);
    }

    /// <summary>
    /// #439 D5: one relay turn replies through the runtime and sends through a Telegram MCP tool to
    /// the same allowlisted DM. Classifier rule 2 still excludes the runtime reply; the tool send the
    /// ledger admits is one <c>agent_tool</c> record.
    /// </summary>
    [Fact]
    public async Task A_relay_turns_runtime_reply_is_excluded_and_its_tool_send_to_the_same_dm_is_journaled()
    {
        var rig = Rig.Build(_root);
        var clock = new ManualClock(ReceiptRig.T(90));
        var ledger = new TurnOriginLedger(NullLogger<TurnOriginLedger>.Instance, clock);

        var executor = Substitute.For<IAgentExecutor>();
        executor
            .ExecuteAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<MessageImage>?>(),
                Arg.Any<IReadOnlyList<MessageDocument>?>(), Arg.Any<CancellationToken>())
            .Returns(_ => RelayTurn(ledger, clock, "the answer"));
        var manager = new TaskManager(rig.AgentOptions, executor, new SessionManager(), NullLogger<TaskManager>.Instance,
            sink: rig.Holder, ledger: ledger);

        var done = new TaskCompletionSource();
        manager.OnTaskCompleted += (_, _, _, _, _, _, _, _) => done.TrySetResult();
        _ = manager.StartTask(chatId: User, task: "directive", displayText: "directive", isSessionTask: false,
            source: TaskSource.Relay, relaySender: "agent2", taskId: "t-1");
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains(rig.Bot.Texts, t => t.Contains("the answer", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFiles(rig.Spool.PendingDir));
        Assert.True(rig.Counters.Get("journal_excluded{reason=system_origin}") >= 1);

        // The same turn's send_message to that DM, stamped while it ran.
        var consumer = new ToolSendReceiptConsumer(
            rig.AgentOptions, new NoBroker(), ledger, rig.Capture, rig.Counters, () => rig.Bot.BotId,
            NullLogger<ToolSendReceiptConsumer>.Instance, clock)
        {
            AckHook = _ => Task.CompletedTask,
        };
        clock.Set(ReceiptRig.T(100.1));
        await consumer.ReceiveAsync(ToolSendReceipts.Serialize(ReceiptRig.Receipt(100) with
        {
            BotId = rig.Bot.BotId,
            Chat = new ToolSendChat(User, "private", null),
        }), deliveryTag: 1);
        clock.Set(ReceiptRig.T(102.25));
        Assert.Equal(1, await consumer.DecideDueAsync());

        var record = Assert.Single(rig.Spool.Pending()).Record;
        Assert.Equal("agent_tool", record["origin"]!.GetValue<string>());
        Assert.Equal("outbound", record["direction"]!.GetValue<string>());
        Assert.Equal(User, record["telegram"]!["chatId"]!.GetValue<long>());
        Assert.Equal(1, rig.Counters.Get("tool_send_captured_relay"));
    }

    /// <summary>A relay turn as an executor runs it: its interval from start to its own successful result.</summary>
    private static async IAsyncEnumerable<AgentProgress> RelayTurn(TurnOriginLedger ledger, ManualClock clock, string answer)
    {
        var interval = ledger.OpenTurn();
        try
        {
            await Task.Yield();
            clock.Set(ReceiptRig.T(100.5));
            interval.CloseNormally();
            yield return new AgentProgress { Summary = answer, EventType = "result", FinalResult = answer };
        }
        finally
        {
            interval.Close();
        }
    }

    private sealed class NoBroker : IAgentBrokerConnection
    {
        public RabbitMQ.Client.IConnection? Connection => null;
    }

    [Fact]
    public async Task The_same_reply_to_a_human_task_is_journaled()
    {
        var rig = Rig.Build(_root);
        var manager = rig.ManagerAnswering("the answer");

        var done = new TaskCompletionSource();
        manager.OnTaskCompleted += (_, _, _, _, _, _, _, _) => done.TrySetResult();
        _ = manager.StartTask(chatId: Group, task: "status?", displayText: "status?", isSessionTask: false,
            source: TaskSource.UserMessage);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var record = Assert.Single(rig.Spool.Pending()).Record;
        Assert.Equal("outbound", record["direction"]!.GetValue<string>());
        Assert.Equal("agent_runtime", record["origin"]!.GetValue<string>());
        Assert.Equal("agent", record["sender"]!["kind"]!.GetValue<string>());
        Assert.Contains("the answer", record["text"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TaskSource.Relay, OutboundOrigin.Relay)]
    [InlineData(TaskSource.Bridge, OutboundOrigin.Bridge)]
    [InlineData(TaskSource.UserMessage, OutboundOrigin.Human)]
    [InlineData(TaskSource.NewCommand, OutboundOrigin.Human)]
    [InlineData(TaskSource.CheckIn, OutboundOrigin.Human)]
    [InlineData(TaskSource.DebouncedGroupBatch, OutboundOrigin.Human)]
    public void The_task_source_decides_the_origin(TaskSource source, OutboundOrigin origin) =>
        Assert.Equal(origin, TaskManager.OriginOf(source));

    // ── AC5: a long reply split into N messages ──────────────────────────────

    [Fact]
    public async Task A_split_reply_is_n_records_sharing_one_send_group()
    {
        var rig = Rig.Build(_root);

        await rig.Transport.SendReplyAsync(User, new AgentReply(new string('a', 3990) + "\n" + new string('b', 3990) + "\n" + new string('c', 100)), OutboundOrigin.Human);

        Assert.Equal(3, rig.Bot.Texts.Count);
        var records = rig.Spool.Pending().Select(e => e.Record).ToList();
        Assert.Equal(3, records.Count);

        var groups = records.Select(r => r["sendGroup"]!).ToList();
        Assert.Single(groups.Select(g => g["id"]!.GetValue<string>()).Distinct());
        Assert.Equal([1, 2, 3], groups.Select(g => g["part"]!.GetValue<int>()).Order());
        Assert.All(groups, g => Assert.Equal(3, g["parts"]!.GetValue<int>()));

        // Each record is the message Telegram got, with Telegram's id.
        Assert.Equal(rig.Bot.Texts.Order(), records.Select(r => r["text"]!.GetValue<string>()).Order());
        Assert.Equal(rig.Bot.SentIds.Order(), records.Select(r => r["telegram"]!["messageId"]!.GetValue<long>()).Order());
    }

    [Fact]
    public async Task A_single_message_reply_has_no_send_group()
    {
        var rig = Rig.Build(_root);

        await rig.Transport.SendReplyAsync(User, new AgentReply("short"), OutboundOrigin.Human);

        Assert.Null(Assert.Single(rig.Spool.Pending()).Record["sendGroup"]);
    }

    // ── AC6: an album ────────────────────────────────────────────────────────

    [Fact]
    public async Task An_album_is_one_record_per_photo_with_the_caption_only_on_the_first()
    {
        var rig = Rig.Build(_root);

        for (var i = 0; i < 3; i++)
        {
            await rig.Transport.OnMessage(new Message
            {
                Id = 20 + i,
                Chat = new Chat { Id = User, Type = ChatType.Private },
                From = new User { Id = User, FirstName = "Ann" },
                Date = Rig.Now,
                MediaGroupId = "album-1",
                Caption = i == 0 ? "our trip" : null,
                Photo = [new PhotoSize { FileId = $"f{i}", FileUniqueId = $"uq-{i}", FileSize = 3, Width = 1, Height = 1 }],
            }, UpdateType.Message);
        }

        var entries = rig.Spool.Pending();
        Assert.Equal(3, entries.Count);
        Assert.All(entries, e =>
        {
            Assert.Equal("album-1", e.Record["telegram"]!["mediaGroupId"]!.GetValue<string>());
            Assert.Single(e.Record["attachments"]!.AsArray());
        });

        var first = entries.Single(e => e.Record["telegram"]!["messageId"]!.GetValue<long>() == 20).Record;
        Assert.Equal("our trip", first["text"]!.GetValue<string>());
        Assert.Equal(2, entries.Count(e => e.Record["text"] is null));

        // Nothing the agent derived: no local path, no image prompt, no hint.
        foreach (var file in Directory.GetFiles(rig.Spool.PendingDir))
        {
            var text = await File.ReadAllTextAsync(file);
            Assert.DoesNotContain(rig.AttachmentDir, text, StringComparison.Ordinal);
            Assert.DoesNotContain(new TelegramOptions().DefaultImagePrompt, text, StringComparison.Ordinal);
            Assert.DoesNotContain("attachment:", text, StringComparison.Ordinal);
        }
    }

    // ── AC7 (agent half): two agents, one voice message ──────────────────────

    [Fact]
    public async Task Two_agents_journal_one_voice_message_identically_but_for_transcript_and_bot()
    {
        var first = Rig.Build(Path.Combine(_root, "a"), botId: 5001, transcript: "hello there", persist: false);
        var second = Rig.Build(Path.Combine(_root, "b"), botId: 5002, transcript: "hello bear", persist: false);

        Message Voice() => new()
        {
            Id = 30,
            Chat = new Chat { Id = Group, Type = ChatType.Supergroup, Title = "Team" },
            From = new User { Id = User, FirstName = "Ann" },
            Date = Rig.Now,
            Voice = new Voice { FileId = "v", FileUniqueId = "vq-1", Duration = 2, FileSize = 3 },
        };

        await first.Transport.OnMessage(Voice(), UpdateType.Message);
        await second.Transport.OnMessage(Voice(), UpdateType.Message);

        var a = first.Inbound();
        var b = second.Inbound();
        Assert.Equal("hello there", a["transcript"]!.GetValue<string>());
        Assert.Equal("hello bear", b["transcript"]!.GetValue<string>());
        Assert.Null(a["text"]);
        Assert.Equal("voice", a["attachments"]![0]!["kind"]!.GetValue<string>());

        // Everything the fingerprint reads is equal; only the per-observer fields differ.
        Assert.Equal(WithoutObserverFields(a), WithoutObserverFields(b));

        // Each agent echoed its transcript, and the echo is not a record (#394): the inbound
        // record already holds the transcript.
        Assert.Contains("🎤 hello there", first.Bot.Texts);
        Assert.DoesNotContain(first.Spool.Pending(), e => e.Record["direction"]!.GetValue<string>() == "outbound");
    }

    internal static string WithoutObserverFields(JsonObject record)
    {
        var copy = record.DeepClone().AsObject();
        copy.Remove("eventId");
        copy.Remove("transcript");
        copy.Remove("transcriptTruncated");
        copy["telegram"]!.AsObject().Remove("botId");
        return copy.ToJsonString();
    }

    // ── AC8: commands and relay images ───────────────────────────────────────

    [Fact]
    public async Task A_command_is_journaled_as_typed()
    {
        var rig = Rig.Build(_root);

        await rig.Transport.OnMessage(new Message
        {
            Id = 40,
            Chat = new Chat { Id = User, Type = ChatType.Private },
            From = new User { Id = User, FirstName = "Ann" },
            Date = Rig.Now,
            Text = "/new do X",
        }, UpdateType.Message);

        var record = Assert.Single(rig.Spool.Pending()).Record;
        Assert.Equal("/new do X", record["text"]!.GetValue<string>());
        Assert.Equal("inbound", record["direction"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_relay_reply_with_an_image_writes_no_spool_file()
    {
        var rig = Rig.Build(_root);
        var image = Path.Combine(_root, "x.png");
        await File.WriteAllBytesAsync(image, [1, 2, 3]);

        await rig.Holder.SendReplyAsync(User, new AgentReply($"see [IMAGE:{image}]"), OutboundOrigin.Relay);
        await rig.Holder.SendReplyAsync(User, new AgentReply("and [IMAGE:/workspace/x.png]"), OutboundOrigin.Relay);

        Assert.NotEmpty(rig.Bot.Requests);
        Assert.Empty(Directory.GetFiles(rig.Spool.PendingDir));
        Assert.Empty(Directory.GetFiles(rig.Spool.MediaDir));
    }

    [Fact]
    public async Task A_human_facing_image_is_journaled_with_its_bytes_copied()
    {
        var rig = Rig.Build(_root);
        var image = Path.Combine(_root, "chart.png");
        await File.WriteAllBytesAsync(image, [1, 2, 3]);

        await rig.Transport.SendReplyAsync(User, new AgentReply($"the chart [IMAGE:{image}]"), OutboundOrigin.Human);
        await File.WriteAllBytesAsync(image, [9, 9, 9]); // overwritten later: the spool keeps what was sent

        // The transport sends the leading text and then the photo captioned with it: two
        // messages, one send group, and the photo's record carries the attachment.
        var entries = rig.Spool.Pending();
        Assert.Equal(2, entries.Count);
        Assert.Single(entries.Select(e => e.Record["sendGroup"]!["id"]!.GetValue<string>()).Distinct());
        var entry = entries.Single(e => e.Record["attachments"]!.AsArray().Count == 1);
        var attachment = entry.Record["attachments"]!.AsArray()[0]!;
        Assert.Equal("image/png", attachment["mimeType"]!.GetValue<string>());
        Assert.Equal("chart.png", attachment["fileName"]!.GetValue<string>());
        Assert.Equal("the chart", entry.Record["text"]!.GetValue<string>());
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(rig.Spool.MediaPath(entry.Id, 0)));
        Assert.DoesNotContain(_root, entry.Record.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reply_sent_as_html_is_journaled_as_html()
    {
        var rig = Rig.Build(_root);

        // A pre-formatted HTML notice is not a reply, so it is not journaled (#394)...
        await rig.Holder.SendHtmlTextAsync(Group, "<b>done</b>");
        Assert.Empty(rig.Spool.Pending());

        // ...while a reply with a tool block goes out as HTML and is journaled as HTML, body only.
        await rig.Holder.SendReplyAsync(Group,
            new AgentReply("done & dusted", "\n(1 in / 2 out)", "\n<blockquote expandable>Read(x)</blockquote>"),
            OutboundOrigin.Human);

        var record = Assert.Single(rig.Spool.Pending()).Record;
        Assert.Equal("done &amp; dusted", record["text"]!.GetValue<string>());
        Assert.Equal("html", record["textFormat"]!.GetValue<string>());
        Assert.Equal("supergroup", record["telegram"]!["chatKind"]!.GetValue<string>());
    }

    // ── what is never written ────────────────────────────────────────────────

    [Fact]
    public async Task An_unauthorized_dm_and_an_operational_group_write_nothing()
    {
        var rig = Rig.Build(_root);

        await rig.Transport.OnMessage(new Message
        {
            Id = 50, Chat = new Chat { Id = Stranger, Type = ChatType.Private },
            From = new User { Id = Stranger, FirstName = "Eve" }, Date = Rig.Now, Text = "let me in",
        }, UpdateType.Message);
        await rig.Transport.OnMessage(new Message
        {
            Id = 51, Chat = new Chat { Id = Operational, Type = ChatType.Supergroup },
            From = new User { Id = User, FirstName = "Ann" }, Date = Rig.Now, Text = "ops chatter",
        }, UpdateType.Message);
        await rig.Transport.SendReplyAsync(Operational, new AgentReply("ops reply"), OutboundOrigin.Human);

        Assert.Empty(Directory.GetFiles(rig.Spool.PendingDir));
        Assert.Equal(1, rig.Counters.Get("journal_excluded{reason=unauthorized_chat}"));
        Assert.Equal(2, rig.Counters.Get("journal_excluded{reason=operational_chat}"));
    }

    [Fact]
    public async Task A_spool_that_cannot_be_written_never_fails_the_send()
    {
        var rig = Rig.Build(_root);
        Directory.Delete(rig.Spool.PendingDir, recursive: true);
        await File.WriteAllTextAsync(rig.Spool.PendingDir, "not a directory");

        await rig.Transport.SendReplyAsync(User, new AgentReply("still delivered"), OutboundOrigin.Human);

        Assert.Equal(["still delivered"], rig.Bot.Texts);
        Assert.Equal(1, rig.Counters.Get("journal_capture_failed"));
    }

    [Fact]
    public async Task Without_a_journal_the_transport_sends_exactly_what_it_did()
    {
        var rig = Rig.Build(_root, journal: false);

        await rig.Transport.SendTextAsync(User, "hello");
        await rig.Holder.SendTextAsync(User, "relayed", OutboundOrigin.Relay);

        Assert.Equal(["hello", "relayed"], rig.Bot.Texts);
        Assert.False(Directory.Exists(rig.SpoolRoot));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static JournalDrainer Drainer(JournalSpool spool, HttpMessageHandler handler, JournalCounters counters) =>
        new(spool,
            new JournalHttpClient(new HttpClient(handler) { BaseAddress = new Uri("http://journal.test") }, Token),
            counters, NullLogger<JournalDrainer>.Instance);

    internal sealed class ScriptedListener(Func<JsonObject, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<JsonObject> Posted { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
            var response = respond(body);
            Posted.Add(body);
            return response;
        }

        public static HttpResponseMessage Json(int status, string json) =>
            new((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    /// <summary>A transport with a fake bot and, unless told otherwise, a journal on a temp spool.</summary>
    internal sealed class Rig
    {
        public static readonly DateTime Now = new(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc);

        public required AgentTransport Transport { get; init; }
        public required MessageSinkHolder Holder { get; init; }
        public required JournalBot Bot { get; init; }
        public required JournalSpool Spool { get; init; }
        public required JournalCapture Capture { get; init; }
        public required string SpoolRoot { get; init; }
        public required JournalCounters Counters { get; init; }
        public required string AttachmentDir { get; init; }
        public required IOptions<AgentOptions> AgentOptions { get; init; }
        public List<IncomingMessage> Routed { get; } = [];

        public JsonObject Inbound() =>
            Spool.Pending().Select(e => e.Record).Single(r => r["direction"]!.GetValue<string>() == "inbound");

        /// <summary>A task manager whose every turn answers <paramref name="answer"/>, sending through this transport.</summary>
        public TaskManager ManagerAnswering(string answer)
        {
            var executor = Substitute.For<IAgentExecutor>();
            executor
                .ExecuteAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<MessageImage>?>(),
                    Arg.Any<IReadOnlyList<MessageDocument>?>(), Arg.Any<CancellationToken>())
                .Returns(_ => Answer(answer));
            return new TaskManager(AgentOptions, executor, new SessionManager(), NullLogger<TaskManager>.Instance, sink: Holder);
        }

        private static async IAsyncEnumerable<AgentProgress> Answer(string text)
        {
            yield return new AgentProgress { Summary = text, EventType = "result", FinalResult = text };
            await Task.CompletedTask;
        }

        public static Rig Build(
            string root, long botId = 5001, string? transcript = null, bool persist = true, bool journal = true,
            TurnBindingPublisher? publisher = null)
        {
            var attachments = Path.Combine(root, "attachments");
            var spoolRoot = Path.Combine(root, "spool");

            var agentOpts = Options.Create(new AgentOptions
            {
                Name = "fleet-agent1",
                Role = "generic-role",
                WorkDir = root,
                FormattingMode = FormattingMode.PlainText,
                ShowStats = false,
            });
            var telegramOpts = Options.Create(new TelegramOptions
            {
                AllowedUserIds = [User],
                AllowedGroupIds = [Group, Operational],
                PersistAttachments = persist,
                AttachmentDir = attachments,
            });
            var rabbitOpts = Options.Create(new RabbitMqOptions());
            var whisperOpts = Options.Create(new WhisperOptions { ServiceUrl = transcript is null ? "" : "http://whisper.test" });

            var executor = Substitute.For<IAgentExecutor>();
            var httpFactory = Substitute.For<IHttpClientFactory>();
            httpFactory.CreateClient("whisper").Returns(_ => new HttpClient(new ScriptedListenerRaw(
                ScriptedListener.Json(200, $"{{\"text\":\"{transcript}\"}}"))));

            var allowlist = new AllowlistHolder(telegramOpts);
            var relay = new GroupRelayService(agentOpts, rabbitOpts, NullLogger<GroupRelayService>.Instance);
            var taskMgr = new TaskManager(agentOpts, executor, new SessionManager(), NullLogger<TaskManager>.Instance, turnBindings: publisher);
            var prompts = new PromptAssembler(executor);
            var commands = new CommandDispatcher(taskMgr, executor, agentOpts, NullLogger<CommandDispatcher>.Instance);
            var voice = new VoiceTranscriptionService(httpFactory, whisperOpts, NullLogger<VoiceTranscriptionService>.Instance);
            var tts = new TtsService(httpFactory, Options.Create(new TtsOptions()), NullLogger<TtsService>.Instance);
            var group = new GroupBehavior(agentOpts, telegramOpts, allowlist, executor, relay, taskMgr, commands, prompts, NullLogger<GroupBehavior>.Instance);
            var router = new MessageRouter(agentOpts, telegramOpts, allowlist, taskMgr, group, relay, commands, NullLogger<MessageRouter>.Instance);

            var counters = new JournalCounters();
            JournalSpool? spool = null;
            JournalCapture? capture = null;
            if (journal)
            {
                spool = new JournalSpool(spoolRoot);
                capture = new JournalCapture(spool, counters, allowlist,
                    Options.Create(new JournalOptions { IngestToken = Token, ExcludedChatIds = Operational.ToString() }),
                    NullLogger<JournalCapture>.Instance);
            }

            var holder = new MessageSinkHolder();
            var transport = new AgentTransport(
                agentOpts, telegramOpts, allowlist, relay, taskMgr, group, router, commands, voice, tts,
                Substitute.For<IFleetConnectionState>(), NullLogger<AgentTransport>.Instance, holder,
                journal: capture, turnBindings: publisher);

            var bot = new JournalBot(botId);
            transport.BotForTesting = bot;

            var rig = new Rig
            {
                Transport = transport,
                Holder = holder,
                Bot = bot,
                Spool = spool ?? null!,
                Capture = capture ?? null!,
                SpoolRoot = spoolRoot,
                Counters = counters,
                AttachmentDir = attachments,
                AgentOptions = agentOpts,
            };
            transport.RouterHookForTesting = m => { lock (rig.Routed) rig.Routed.Add(m); return Task.CompletedTask; };
            return rig;
        }
    }

    private sealed class ScriptedListenerRaw(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(response);
    }

    /// <summary>
    /// A bot that answers like the Bot API: every send returns the Message Telegram would, with the
    /// chat's real type, the bot as sender and a fresh message id.
    /// </summary>
    internal sealed class JournalBot(long botId) : ITelegramBotClient
    {
        private int _nextId = 1000;

        public List<object> Requests { get; } = [];
        public List<string> Texts { get; } = [];
        public List<long> SentIds { get; } = [];

        public bool LocalBotServer => false;
        public long BotId => botId;
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
        public IExceptionParser ExceptionsParser { get; set; } = new DefaultExceptionParser();

#pragma warning disable CS0067
        public event AsyncEventHandler<ApiRequestEventArgs>? OnMakingApiRequest;
        public event AsyncEventHandler<ApiResponseEventArgs>? OnApiResponseReceived;
#pragma warning restore CS0067

        private Message Sent(long chatId, int? replyTo)
        {
            var id = Interlocked.Increment(ref _nextId);
            lock (SentIds) SentIds.Add(id);
            return new Message
            {
                Id = id,
                Chat = new Chat
                {
                    Id = chatId,
                    Type = chatId > 0 ? ChatType.Private : ChatType.Supergroup,
                    Title = chatId > 0 ? null : "Team",
                },
                From = new User { Id = botId, IsBot = true, FirstName = "Agent", Username = $"agent{botId}_bot" },
                Date = Rig.Now,
                ReplyToMessage = replyTo is { } r ? new Message { Id = r, Chat = new Chat { Id = chatId } } : null,
            };
        }

        public Task<TResponse> MakeRequestAsync<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            lock (Requests) Requests.Add(request);
            object result = request switch
            {
                SendMessageRequest m => Record(Sent(m.ChatId.Identifier ?? 0, m.ReplyParameters?.MessageId), m.Text),
                SendPhotoRequest p => WithPhoto(Sent(p.ChatId.Identifier ?? 0, null)),
                SendVoiceRequest v => WithVoice(Sent(v.ChatId.Identifier ?? 0, v.ReplyParameters?.MessageId)),
                GetFileRequest => new TGFile { FileId = "f", FileUniqueId = "fu", FilePath = "files/f" },
                SendChatActionRequest => true,
                _ => throw new InvalidOperationException($"unexpected {request.GetType().Name}"),
            };
            return Task.FromResult((TResponse)result);
        }

        private Message Record(Message message, string text)
        {
            lock (Texts) Texts.Add(text);
            return message;
        }

        private static Message WithPhoto(Message message)
        {
            message.Photo = [new PhotoSize { FileId = "out", FileUniqueId = "out-uq", Width = 1, Height = 1 }];
            return message;
        }

        private static Message WithVoice(Message message)
        {
            message.Voice = new Voice { FileId = "tts", FileUniqueId = "tts-uq", Duration = 1 };
            return message;
        }

        public Task<TResponse> SendRequest<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
            => MakeRequestAsync(request, cancellationToken);

        public Task<TResponse> MakeRequest<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
            => MakeRequestAsync(request, cancellationToken);

        public Task<bool> TestApi(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task DownloadFile(string filePath, Stream destination, CancellationToken cancellationToken = default)
            => destination.WriteAsync(new byte[] { 1, 2, 3 }, 0, 3, cancellationToken);

        public Task DownloadFile(TGFile file, Stream destination, CancellationToken cancellationToken = default)
            => DownloadFile(file.FilePath ?? "", destination, cancellationToken);
    }
}

/// <summary>
/// AC4: without <c>Journal:IngestToken</c> nothing is registered — no drainer, no capture, no HTTP
/// client — and a malformed token stops the agent at startup.
/// </summary>
public sealed class JournalRegistrationTests : IDisposable
{
    private const string BotToken = "123456:AAHfakefakefakefakefakefakefakefakeff";
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "journal-reg-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch (IOException) { }
    }

    private static IConfiguration Config(string? token) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Journal:IngestToken"] = token })
        .Build();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Without_a_token_nothing_is_registered(string? token)
    {
        var services = new ServiceCollection();

        AgentHostRegistration.AddJournal(services, Config(token));

        Assert.Empty(services);
    }

    [Fact]
    public void Without_a_token_the_agent_graph_has_no_journal_service_or_client()
    {
        using var host = BuildHost(token: null);

        Assert.DoesNotContain(host.Services.GetServices<IHostedService>(), s => s is JournalDrainer);
        Assert.Null(host.Services.GetService<JournalDrainer>());
        Assert.Null(host.Services.GetService<TurnBindingPublisher>());
        Assert.Null(host.Services.GetService<JournalCapture>());
        Assert.Null(host.Services.GetService<JournalSpool>());
        Assert.Null(host.Services.GetService<JournalHttpClient>());
        Assert.Null(host.Services.GetService<IOptions<JournalOptions>>()?.Value.IngestToken is { Length: > 0 } ? "set" : null);

        var transport = host.Services.GetServices<IHostedService>().OfType<AgentTransport>().Single();
        Assert.False(transport.JournalEnabled);
        Assert.False(Directory.Exists(Path.Combine(_workDir, ".fleet", "journal-spool")));

        AgentHostRegistration.ValidateStartupConfiguration(host.Services);
    }

    [Fact]
    public void With_a_token_the_drainer_and_capture_are_wired()
    {
        using var host = BuildHost(JournalCaptureTests.Token);

        var drainer = Assert.Single(host.Services.GetServices<IHostedService>().OfType<JournalDrainer>());
        Assert.Same(drainer, host.Services.GetRequiredService<JournalDrainer>());
        var publisher = Assert.Single(host.Services.GetServices<IHostedService>().OfType<TurnBindingPublisher>());
        Assert.Same(publisher, host.Services.GetRequiredService<TurnBindingPublisher>());
        Assert.True(host.Services.GetServices<IHostedService>().OfType<AgentTransport>().Single().JournalEnabled);
        Assert.True(Directory.Exists(Path.Combine(_workDir, ".fleet", "journal-spool", "pending")));

        AgentHostRegistration.ValidateStartupConfiguration(host.Services);
    }

    [Theory]
    [InlineData("not-a-journal-token")]
    [InlineData("cj1.notifier.agent1.AAAA")]
    public void A_malformed_token_fails_startup_naming_the_key_not_the_value(string token)
    {
        using var host = BuildHost(token);

        var error = Assert.Throws<InvalidOperationException>(() => AgentHostRegistration.ValidateStartupConfiguration(host.Services));
        Assert.Contains("Journal:IngestToken", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(token, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SendGrant_UsesSeparateNoRetryClientAndPreservesOrdinaryReplyPolicy()
    {
        using var host = BuildHost(JournalCaptureTests.Token, send: true);
        var transport = host.Services.GetServices<IHostedService>().OfType<AgentTransport>().Single();
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var normal = (TelegramBotClient)typeof(AgentTransport).GetField("_bot", flags)!.GetValue(transport)!;
        var sending = (TelegramBotClient)typeof(AgentTransport).GetField("_journalSendBot", flags)!.GetValue(transport)!;
        Assert.NotSame(normal, sending); Assert.Equal(normal.Token, sending.Token);
        var options = typeof(TelegramBotClient).GetField("_options", flags)!;
        Assert.Equal(3, ((TelegramBotClientOptions)options.GetValue(normal)!).RetryCount);
        Assert.Equal(0, ((TelegramBotClientOptions)options.GetValue(sending)!).RetryCount);
    }

    private IHost BuildHost(string? token, bool send = false)
    {
        Directory.CreateDirectory(_workDir);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agent:Name"] = "fleet-agent1",
            ["Agent:Role"] = "generic-role",
            ["Agent:WorkDir"] = _workDir,
            ["Agent:Provider"] = "claude",
            ["Telegram:BotToken"] = BotToken,
            ["Journal:IngestToken"] = token,
            ["Journal:SendEnabled"] = send ? "true" : "false",
            ["Journal:ReadToken"] = send ? "cj1.read.agent1.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" : null,
        });
        builder.Services.AddLogging();
        builder.Services.AddAgentCoreServices(builder.Configuration);
        builder.Services.AddAgentDaemonServices(builder.Configuration);
        return builder.Build();
    }
}
