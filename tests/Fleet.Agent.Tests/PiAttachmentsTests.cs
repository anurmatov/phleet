using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Fleet.Agent.Tests.Harness;
using Fleet.Shared;

namespace Fleet.Agent.Tests;

/// <summary>
/// #455 §3.2 and A4 with a fake ffmpeg: magic bytes over the label, header-only dimensions, the
/// decode bound, the cap before any downscale, pass-through boundaries, the downscale arguments and
/// output rules, the reason order and the notes. The real ffmpeg rows are in
/// <c>PiAttachmentsRealToolTests</c>.
/// </summary>
public sealed class PiAttachmentsTests
{
    private const int Mib3 = PiLocalModel.MaxImageBytes;

    private static (PiAttachments Attachments, FakePiLocalTools Tools) Create(FakePiLocalTools? tools = null)
    {
        tools ??= new FakePiLocalTools();
        var logger = new PiTestLogger();
        return (new PiAttachments(tools, new PiPdfExtractor(tools, logger), logger), tools);
    }

    private static MessageImage Image(byte[] bytes, string? path = "/workspace/example/a.jpg", string label = "image/jpeg") =>
        new(bytes, label) { FilePath = path };

    private static Task<PiPreparedCommand> PrepareAsync(PiAttachments attachments, params MessageImage[] images) =>
        attachments.PrepareAsync("task", images, null, imageInput: true, TimeSpan.FromSeconds(60), CancellationToken.None);

    // ── Step 1 and 2 ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("png", "image/png", 640, 480)]
    [InlineData("jpeg", "image/jpeg", 2560, 1920)]
    [InlineData("gif", "image/gif", 300, 200)]
    [InlineData("vp8", "image/webp", 640, 480)]
    [InlineData("vp8l", "image/webp", 1000, 700)]
    [InlineData("vp8x", "image/webp", 4000, 3000)]
    public void SniffAndReadDimensions_ReadTheHeaderOnly(string kind, string mime, int width, int height)
    {
        var bytes = kind switch
        {
            "png" => TestImages.Png(width, height),
            "jpeg" => TestImages.Jpeg(width, height),
            "gif" => TestImages.Gif(width, height),
            "vp8" => TestImages.WebpVp8(width, height),
            "vp8l" => TestImages.WebpVp8L(width, height),
            _ => TestImages.WebpVp8X(width, height),
        };

        Assert.Equal(mime, PiAttachments.Sniff(bytes));
        Assert.Equal((width, height), PiAttachments.ReadDimensions(bytes, mime));
    }

    [Fact]
    public async Task TheSniffedType_WinsOverTheTransportLabel()
    {
        var (attachments, _) = Create();
        var png = TestImages.Png(100, 100);

        var prepared = await PrepareAsync(attachments, Image(png, label: "image/jpeg"));

        Assert.Equal("image/png", Assert.Single(prepared.Images).MimeType);
        Assert.Same(png, prepared.Images[0].Bytes);
    }

    [Fact]
    public async Task UnknownMagicBytes_AreAnUnsupportedFormat_AndABrokenHeaderIsUnreadable()
    {
        var (attachments, tools) = Create();
        var broken = TestImages.Png(10, 10);
        "XXXX"u8.CopyTo(broken.AsSpan(12));

        var prepared = await PrepareAsync(attachments, Image([1, 2, 3, 4, 5, 6, 7, 8]), Image(broken));

        Assert.Empty(prepared.Images);
        Assert.Empty(tools.Calls);
        Assert.Equal("task\n\n[2 image attachment(s) omitted: 1 unsupported format, 1 unreadable]", prepared.Message);
    }

    // ── Step 3: decode bound ────────────────────────────────────────────────

    [Theory]
    [InlineData(12_001, 10)]
    [InlineData(10, 12_001)]
    [InlineData(12_000, 4_167)] // 50 004 000 pixels
    public async Task OverTheDecodeBound_IsTooLargeToProcess_AndNeverReachesADecoder(int width, int height)
    {
        var (attachments, tools) = Create();

        var prepared = await PrepareAsync(attachments, Image(TestImages.Png(width, height)));

        Assert.Empty(prepared.Images);
        Assert.Empty(tools.Calls);
        Assert.Contains("1 too large to process", prepared.Message);
    }

    [Fact]
    public async Task JustInsideTheDecodeBound_IsDownscaled()
    {
        var (attachments, tools) = Create();

        var prepared = await PrepareAsync(attachments, Image(TestImages.Png(12_000, 4_166)));

        Assert.Single(prepared.Images);
        Assert.Equal(1, tools.Count(PiAttachments.FfmpegBinary));
    }

    // ── Step 5: pass-through boundaries ─────────────────────────────────────

    [Fact]
    public async Task AtTheBounds_TheImagePassesByteForByte_WithNoProcessing()
    {
        var (attachments, tools) = Create();
        var edge = TestImages.Jpeg(2000, 2000, Mib3);

        var prepared = await PrepareAsync(attachments, Image(edge));

        Assert.Same(edge, Assert.Single(prepared.Images).Bytes);
        Assert.Empty(tools.Calls);
        Assert.Equal("task", prepared.Message);
    }

