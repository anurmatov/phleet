using System.Diagnostics;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Fleet.Agent.Tests.Harness;
using Fleet.Shared;
using Microsoft.Extensions.Logging;

namespace Fleet.Agent.Tests;

/// <summary>
/// #455 §3.3 and A5 with a fake poppler: every exit-code row, the copy restriction, the 4 MiB
/// truncation, the inline budget, the 3-PDF limit, renders and their step-down, k = 0, the cache
/// bound, cancellation and the shared deadline. Real poppler's exit codes are A5b, in
/// <c>PiAttachmentsRealToolTests</c>.
/// </summary>
public sealed class PiPdfExtractorTests : IDisposable
{
    private readonly PiTempDirectory _temp = new("pi-pdf");
    private readonly PiTestLogger _logger = new();

    public void Dispose() => _temp.Dispose();

    private MessageDocument Pdf(string name = "report.pdf", string body = "%PDF-1.4\nsynthetic\n")
    {
        var path = Path.Combine(_temp.Path, name);
        File.WriteAllText(path, body);
        return new MessageDocument("file-id", "application/pdf", body.Length, name) { FilePath = path };
    }

    private (PiAttachments Attachments, PiPdfExtractor Extractor) Create(FakePiLocalTools tools)
    {
        var extractor = new PiPdfExtractor(tools, _logger);
        return (new PiAttachments(tools, extractor, _logger), extractor);
    }

    private static Task<PiPreparedCommand> PrepareAsync(
        PiAttachments attachments, IReadOnlyList<MessageDocument> documents, bool textOnly = true,
        IReadOnlyList<MessageImage>? images = null, TimeSpan? deadline = null, CancellationToken ct = default) =>
        attachments.PrepareAsync("task", images, documents, textOnly, deadline ?? TimeSpan.FromSeconds(60), ct);

    private static Func<string, IReadOnlyList<string>, PiProcessingBudget, CancellationToken, Task<PiToolResult?>> On(
        string tool, PiToolResult result) =>
        (t, _, _, _) => Task.FromResult(t == tool ? result : null);

    // ── Text ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AText_Pdf_IsInlinedWithItsHeader_AndTheFullTextGoesToASidecarAtomically()
    {
        var tools = new FakePiLocalTools { PdfText = "Hello from page one; this is a text layer.\n" };
        var (attachments, _) = Create(tools);
        var pdf = Pdf();

        var prepared = await PrepareAsync(attachments, [pdf]);

        var sidecar = pdf.FilePath + ".txt";
        Assert.Equal(
            $"task\n\n[PDF text: report.pdf, pages 1–3 of 3; full text: {sidecar}]\n"
            + "Hello from page one; this is a text layer.\n\n[end of PDF text]",
            prepared.Message);
        Assert.Equal(tools.PdfText, File.ReadAllText(sidecar));
        Assert.False(File.Exists(sidecar + ".tmp"));
        Assert.Equal(["-enc", "UTF-8", "-layout", "-f", "1", "-l", "100", pdf.FilePath!, "-"],
            tools.Calls.Single(c => c.Tool == PiPdfExtractor.PdfToText).Args);
        Assert.Empty(prepared.Images);
    }

    [Fact]
    public async Task A120PagePdf_ReadsPages1To100()
    {
        var tools = new FakePiLocalTools { PdfInfoText = "Pages:          120\nEncrypted:      no\n" };
        var (attachments, _) = Create(tools);

        var prepared = await PrepareAsync(attachments, [Pdf()]);

        Assert.Contains("[PDF text: report.pdf, pages 1–100 of 120;", prepared.Message);
    }

    [Fact]
    public async Task TextOver4MiB_IsKeptTruncated_NotTimedOut()
    {
        var tools = new FakePiLocalTools { PdfText = new string('x', PiLocalModel.MaxPdfTextBytes + 1000) };
        var (attachments, _) = Create(tools);
        var pdf = Pdf();

        var prepared = await PrepareAsync(attachments, [pdf]);

        Assert.Contains($"pages 1–3 of 3, truncated at 4 MiB; full text: {pdf.FilePath}.txt]", prepared.Message);
        Assert.DoesNotContain("timed out", prepared.Message);
        Assert.Equal(PiLocalModel.MaxPdfTextBytes, new FileInfo(pdf.FilePath + ".txt").Length);
        Assert.Equal(1, prepared.Counts!.PdfTruncated);
    }

