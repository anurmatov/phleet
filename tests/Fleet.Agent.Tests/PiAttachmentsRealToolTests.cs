using System.Diagnostics;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Fleet.Agent.Tests.Harness;
using Fleet.Shared;
using Xunit.Abstractions;

namespace Fleet.Agent.Tests;

/// <summary>
/// #455 A4 and A5b against the real tools the agent image installs — poppler (<c>pdfinfo</c>,
/// <c>pdftotext</c>, <c>pdftoppm</c>), <c>ffmpeg</c> and <c>prlimit</c> — through the real
/// <see cref="PiLocalTools"/>, on the generated synthetic fixtures. CI's <c>pi</c> job runs them with
/// <c>Category=RealPi</c>; a missing tool is a hard failure. The observed poppler exit codes and the
/// EXIF-orientation result are written to the test output for the PR.
/// </summary>
[Trait("Category", "RealPi")]
public sealed class PiAttachmentsRealToolTests(ITestOutputHelper output) : IDisposable
{
    private readonly AttachmentFixtures _fixtures = AttachmentFixtures.Generate();
    private readonly PiTestLogger _logger = new();

    public void Dispose() => _fixtures.Dispose();

    private PiAttachments Create(PiLocalTools? tools = null)
    {
        tools ??= new PiLocalTools();
        return new PiAttachments(tools, new PiPdfExtractor(tools, _logger), _logger);
    }

    private MessageDocument Pdf(string fixture)
    {
        var path = _fixtures.PathOf(fixture);
        return new MessageDocument("file-id", "application/pdf", new FileInfo(path).Length, Path.GetFileName(path)) { FilePath = path };
    }

    private MessageImage Image(string fixture) =>
        new(_fixtures.Bytes(fixture), "image/jpeg") { FilePath = _fixtures.PathOf(fixture) };

    private Task<PiPreparedCommand> PrepareAsync(
        IReadOnlyList<MessageDocument>? documents = null, IReadOnlyList<MessageImage>? images = null, bool imageInput = false) =>
        Create().PrepareAsync("task", images, documents, imageInput, PiLocalModel.CommandProcessingDeadline, CancellationToken.None);

    // ── A5b: real poppler ───────────────────────────────────────────────────

    [Fact]
    public async Task A5b_RecordsTheObservedPopplerExitCodes()
    {
        var tools = new PiLocalTools();
        var budget = new PiProcessingBudget(TimeSpan.FromSeconds(120));
        foreach (var fixture in new[] { "pdf-text", "pdf-owner-only", "pdf-copy-restricted", "pdf-user-password", "pdf-truncated", "pdf-not-pdf", "pdf-scanned" })
        {
            var path = _fixtures.PathOf(fixture);
            var info = await tools.RunAsync(PiPdfExtractor.PdfInfo, [path], budget, 64 * 1024, CancellationToken.None);
            var text = await tools.RunAsync(PiPdfExtractor.PdfToText, ["-enc", "UTF-8", "-layout", "-f", "1", "-l", "100", path, "-"],
                budget, PiLocalModel.MaxPdfTextBytes, CancellationToken.None);
            var encrypted = System.Text.Encoding.UTF8.GetString(info.Stdout).Split('\n')
                .FirstOrDefault(l => l.StartsWith("Encrypted:", StringComparison.Ordinal))?.Trim() ?? "-";
            output.WriteLine($"A5b {fixture}: pdfinfo exit {info.ExitCode} ({encrypted}); pdftotext exit {text.ExitCode}, {text.Stdout.Length} bytes");
            Assert.False(info.Unavailable || text.Unavailable, "poppler is not installed: a hard failure by design");
        }
    }

    [Fact]
    public async Task A5b_TextAndOwnerOnlyPdfs_AreExtracted_WithTheirCodes()
    {
        var prepared = await PrepareAsync([Pdf("pdf-text"), Pdf("pdf-owner-only")]);

        Assert.Contains(_fixtures.Code("pdf-text"), prepared.Message);
        Assert.Contains(_fixtures.Code("pdf-owner-only"), prepared.Message);
        Assert.Equal(2, prepared.Counts!.PdfExtracted);
    }

    [Fact]
    public async Task A5b_EveryFailureFixture_GivesItsNamedNote()
    {
        var prepared = await PrepareAsync(
            [Pdf("pdf-user-password"), Pdf("pdf-copy-restricted"), Pdf("pdf-truncated")]);
        var notPdf = await PrepareAsync([Pdf("pdf-not-pdf")]);

        Assert.Contains("[PDF user-password.pdf could not be read: password-protected]", prepared.Message);
        Assert.Contains("[PDF copy-restricted.pdf text copying restricted and this model is text-only: not readable]", prepared.Message);
        Assert.DoesNotContain(_fixtures.Code("pdf-copy-restricted"), prepared.Message);
        Assert.Contains("[PDF truncated.pdf could not be read: damaged]", prepared.Message);
        Assert.Contains("[PDF not-a.pdf: not a valid PDF]", notPdf.Message);
    }

    [Fact]
    public async Task A5b_MoreThan4MiBOfText_IsTruncated_And120Pages_ReadsPages1To100()
    {
        var prepared = await PrepareAsync([Pdf("pdf-4mib"), Pdf("pdf-120")]);

        Assert.Contains("[PDF text: big-text.pdf, pages 1–100 of 100, truncated at 4 MiB;", prepared.Message);
        Assert.Contains("[PDF text: pages-120.pdf, pages 1–100 of 120;", prepared.Message);
        Assert.Equal(PiLocalModel.MaxPdfTextBytes, new FileInfo(_fixtures.PathOf("pdf-4mib") + ".txt").Length);
    }