    [Theory]
    [InlineData(2001, 2000, 1000)]
    [InlineData(2000, 2001, 1000)]
    [InlineData(2000, 2000, Mib3 + 1)]
    public async Task OnePixelOrOneByteOver_IsDownscaled_WithTheResizedNote(int width, int height, int size)
    {
        var (attachments, tools) = Create();

        var prepared = await PrepareAsync(attachments, Image(TestImages.Jpeg(width, height, size)));

        Assert.Single(prepared.Images);
        Assert.Equal(1, tools.Count(PiAttachments.FfmpegBinary));
        Assert.Equal("task\n\n[1 image(s) resized to fit 2000 px]", prepared.Message);
    }

    // ── Step 4: the cap ─────────────────────────────────────────────────────

    [Fact]
    public async Task ThreeImages_TwoInline_TheThirdKeepsItsPathForRead()
    {
        var (attachments, _) = Create();

        var prepared = await PrepareAsync(attachments,
            Image(TestImages.Png(10, 10)), Image(TestImages.Png(20, 20)), Image(TestImages.Png(30, 30)));

        Assert.Equal(2, prepared.Images.Count);
        Assert.Equal(
            "task\n\n[1 image attachment(s) omitted: 1 not inline: over the 2-image limit — open the path with read]",
            prepared.Message);
    }

    [Fact]
    public async Task ThreeOverSizeImages_FfmpegRunsExactlyTwice_TheThirdHitsTheCapBeforeAnyDownscale()
    {
        var (attachments, tools) = Create();
        var big = TestImages.Jpeg(2560, 1920, 4096);

        var prepared = await PrepareAsync(attachments, Image(big), Image(big), Image(big, path: null));

        Assert.Equal(2, prepared.Images.Count);
        Assert.Equal(2, tools.Count(PiAttachments.FfmpegBinary));
        // Without a persisted path the image cannot be opened later: it is simply omitted.
        Assert.Equal(
            "task\n\n[1 image attachment(s) omitted: 1 over the 2-image limit]\n\n[2 image(s) resized to fit 2000 px]",
            prepared.Message);
    }

    [Fact]
    public async Task AFailedDownscale_KeepsItsSlot()
    {
        var tools = new FakePiLocalTools { Handler = (_, _, _, _) => Task.FromResult<PiToolResult?>(FakePiLocalTools.Exit(1)) };
        var (attachments, _) = Create(tools);
        var big = TestImages.Jpeg(2560, 1920, 4096);

        var prepared = await PrepareAsync(attachments, Image(big), Image(big), Image(TestImages.Png(10, 10)));

        Assert.Empty(prepared.Images);
        Assert.Equal(2, tools.Count(PiAttachments.FfmpegBinary));
        Assert.Equal(
            "task\n\n[3 image attachment(s) omitted: 1 not inline: over the 2-image limit — open the path with read, "
            + "2 processing failed]",
            prepared.Message);
    }

    // ── Step 6: downscale ───────────────────────────────────────────────────

    [Theory]
    [InlineData("png", "out.png")]
    [InlineData("gif", "out.png")]
    [InlineData("jpeg", "out.jpg")]
    [InlineData("webp", "out.jpg")]
    public async Task TheDownscaleOutput_IsPngForPngAndGif_JpegOtherwise(string kind, string output)
    {
        var (attachments, tools) = Create();
        var bytes = kind switch
        {
            "png" => TestImages.Png(4000, 3000),
            "gif" => TestImages.Gif(4000, 3000),
            "jpeg" => TestImages.Jpeg(4000, 3000),
            _ => TestImages.WebpVp8X(4000, 3000),
        };

        var prepared = await PrepareAsync(attachments, Image(bytes));

        var call = Assert.Single(tools.Calls);
        Assert.Equal(output, Path.GetFileName(call.Args[^1]));
        Assert.Equal(output.EndsWith(".jpg") ? "image/jpeg" : "image/png", Assert.Single(prepared.Images).MimeType);
        Assert.Equal((2000, 1500), PiAttachments.ReadDimensions(prepared.Images[0].Bytes, prepared.Images[0].MimeType));
    }

    [Fact]
    public async Task APngOutputOver3MiB_IsReEncodedAsJpeg_WithAlphaFlattenedOnWhite()
    {
        var tools = new FakePiLocalTools();
        tools.Handler = (tool, args, _, _) =>
        {
            if (args[^1].EndsWith(".png"))
            {
                File.WriteAllBytes(args[^1], TestImages.Png(1800, 680, Mib3 + 100));
                return Task.FromResult<PiToolResult?>(FakePiLocalTools.Ok(""));
            }
            return Task.FromResult<PiToolResult?>(null);
        };
        var (attachments, _) = Create(tools);

        // An 1800 px PNG over 3 MiB: re-encoded, never upscaled.
        var prepared = await PrepareAsync(attachments, Image(TestImages.Png(1800, 680, Mib3 + 500_000)));

        Assert.Equal(2, tools.Count(PiAttachments.FfmpegBinary));
        Assert.Equal(PiAttachments.FlattenFilter, tools.Calls[1].Args[tools.Calls[1].Args.ToList().IndexOf("-vf") + 1]);
        var image = Assert.Single(prepared.Images);
        Assert.Equal("image/jpeg", image.MimeType);
        Assert.Equal((1800, 680), PiAttachments.ReadDimensions(image.Bytes, image.MimeType));
    }