    [Fact]
    public async Task InlineText_StopsAt24576CharsAcrossAllPdfs_WithThePointerToTheRest()
    {
        var tools = new FakePiLocalTools { PdfText = new string('a', 20_000) };
        var (attachments, _) = Create(tools);
        var first = Pdf("one.pdf");
        var second = Pdf("two.pdf");

        var prepared = await PrepareAsync(attachments, [first, second]);

        Assert.Equal(PiLocalModel.MaxInlinePdfChars, prepared.Counts!.InlineChars);
        Assert.Contains($"{new string('a', PiLocalModel.MaxInlinePdfChars - 20_000)}…rest in {second.FilePath}.txt\n[end of PDF text]",
            prepared.Message);
        Assert.DoesNotContain($"…rest in {first.FilePath}.txt", prepared.Message);
    }

    [Fact]
    public async Task TheFourthPdf_GetsTheLimitNote()
    {
        var tools = new FakePiLocalTools();
        var (attachments, _) = Create(tools);

        var prepared = await PrepareAsync(attachments, [Pdf("1.pdf"), Pdf("2.pdf"), Pdf("3.pdf"), Pdf("4.pdf")]);

        Assert.Equal(3, tools.Count(PiPdfExtractor.PdfToText));
        Assert.EndsWith("[1 PDF(s) not extracted: over the 3-PDF limit; paths above]", prepared.Message);
    }

