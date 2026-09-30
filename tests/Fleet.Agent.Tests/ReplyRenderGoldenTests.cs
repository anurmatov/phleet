using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Interfaces;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Fleet.Agent.Tests.Harness;
using Fleet.Journal.Client;
using Fleet.Shared;
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
/// The reply render golden (#394 AC3): every Bot API call a reply turn makes, compared with the
/// fixtures under <c>Fixtures/reply-render-golden/</c>, which were recorded from the code before
/// S5 changed the reply path. Journaling only observes, so Telegram output must stay byte-identical
/// with the journal on and off.
/// </summary>
/// <remarks>
/// Set <c>PHLEET_RECORD_REPLY_GOLDEN=1</c> to rewrite the fixtures in the source tree instead of
/// comparing. That is only ever correct on the pre-change code: a fixture recorded from the code
/// under test proves nothing.
/// </remarks>
public sealed class ReplyRenderGoldenTests : IDisposable
{
    internal const string FixtureDirectory = "reply-render-golden";
    private const string RecordVariable = "PHLEET_RECORD_REPLY_GOLDEN";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "reply-golden-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static bool Recording => Environment.GetEnvironmentVariable(RecordVariable) == "1";

    public static TheoryData<string, bool> Cases()
    {
        var data = new TheoryData<string, bool>();
        foreach (var row in GoldenRow.All)
        {
            data.Add(row.Id, true);
            data.Add(row.Id, false);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Telegram_output_equals_the_recorded_fixture(string rowId, bool journal)
    {
        var row = GoldenRow.All.Single(r => r.Id == rowId);
        var run = await ReplyRun.ExecuteAsync(row, Path.Combine(_root, rowId + (journal ? "-on" : "-off")), journal);
        var actual = run.Fixture();

        if (Recording)
        {
            if (!journal) return;
            var directory = Path.Combine(RepoPaths.Resolve("tests/Fleet.Agent.Tests/Fixtures"), FixtureDirectory);
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, row.Id + ".json"), actual);
            return;
        }

        var expected = await File.ReadAllTextAsync(FixturePath(row.Id));
        Assert.Equal(expected.ReplaceLineEndings("\n"), actual);
    }

    /// <summary>
    /// A missing fixture directory must fail, not let every row compare against nothing. Asserted
    /// against the test output, which is where an uncopied fixture would show up.
    /// </summary>
    [Fact]
    public void Every_row_has_exactly_one_committed_fixture()
    {
        if (Recording) return;
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", FixtureDirectory);

        var files = Directory.GetFiles(directory, "*.json").Select(Path.GetFileNameWithoutExtension).Order().ToList();

        Assert.Equal(95, GoldenRow.All.Count);
        Assert.Equal(GoldenRow.All.Select(r => r.Id).Order(), files);
    }

    [Fact]
    public async Task Path_t_rows_send_the_image_marker_as_literal_text_and_no_photo()
    {
        if (Recording) return;
        foreach (var row in GoldenRow.All.Where(r => r.Footer == GoldenFooter.StatsAndTools && r.Body is GoldenBody.TrailingImage or GoldenBody.LeadingImage))
        {
            var fixture = JsonNode.Parse(await File.ReadAllTextAsync(FixturePath(row.Id)))!;
            var calls = fixture["calls"]!.AsArray();

            Assert.DoesNotContain(calls, c => c!["method"]!.GetValue<string>() == "sendPhoto");
            Assert.Contains(calls, c => c!["method"]!.GetValue<string>() == "sendMessage"
                && c["text"]!.GetValue<string>().Contains("[IMAGE:/workspace/example/chart.png]", StringComparison.Ordinal));
        }
    }

    internal static string FixturePath(string rowId) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", FixtureDirectory, rowId + ".json");

    // ── AC4: the journal side of the same matrix ─────────────────────────────