    [Fact]
    public async Task AJpegStillOver3MiB_IsOmittedAsOver3MiB()
    {
        var tools = new FakePiLocalTools();
        tools.Handler = (_, args, _, _) =>
        {
            File.WriteAllBytes(args[^1], TestImages.Jpeg(2000, 1500, Mib3 + 1));
            return Task.FromResult<PiToolResult?>(FakePiLocalTools.Ok(""));
        };
        var (attachments, _) = Create(tools);

        var prepared = await PrepareAsync(attachments, Image(TestImages.Jpeg(2560, 1920)));

        Assert.Empty(prepared.Images);
        Assert.Contains("1 over 3 MiB", prepared.Message);
    }

    [Fact]
    public async Task ADownscaleTimeout_IsProcessingTimedOut_AndTheTurnGoesOn()
    {
        var tools = new FakePiLocalTools();
        tools.Handler = async (_, _, budget, ct) => await tools.BlockAsync(budget, ct);
        var (attachments, _) = Create(tools);

        var prepared = await attachments.PrepareAsync("task", [Image(TestImages.Jpeg(2560, 1920)), Image(TestImages.Png(10, 10))],
            null, imageInput: true, TimeSpan.FromMilliseconds(300), CancellationToken.None);

        Assert.Single(prepared.Images);
        Assert.Equal("task\n\n[1 image attachment(s) omitted: 1 processing timed out]", prepared.Message);
    }

    [Fact]
    public async Task TheOmittedNote_ListsReasonsInTableOrder()
    {
        var tools = new FakePiLocalTools { Handler = (_, _, _, _) => Task.FromResult<PiToolResult?>(FakePiLocalTools.Exit(1)) };
        var (attachments, _) = Create(tools);

        var prepared = await PrepareAsync(attachments,
            Image(TestImages.Jpeg(2560, 1920)),
            Image(TestImages.Png(13_000, 10)),
            Image([9, 9, 9, 9]),
            Image(TestImages.Png(10, 10)),
            Image(TestImages.Png(10, 10)));

        Assert.Equal(
            "task\n\n[4 image attachment(s) omitted: 1 unsupported format, 1 too large to process, "
            + "1 not inline: over the 2-image limit — open the path with read, 1 processing failed]",
            prepared.Message);
    }

    // ── Image input off ─────────────────────────────────────────────────────

    [Fact]
    public async Task ImageInputOff_ProcessesNothing_AndKeepsTodaysNoteByteForByte()
    {
        var (attachments, tools) = Create();

        var prepared = await attachments.PrepareAsync("what is this?",
            [Image(TestImages.Jpeg(2560, 1920)), Image(TestImages.Png(10, 10))],
            null, imageInput: false, TimeSpan.FromSeconds(60), CancellationToken.None);

        Assert.Empty(prepared.Images);
        Assert.Empty(tools.Calls);
        Assert.Equal("what is this?\n\n[2 image attachment(s) omitted: this local model is text-only]", prepared.Message);
    }

    [Fact]
    public async Task NoImagesAndNoPdf_LeaveTheTaskUntouched_AndLogNothing()
    {
        var tools = new FakePiLocalTools();
        var logger = new PiTestLogger();
        var attachments = new PiAttachments(tools, new PiPdfExtractor(tools, logger), logger);

        var prepared = await attachments.PrepareAsync("task", null,
            [new MessageDocument("f", "text/plain", 10, "notes.txt") { FilePath = "/workspace/example/notes.txt" }],
            imageInput: true, TimeSpan.FromSeconds(60), CancellationToken.None);

        Assert.Equal("task", prepared.Message);
        Assert.Null(prepared.Counts);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task TheLogLine_CarriesCountsAndReasonNamesOnly()
    {
        var tools = new FakePiLocalTools();
        var logger = new PiTestLogger();
        var attachments = new PiAttachments(tools, new PiPdfExtractor(tools, logger), logger);

        await attachments.PrepareAsync("task",
            [Image(TestImages.Jpeg(2560, 1920)), Image(TestImages.Png(10, 10)), Image(TestImages.Png(10, 10))],
            null, imageInput: true, TimeSpan.FromSeconds(60), CancellationToken.None);

        var line = Assert.Single(logger.Entries).Message;
        Assert.StartsWith("pi attachments: images sent 2 resized 1 omitted 1 (over the 2-image limit:1); "
                          + "pdf extracted 0 truncated 0 failed 0 () chars 0 ms ", line);
        Assert.DoesNotContain("/workspace", line);
    }
}