    [Fact]
    public async Task ASidecarWriteFailure_StillSendsTheInlineText_WithoutTheSidecarNote()
    {
        var tools = new FakePiLocalTools();
        var (attachments, _) = Create(tools);
        var pdf = Pdf();
        Directory.CreateDirectory(pdf.FilePath + ".txt"); // a directory where the file would go

        var prepared = await PrepareAsync(attachments, [pdf]);

        Assert.Contains("[PDF text: report.pdf, pages 1–3 of 3]\n", prepared.Message);
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("full-text file could not be written"));
    }

    // ── §3.3 tables ─────────────────────────────────────────────────────────

    [Fact]
    public async Task AMissingFile_IsFileMissing_WithNoToolRun()
    {
        var tools = new FakePiLocalTools();
        var (attachments, _) = Create(tools);
        var missing = new MessageDocument("f", "application/pdf", 10, "gone.pdf") { FilePath = Path.Combine(_temp.Path, "gone.pdf") };
        var unsaved = new MessageDocument("f", "application/pdf", 10, "unsaved.pdf");

        var prepared = await PrepareAsync(attachments, [missing, unsaved]);

        Assert.Empty(tools.Calls);
        Assert.Contains("[PDF gone.pdf could not be read: file missing]", prepared.Message);
        Assert.Contains("[PDF unsaved.pdf could not be read: file missing]", prepared.Message);
    }

    [Fact]
    public void TheCacheKey_OfAFileSweptAfterTheExistsCheck_IsNull_NotAThrow()
    {
        var pdf = Pdf();
        Assert.NotNull(PiPdfExtractor.CacheKey.TryOf(pdf.FilePath!));

        File.Delete(pdf.FilePath!);

        Assert.Null(PiPdfExtractor.CacheKey.TryOf(pdf.FilePath!));
    }

    [Fact]
    public async Task AFileWithoutPdfMagic_IsNotAValidPdf()
    {
        var tools = new FakePiLocalTools();
        var (attachments, _) = Create(tools);

        var prepared = await PrepareAsync(attachments, [Pdf("fake.pdf", "just text\n")]);

        Assert.Empty(tools.Calls);
        Assert.Contains("[PDF fake.pdf: not a valid PDF]", prepared.Message);
    }

    [Theory]
    [InlineData(1, "Command Line Error: Incorrect password", "password-protected")]
    [InlineData(1, "Syntax Error: Couldn't read xref table", "damaged")]
    [InlineData(99, "", "damaged")]
    public async Task PdfInfoFailures_AreNamed_AndPdfToTextNeverRuns(int exit, string stderr, string reason)
    {
        var tools = new FakePiLocalTools { Handler = On(PiPdfExtractor.PdfInfo, FakePiLocalTools.Exit(exit, stderr)) };
        var (attachments, _) = Create(tools);

        var prepared = await PrepareAsync(attachments, [Pdf()]);

        Assert.Equal(0, tools.Count(PiPdfExtractor.PdfToText));
        Assert.Contains($"[PDF report.pdf could not be read: {reason}]", prepared.Message);
    }

    [Fact]
    public async Task OwnerOnlyEncryption_WithCopyAllowed_IsExtracted_WithNoPassword()
    {
        var tools = new FakePiLocalTools
        {
            PdfInfoText = "Pages:          1\nEncrypted:      yes (print:yes copy:yes change:yes addNotes:yes algorithm:AES-256)\n",
        };
        var (attachments, _) = Create(tools);

        var prepared = await PrepareAsync(attachments, [Pdf()]);

        Assert.Contains("[PDF text: report.pdf, pages 1–1 of 1;", prepared.Message);
        Assert.DoesNotContain(tools.Calls.SelectMany(c => c.Args), a => a is "-upw" or "-opw");
    }

    [Fact]
    public async Task CopyRestricted_PerPdfInfo_IsNeverTextExtracted_AndTextOnlyGetsTheNote()
    {
        // poppler 24.02 pdftotext ignores the restriction (exit 0), so pdfinfo's copy:no decides.
        var tools = new FakePiLocalTools
        {
            PdfInfoText = "Pages:          2\nEncrypted:      yes (print:yes copy:no change:yes addNotes:yes algorithm:AES-256)\n",
        };
        var (attachments, _) = Create(tools);

        var prepared = await PrepareAsync(attachments, [Pdf()]);

        Assert.Equal(0, tools.Count(PiPdfExtractor.PdfToText));
        Assert.Contains("[PDF report.pdf text copying restricted and this model is text-only: not readable]", prepared.Message);
    }

    [Fact]
    public async Task PdfToTextExit3_IsCopyRestricted_AndRenders()
    {
        var tools = new FakePiLocalTools { Handler = On(PiPdfExtractor.PdfToText, FakePiLocalTools.Exit(3, "Copying of text from this document is not allowed.")) };
        var (attachments, _) = Create(tools);

        var prepared = await PrepareAsync(attachments, [Pdf()], textOnly: false);

        Assert.Equal(2, prepared.Images.Count);
        Assert.Contains("[PDF report.pdf text copying restricted: pages 1–2 of 3 sent as images]", prepared.Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(99)]
    public async Task OtherPdfToTextExits_AreDamaged(int exit)
    {
        var tools = new FakePiLocalTools { Handler = On(PiPdfExtractor.PdfToText, FakePiLocalTools.Exit(exit)) };
        var (attachments, _) = Create(tools);

        var prepared = await PrepareAsync(attachments, [Pdf()]);

        Assert.Contains("[PDF report.pdf could not be read: damaged]", prepared.Message);
    }

    [Fact]
    public async Task AMissingExtractor_IsNamed_AndLoggedOncePerProcess()
    {
        var tools = new FakePiLocalTools
        {
            Handler = On(PiPdfExtractor.PdfInfo, FakePiLocalTools.Exit(127, "prlimit: failed to execute pdfinfo: No such file or directory")),
        };
        var (attachments, _) = Create(tools);

        var first = await PrepareAsync(attachments, [Pdf("a.pdf")]);
        await PrepareAsync(attachments, [Pdf("b.pdf")]);

        Assert.Contains("[PDF a.pdf could not be read: extractor unavailable]", first.Message);
        Assert.Single(_logger.Entries, e => e.Level == LogLevel.Error && e.Message == "pi pdf: extractor unavailable");
    }

    // ── Step 3: no text layer ───────────────────────────────────────────────

    [Fact]
    public async Task NoTextLayer_TextOnly_GetsTheNotReadableNote()
    {
        var tools = new FakePiLocalTools { PdfText = "\f \n" };
        var (attachments, _) = Create(tools);

        var prepared = await PrepareAsync(attachments, [Pdf("scan.pdf")]);

        Assert.Equal(0, tools.Count(PiPdfExtractor.PdfToPpm));
        Assert.Contains("[PDF scan.pdf has no text layer and this model is text-only: not readable]", prepared.Message);
    }

    [Fact]
    public async Task NoTextLayer_RendersIntoTheSlotsLeft()
    {
        var tools = new FakePiLocalTools { PdfText = "" };
        var (attachments, _) = Create(tools);

        var prepared = await PrepareAsync(attachments, [Pdf("scan.pdf")], textOnly: false,
            images: [new MessageImage(TestImages.Png(10, 10), "image/jpeg")]);

        // One user image holds one slot: one page.
        Assert.Equal(2, prepared.Images.Count);
        Assert.Equal("image/jpeg", prepared.Images[1].MimeType);
        var render = tools.Calls.Single(c => c.Tool == PiPdfExtractor.PdfToPpm).Args;
        Assert.Equal(["-jpeg", "-jpegopt", "quality=85", "-r", "96", "-scale-to", "1600", "-f", "1", "-l", "1"], render.Take(11));
        Assert.Contains("[PDF scan.pdf has no text layer: pages 1 of 3 sent as images]", prepared.Message);
    }

    [Fact]
    public async Task NoTextLayer_WithNoSlotLeft_SaysSendItOnItsOwn()
    {
        var tools = new FakePiLocalTools { PdfText = "" };
        var (attachments, _) = Create(tools);

        var prepared = await PrepareAsync(attachments, [Pdf("scan.pdf")], textOnly: false,
            images: [new MessageImage(TestImages.Png(10, 10), "image/png"), new MessageImage(TestImages.Png(10, 10), "image/png")]);

        Assert.Equal(2, prepared.Images.Count);
        Assert.Equal(0, tools.Count(PiPdfExtractor.PdfToPpm));
        Assert.Contains("[PDF scan.pdf has no text layer: no image slot left in this message; send it on its own]", prepared.Message);
    }

    [Fact]
    public async Task APageOver3MiB_StepsDownTo1200Then800()
    {
        var tools = new FakePiLocalTools
        {
            PdfText = "",
            PdfInfoText = "Pages:          1\nEncrypted:      no\n",
            RenderPage = (_, scale) => scale switch
            {
                1600 => TestImages.Jpeg(1131, 1600, PiLocalModel.MaxImageBytes + 1),
                1200 => TestImages.Jpeg(848, 1200, PiLocalModel.MaxImageBytes + 1),
                _ => TestImages.Jpeg(565, 800, 100_000),
            },
        };
        var (attachments, _) = Create(tools);

        var prepared = await PrepareAsync(attachments, [Pdf("scan.pdf")], textOnly: false);

        Assert.Equal(["1600", "1200", "800"],
            tools.Calls.Where(c => c.Tool == PiPdfExtractor.PdfToPpm).Select(c => c.Args[c.Args.ToList().IndexOf("-scale-to") + 1]));
        Assert.Equal((565, 800), PiAttachments.ReadDimensions(Assert.Single(prepared.Images).Bytes, "image/jpeg"));
    }

    [Fact]
    public async Task APageStillOver3MiBAt800_GetsThePageNote()
    {
        var tools = new FakePiLocalTools
        {
            PdfText = "",
            PdfInfoText = "Pages:          1\nEncrypted:      no\n",
            RenderPage = (_, scale) => TestImages.Jpeg(scale, scale, PiLocalModel.MaxImageBytes + 1),
        };
        var (attachments, _) = Create(tools);

        var prepared = await PrepareAsync(attachments, [Pdf("scan.pdf")], textOnly: false);

        Assert.Empty(prepared.Images);
        Assert.Contains("[PDF scan.pdf page 1: over 3 MiB, not sent]", prepared.Message);
    }

    // ── Cache ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheCache_RunsPdfToTextOnce_ForTheSameFile()
    {
        var tools = new FakePiLocalTools();
        var (attachments, _) = Create(tools);
        var pdf = Pdf();

        var first = await PrepareAsync(attachments, [pdf]);
        var second = await PrepareAsync(attachments, [pdf]);

        Assert.Equal(1, tools.Count(PiPdfExtractor.PdfToText));
        Assert.Equal(first.Message, second.Message);
    }

    [Fact]
    public async Task TheCache_RedoesRendersOnAHit_AndHoldsNoImageBytes()
    {
        var tools = new FakePiLocalTools { PdfText = "" };
        var (attachments, _) = Create(tools);
        var pdf = Pdf("scan.pdf");

        await PrepareAsync(attachments, [pdf], textOnly: false);
        var again = await PrepareAsync(attachments, [pdf], textOnly: false);

        Assert.Equal(1, tools.Count(PiPdfExtractor.PdfToText));
        Assert.Equal(2, tools.Count(PiPdfExtractor.PdfToPpm));
        Assert.Equal(2, again.Images.Count);
    }

    [Fact]
    public async Task TheCache_NeverExceeds32Entries_AndDropsEntriesWhoseFileIsGone()
    {
        var tools = new FakePiLocalTools();
        var (attachments, extractor) = Create(tools);
        var pdfs = Enumerable.Range(0, 40).Select(i => Pdf($"{i}.pdf")).ToList();

        foreach (var pdf in pdfs)
            await PrepareAsync(attachments, [pdf]);
        Assert.Equal(PiLocalModel.PdfCacheEntries, extractor.CacheCount);

        foreach (var pdf in pdfs.Skip(30))
            File.Delete(pdf.FilePath!);
        await PrepareAsync(attachments, [pdfs[30 - 1]]);

        Assert.Equal(PiLocalModel.PdfCacheEntries - 10, extractor.CacheCount);
    }

    [Fact]
    public async Task AChangedFile_IsExtractedAgain()
    {
        var tools = new FakePiLocalTools();
        var (attachments, _) = Create(tools);
        var pdf = Pdf();

        await PrepareAsync(attachments, [pdf]);
        File.AppendAllText(pdf.FilePath!, "more bytes\n");
        await PrepareAsync(attachments, [pdf]);

        Assert.Equal(2, tools.Count(PiPdfExtractor.PdfToText));
    }

    // ── Deadline and cancellation ───────────────────────────────────────────

    [Fact]
    public async Task ARunnerThatBlocks_AcrossThreePdfs_EndsAtTheCommandDeadline_WithTimedOutForTheUnfinished()
    {
        var tools = new FakePiLocalTools();
        tools.Handler = async (_, _, budget, ct) => await tools.BlockAsync(budget, ct);
        var (attachments, _) = Create(tools);
        var deadline = TimeSpan.FromSeconds(2);
        var clock = Stopwatch.StartNew();

        var prepared = await PrepareAsync(attachments, [Pdf("1.pdf"), Pdf("2.pdf"), Pdf("3.pdf")], deadline: deadline);

        Assert.InRange(clock.Elapsed, deadline - TimeSpan.FromSeconds(0.1), deadline + TimeSpan.FromSeconds(1));
        foreach (var name in new[] { "1.pdf", "2.pdf", "3.pdf" })
            Assert.Contains($"[PDF {name} could not be read: extraction timed out]", prepared.Message);
        Assert.Equal(3, prepared.Counts!.PdfFailed[PiLocalModel.PdfReasons.TimedOut]);
    }

    [Fact]
    public async Task ATimeout_IsNotCached()
    {
        var tools = new FakePiLocalTools();
        var block = true;
        tools.Handler = async (_, _, budget, ct) => block ? await tools.BlockAsync(budget, ct) : null;
        var (attachments, _) = Create(tools);
        var pdf = Pdf();

        await PrepareAsync(attachments, [pdf], deadline: TimeSpan.FromMilliseconds(200));
        block = false;
        var again = await PrepareAsync(attachments, [pdf]);

        Assert.Contains("[PDF text: report.pdf", again.Message);
    }

    [Fact]
    public async Task Cancellation_Throws_AndLeavesNoRenderDirectory()
    {
        using var cts = new CancellationTokenSource();
        var tools = new FakePiLocalTools { PdfText = "" };
        string? prefix = null;
        tools.Handler = async (tool, args, budget, ct) =>
        {
            if (tool != PiPdfExtractor.PdfToPpm)
                return null;
            prefix = args[^1];
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
            return null;
        };
        var (attachments, _) = Create(tools);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PrepareAsync(attachments, [Pdf("scan.pdf")], textOnly: false, ct: cts.Token));

        Assert.NotNull(prefix);
        Assert.False(Directory.Exists(Path.GetDirectoryName(prefix)));
    }

    [Fact]
    public void PageList_IsARangeOrAList()
    {
        Assert.Equal("1", PiPdfExtractor.PageList([1]));
        Assert.Equal("1–2", PiPdfExtractor.PageList([1, 2]));
        Assert.Equal("1, 3", PiPdfExtractor.PageList([1, 3]));
    }

    [Fact]
    public void IsPdf_ByMimeOrName()
    {
        Assert.True(PiPdfExtractor.IsPdf(new MessageDocument("f", "application/pdf", 1, null)));
        Assert.True(PiPdfExtractor.IsPdf(new MessageDocument("f", "application/octet-stream", 1, "A.PDF")));
        Assert.False(PiPdfExtractor.IsPdf(new MessageDocument("f", "text/plain", 1, "notes.txt")));
        Assert.Equal("a_b_.pdf", PiPdfExtractor.DisplayName(new MessageDocument("f", "application/pdf", 1, "a]b\n.pdf")));
    }

    [Fact]
    public async Task TheLogLine_NamesReasons_NeverFileNamesOrText()
    {
        var tools = new FakePiLocalTools { PdfText = "SECRET-TEXT-ABC lots of text in the text layer here.\n" };
        var (attachments, _) = Create(tools);

        await PrepareAsync(attachments, [Pdf("private-name.pdf"), Pdf("fake.pdf", "nope")]);

        var line = Assert.Single(_logger.Entries, e => e.Message.StartsWith("pi attachments:")).Message;
        Assert.Contains("pdf extracted 1 truncated 0 failed 1 (not a valid PDF:1)", line);
        Assert.DoesNotContain("private-name", line);
        Assert.DoesNotContain("SECRET", line);
        Assert.DoesNotContain(_temp.Path, line);
    }
}
