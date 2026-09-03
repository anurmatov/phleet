using System.Text;
using Fleet.Agent.Configuration;
using Fleet.Agent.Interfaces;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TgDocument = Telegram.Bot.Types.Document;

namespace Fleet.Agent.Tests;

/// <summary>
/// Tests for persisting every file-backed Telegram media type (issue #261).
///
/// Two layers:
/// - <see cref="TelegramMediaMapper"/> — how each Telegram type is normalised, and the
///   direct-vs-forwarded parity that makes forwarded media behave identically.
/// - <see cref="DocumentDownloadHelper"/> — the shared download / size-gate / persist /
///   safe-extension path all of them now travel through.
///
/// End-to-end <c>OnMessage</c> behaviour lives in <see cref="TelegramMediaTransportTests"/>.
/// </summary>
public class TelegramMediaAttachmentTests
{
    // ── Message builders ──────────────────────────────────────────────────────

    internal static Message WithVideo(bool forwarded = false) => Build(forwarded, m =>
        m.Video = new Video { FileId = "vid", FileName = "clip.mov", MimeType = "video/quicktime", FileSize = 2048 });

    internal static Message WithVideoNoFileName(bool forwarded = false) => Build(forwarded, m =>
        m.Video = new Video { FileId = "vid", FileSize = 2048 });

    internal static Message WithVideoNote(bool forwarded = false) => Build(forwarded, m =>
        m.VideoNote = new VideoNote { FileId = "vnote", FileSize = 1024 });

    internal static Message WithAudio(bool forwarded = false) => Build(forwarded, m =>
        m.Audio = new Audio { FileId = "aud", FileName = "song.m4a", MimeType = "audio/mp4", FileSize = 4096 });

    internal static Message WithVoice(bool forwarded = false) => Build(forwarded, m =>
        m.Voice = new Voice { FileId = "voi", MimeType = "audio/ogg", FileSize = 512 });

    internal static Message WithAnimation(bool forwarded = false) => Build(forwarded, m =>
    {
        m.Animation = new Animation { FileId = "ani", FileName = "loop.mp4", MimeType = "video/mp4", FileSize = 3072 };
        // The Bot API sets `document` alongside `animation` for backward compatibility.
        m.Document = new TgDocument { FileId = "ani", FileName = "loop.mp4", MimeType = "video/mp4", FileSize = 3072 };
    });

    internal static Message WithSticker(bool forwarded = false, bool animated = false, bool video = false, string? emoji = "😀")
        => Build(forwarded, m => m.Sticker = new Sticker
        {
            FileId = "stk",
            FileSize = 256,
            IsAnimated = animated,
            IsVideo = video,
            Emoji = emoji,
        });

    internal static Message WithDocument(bool forwarded = false) => Build(forwarded, m =>
        m.Document = new TgDocument { FileId = "doc", FileName = "report.pdf", MimeType = "application/pdf", FileSize = 999 });

    internal static Message WithPhoto(int messageId = 77, string? mediaGroupId = null) => Build(false, m =>
    {
        m.Photo = [new PhotoSize { FileId = $"photo-{messageId}", FileSize = 128, Width = 100, Height = 100 }];
        m.MediaGroupId = mediaGroupId;
    }, messageId);

    internal static Message WithLocation() => Build(false, m =>
        m.Location = new Location { Latitude = 42.87, Longitude = 74.59 });

    internal static Message WithText(string text) => Build(false, m => m.Text = text);

