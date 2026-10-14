using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Fleet.Agent.Models;
using Fleet.Shared;
using Microsoft.Extensions.Logging;

namespace Fleet.Agent.Services;

/// <summary>An image Fleet sends to Pi: bytes within the bounds and the type their magic bytes say.</summary>
internal sealed record PiImageBlock(byte[] Bytes, string MimeType)
{
    /// <summary>P1's <c>ImageContent</c>. The base64 lives only while one frame is serialized.</summary>
    public JsonObject ToJson() => new()
    {
        ["type"] = "image",
        ["data"] = Convert.ToBase64String(Bytes),
        ["mimeType"] = MimeType,
    };
}

/// <summary>One command's message and images after local processing, and the counts its log line carries.</summary>
internal sealed record PiPreparedCommand(string Message, IReadOnlyList<PiImageBlock> Images, PiAttachmentCounts? Counts);

/// <summary>Counts and reason names only (§3.6): never bytes, text, file names, paths or dimensions.</summary>
internal sealed class PiAttachmentCounts
{
    public int ImagesSent { get; set; }
    public int ImagesResized { get; set; }
    public Dictionary<string, int> ImagesOmitted { get; } = new(StringComparer.Ordinal);
    public int PdfExtracted { get; set; }
    public int PdfTruncated { get; set; }
    public Dictionary<string, int> PdfFailed { get; } = new(StringComparer.Ordinal);
    public int InlineChars { get; set; }
    public long Milliseconds { get; set; }

    public static string Reasons(Dictionary<string, int> reasons) =>
        string.Join(',', reasons.Select(r => $"{r.Key}:{r.Value}"));

    public void Log(ILogger logger) =>
        logger.LogInformation(
            "pi attachments: images sent {Sent} resized {Resized} omitted {Omitted} ({ImageReasons}); "
            + "pdf extracted {Extracted} truncated {Truncated} failed {Failed} ({PdfReasons}) chars {Chars} ms {Ms}",
            ImagesSent, ImagesResized, ImagesOmitted.Values.Sum(), Reasons(ImagesOmitted),
            PdfExtracted, PdfTruncated, PdfFailed.Values.Sum(), Reasons(PdfFailed), InlineChars, Milliseconds);
}

/// <summary>
/// Pi attachment handling (#455 §3.2–§3.4): user images through the §3.2 checks and the isolated
/// <c>ffmpeg</c> downscale, PDFs through <see cref="PiPdfExtractor"/>, then one message and one
/// image list for <c>prompt</c> or <c>steer</c>. Every bound is Fleet's, on every path: Pi
/// normalizes images only on an idle prompt (P3, P4) and never reads <c>inputLimits</c> (P6).
/// </summary>
/// <remarks>
/// <para>
/// <b>Image input off</b> (the default): no image is processed and the message is exactly the
/// pre-#455 text plus <see cref="PiExecutor.ImagesOmittedText"/>. PDF text extraction runs for
/// every Pi agent: it is the parity default.
/// </para>
/// <para>
/// <b>Slots.</b> An image holds one of the <see cref="PiLocalModel.MaxImagesPerCommand"/> slots from
/// step 4, before any downscale, and keeps it even if its downscale then fails: ffmpeg runs at most
/// once per slot, and the cap note never depends on a decoder's result. PDF page renders take the
/// slots left after the user images.
/// </para>
/// </remarks>
internal sealed class PiAttachments(IPiLocalTools tools, PiPdfExtractor pdfs, ILogger logger)
{
    public const string FfmpegBinary = "ffmpeg";

    private const int SmallOutputCap = 64 * 1024;

    /// <summary>
    /// The §3.2 step 6 scale filter. The quotes are ffmpeg filtergraph quoting, passed literally in
    /// one argument (no shell): without them the comma inside <c>min()</c> would split the chain.
    /// </summary>
    internal const string ScaleFilter =
        "scale=w='min(iw,2000)':h='min(ih,2000)':force_original_aspect_ratio=decrease";

    /// <summary>The scale filter, then the image over an opaque white copy of itself: alpha flattened on white.</summary>
    internal const string FlattenFilter =
        ScaleFilter + ",split[a][b];[a]drawbox=c=white:replace=1:t=fill[bg];[bg][b]overlay=format=auto";

