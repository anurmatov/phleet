using System.Net;
using System.Text;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Interfaces;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
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
/// End-to-end tests for <c>AgentTransport.OnMessage</c> media handling (issue #261).
///
/// These are the tests that actually prove the acceptance criteria: a direct and a
/// forwarded message of every supported family must reach the agent with an existing
/// local path, photos and photo media groups must be untouched, and voice must keep its
/// file-backed path whether transcription succeeds, fails, or is disabled — without
/// downloading the same file twice.
/// </summary>
public class TelegramMediaTransportTests
{
    // ── Harness ───────────────────────────────────────────────────────────────

    private sealed class Harness : IDisposable
    {
        public required AgentTransport Transport { get; init; }
        public required MediaFakeBot Bot { get; init; }
        public required CountingDownloader Downloader { get; init; }
        public required string AttachmentDir { get; init; }
        public readonly List<IncomingMessage> Captured = [];

        public void Dispose()
        {
            try { Directory.Delete(AttachmentDir, recursive: true); }
            catch { /* best effort */ }
        }
    }

    private static Harness BuildHarness(
        bool persistAttachments = true,
        string whisperUrl = "",
        HttpStatusCode whisperStatus = HttpStatusCode.OK,
        string whisperBody = "{\"text\":\"transcribed words\"}",
        byte[]? fileBytes = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"fleet-media-tx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        var agentOpts = Options.Create(new AgentOptions
        {
            Name = "fleet-agent1",
            Role = "generic-role",
            WorkDir = "/tmp/fleet-test",
            ShortName = "agent1",
        });
        var telegramOpts = Options.Create(new TelegramOptions
        {
            PersistAttachments = persistAttachments,
            AttachmentDir = dir,
            MaxDocumentBytes = 1024 * 1024,
            MaxImageBytes = 1024 * 1024,
            AttachmentRetentionHours = 48,
        });
        var rabbitOpts = Options.Create(new RabbitMqOptions());
        var whisperOpts = Options.Create(new WhisperOptions { ServiceUrl = whisperUrl });
        var ttsOpts = Options.Create(new TtsOptions());

        var executor = Substitute.For<IAgentExecutor>();
        var sessions = new SessionManager();
        var connState = Substitute.For<IFleetConnectionState>();

        var httpFact = Substitute.For<IHttpClientFactory>();
        httpFact.CreateClient(Arg.Any<string>())
            .Returns(_ => new HttpClient(new StubWhisperHandler(whisperStatus, whisperBody)));

        var allowlist = new AllowlistHolder(telegramOpts);
        var relay = new GroupRelayService(agentOpts, rabbitOpts, NullLogger<GroupRelayService>.Instance);
        var taskMgr = new TaskManager(agentOpts, executor, sessions, NullLogger<TaskManager>.Instance);
        var prompts = new PromptAssembler(executor);
        var cmdDisp = new CommandDispatcher(taskMgr, executor, agentOpts, NullLogger<CommandDispatcher>.Instance);
        var voice = new VoiceTranscriptionService(httpFact, whisperOpts, NullLogger<VoiceTranscriptionService>.Instance);
        var tts = new TtsService(httpFact, ttsOpts, NullLogger<TtsService>.Instance);
        var groupBhvr = new GroupBehavior(agentOpts, telegramOpts, allowlist, executor, relay, taskMgr, cmdDisp, prompts, NullLogger<GroupBehavior>.Instance);
        var router = new MessageRouter(agentOpts, telegramOpts, allowlist, taskMgr, groupBhvr, relay, cmdDisp, NullLogger<MessageRouter>.Instance);

        var transport = new AgentTransport(
            agentOpts, telegramOpts, allowlist, relay, taskMgr,
            groupBhvr, router, cmdDisp, voice, tts, connState,
            NullLogger<AgentTransport>.Instance);

        var bot = new MediaFakeBot { FileBytes = fileBytes ?? [0x01, 0x02, 0x03] };
        transport.BotForTesting = bot;

        var downloader = new CountingDownloader(fileBytes ?? [0x01, 0x02, 0x03]);
        transport.DownloadHelper = new DocumentDownloadHelper(
            downloader, telegramOpts.Value, (_, _) => Task.CompletedTask, NullLogger.Instance);