    private static Message Build(bool forwarded, Action<Message> configure, int messageId = 77)
    {
        var msg = new Message
        {
            Id = messageId,
            Chat = new Chat { Id = 42, Type = ChatType.Private },
            From = new User { Id = 7, Username = "someone" },
        };
        configure(msg);
        if (forwarded)
            msg.ForwardOrigin = new MessageOriginUser
            {
                Date = new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc),
                SenderUser = new User { Id = 999, Username = "originalsender" },
            };
        return msg;
    }

    // ── Direct vs forwarded parity (AC1 / AC2) ────────────────────────────────

    public static TheoryData<string> MediaFamilies() =>
        ["video", "videoNote", "audio", "voice", "animation", "stickerStatic", "stickerAnimated", "stickerVideo", "document"];

    private static Message BuildFamily(string family, bool forwarded) => family switch
    {
        "video" => WithVideo(forwarded),
        "videoNote" => WithVideoNote(forwarded),
        "audio" => WithAudio(forwarded),
        "voice" => WithVoice(forwarded),
        "animation" => WithAnimation(forwarded),
        "stickerStatic" => WithSticker(forwarded),
        "stickerAnimated" => WithSticker(forwarded, animated: true),
        "stickerVideo" => WithSticker(forwarded, video: true),
        "document" => WithDocument(forwarded),
        _ => throw new ArgumentOutOfRangeException(nameof(family), family, null),
    };

    [Theory]
    [MemberData(nameof(MediaFamilies))]
    public void TryMap_ForwardedProducesIdenticalDescriptorToDirect(string family)
    {
        var direct = TelegramMediaMapper.TryMap(BuildFamily(family, forwarded: false));
        var forwarded = TelegramMediaMapper.TryMap(BuildFamily(family, forwarded: true));

        Assert.NotNull(direct);
        Assert.NotNull(forwarded);
        // Records compare by value: identical descriptor ⇒ identical downstream persistence.
        Assert.Equal(direct, forwarded);
    }

    [Theory]
    [MemberData(nameof(MediaFamilies))]
    public void TryMap_ForwardedMessageIsAlwaysRecognisedAsFileBacked(string family)
    {
        Assert.NotNull(TelegramMediaMapper.TryMap(BuildFamily(family, forwarded: true)));
    }

    // ── Per-family descriptor shape ───────────────────────────────────────────

    [Fact]
    public void TryMap_Video_UsesFileNameExtension()
    {
        var media = TelegramMediaMapper.TryMap(WithVideo())!;
        Assert.Equal(TelegramMediaKind.Video, media.Kind);
        Assert.Equal(".mov", AgentTransport.ExtractSafeExtension(media.FileName, media.DefaultExtension));
    }

    [Fact]
    public void TryMap_VideoWithoutFileName_FallsBackToMp4()
    {
        var media = TelegramMediaMapper.TryMap(WithVideoNoFileName())!;
        Assert.Equal(".mp4", AgentTransport.ExtractSafeExtension(media.FileName, media.DefaultExtension));
    }

    [Fact]
    public void TryMap_VideoNote_HasNoFileNameAndFallsBackToMp4()
    {
        var media = TelegramMediaMapper.TryMap(WithVideoNote())!;
        Assert.Equal(TelegramMediaKind.VideoNote, media.Kind);
        Assert.Null(media.FileName);
        Assert.Equal(".mp4", AgentTransport.ExtractSafeExtension(media.FileName, media.DefaultExtension));
    }

    [Fact]
    public void TryMap_Voice_HasNoFileNameAndFallsBackToOga()
    {
        var media = TelegramMediaMapper.TryMap(WithVoice())!;
        Assert.Equal(TelegramMediaKind.Voice, media.Kind);
        Assert.Null(media.FileName);
        Assert.Equal(".oga", AgentTransport.ExtractSafeExtension(media.FileName, media.DefaultExtension));
    }

    [Theory]
    [InlineData(false, false, ".webp")]
    [InlineData(true, false, ".tgs")]
    [InlineData(false, true, ".webm")]
    public void TryMap_Sticker_ExtensionMatchesStickerFormat(bool animated, bool video, string expectedExt)
    {
        var media = TelegramMediaMapper.TryMap(WithSticker(animated: animated, video: video))!;
        Assert.Equal(TelegramMediaKind.Sticker, media.Kind);
        Assert.Equal(expectedExt, AgentTransport.ExtractSafeExtension(media.FileName, media.DefaultExtension));
    }

    [Fact]
    public void TryMap_Animation_WinsOverBackwardCompatDocumentField()
    {
        // Telegram sets `document` alongside `animation`; checking Document first would
        // misclassify every GIF, so the ordering in the mapper is load-bearing.
        var media = TelegramMediaMapper.TryMap(WithAnimation())!;
        Assert.Equal(TelegramMediaKind.Animation, media.Kind);
    }

    [Fact]
    public void TryMap_Document_UnchangedShape()
    {
        var media = TelegramMediaMapper.TryMap(WithDocument())!;
        Assert.Equal(TelegramMediaKind.Document, media.Kind);
        Assert.Equal("report.pdf", media.FileName);
        Assert.Equal("application/pdf", media.MimeType);
        Assert.Equal(".bin", media.DefaultExtension); // preserves the pre-existing fallback
    }

    // ── Types that must NOT be treated as attachments ────────────────────────

    [Fact]
    public void TryMap_TextOnly_ReturnsNull()
        => Assert.Null(TelegramMediaMapper.TryMap(Build(false, m => m.Text = "hello")));

    [Fact]
    public void TryMap_Photo_ReturnsNull_PhotoKeepsItsOwnPath()
    {
        // Photos are excluded on purpose — DownloadPhotoAsync also retains bytes for vision blocks.
        var msg = Build(false, m => m.Photo = [new PhotoSize { FileId = "p", FileSize = 10 }]);
        Assert.Null(TelegramMediaMapper.TryMap(msg));
    }

    [Fact]
    public void TryMap_Location_ReturnsNull()
        => Assert.Null(TelegramMediaMapper.TryMap(Build(false, m => m.Location = new Location { Latitude = 1, Longitude = 2 })));

    [Fact]
    public void TryMap_Contact_ReturnsNull()
        => Assert.Null(TelegramMediaMapper.TryMap(Build(false, m => m.Contact = new Contact { PhoneNumber = "1", FirstName = "a" })));

    [Fact]
    public void TryMap_Poll_ReturnsNull()
        => Assert.Null(TelegramMediaMapper.TryMap(Build(false, m => m.Poll = new Poll { Id = "1", Question = "q?" })));

    [Fact]
    public void TryMap_Venue_ReturnsNull()
        => Assert.Null(TelegramMediaMapper.TryMap(Build(false, m => m.Venue = new Venue
        {
            Location = new Location { Latitude = 1, Longitude = 2 },
            Title = "t",
            Address = "a",
        })));

    // ── Placeholder text ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("video", "(video message)")]
    [InlineData("videoNote", "(video note)")]
    [InlineData("audio", "(audio message)")]
    [InlineData("voice", "(voice message)")]
    [InlineData("animation", "(animation)")]
    public void DescribePlaceholder_PerFamily(string family, string expected)
    {
        var media = TelegramMediaMapper.TryMap(BuildFamily(family, forwarded: false))!;
        Assert.Equal(expected, TelegramMediaMapper.DescribePlaceholder(media));
    }

    [Fact]
    public void DescribePlaceholder_Sticker_IncludesEmojiWhenPresent()
    {
        var media = TelegramMediaMapper.TryMap(WithSticker())!;
        Assert.Equal("(sticker 😀)", TelegramMediaMapper.DescribePlaceholder(media, "😀"));
    }

    [Fact]
    public void DescribePlaceholder_Sticker_WithoutEmoji()
    {
        var media = TelegramMediaMapper.TryMap(WithSticker(emoji: null))!;
        Assert.Equal("(sticker)", TelegramMediaMapper.DescribePlaceholder(media, null));
    }

    [Fact]
    public void DescribePlaceholder_Document_KeepsExistingWording()
    {
        var media = TelegramMediaMapper.TryMap(WithDocument())!;
        Assert.Equal("(document: report.pdf)", TelegramMediaMapper.DescribePlaceholder(media));
    }

    [Fact]
    public void DescribePlaceholder_DocumentWithoutFileName_KeepsExistingWording()
    {
        var msg = Build(false, m => m.Document = new TgDocument { FileId = "d", FileSize = 1 });
        var media = TelegramMediaMapper.TryMap(msg)!;
        Assert.Equal("(document)", TelegramMediaMapper.DescribePlaceholder(media));
    }

    // ── Shared download / persist path ────────────────────────────────────────

    [Theory]
    [InlineData("video", ".mov", "[file attachment:")]
    [InlineData("videoNote", ".mp4", "[file attachment:")]
    [InlineData("audio", ".m4a", "[file attachment:")]
    [InlineData("voice", ".oga", "[file attachment:")]
    [InlineData("animation", ".mp4", "[file attachment:")]
    [InlineData("stickerStatic", ".webp", "[file attachment:")]
    [InlineData("stickerAnimated", ".tgs", "[file attachment:")]
    [InlineData("stickerVideo", ".webm", "[file attachment:")]
    [InlineData("document", ".pdf", "[document attachment:")]
    public async Task DownloadMediaAsync_PersistsWithExpectedExtensionAndHint(string family, string expectedExt, string expectedHintPrefix)
    {
        await WithTempDirAsync(async dir =>
        {
            var bytes = Encoding.UTF8.GetBytes("payload");
            var helper = BuildHelper(dir, new StubDownloader(bytes), out _);
            var media = TelegramMediaMapper.TryMap(BuildFamily(family, forwarded: false))!;

            var result = await helper.DownloadMediaAsync(media, chatId: 42, messageId: 77, index: 1);

            Assert.NotNull(result);
            Assert.NotNull(result!.Document.FilePath);
            Assert.EndsWith(expectedExt, result.Document.FilePath, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(result.Document.FilePath));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(result.Document.FilePath!));
            Assert.Equal(bytes, result.Bytes);

            var hint = AttachmentSweeper.BuildHints([], [result.Document]);
            Assert.StartsWith(expectedHintPrefix, hint);
            Assert.Contains(result.Document.FilePath!, hint);
        });
    }

    [Theory]
    [MemberData(nameof(MediaFamilies))]
    public async Task DownloadMediaAsync_ForwardedPersistsSameFileAsDirect(string family)
    {
        await WithTempDirAsync(async dir =>
        {
            var helper = BuildHelper(dir, new StubDownloader([1, 2, 3]), out _);

            var direct = await helper.DownloadMediaAsync(
                TelegramMediaMapper.TryMap(BuildFamily(family, forwarded: false))!, 42, 77, 1);
            var forwarded = await helper.DownloadMediaAsync(
                TelegramMediaMapper.TryMap(BuildFamily(family, forwarded: true))!, 42, 78, 1);

            Assert.NotNull(direct);
            Assert.NotNull(forwarded);
            Assert.Equal(Path.GetExtension(direct!.Document.FilePath), Path.GetExtension(forwarded!.Document.FilePath));
            Assert.Equal(direct.Document.MimeType, forwarded.Document.MimeType);
            Assert.Equal(direct.Bytes, forwarded.Bytes);
            Assert.True(File.Exists(forwarded.Document.FilePath));
        });
    }

    [Fact]
    public async Task DownloadMediaAsync_MissingFileMetadata_StillPersistsWithSafeFallback()
    {
        // Video note: no filename, no MIME, no size reported by Telegram.
        await WithTempDirAsync(async dir =>
        {
            var helper = BuildHelper(dir, new StubDownloader([9]), out _);
            var media = new TelegramMediaFile(TelegramMediaKind.VideoNote, "id", null, null, 0, ".mp4");

            var result = await helper.DownloadMediaAsync(media, 1, 1, 1);

            Assert.NotNull(result);
            Assert.EndsWith(".mp4", result!.Document.FilePath, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("video/mp4", result.Document.MimeType);
        });
    }

    [Fact]
    public async Task DownloadMediaAsync_MaliciousFileName_CannotEscapeAttachmentDir()
    {
        await WithTempDirAsync(async dir =>
        {
            var helper = BuildHelper(dir, new StubDownloader([1]), out _);
            var media = new TelegramMediaFile(
                TelegramMediaKind.Video, "id", "../../../etc/evil.mp4", "video/mp4", 1, ".mp4");

            var result = await helper.DownloadMediaAsync(media, 1, 1, 1);

            Assert.NotNull(result);
            Assert.Equal(dir, Path.GetDirectoryName(result!.Document.FilePath));
        });
    }

    [Fact]
    public async Task DownloadMediaAsync_Oversize_ReturnsNullAndWarnsUser()
    {
        var config = new TelegramOptions
        {
            PersistAttachments = true,
            AttachmentDir = Path.GetTempPath(),
            MaxDocumentBytes = 100,
        };
        var sent = new List<string>();
        var helper = new DocumentDownloadHelper(
            new StubDownloader(new byte[101]), config, (_, m) => { sent.Add(m); return Task.CompletedTask; }, NullLogger.Instance);

        var media = new TelegramMediaFile(TelegramMediaKind.Video, "id", "big.mp4", "video/mp4", 1_000, ".mp4");
        var result = await helper.DownloadMediaAsync(media, 1, 1, 1);

        Assert.Null(result);
        Assert.Single(sent);
        Assert.Contains("too large", sent[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DownloadMediaAsync_DownloadThrows_ReturnsNullAndWarnsWithoutCrashing()
    {
        var config = new TelegramOptions
        {
            PersistAttachments = true,
            AttachmentDir = Path.GetTempPath(),
            MaxDocumentBytes = 1024,
        };
        var sent = new List<string>();
        var helper = new DocumentDownloadHelper(
            new ThrowingDownloader(), config, (_, m) => { sent.Add(m); return Task.CompletedTask; }, NullLogger.Instance);

        var media = new TelegramMediaFile(TelegramMediaKind.Audio, "id", "a.mp3", "audio/mpeg", 10, ".mp3");
        var result = await helper.DownloadMediaAsync(media, 1, 1, 1);

        Assert.Null(result);
        Assert.Single(sent);
        Assert.Contains("download failed", sent[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DownloadMediaAsync_PersistenceDisabled_ReturnsNullWithoutDownloading()
    {
        var config = new TelegramOptions { PersistAttachments = false, AttachmentDir = Path.GetTempPath() };
        var downloader = new StubDownloader([1]);
        var helper = new DocumentDownloadHelper(downloader, config, (_, _) => Task.CompletedTask, NullLogger.Instance);

        var media = new TelegramMediaFile(TelegramMediaKind.Video, "id", "v.mp4", "video/mp4", 1, ".mp4");

        Assert.Null(await helper.DownloadMediaAsync(media, 1, 1, 1));
        Assert.Equal(0, downloader.CallCount);
    }

    [Fact]
    public async Task DownloadMediaAsync_IssuesExactlyOneDownloadPerCall()
    {
        await WithTempDirAsync(async dir =>
        {
            var helper = BuildHelper(dir, new StubDownloader([1]), out var downloader);
            var media = TelegramMediaMapper.TryMap(WithVoice())!;

            await helper.DownloadMediaAsync(media, 1, 1, 1);

            Assert.Equal(1, downloader.CallCount);
        });
    }

    [Fact]
    public async Task DownloadMediaAsync_RetentionSweepStillRuns()
    {
        await WithTempDirAsync(async dir =>
        {
            var stale = Path.Combine(dir, "stale.mp4");
            await File.WriteAllTextAsync(stale, "old");
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - TimeSpan.FromHours(72));

            var helper = BuildHelper(dir, new StubDownloader([1]), out _);
            await helper.DownloadMediaAsync(TelegramMediaMapper.TryMap(WithVideo())!, 1, 1, 1);

            Assert.False(File.Exists(stale), "expired attachment should have been swept");
        });
    }

    // ── MIME inference for the new families ──────────────────────────────────

    [Theory]
    [InlineData(".mp4", "video/mp4")]
    [InlineData(".webm", "video/webm")]
    [InlineData(".mov", "video/quicktime")]
    [InlineData(".mp3", "audio/mpeg")]
    [InlineData(".m4a", "audio/mp4")]
    [InlineData(".oga", "audio/ogg")]
    [InlineData(".ogg", "audio/ogg")]
    [InlineData(".wav", "audio/wav")]
    [InlineData(".webp", "image/webp")]
    [InlineData(".gif", "image/gif")]
    [InlineData(".tgs", "application/gzip")]
    public void InferMimeType_NewMediaExtensions(string ext, string expected)
        => Assert.Equal(expected, AgentTransport.InferMimeType(ext));

    [Theory]
    [InlineData(".mp4")]
    [InlineData(".oga")]
    [InlineData(".webp")]
    [InlineData(".tgs")]
    public void InferMimeType_NewExtensionsNeverResolveToPdf(string ext)
        => Assert.NotEqual("application/pdf", AgentTransport.InferMimeType(ext));

    // ── ExtractSafeExtension custom fallback ─────────────────────────────────

    [Fact]
    public void ExtractSafeExtension_CustomDefault_UsedWhenNoFileName()
        => Assert.Equal(".oga", AgentTransport.ExtractSafeExtension(null, ".oga"));

    [Fact]
    public void ExtractSafeExtension_CustomDefault_IgnoredWhenFileNameHasExtension()
        => Assert.Equal(".mov", AgentTransport.ExtractSafeExtension("clip.mov", ".mp4"));

    [Fact]
    public void ExtractSafeExtension_CustomDefault_SurvivesTraversalAttempt()
        => Assert.Equal(".webp", AgentTransport.ExtractSafeExtension("..\\..\\etc\\passwd", ".webp"));

    [Fact]
    public void ExtractSafeExtension_DefaultOverloadStillFallsBackToBin()
        => Assert.Equal(".bin", AgentTransport.ExtractSafeExtension(null));

    // ── helpers ───────────────────────────────────────────────────────────────

    private static DocumentDownloadHelper BuildHelper(string dir, StubDownloader downloader, out StubDownloader captured)
    {
        captured = downloader;
        var config = new TelegramOptions
        {
            PersistAttachments = true,
            AttachmentDir = dir,
            MaxDocumentBytes = 1024 * 1024,
            AttachmentRetentionHours = 48,
        };
        return new DocumentDownloadHelper(downloader, config, (_, _) => Task.CompletedTask, NullLogger.Instance);
    }

    private static async Task WithTempDirAsync(Func<string, Task> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"fleet-media-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try { await body(dir); }
        finally { Directory.Delete(dir, recursive: true); }
    }

    internal sealed class StubDownloader : IDocumentDownloader
    {
        private readonly byte[] _bytes;
        internal int CallCount { get; private set; }
        internal StubDownloader(byte[] bytes) => _bytes = bytes;
        public Task<byte[]> DownloadAsync(string fileId, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(_bytes);
        }
    }

    private sealed class ThrowingDownloader : IDocumentDownloader
    {
        public Task<byte[]> DownloadAsync(string fileId, CancellationToken ct)
            => throw new HttpRequestException("simulated network failure");
    }
}