    public async Task<PiPreparedCommand> PrepareAsync(
        string task, IReadOnlyList<MessageImage>? images, IReadOnlyList<MessageDocument>? documents,
        bool imageInput, TimeSpan deadline, CancellationToken ct)
    {
        var userImages = images ?? [];
        var pdfDocuments = (documents ?? []).Where(PiPdfExtractor.IsPdf).ToList();
        if (userImages.Count == 0 && pdfDocuments.Count == 0)
            return new PiPreparedCommand(task, [], null);

        var clock = Stopwatch.StartNew();
        var budget = new PiProcessingBudget(deadline);
        var counts = new PiAttachmentCounts();
        var notes = new List<string>();
        var pdfBlocks = new List<string>();
        var blocks = new List<PiImageBlock>();

        // Images first, in order.
        var slotsHeld = 0;
        if (userImages.Count > 0 && !imageInput)
        {
            counts.ImagesOmitted["text-only"] = userImages.Count;
        }
        else if (userImages.Count > 0)
        {
            var selection = await PrepareImagesAsync(userImages, budget, ct);
            blocks.AddRange(selection.Blocks);
            slotsHeld = selection.SlotsHeld;
            counts.ImagesResized = selection.Resized;
            foreach (var (reason, count) in selection.Omitted)
                counts.ImagesOmitted[LogReason(reason)] = count;
            if (selection.OmittedNote() is { } omitted)
                notes.Add(omitted);
            if (selection.Resized > 0)
                notes.Add($"[{selection.Resized} image(s) resized to fit {PiLocalModel.MaxImageDimension} px]");
        }

        // Then PDFs: text inline within one budget for the command, renders into the slots left.
        var slotsLeft = PiLocalModel.MaxImagesPerCommand - slotsHeld;
        var inlineLeft = PiLocalModel.MaxInlinePdfChars;
        foreach (var document in pdfDocuments.Take(PiLocalModel.MaxPdfsPerCommand))
        {
            var pdf = budget.Expired
                ? Expired(document)
                : await pdfs.ExtractAsync(document, slotsLeft, imageInput, budget, ct);

            if (pdf.Excerpt is { } excerpt)
            {
                var shown = excerpt.Length > inlineLeft ? excerpt[..inlineLeft] : excerpt;
                inlineLeft -= shown.Length;
                counts.InlineChars += shown.Length;
                pdfBlocks.Add(TextBlock(pdf, shown, cut: shown.Length < excerpt.Length || pdf.MoreText));
                counts.PdfExtracted++;
                if (pdf.Truncated)
                    counts.PdfTruncated++;
            }
            else if (pdf.Renders.Count > 0)
            {
                foreach (var render in pdf.Renders)
                    blocks.Add(new PiImageBlock(render, "image/jpeg"));
                slotsLeft -= pdf.Renders.Count;
                counts.PdfExtracted++;
            }

            if (pdf.FailureReason is { } reason)
                counts.PdfFailed[reason] = counts.PdfFailed.GetValueOrDefault(reason) + 1;
            notes.AddRange(pdf.Notes);
        }

        if (pdfDocuments.Count > PiLocalModel.MaxPdfsPerCommand)
        {
            var over = pdfDocuments.Count - PiLocalModel.MaxPdfsPerCommand;
            notes.Add($"[{over} PDF(s) not extracted: over the {PiLocalModel.MaxPdfsPerCommand}-PDF limit; paths above]");
            counts.PdfFailed["over the 3-PDF limit"] = over;
        }

        // Flag off keeps the pre-#455 omission note, byte for byte, as the last part.
        if (userImages.Count > 0 && !imageInput)
            notes.Add(PiExecutor.ImagesOmittedText(userImages.Count));

        counts.ImagesSent = blocks.Count;
        counts.Milliseconds = clock.ElapsedMilliseconds;
        counts.Log(logger);
        var message = string.Join("\n\n", new[] { task }.Concat(pdfBlocks).Concat(notes));
        return new PiPreparedCommand(message, blocks, counts);
    }

    private static PiPdfResult Expired(MessageDocument document) => new()
    {
        Name = PiPdfExtractor.DisplayName(document),
        Notes = [$"[PDF {PiPdfExtractor.DisplayName(document)} could not be read: {PiLocalModel.PdfReasons.TimedOut}]"],
        FailureReason = PiLocalModel.PdfReasons.TimedOut,
    };