    public static TheoryData<string> Rows()
    {
        var data = new TheoryData<string>();
        foreach (var row in GoldenRow.All) data.Add(row.Id);
        return data;
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public async Task The_journal_holds_the_reply_body_and_never_the_footer(string rowId)
    {
        var row = GoldenRow.All.Single(r => r.Id == rowId);
        var run = await ReplyRun.ExecuteAsync(row, Path.Combine(_root, rowId), journal: true);
        var records = run.Records();

        Assert.NotEmpty(records);
        foreach (var record in records)
        {
            var text = record["text"]?.GetValue<string>() ?? "";
            var json = record.ToJsonString();
            Assert.Equal("outbound", record["direction"]!.GetValue<string>());
            Assert.DoesNotContain(GoldenRow.StatsLine, text, StringComparison.Ordinal);
            Assert.DoesNotContain("<blockquote expandable>", text, StringComparison.Ordinal);
            Assert.DoesNotContain("[IMAGE:", text, StringComparison.Ordinal);
            Assert.DoesNotContain("[reply_to:", text, StringComparison.Ordinal);
            // No tool argument and no local path, anywhere in the record.
            Assert.DoesNotContain("notes.md", json, StringComparison.Ordinal);
            Assert.DoesNotContain(run.ImageDirectory, json, StringComparison.Ordinal);
        }

        // Every record is a message Telegram accepted, and one reply is one send group.
        var accepted = run.Bot.Calls.Where(c => c.Ok).Select(c => c.SentId!.Value).ToList();
        Assert.All(records, r => Assert.Contains(r["telegram"]!["messageId"]!.GetValue<long>(), accepted));
        if (records.Count == 1)
        {
            Assert.Null(records[0]["sendGroup"]);
        }
        else
        {
            Assert.Single(records.Select(r => r["sendGroup"]!["id"]!.GetValue<string>()).Distinct());
            Assert.All(records, r => Assert.Equal(records.Count, r["sendGroup"]!["parts"]!.GetValue<int>()));
        }

        if (row.Footer == GoldenFooter.StatsAndTools)
        {
            // Path T: the pieces before balancing concatenate to B with the markers removed, and
            // each record is its piece balanced.
            var b = (row.Prefix ? "<b>Agent1:</b>\n" : "") + System.Net.WebUtility.HtmlEncode(row.Content(run.ImageDirectory));
            var pieces = run.Rig.Transport.ToolBlockJournalPieces(b);
            Assert.Equal(WithoutMarkers(b), string.Concat(pieces));
            Assert.Equal(
                pieces.Where(p => !string.IsNullOrWhiteSpace(p)).Select(AgentTransport.BalanceBlockquotesInChunk),
                records.Select(r => r["text"]!.GetValue<string>()));
            Assert.All(records, r => Assert.Equal("html", r["textFormat"]!.GetValue<string>()));
        }
        else if (row.Mode != FormattingMode.Rich || row.Failure == GoldenFailure.SendRichMessage)
        {
            // Path S: each journaled message is the message sent, less its footer; a message that
            // was only footer has no record.
            foreach (var call in run.Bot.Calls.Where(c => c.Ok && c.Method == "sendMessage"))
            {
                var record = records.SingleOrDefault(r => r["telegram"]!["messageId"]!.GetValue<long>() == call.SentId);
                var body = WithoutFooter(call.Text!);
                if (WithoutPrefix(body).Trim().Length == 0)
                {
                    Assert.Null(record);
                    continue;
                }
                Assert.NotNull(record);
                Assert.Equal(body, record["text"]!.GetValue<string>());
            }
        }
    }

    /// <summary>Footer-only messages have no record: stats as its own message on Path S, the footer as its own chunk on Path T.</summary>
    [Theory]
    [InlineData("plain-noprefix-stats-boundary", 3, 2)]
    [InlineData("plain-noprefix-tools-boundary", 3, 2)]
    [InlineData("plain-noprefix-stats-trailing-image", 3, 2)]
    [InlineData("legacyhtml-noprefix-stats-trailing-image", 3, 2)]
    [InlineData("rich-noprefix-stats-trailing-image", 3, 2)]
    public async Task A_footer_only_message_has_no_record(string rowId, int replyMessages, int records)
    {
        var row = GoldenRow.All.Single(r => r.Id == rowId);
        var run = await ReplyRun.ExecuteAsync(row, Path.Combine(_root, rowId), journal: true);

        var sent = run.Bot.Calls.Where(c => c.Ok && !IsProgressPost(c)).ToList();
        Assert.Equal(replyMessages, sent.Count);
        Assert.Equal(records, run.Records().Count);

        // The last message is the footer, and it is the one without a record.
        Assert.DoesNotContain(run.Records(), r => r["telegram"]!["messageId"]!.GetValue<long>() == sent[^1].SentId);
        Assert.All(run.Records(), r => Assert.Equal(records, r["sendGroup"]!["parts"]!.GetValue<int>()));
    }

    [Fact]
    public async Task Stats_as_the_only_caption_journals_the_photo_with_no_caption()
    {
        var row = GoldenRow.All.Single(r => r.Id == "plain-noprefix-stats-image-only");
        var run = await ReplyRun.ExecuteAsync(row, Path.Combine(_root, row.Id), journal: true);

        Assert.Equal(GoldenRow.StatsLine, Assert.Single(run.Bot.Calls).Caption);
        var record = Assert.Single(run.Records());
        Assert.Null(record["text"]);
        Assert.Equal("photo", Assert.Single(record["attachments"]!.AsArray())!["kind"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("plain-noprefix-stats-leading-image")]
    [InlineData("legacyhtml-prefix-stats-leading-image")]
    [InlineData("rich-noprefix-stats-leading-image")]
    public async Task A_caption_carrying_the_stats_line_is_journaled_without_it(string rowId)
    {
        var row = GoldenRow.All.Single(r => r.Id == rowId);
        var run = await ReplyRun.ExecuteAsync(row, Path.Combine(_root, rowId), journal: true);

        Assert.EndsWith(GoldenRow.StatsLine, Assert.Single(run.Bot.Calls).Caption);
        var record = Assert.Single(run.Records());
        Assert.Equal("The chart shows **steady** growth.", record["text"]!.GetValue<string>());
        Assert.Single(record["attachments"]!.AsArray());
    }

    [Fact]
    public async Task The_parse_entities_fallback_journals_the_plain_body()
    {
        var row = GoldenRow.All.Single(r => r.Failure == GoldenFailure.ParseEntities);
        var run = await ReplyRun.ExecuteAsync(row, Path.Combine(_root, row.Id), journal: true);

        var record = Assert.Single(run.Records());
        Assert.Equal("plain", record["textFormat"]!.GetValue<string>());
        Assert.Equal("Agent1:\nHere is the answer: use x < y & keep <b>tags</b> literal.", record["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_rich_fallback_journals_each_legacy_html_chunk_without_the_footer()
    {
        var row = GoldenRow.All.Single(r => r.Failure == GoldenFailure.SendRichMessage);
        var run = await ReplyRun.ExecuteAsync(row, Path.Combine(_root, row.Id), journal: true);

        var records = run.Records();
        Assert.Equal(3, records.Count);
        Assert.All(records, r => Assert.Equal("html", r["textFormat"]!.GetValue<string>()));
        Assert.Equal(TelegramFormatter.FormatAndSplit(GoldenRow.LongBody.Trim()), records.Select(r => r["text"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_rich_reply_is_journaled_as_its_markdown_without_the_footer()
    {
        var row = GoldenRow.All.Single(r => r.Id == "rich-prefix-stats-short");
        var run = await ReplyRun.ExecuteAsync(row, Path.Combine(_root, row.Id), journal: true);

        var record = Assert.Single(run.Records());
        Assert.Equal("rich", record["textFormat"]!.GetValue<string>());
        Assert.Equal("Agent1: " + row.Content(run.ImageDirectory), record["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_reply_to_token_is_journaled_as_the_reply_target_not_as_text()
    {
        var row = GoldenRow.All.Single(r => r.Id == "plain-noprefix-stats-reply-to");
        var run = await ReplyRun.ExecuteAsync(row, Path.Combine(_root, row.Id), journal: true);

        var record = Assert.Single(run.Records());
        Assert.Equal(GoldenRow.ReplyTarget, record["telegram"]!["replyToMessageId"]!.GetValue<long>());
        Assert.Equal("Replying to your question: yes, **it works**.", record["text"]!.GetValue<string>());
    }

    private static bool IsProgressPost(BotCall call) =>
        call.Text?.Contains("<blockquote expandable>... Using", StringComparison.Ordinal) == true;

    private static string WithoutMarkers(string text) =>
        System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Replace(text, @"\[IMAGE:(.+?)\]", ""), @"\[reply_to:\s*(-?\d+)\]", "");

    /// <summary>The text less the per-message name header each render mode puts first.</summary>
    private static string WithoutPrefix(string text)
    {
        foreach (var prefix in new[] { "<b>Agent1:</b>", "Agent1:" })
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal)) return text[prefix.Length..];
        }
        return text;
    }

    /// <summary>The sent text less its stats line, which only ever ends a message.</summary>
    private static string WithoutFooter(string text)
    {
        var at = text.IndexOf(GoldenRow.StatsLine, StringComparison.Ordinal);
        if (at < 0) return text;
        Assert.Equal(text.Length, at + GoldenRow.StatsLine.Length);
        var body = text[..at];
        return body.EndsWith('\n') ? body[..^1] : body;
    }
}

internal enum GoldenFooter { None, Stats, StatsAndTools }

internal enum GoldenBody { Short, Long, TrailingImage, LeadingImage, ReplyTo, ImageOnly, Boundary }

internal enum GoldenFailure { None, SendRichMessage, ParseEntities }

/// <summary>One cell of the render matrix (#394 AC3).</summary>
internal sealed record GoldenRow(FormattingMode Mode, bool Prefix, GoldenFooter Footer, GoldenBody Body, GoldenFailure Failure = GoldenFailure.None)
{
    public const long Chat = 111;
    public const int ReplyTarget = 4242;
    public const string ImageName = "chart.png";

    /// <summary>What every fixture shows instead of the per-run temp directory holding the image.</summary>
    public const string ImageDirectoryPlaceholder = "/workspace/example";

    /// <summary>
    /// The 90-cell matrix, the two forced failures, then three rows the matrix cannot reach: stats as
    /// the only caption, and a body that ends exactly on a 4,000-character cut so the footer is a
    /// message of its own on each render path.
    /// </summary>
    public static readonly IReadOnlyList<GoldenRow> All = Build();

    public string Id
    {
        get
        {
            var mode = Mode switch
            {
                FormattingMode.PlainText => "plain",
                FormattingMode.LegacyHtml => "legacyhtml",
                _ => "rich",
            };
            var footer = Footer switch
            {
                GoldenFooter.None => "none",
                GoldenFooter.Stats => "stats",
                _ => "tools",
            };
            var body = Body switch
            {
                GoldenBody.Short => "short",
                GoldenBody.Long => "long",
                GoldenBody.TrailingImage => "trailing-image",
                GoldenBody.LeadingImage => "leading-image",
                GoldenBody.ReplyTo => "reply-to",
                GoldenBody.ImageOnly => "image-only",
                _ => "boundary",
            };
            var failure = Failure switch
            {
                GoldenFailure.SendRichMessage => "-richfail",
                GoldenFailure.ParseEntities => "-parsefail",
                _ => "",
            };
            return $"{mode}-{(Prefix ? "prefix" : "noprefix")}-{footer}-{body}{failure}";
        }
    }

    public override string ToString() => Id;

    private static List<GoldenRow> Build()
    {
        var rows = new List<GoldenRow>();
        foreach (var mode in new[] { FormattingMode.PlainText, FormattingMode.LegacyHtml, FormattingMode.Rich })
        foreach (var prefix in new[] { false, true })
        foreach (var footer in Enum.GetValues<GoldenFooter>())
        foreach (var body in new[] { GoldenBody.Short, GoldenBody.Long, GoldenBody.TrailingImage, GoldenBody.LeadingImage, GoldenBody.ReplyTo })
            rows.Add(new GoldenRow(mode, prefix, footer, body));

        rows.Add(new GoldenRow(FormattingMode.Rich, false, GoldenFooter.Stats, GoldenBody.Long, GoldenFailure.SendRichMessage));
        rows.Add(new GoldenRow(FormattingMode.LegacyHtml, true, GoldenFooter.Stats, GoldenBody.Short, GoldenFailure.ParseEntities));

        rows.Add(new GoldenRow(FormattingMode.PlainText, false, GoldenFooter.Stats, GoldenBody.ImageOnly));
        rows.Add(new GoldenRow(FormattingMode.PlainText, false, GoldenFooter.Stats, GoldenBody.Boundary));
        rows.Add(new GoldenRow(FormattingMode.PlainText, false, GoldenFooter.StatsAndTools, GoldenBody.Boundary));
        return rows;
    }

    /// <summary>The agent's final answer for this row. <paramref name="imageDirectory"/> holds a real image.</summary>
    public string Content(string imageDirectory)
    {
        var image = Path.Combine(imageDirectory, ImageName);
        return Body switch
        {
            GoldenBody.Short => "Here is the **answer**: use `x < y` & keep <b>tags</b> literal.",
            GoldenBody.Long => LongBody,
            GoldenBody.TrailingImage => $"Here is the chart:\n[IMAGE:{image}]",
            GoldenBody.LeadingImage => $"[IMAGE:{image}]\nThe chart shows **steady** growth.",
            GoldenBody.ReplyTo => $"[reply_to: {ReplyTarget}] Replying to your question: yes, **it works**.",
            GoldenBody.ImageOnly => $"[IMAGE:{image}]",
            _ => BoundaryBody,
        };
    }

    /// <summary>
    /// Exactly 8,000 characters with nothing HTML-special and no newline, so both the plain split
    /// and Path T's hard cut end the body exactly on the second 4,000-character boundary.
    /// </summary>
    public static readonly string BoundaryBody = string.Concat(Enumerable.Repeat("lorem ipsu", 800));

    /// <summary>Exactly 9,000 characters: long enough to split in every mode, with Markdown and HTML-special text.</summary>
    public static readonly string LongBody = BuildLongBody();

    private static string BuildLongBody()
    {
        var sb = new StringBuilder();
        for (var i = 1; sb.Length < 9000; i++)
            sb.Append($"Paragraph {i}: the **quick** brown fox checks `step {i}` & jumps over the lazy dog <again>.\n");
        return sb.ToString(0, 9000);
    }

    /// <summary>Culture-independent stats: every number formats the same under any culture.</summary>
    public static ExecutionStats Stats() => new()
    {
        InputTokens = 12000,
        OutputTokens = 340,
        CacheReadTokens = 5000,
        CostUsd = 2m,
        ContextWindow = 100000,
    };

    /// <summary>The stats line this row's stats render to (the footer the journal must never hold).</summary>
    public const string StatsLine = "(12k in / 340 out | cache: 5k | $2 | ctx: 17%)";

    public const string ToolName = "Read";
    public const string ToolArgs = "{\"file_path\":\"/workspace/example/notes.md\"}";
}

/// <summary>
/// One reply turn through the real <see cref="TaskManager"/>, the real sink holder and the real
/// <see cref="AgentTransport"/>, with a fake Bot API behind it.
/// </summary>
internal sealed class ReplyRun
{
    public required GoldenRow Row { get; init; }
    public required ReplyRig Rig { get; init; }
    public required GoldenBot Bot { get; init; }
    public required string ImageDirectory { get; init; }
    public JournalSpool? Spool { get; init; }
    public required JournalCounters Counters { get; init; }

    private static readonly JsonSerializerOptions FixtureJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static async Task<ReplyRun> ExecuteAsync(GoldenRow row, string root, bool journal)
    {
        var images = Path.Combine(root, "img");
        Directory.CreateDirectory(images);
        await File.WriteAllBytesAsync(Path.Combine(images, GoldenRow.ImageName), [0x89, 0x50, 0x4E, 0x47]);

        var rig = ReplyRig.Build(root, row.Mode, row.Prefix, showStats: row.Footer != GoldenFooter.None, journal: journal);
        rig.Bot.FailRich = row.Failure == GoldenFailure.SendRichMessage;
        rig.Bot.FailParseEntities = row.Failure == GoldenFailure.ParseEntities;

        var steps = new List<AgentProgress>();
        if (row.Footer == GoldenFooter.StatsAndTools)
            steps.Add(ReplyRig.ToolStep(GoldenRow.ToolName, GoldenRow.ToolArgs));
        var content = row.Content(images);
        steps.Add(new AgentProgress
        {
            Summary = content, EventType = "result", FinalResult = content,
            Stats = row.Footer == GoldenFooter.None ? null : GoldenRow.Stats(),
        });

        await rig.RunTurnAsync(GoldenRow.Chat, steps);

        return new ReplyRun
        {
            Row = row,
            Rig = rig,
            Bot = rig.Bot,
            ImageDirectory = images,
            Spool = rig.Spool,
            Counters = rig.Counters,
        };
    }

    /// <summary>The Bot API calls as the fixture stores them, with the temp image directory normalised.</summary>
    public string Fixture()
    {
        var calls = new JsonArray();
        foreach (var call in Bot.Calls)
            calls.Add(call.ToJson());

        var document = new JsonObject
        {
            ["row"] = Row.Id,
            ["callCount"] = Bot.Calls.Count,
            ["calls"] = calls,
        };
        return Normalise(document.ToJsonString(FixtureJson)).ReplaceLineEndings("\n") + "\n";
    }

    public string Normalise(string text) => text.Replace(ImageDirectory, GoldenRow.ImageDirectoryPlaceholder, StringComparison.Ordinal);

    /// <summary>The journal records this turn wrote, in Telegram send order.</summary>
    public List<JsonObject> Records() =>
        Spool!.Pending().Select(e => e.Record).OrderBy(r => r["telegram"]!["messageId"]!.GetValue<long>()).ToList();
}

/// <summary>A transport with a fake Bot API, a spool when the journal is on, and a task manager sending through it.</summary>
internal sealed class ReplyRig
{
    public const string Token = "cj1.ingest.agent1.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    public required AgentTransport Transport { get; init; }
    public required MessageSinkHolder Holder { get; init; }
    public required GoldenBot Bot { get; init; }
    public JournalSpool? Spool { get; init; }
    public required JournalCounters Counters { get; init; }
    public required IOptions<AgentOptions> AgentOptions { get; init; }

    public static AgentProgress ToolStep(string name, string args) => new()
    {
        Summary = $"Using {name}", EventType = "tool_use", IsSignificant = true, ToolName = name, ToolArgs = args,
    };

    public static ReplyRig Build(
        string root, FormattingMode mode = FormattingMode.PlainText, bool prefix = false, bool showStats = false,
        bool journal = true, IEnumerable<long>? extraAllowedGroups = null)
    {
        var agentOpts = Options.Create(new AgentOptions
        {
            Name = "fleet-agent1",
            Role = "generic-role",
            WorkDir = root,
            ShortName = "agent1",
            FormattingMode = mode,
            PrefixMessages = prefix,
            ShowStats = showStats,
        });
        var telegramOpts = Options.Create(new TelegramOptions
        {
            AllowedUserIds = [GoldenRow.Chat],
            AllowedGroupIds = [.. extraAllowedGroups ?? []],
            PersistAttachments = false,
            AttachmentDir = Path.Combine(root, "attachments"),
        });
        var rabbitOpts = Options.Create(new RabbitMqOptions());

        // Every service that sends goes through the holder the transport attaches to, as in
        // production, so a router or command notice reaches the same fake Bot API.
        var holder = new MessageSinkHolder();
        var executor = Substitute.For<IAgentExecutor>();
        var httpFactory = Substitute.For<IHttpClientFactory>();
        var allowlist = new AllowlistHolder(telegramOpts);
        var relay = new GroupRelayService(agentOpts, rabbitOpts, NullLogger<GroupRelayService>.Instance);
        var taskMgr = new TaskManager(agentOpts, executor, new SessionManager(), NullLogger<TaskManager>.Instance, sink: holder);
        var prompts = new PromptAssembler(executor);
        var commands = new CommandDispatcher(taskMgr, executor, agentOpts, NullLogger<CommandDispatcher>.Instance, sink: holder);
        var voice = new VoiceTranscriptionService(httpFactory, Options.Create(new WhisperOptions()), NullLogger<VoiceTranscriptionService>.Instance);
        var tts = new TtsService(httpFactory, Options.Create(new TtsOptions()), NullLogger<TtsService>.Instance);
        var group = new GroupBehavior(agentOpts, telegramOpts, allowlist, executor, relay, taskMgr, commands, prompts, NullLogger<GroupBehavior>.Instance);
        var router = new MessageRouter(agentOpts, telegramOpts, allowlist, taskMgr, group, relay, commands, NullLogger<MessageRouter>.Instance, sink: holder);

        var counters = new JournalCounters();
        JournalSpool? spool = null;
        JournalCapture? capture = null;
        if (journal)
        {
            spool = new JournalSpool(Path.Combine(root, "spool"));
            capture = new JournalCapture(spool, counters, allowlist,
                Options.Create(new JournalOptions { IngestToken = Token }),
                NullLogger<JournalCapture>.Instance);
        }

        var transport = new AgentTransport(
            agentOpts, telegramOpts, allowlist, relay, taskMgr, group, router, commands, voice, tts,
            Substitute.For<IFleetConnectionState>(), NullLogger<AgentTransport>.Instance, holder,
            journal: capture);
        var bot = new GoldenBot(5001);
        transport.BotForTesting = bot;
        transport.RouterHookForTesting = _ => Task.CompletedTask;

        return new ReplyRig
        {
            Transport = transport,
            Holder = holder,
            Bot = bot,
            Spool = spool,
            Counters = counters,
            AgentOptions = agentOpts,
        };
    }

    /// <summary>A task manager whose turn yields <paramref name="steps"/>, sending through this transport.</summary>
    public TaskManager Manager(IReadOnlyList<AgentProgress> steps, IConversationEventPublisher? events = null) =>
        Manager(ExecutorYielding(steps), events);

    /// <summary>A task manager over <paramref name="executor"/>, sending through this transport.</summary>
    public TaskManager Manager(IAgentExecutor executor, IConversationEventPublisher? events = null) =>
        new(AgentOptions, executor, new SessionManager(), NullLogger<TaskManager>.Instance, events: events, sink: Holder);

    public static IAgentExecutor ExecutorYielding(IReadOnlyList<AgentProgress> steps)
    {
        var executor = Substitute.For<IAgentExecutor>();
        executor
            .ExecuteAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<MessageImage>?>(),
                Arg.Any<IReadOnlyList<MessageDocument>?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Yield(steps));
        return executor;
    }

    /// <summary>Runs one human turn to completion: every Bot API call it makes has been made on return.</summary>
    public async Task RunTurnAsync(long chatId, IReadOnlyList<AgentProgress> steps,
        IConversationEventPublisher? events = null, TaskSource source = TaskSource.UserMessage)
    {
        var manager = Manager(steps, events);
        var done = new TaskCompletionSource();
        manager.OnTaskCompleted += (_, _, _, _, _, _, _, _) => done.TrySetResult();
        _ = manager.StartTask(chatId, "question", "question", isSessionTask: false, source: source);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    internal static async IAsyncEnumerable<AgentProgress> Yield(IReadOnlyList<AgentProgress> steps)
    {
        foreach (var step in steps)
            yield return step;
        await Task.CompletedTask;
    }
}

/// <summary>One Bot API call as the golden records it.</summary>
internal sealed record BotCall(
    string Method, long ChatId, string? Text, string? Caption, ParseMode ParseMode, int? ReplyTo,
    string? RichJson, string? FileName, bool Ok)
{
    /// <summary>The id Telegram gave an accepted send. Not part of the fixture.</summary>
    public long? SentId { get; init; }

    public JsonObject ToJson()
    {
        var node = new JsonObject { ["method"] = Method, ["chatId"] = ChatId };
        switch (Method)
        {
            case "sendMessage":
                node["text"] = Text;
                node["parseMode"] = ParseName(ParseMode);
                break;
            case "sendPhoto":
                node["caption"] = Caption;
                node["parseMode"] = ParseName(ParseMode);
                node["file"] = FileName;
                break;
            case "sendRichMessage":
                node["rich"] = RichJson is null ? null : JsonNode.Parse(RichJson);
                break;
        }
        node["replyTo"] = ReplyTo;
        node["outcome"] = Ok ? "ok" : "failed";
        return node;
    }

    private static string? ParseName(ParseMode mode) => mode == ParseMode.None ? null : mode.ToString();
}

/// <summary>
/// A Bot API that answers like Telegram (the chat's type, the bot as sender, a fresh message id, the
/// reply target echoed back) and records every send in order. Chat actions are answered, not
/// recorded: the typing loop's cadence is timing, not rendering.
/// </summary>
internal sealed class GoldenBot(long botId) : ITelegramBotClient
{
    private int _nextId = 1000;
    private readonly List<BotCall> _calls = [];

    /// <summary>Every sendRichMessage fails, forcing the LegacyHtml fallback.</summary>
    public bool FailRich { get; set; }

    /// <summary>Every HTML sendMessage is refused with a parse-entities error, forcing the plain fallback.</summary>
    public bool FailParseEntities { get; set; }

    /// <summary>Every sendPhoto fails, forcing the photo-failed notice.</summary>
    public bool FailPhoto { get; set; }

    public IReadOnlyList<BotCall> Calls
    {
        get { lock (_calls) return [.. _calls]; }
    }

    public List<long> SentIds { get; } = [];

    public bool LocalBotServer => false;
    public long BotId => botId;
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
    public IExceptionParser ExceptionsParser { get; set; } = new DefaultExceptionParser();

#pragma warning disable CS0067
    public event AsyncEventHandler<ApiRequestEventArgs>? OnMakingApiRequest;
    public event AsyncEventHandler<ApiResponseEventArgs>? OnApiResponseReceived;
#pragma warning restore CS0067

    private void Record(BotCall call)
    {
        lock (_calls) _calls.Add(call);
    }

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
            Date = new DateTime(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc),
            ReplyToMessage = replyTo is { } r ? new Message { Id = r, Chat = new Chat { Id = chatId } } : null,
        };
    }

    public Task<TResponse> MakeRequestAsync<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        object result;
        switch (request)
        {
            case SendChatActionRequest:
                result = true;
                break;

            case SendMessageRequest m:
            {
                var chat = m.ChatId.Identifier ?? 0;
                if (FailParseEntities && m.ParseMode == ParseMode.Html)
                {
                    Record(new BotCall("sendMessage", chat, m.Text, null, m.ParseMode, m.ReplyParameters?.MessageId, null, null, false));
                    throw new ApiRequestException("Bad Request: can't parse entities: unsupported start tag", 400);
                }
                var sent = Sent(chat, m.ReplyParameters?.MessageId);
                Record(new BotCall("sendMessage", chat, m.Text, null, m.ParseMode, m.ReplyParameters?.MessageId, null, null, true) { SentId = sent.Id });
                result = sent;
                break;
            }

            case SendRichMessageRequest r:
            {
                var chat = r.ChatId.Identifier ?? 0;
                var rich = JsonSerializer.Serialize(r.RichMessage, JsonBotAPI.Options);
                if (FailRich)
                {
                    Record(new BotCall("sendRichMessage", chat, null, null, ParseMode.None, r.ReplyParameters?.MessageId, rich, null, false));
                    throw new ApiRequestException("Bad Request: rich messages are unavailable in this chat", 400);
                }
                var sent = Sent(chat, r.ReplyParameters?.MessageId);
                Record(new BotCall("sendRichMessage", chat, null, null, ParseMode.None, r.ReplyParameters?.MessageId, rich, null, true) { SentId = sent.Id });
                result = sent;
                break;
            }

            case SendPhotoRequest p:
            {
                var chat = p.ChatId.Identifier ?? 0;
                var file = p.Photo is InputFileStream stream ? stream.FileName : p.Photo?.ToString();
                if (FailPhoto)
                {
                    Record(new BotCall("sendPhoto", chat, null, p.Caption, p.ParseMode, p.ReplyParameters?.MessageId, null, file, false));
                    throw new ApiRequestException("Bad Request: wrong file", 400);
                }
                var message = Sent(chat, p.ReplyParameters?.MessageId);
                Record(new BotCall("sendPhoto", chat, null, p.Caption, p.ParseMode, p.ReplyParameters?.MessageId, null, file, true) { SentId = message.Id });
                message.Photo = [new PhotoSize { FileId = "out", FileUniqueId = "out-uq", Width = 1, Height = 1 }];
                result = message;
                break;
            }

            default:
                throw new InvalidOperationException($"unexpected {request.GetType().Name}");
        }
        return Task.FromResult((TResponse)result);
    }

    public Task<TResponse> SendRequest<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        => MakeRequestAsync(request, cancellationToken);

    public Task<TResponse> MakeRequest<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        => MakeRequestAsync(request, cancellationToken);

    public Task<bool> TestApi(CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task DownloadFile(string filePath, Stream destination, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task DownloadFile(TGFile file, Stream destination, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