    [Fact]
    public async Task A5b_ScannedPdfs_RenderToJpegsWithinBounds_OnlyWithImageInput()
    {
        var on = await PrepareAsync([Pdf("pdf-scanned"), Pdf("pdf-large-scan")], imageInput: true);
        var off = await PrepareAsync([Pdf("pdf-scanned")]);

        Assert.Equal(2, on.Images.Count);
        foreach (var image in on.Images)
        {
            Assert.Equal("image/jpeg", image.MimeType);
            Assert.True(PiAttachments.IsWithinBounds(image.Bytes));
            output.WriteLine($"render: {image.Bytes.Length} bytes {PiAttachments.ReadDimensions(image.Bytes, "image/jpeg")}");
        }
        Assert.DoesNotContain("over 3 MiB", on.Message);
        Assert.Empty(off.Images);
        Assert.Contains("[PDF scanned.pdf has no text layer and this model is text-only: not readable]", off.Message);
    }

    [Fact]
    public async Task A5b_ACopyRestrictedPdf_IsOnlyViewed_AsPageImages_WithImageInput()
    {
        var prepared = await PrepareAsync([Pdf("pdf-copy-restricted")], imageInput: true);

        Assert.Single(prepared.Images);
        Assert.Contains("[PDF copy-restricted.pdf text copying restricted: pages 1 of 1 sent as images]", prepared.Message);
        Assert.DoesNotContain(_fixtures.Code("pdf-copy-restricted"), prepared.Message);
    }

    // ── A4: real ffmpeg ─────────────────────────────────────────────────────

    [Fact]
    public async Task A4_A2560PxPhoto_IsDownscaledUnder1GiBSingleThreaded_ToAJpegWithinBounds()
    {
        var clock = Stopwatch.StartNew();
        var prepared = await PrepareAsync(images: [Image("photo-2560")], imageInput: true);

        var image = Assert.Single(prepared.Images);
        Assert.Equal("image/jpeg", image.MimeType);
        Assert.Equal((2000, 1500), PiAttachments.ReadDimensions(image.Bytes, image.MimeType));
        Assert.True(image.Bytes.Length <= PiLocalModel.MaxImageBytes);
        Assert.Equal("task\n\n[1 image(s) resized to fit 2000 px]", prepared.Message);
        output.WriteLine($"A4 photo-2560: {image.Bytes.Length} bytes in {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task A4_An1800PxPngOver3MiB_StaysExactly1800Px_AndEndsUnder3MiB()
    {
        var source = _fixtures.Bytes("png-3_5mib");
        Assert.True(source.Length > PiLocalModel.MaxImageBytes);

        var prepared = await PrepareAsync(images: [Image("png-3_5mib")], imageInput: true);

        var image = Assert.Single(prepared.Images);
        Assert.Equal((1800, 680), PiAttachments.ReadDimensions(image.Bytes, image.MimeType));
        Assert.True(image.Bytes.Length <= PiLocalModel.MaxImageBytes);
        output.WriteLine($"A4 png-3_5mib: {source.Length} → {image.Bytes.Length} bytes as {image.MimeType}");
    }

    [Fact]
    public async Task A4_TheExifOrientation6Photo_IsMeasured()
    {
        var prepared = await PrepareAsync(images: [Image("photo-exif6")], imageInput: true);

        var image = Assert.Single(prepared.Images);
        var (width, height) = PiAttachments.ReadDimensions(image.Bytes, image.MimeType)!.Value;
        // The source is 2560×1920 stored with Orientation = 6 (display 1920×2560). Portrait output
        // means ffmpeg applied the orientation; landscape would be a rotated image (a review finding).
        output.WriteLine($"A10c EXIF-6: output {width}×{height} — {(height > width ? "upright" : "ROTATED")}");
        Assert.True(height > width, $"the EXIF-6 photo came out {width}×{height}: rotated");
    }

    [Fact]
    public async Task A4_ImagesWithinBounds_PassByteForByte_WithTheSniffedType()
    {
        var prepared = await PrepareAsync(images: [Image("png"), Image("webp")], imageInput: true);

        Assert.Equal(_fixtures.Bytes("png"), prepared.Images[0].Bytes);
        Assert.Equal("image/png", prepared.Images[0].MimeType);
        Assert.Equal(_fixtures.Bytes("webp"), prepared.Images[1].Bytes);
        Assert.Equal("image/webp", prepared.Images[1].MimeType);
    }

    [Fact]
    public async Task A4_The13000PxPng_IsTooLargeToProcess()
    {
        var prepared = await PrepareAsync(images: [Image("png-13000")], imageInput: true);

        Assert.Empty(prepared.Images);
        Assert.Contains("1 too large to process", prepared.Message);
    }

    [Fact]
    public async Task TheRealRunner_LeavesNoTemporaryDirectory()
    {
        static string[] Leftovers() => Directory.GetDirectories(Path.GetTempPath(), "fleet-pi-*");
        var before = Leftovers();

        await PrepareAsync([Pdf("pdf-scanned"), Pdf("pdf-text")], [Image("photo-2560")], imageInput: true);

        Assert.Equal(before.Order(), Leftovers().Order());
    }
}