    /// <summary>The inline block: header, text, the pointer to the rest, and the end marker.</summary>
    private static string TextBlock(PiPdfResult pdf, string text, bool cut)
    {
        var header = new StringBuilder($"[PDF text: {pdf.Name}, pages 1–{pdf.PagesRead} of {pdf.PageCount}");
        if (pdf.Truncated)
            header.Append(", ").Append(PiLocalModel.PdfReasons.Truncated);
        if (pdf.SidecarPath is { } sidecar)
            header.Append("; full text: ").Append(sidecar);
        header.Append(']');

        var rest = !cut ? "" : pdf.SidecarPath is { } path ? $"…rest in {path}" : "…rest not shown";
        return $"{header}\n{text}{rest}\n[end of PDF text]";
    }

    // ── §3.2: user images ───────────────────────────────────────────────────

    internal sealed class ImageSelection
    {
        public List<PiImageBlock> Blocks { get; } = [];
        public int SlotsHeld { get; set; }
        public int Resized { get; set; }

        /// <summary>Note reason → count.</summary>
        public Dictionary<string, int> Omitted { get; } = new(StringComparer.Ordinal);

        public void Omit(string reason) => Omitted[reason] = Omitted.GetValueOrDefault(reason) + 1;

        /// <summary>The §3.2 note, reasons in table order.</summary>
        public string? OmittedNote() =>
            Omitted.Count == 0
                ? null
                : $"[{Omitted.Values.Sum()} image attachment(s) omitted: "
                  + string.Join(", ", Omitted.OrderBy(r => NoteOrder(r.Key)).ThenBy(r => r.Key, StringComparer.Ordinal)
                      .Select(r => $"{r.Value} {r.Key}")) + "]";
    }

    /// <summary>The over-cap note reason: open it with <c>read</c> when it has a path, else omitted.</summary>
    internal static string OverLimitReason(bool hasPath) =>
        hasPath
            ? $"not inline: {PiLocalModel.ImageReasons.OverImageLimit} — open the path with read"
            : PiLocalModel.ImageReasons.OverImageLimit;

    private static int NoteOrder(string noteReason)
    {
        var order = PiLocalModel.ImageReasons.Order;
        for (var i = 0; i < order.Count; i++)
        {
            if (noteReason == order[i] || noteReason.Contains(order[i], StringComparison.Ordinal))
                return i;
        }
        return order.Count;
    }

    /// <summary>The log name of a note reason: the over-cap variants collapse to the reason.</summary>
    private static string LogReason(string noteReason) =>
        noteReason.Contains(PiLocalModel.ImageReasons.OverImageLimit, StringComparison.Ordinal)
            ? PiLocalModel.ImageReasons.OverImageLimit
            : noteReason;

    public async Task<ImageSelection> PrepareImagesAsync(
        IReadOnlyList<MessageImage> images, PiProcessingBudget budget, CancellationToken ct)
    {
        var selection = new ImageSelection();
        foreach (var image in images)
        {
            ct.ThrowIfCancellationRequested();

            // Steps 1–3.
            if (Sniff(image.Bytes) is not { } mime)
            {
                selection.Omit(PiLocalModel.ImageReasons.UnsupportedFormat);
                continue;
            }
            if (ReadDimensions(image.Bytes, mime) is not { } dimensions)
            {
                selection.Omit(PiLocalModel.ImageReasons.Unreadable);
                continue;
            }
            var (width, height) = dimensions;
            if (width > PiLocalModel.MaxDecodeDimension || height > PiLocalModel.MaxDecodeDimension
                || (long)width * height > PiLocalModel.MaxDecodePixels)
            {
                selection.Omit(PiLocalModel.ImageReasons.TooLargeToProcess);
                continue;
            }

            // Step 4: the cap, before any downscale.
            if (selection.SlotsHeld >= PiLocalModel.MaxImagesPerCommand)
            {
                selection.Omit(OverLimitReason(image.FilePath is not null));
                continue;
            }
            selection.SlotsHeld++;

            // Step 5: within bounds goes byte for byte.
            if (IsWithinBounds(width, height, image.Bytes.Length))
            {
                selection.Blocks.Add(new PiImageBlock(image.Bytes, mime));
                continue;
            }

            // Step 6.
            var (block, reason) = await DownscaleAsync(image.Bytes, mime, budget, ct);
            if (block is null)
            {
                selection.Omit(reason!);
                continue;
            }
            selection.Blocks.Add(block);
            selection.Resized++;
        }
        return selection;
    }