        var harness = new Harness
        {
            Transport = transport,
            Bot = bot,
            Downloader = downloader,
            AttachmentDir = dir,
        };
        transport.RouterHookForTesting = msg => { harness.Captured.Add(msg); return Task.CompletedTask; };
        return harness;
    }

    private static Task DeliverAsync(Harness h, Message msg)
        => h.Transport.OnMessage(msg, UpdateType.Message);

    // ── Every family, direct and forwarded, reaches the agent with a local path ─

    public static TheoryData<string, bool> FamiliesDirectAndForwarded()
    {
        var data = new TheoryData<string, bool>();
        foreach (var family in new[] { "video", "videoNote", "audio", "voice", "animation", "stickerStatic", "stickerAnimated", "stickerVideo", "document" })
        {
            data.Add(family, false);
            data.Add(family, true);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(FamiliesDirectAndForwarded))]
    public async Task EveryFamily_DirectOrForwarded_DeliversExistingLocalPath(string family, bool forwarded)
    {
        using var h = BuildHarness();

        await DeliverAsync(h, BuildFamily(family, forwarded));

        var msg = Assert.Single(h.Captured);
        Assert.True(msg.HasMediaAttachment, "file-backed media must bypass the group mention gate");
        var doc = Assert.Single(msg.Documents);
        Assert.NotNull(doc.FilePath);
        Assert.True(File.Exists(doc.FilePath), $"{family} should have been persisted to {doc.FilePath}");
        // The agent is told where the file is, so no resend-as-document workaround is needed.
        Assert.Contains(doc.FilePath!, msg.Text);
        Assert.Contains(doc.FilePath!, msg.StrippedText);
    }

    [Theory]
    [InlineData("video")]
    [InlineData("videoNote")]
    [InlineData("audio")]
    [InlineData("voice")]
    [InlineData("animation")]
    [InlineData("stickerStatic")]
    [InlineData("stickerAnimated")]
    [InlineData("stickerVideo")]
    [InlineData("document")]
    public async Task ForwardedResultIsIdenticalToDirectResult(string family)
    {
        using var direct = BuildHarness();
        using var fwd = BuildHarness();

        await DeliverAsync(direct, BuildFamily(family, forwarded: false));
        await DeliverAsync(fwd, BuildFamily(family, forwarded: true));

        var a = Assert.Single(direct.Captured);
        var b = Assert.Single(fwd.Captured);

        // Paths differ only by the temp attachment dir; everything the agent reasons about matches.
        Assert.Equal(Path.GetFileName(a.Documents[0].FilePath), Path.GetFileName(b.Documents[0].FilePath));
        Assert.Equal(a.Documents[0].MimeType, b.Documents[0].MimeType);
        Assert.Equal(a.HasMediaAttachment, b.HasMediaAttachment);
        Assert.Equal(
            a.Text.Replace(direct.AttachmentDir, "<dir>"),
            b.Text.Replace(fwd.AttachmentDir, "<dir>"));
    }

    // ── Captions and placeholders ─────────────────────────────────────────────

    [Fact]
    public async Task MediaWithoutCaption_GetsReadablePlaceholder()
    {
        using var h = BuildHarness();
        await DeliverAsync(h, BuildFamily("video", forwarded: false));

        var msg = Assert.Single(h.Captured);
        Assert.StartsWith("(video message)", msg.Text);
    }

    [Fact]
    public async Task MediaWithCaption_PreservesCaptionAndAppendsHint()
    {
        using var h = BuildHarness();
        var video = BuildFamily("video", forwarded: false);
        video.Caption = "look at this";

        await DeliverAsync(h, video);

        var msg = Assert.Single(h.Captured);
        Assert.StartsWith("look at this\n", msg.Text);
        Assert.Contains(msg.Documents[0].FilePath!, msg.Text);
    }

    [Fact]
    public async Task Sticker_PlaceholderIncludesEmoji()
    {
        using var h = BuildHarness();
        await DeliverAsync(h, BuildFamily("stickerStatic", forwarded: false));

        var msg = Assert.Single(h.Captured);
        Assert.StartsWith("(sticker 😀)", msg.Text);
    }

    // ── Photos and photo media groups are untouched (AC3) ────────────────────

    [Fact]
    public async Task SinglePhoto_StillDeliversImageWithJpgPathAndImageHint()
    {
        using var h = BuildHarness();
        var photo = TelegramMediaAttachmentTests.WithPhoto();

        await DeliverAsync(h, photo);

        var msg = Assert.Single(h.Captured);
        var image = Assert.Single(msg.Images);
        Assert.NotNull(image.FilePath);
        Assert.EndsWith(".jpg", image.FilePath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(image.FilePath));
        Assert.Empty(msg.Documents);
        Assert.Equal($"[image attachment: {image.FilePath}]", msg.Text);
        // Photos travel their own path — the shared file pipeline must not have been used.
        Assert.Equal(0, h.Downloader.CallCount);
    }

    [Fact]
    public async Task PhotoMediaGroup_StillFlushesAsOneMessageWithAllImages()
    {
        using var h = BuildHarness();

        await DeliverAsync(h, TelegramMediaAttachmentTests.WithPhoto(messageId: 1, mediaGroupId: "grp1"));
        await DeliverAsync(h, TelegramMediaAttachmentTests.WithPhoto(messageId: 2, mediaGroupId: "grp1"));

        // MediaGroupBuffer debounces 1500 ms after the last photo.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (h.Captured.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(100);

        var msg = Assert.Single(h.Captured);
        Assert.Equal(2, msg.Images.Count);
        Assert.All(msg.Images, i => Assert.True(File.Exists(i.FilePath)));
        Assert.Empty(msg.Documents);
    }

    // ── Voice: file-backed path survives every transcription outcome (AC4) ────

    [Fact]
    public async Task Voice_TranscriptionSucceeds_KeepsFileAndMarksTranscription()
    {
        using var h = BuildHarness(whisperUrl: "http://whisper.test");

        await DeliverAsync(h, BuildFamily("voice", forwarded: false));

        var msg = Assert.Single(h.Captured);
        Assert.StartsWith("transcribed words", msg.Text);
        Assert.Equal(MessageInputSource.VoiceTranscription, msg.InputSource);
        var doc = Assert.Single(msg.Documents);
        Assert.EndsWith(".oga", doc.FilePath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(doc.FilePath));
        Assert.Contains(doc.FilePath!, msg.Text);
        AssertSingleDownload(h);
    }

    [Fact]
    public async Task Voice_TranscriptionDisabled_StillDeliversFileAndPlaceholder()
    {
        using var h = BuildHarness(whisperUrl: "");

        await DeliverAsync(h, BuildFamily("voice", forwarded: false));

        var msg = Assert.Single(h.Captured);
        Assert.StartsWith("(voice message)", msg.Text);
        Assert.Equal(MessageInputSource.Typed, msg.InputSource);
        var doc = Assert.Single(msg.Documents);
        Assert.True(File.Exists(doc.FilePath));
        AssertSingleDownload(h);
    }

    [Fact]
    public async Task Voice_TranscriptionFails_MessageIsNotDropped()
    {
        // Before this change a failed transcription returned early and the whole
        // message — including the voice file — was silently discarded.
        using var h = BuildHarness(whisperUrl: "http://whisper.test", whisperStatus: HttpStatusCode.InternalServerError, whisperBody: "boom");

        await DeliverAsync(h, BuildFamily("voice", forwarded: false));

        var msg = Assert.Single(h.Captured);
        Assert.StartsWith("(voice message)", msg.Text);
        Assert.Equal(MessageInputSource.Typed, msg.InputSource);
        var doc = Assert.Single(msg.Documents);
        Assert.True(File.Exists(doc.FilePath));
        AssertSingleDownload(h);
    }

    [Fact]
    public async Task ForwardedVoice_BehavesLikeDirectVoice()
    {
        using var h = BuildHarness(whisperUrl: "http://whisper.test");

        await DeliverAsync(h, BuildFamily("voice", forwarded: true));

        var msg = Assert.Single(h.Captured);
        Assert.StartsWith("transcribed words", msg.Text);
        Assert.True(File.Exists(Assert.Single(msg.Documents).FilePath));
        AssertSingleDownload(h);
    }

    [Fact]
    public async Task Voice_PersistenceDisabled_TranscribesViaSingleDirectDownload()
    {
        using var h = BuildHarness(persistAttachments: false, whisperUrl: "http://whisper.test");

        await DeliverAsync(h, BuildFamily("voice", forwarded: false));

        var msg = Assert.Single(h.Captured);
        Assert.StartsWith("transcribed words", msg.Text);
        Assert.Empty(msg.Documents);
        // Kill switch on: the pipeline never downloads, so exactly one direct fetch happens.
        Assert.Equal(0, h.Downloader.CallCount);
        Assert.Equal(1, h.Bot.FileDownloadCount);
    }

    // The pipeline downloads once and transcription reuses those bytes —
    // the bot-level fetch must never fire as a second copy of the same file.
    private static void AssertSingleDownload(Harness h)
    {
        Assert.Equal(1, h.Downloader.CallCount);
        Assert.Equal(0, h.Bot.FileDownloadCount);
    }

    // ── Failure modes must not suppress the message ──────────────────────────

    [Fact]
    public async Task DownloadFailure_StillDeliversPlaceholderWithoutCrashing()
    {
        using var h = BuildHarness();
        h.Downloader.ThrowOnDownload = true;

        await DeliverAsync(h, BuildFamily("video", forwarded: false));

        var msg = Assert.Single(h.Captured);
        Assert.Equal("(video message)", msg.Text);
        Assert.Empty(msg.Documents);
        Assert.True(msg.HasMediaAttachment);
    }

    [Fact]
    public async Task PersistenceDisabled_StillDeliversPlaceholderWithoutAttachment()
    {
        using var h = BuildHarness(persistAttachments: false);

        await DeliverAsync(h, BuildFamily("audio", forwarded: false));

        var msg = Assert.Single(h.Captured);
        Assert.Equal("(audio message)", msg.Text);
        Assert.Empty(msg.Documents);
        Assert.True(msg.HasMediaAttachment);
    }

    // ── Non-file events are not attachments ──────────────────────────────────

    [Fact]
    public async Task LocationWithoutText_IsNotTreatedAsAttachment()
    {
        using var h = BuildHarness();
        var msg = TelegramMediaAttachmentTests.WithLocation();

        await DeliverAsync(h, msg);

        Assert.Empty(h.Captured);
        Assert.Equal(0, h.Downloader.CallCount);
    }

    [Fact]
    public async Task PlainTextMessage_IsUnaffected()
    {
        using var h = BuildHarness();
        var text = TelegramMediaAttachmentTests.WithText("hello there");

        await DeliverAsync(h, text);

        var msg = Assert.Single(h.Captured);
        Assert.Equal("hello there", msg.Text);
        Assert.False(msg.HasMediaAttachment);
        Assert.Empty(msg.Documents);
        Assert.Empty(msg.Images);
    }

    // ── shared builders ───────────────────────────────────────────────────────

    private static Message BuildFamily(string family, bool forwarded) => family switch
    {
        "video" => TelegramMediaAttachmentTests.WithVideo(forwarded),
        "videoNote" => TelegramMediaAttachmentTests.WithVideoNote(forwarded),
        "audio" => TelegramMediaAttachmentTests.WithAudio(forwarded),
        "voice" => TelegramMediaAttachmentTests.WithVoice(forwarded),
        "animation" => TelegramMediaAttachmentTests.WithAnimation(forwarded),
        "stickerStatic" => TelegramMediaAttachmentTests.WithSticker(forwarded),
        "stickerAnimated" => TelegramMediaAttachmentTests.WithSticker(forwarded, animated: true),
        "stickerVideo" => TelegramMediaAttachmentTests.WithSticker(forwarded, video: true),
        "document" => TelegramMediaAttachmentTests.WithDocument(forwarded),
        _ => throw new ArgumentOutOfRangeException(nameof(family), family, null),
    };

    // ── fakes ─────────────────────────────────────────────────────────────────

    internal sealed class CountingDownloader : IDocumentDownloader
    {
        private readonly byte[] _bytes;
        internal int CallCount { get; private set; }
        internal bool ThrowOnDownload { get; set; }
        internal CountingDownloader(byte[] bytes) => _bytes = bytes;

        public Task<byte[]> DownloadAsync(string fileId, CancellationToken ct)
        {
            CallCount++;
            if (ThrowOnDownload) throw new HttpRequestException("simulated download failure");
            return Task.FromResult(_bytes);
        }
    }

    private sealed class StubWhisperHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    /// <summary>
    /// Minimal <see cref="ITelegramBotClient"/> that answers the calls OnMessage makes:
    /// chat actions, outbound messages, and file info + download. Counts direct file
    /// downloads so tests can prove the same file is never fetched twice.
    /// </summary>
    internal sealed class MediaFakeBot : ITelegramBotClient
    {
        public readonly List<object> Requests = [];
        public byte[] FileBytes = [0x01];
        public int FileDownloadCount;

        public bool LocalBotServer => false;
        public long BotId => 1;
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
        public IExceptionParser ExceptionsParser { get; set; } = new DefaultExceptionParser();

#pragma warning disable CS0067
        public event AsyncEventHandler<ApiRequestEventArgs>? OnMakingApiRequest;
        public event AsyncEventHandler<ApiResponseEventArgs>? OnApiResponseReceived;
#pragma warning restore CS0067

        public Task<TResponse> MakeRequestAsync<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (request is SendMessageRequest smr)
            {
                var msg = new Message { Id = Requests.Count, Chat = new Chat { Id = smr.ChatId.Identifier ?? 0 } };
                return Task.FromResult((TResponse)(object)msg);
            }
            if (request is GetFileRequest)
            {
                var file = new global::Telegram.Bot.Types.TGFile { FileId = "f", FilePath = "files/f" };
                return Task.FromResult((TResponse)(object)file);
            }
            return Task.FromResult(default(TResponse)!);
        }

        public Task<TResponse> SendRequest<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
            => MakeRequestAsync(request, cancellationToken);

        public Task<TResponse> MakeRequest<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
            => MakeRequestAsync(request, cancellationToken);

        public Task<bool> TestApi(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task DownloadFile(string filePath, Stream destination, CancellationToken cancellationToken = default)
        {
            FileDownloadCount++;
            return destination.WriteAsync(FileBytes, 0, FileBytes.Length, cancellationToken);
        }

        public Task DownloadFile(global::Telegram.Bot.Types.TGFile file, Stream destination, CancellationToken cancellationToken = default)
            => DownloadFile(file.FilePath ?? "", destination, cancellationToken);
    }
}
