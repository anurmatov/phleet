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

        var executor = Substitute.For<IAgentExecutor>();
        var httpFactory = Substitute.For<IHttpClientFactory>();
        var allowlist = new AllowlistHolder(telegramOpts);
        var relay = new GroupRelayService(agentOpts, rabbitOpts, NullLogger<GroupRelayService>.Instance);
        var taskMgr = new TaskManager(agentOpts, executor, new SessionManager(), NullLogger<TaskManager>.Instance);
        var prompts = new PromptAssembler(executor);
        var commands = new CommandDispatcher(taskMgr, executor, agentOpts, NullLogger<CommandDispatcher>.Instance);
        var voice = new VoiceTranscriptionService(httpFactory, Options.Create(new WhisperOptions()), NullLogger<VoiceTranscriptionService>.Instance);
        var tts = new TtsService(httpFactory, Options.Create(new TtsOptions()), NullLogger<TtsService>.Instance);
        var group = new GroupBehavior(agentOpts, telegramOpts, allowlist, executor, relay, taskMgr, commands, prompts, NullLogger<GroupBehavior>.Instance);
        var router = new MessageRouter(agentOpts, telegramOpts, allowlist, taskMgr, group, relay, commands, NullLogger<MessageRouter>.Instance);

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

        var holder = new MessageSinkHolder();
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
    public TaskManager Manager(IReadOnlyList<AgentProgress> steps, IConversationEventPublisher? events = null)
    {
        var executor = Substitute.For<IAgentExecutor>();
        executor
            .ExecuteAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<MessageImage>?>(),
                Arg.Any<IReadOnlyList<MessageDocument>?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Yield(steps));
        return new TaskManager(AgentOptions, executor, new SessionManager(), NullLogger<TaskManager>.Instance,
            events: events, sink: Holder);
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

    private static async IAsyncEnumerable<AgentProgress> Yield(IReadOnlyList<AgentProgress> steps)
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
                var refused = FailParseEntities && m.ParseMode == ParseMode.Html;
                Record(new BotCall("sendMessage", chat, m.Text, null, m.ParseMode, m.ReplyParameters?.MessageId, null, null, !refused));
                if (refused)
                    throw new ApiRequestException("Bad Request: can't parse entities: unsupported start tag", 400);
                result = Sent(chat, m.ReplyParameters?.MessageId);
                break;
            }

            case SendRichMessageRequest r:
            {
                var chat = r.ChatId.Identifier ?? 0;
                Record(new BotCall("sendRichMessage", chat, null, null, ParseMode.None, r.ReplyParameters?.MessageId,
                    JsonSerializer.Serialize(r.RichMessage, JsonBotAPI.Options), null, !FailRich));
                if (FailRich)
                    throw new ApiRequestException("Bad Request: rich messages are unavailable in this chat", 400);
                result = Sent(chat, r.ReplyParameters?.MessageId);
                break;
            }

            case SendPhotoRequest p:
            {
                var chat = p.ChatId.Identifier ?? 0;
                var file = p.Photo is InputFileStream stream ? stream.FileName : p.Photo?.ToString();
                Record(new BotCall("sendPhoto", chat, null, p.Caption, p.ParseMode, p.ReplyParameters?.MessageId, null, file, true));
                var message = Sent(chat, p.ReplyParameters?.MessageId);
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