    /// <summary>Step 6: one ffmpeg call (two for a PNG output over 3 MiB), then steps 1, 2 and 5 again.</summary>
    private async Task<(PiImageBlock? Block, string? Reason)> DownscaleAsync(
        byte[] bytes, string mime, PiProcessingBudget budget, CancellationToken ct)
    {
        var dir = Directory.CreateTempSubdirectory("fleet-pi-image-").FullName;
        try
        {
            var input = Path.Combine(dir, "in" + Extension(mime));
            await File.WriteAllBytesAsync(input, bytes, ct);

            var keepsPng = mime is "image/png" or "image/gif";
            var output = Path.Combine(dir, keepsPng ? "out.png" : "out.jpg");
            var result = await tools.RunAsync(FfmpegBinary, DownscaleArgs(input, output, keepsPng ? null : ScaleFilter),
                budget, SmallOutputCap, ct);
            if (Failure(result, output) is { } failure)
                return (null, failure);

            var produced = await File.ReadAllBytesAsync(output, ct);
            if (keepsPng && produced.Length > PiLocalModel.MaxImageBytes)
            {
                output = Path.Combine(dir, "out.jpg");
                result = await tools.RunAsync(FfmpegBinary, DownscaleArgs(input, output, FlattenFilter), budget, SmallOutputCap, ct);
                if (Failure(result, output) is { } again)
                    return (null, again);
                produced = await File.ReadAllBytesAsync(output, ct);
            }

            if (Sniff(produced) is not { } producedMime || ReadDimensions(produced, producedMime) is not { } size)
                return (null, PiLocalModel.ImageReasons.ProcessingFailed);
            if (produced.Length > PiLocalModel.MaxImageBytes)
                return (null, PiLocalModel.ImageReasons.Over3MiB);
            if (!IsWithinBounds(size.Width, size.Height, produced.Length))
                return (null, PiLocalModel.ImageReasons.ProcessingFailed);
            return (new PiImageBlock(produced, producedMime), null);
        }
        catch (IOException)
        {
            return (null, PiLocalModel.ImageReasons.ProcessingFailed);
        }
        finally
        {
            PiLocalTools.TryDeleteDirectory(dir);
        }
    }

    private static string? Failure(PiToolResult result, string output) =>
        result.TimedOut ? PiLocalModel.ImageReasons.ProcessingTimedOut
        : result.Unavailable || result.ExitCode != 0 || !File.Exists(output) ? PiLocalModel.ImageReasons.ProcessingFailed
        : null;

    /// <summary>
    /// The ffmpeg arguments. <c>-threads 1 -filter_threads 1</c> before <c>-i</c> bound the decoder and
    /// the filters; <c>-threads 1</c> again before the output bounds the encoder, whose default frame
    /// threads (cores + 1) exhaust the 1 GiB address-space limit on a many-core host (measured: ENOMEM
    /// on 20 cores with the input-side options alone). PNG for a PNG or GIF source (its first frame),
    /// JPEG otherwise; <paramref name="jpegFilter"/> null means PNG.
    /// </summary>
    internal static IReadOnlyList<string> DownscaleArgs(string input, string output, string? jpegFilter) =>
        jpegFilter is null
            ? ["-nostdin", "-threads", "1", "-filter_threads", "1", "-i", input, "-frames:v", "1",
               "-vf", ScaleFilter, "-threads", "1", "-v", "error", output]
            : ["-nostdin", "-threads", "1", "-filter_threads", "1", "-i", input, "-frames:v", "1",
               "-vf", jpegFilter, "-threads", "1", "-q:v", "3", "-v", "error", output];

    private static string Extension(string mime) => mime switch
    {
        "image/png" => ".png",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        _ => ".jpg",
    };

    // ── Headers ─────────────────────────────────────────────────────────────

    /// <summary>Step 5's bounds for a render or a downscale output: a known type, readable, within 2000 px and 3 MiB.</summary>
    internal static bool IsWithinBounds(byte[] bytes) =>
        Sniff(bytes) is { } mime && ReadDimensions(bytes, mime) is { } size && IsWithinBounds(size.Width, size.Height, bytes.Length);

    private static bool IsWithinBounds(int width, int height, int length) =>
        width <= PiLocalModel.MaxImageDimension && height <= PiLocalModel.MaxImageDimension
        && length <= PiLocalModel.MaxImageBytes;

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Step 1: the type from the magic bytes; the transport's label is ignored.</summary>
    internal static string? Sniff(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)
            return "image/jpeg";
        if (b.Length >= 8 && b[..8].SequenceEqual(PngSignature))
            return "image/png";
        if (b.Length >= 6 && (b[..6].SequenceEqual("GIF87a"u8) || b[..6].SequenceEqual("GIF89a"u8)))
            return "image/gif";
        if (b.Length >= 12 && b[..4].SequenceEqual("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8))
            return "image/webp";
        return null;
    }

    /// <summary>
    /// Step 2: width and height from the header only — JPEG SOFn, PNG IHDR, the GIF logical screen,
    /// WebP VP8/VP8L/VP8X — or null. Nothing is decoded.
    /// </summary>
    internal static (int Width, int Height)? ReadDimensions(ReadOnlySpan<byte> b, string mime)
    {
        switch (mime)
        {
            case "image/png":
                if (b.Length < 24 || !b[12..16].SequenceEqual("IHDR"u8))
                    return null;
                return Positive((long)BinaryPrimitives.ReadUInt32BigEndian(b[16..]), BinaryPrimitives.ReadUInt32BigEndian(b[20..]));

            case "image/gif":
                return b.Length < 10 ? null : Positive(BinaryPrimitives.ReadUInt16LittleEndian(b[6..]), BinaryPrimitives.ReadUInt16LittleEndian(b[8..]));

            case "image/webp":
                if (b.Length < 30)
                    return null;
                if (b[12..16].SequenceEqual("VP8 "u8))
                {
                    if (b[23] != 0x9D || b[24] != 0x01 || b[25] != 0x2A)
                        return null;
                    return Positive(BinaryPrimitives.ReadUInt16LittleEndian(b[26..]) & 0x3FFF,
                        BinaryPrimitives.ReadUInt16LittleEndian(b[28..]) & 0x3FFF);
                }
                if (b[12..16].SequenceEqual("VP8L"u8))
                {
                    if (b[20] != 0x2F)
                        return null;
                    var bits = BinaryPrimitives.ReadUInt32LittleEndian(b[21..]);
                    return Positive((bits & 0x3FFF) + 1, ((bits >> 14) & 0x3FFF) + 1);
                }
                if (b[12..16].SequenceEqual("VP8X"u8))
                {
                    var w = b[24] | (b[25] << 8) | (b[26] << 16);
                    var h = b[27] | (b[28] << 8) | (b[29] << 16);
                    return Positive(w + 1L, h + 1L);
                }
                return null;

            case "image/jpeg":
                var i = 2;
                while (i + 4 <= b.Length)
                {
                    if (b[i] != 0xFF)
                        return null;
                    var marker = b[i + 1];
                    if (marker == 0xFF)
                    {
                        i++;
                        continue;
                    }
                    if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7)
                    {
                        i += 2;
                        continue;
                    }
                    var length = BinaryPrimitives.ReadUInt16BigEndian(b[(i + 2)..]);
                    if (length < 2)
                        return null;
                    if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                    {
                        if (i + 9 > b.Length)
                            return null;
                        return Positive(BinaryPrimitives.ReadUInt16BigEndian(b[(i + 7)..]),
                            BinaryPrimitives.ReadUInt16BigEndian(b[(i + 5)..]));
                    }
                    if (marker is 0xD9 or 0xDA)
                        return null;
                    i += 2 + length;
                }
                return null;

            default:
                return null;
        }
    }

    private static (int, int)? Positive(long width, long height) =>
        width is > 0 and <= int.MaxValue && height is > 0 and <= int.MaxValue ? ((int)width, (int)height) : null;
}
