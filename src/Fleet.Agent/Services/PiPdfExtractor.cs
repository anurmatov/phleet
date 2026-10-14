using System.Text;
using System.Text.RegularExpressions;
using Fleet.Agent.Models;
using Fleet.Shared;
using Microsoft.Extensions.Logging;

namespace Fleet.Agent.Services;

/// <summary>
/// What one PDF contributes to a Pi command (#455 §3.3): an inline text excerpt with its header
/// facts, page renders for a vision model, or a named failure.
/// </summary>
internal sealed record PiPdfResult
{
    public required string Name { get; init; }

    /// <summary>The inline excerpt, at most <see cref="PiLocalModel.MaxInlinePdfChars"/>; null with no text.</summary>
    public string? Excerpt { get; init; }

    /// <summary>The extracted text is longer than <see cref="Excerpt"/>.</summary>
    public bool MoreText { get; init; }

    /// <summary>The full text file next to the attachment, or null when it could not be written.</summary>
    public string? SidecarPath { get; init; }

    /// <summary><c>p</c>: the pages <c>pdftotext</c> read, min(100, P).</summary>
    public int PagesRead { get; init; }

    /// <summary><c>P</c>, from <c>pdfinfo</c>.</summary>
    public int PageCount { get; init; }

    /// <summary>The text reached the 4 MiB cap and was kept truncated.</summary>
    public bool Truncated { get; init; }

    /// <summary>JPEG page renders, each within the image bounds; they hold one image slot each.</summary>
    public IReadOnlyList<byte[]> Renders { get; init; } = [];

    /// <summary>The bracketed notes for the model, in order.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>The §5 reason when the PDF produced neither text nor renders; null otherwise.</summary>
    public string? FailureReason { get; init; }
}

/// <summary>
/// Bounded local PDF extraction for every Pi agent (#455 §3.3): <c>pdfinfo</c>, then
/// <c>pdftotext</c>, then — only with image input and a free image slot — <c>pdftoppm</c> JPEG
/// renders of a PDF with no text layer or with text copying restricted. Every tool runs through
/// <see cref="IPiLocalTools"/>. No password is ever supplied and no permission is bypassed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Copy restriction.</b> poppler 24.02 <c>pdftotext</c> is built without
/// <c>ENFORCE_PERMISSIONS</c>: on a copy-restricted PDF it exits 0 with the text (measured, A5b),
/// not 3. So the restriction is read from <c>pdfinfo</c>'s <c>Encrypted:</c> line (<c>copy:no</c>)
/// and <c>pdftotext</c> is never run on such a file; exit 3 is still honoured for a build that
/// enforces it.
/// </para>
/// <para>
/// <b>Cache.</b> Keyed by (path, size, mtime); at most <see cref="PiLocalModel.PdfCacheEntries"/>
/// entries, least recently used evicted first, an entry whose file is gone evicted early. It holds
/// the excerpt, page counts, sidecar path and outcome only — never render bytes; renders are redone
/// on a hit. A steered PDF that is queued and re-sent as a prompt therefore runs <c>pdftotext</c> once.
/// </para>
/// </remarks>
internal sealed partial class PiPdfExtractor(IPiLocalTools tools, ILogger logger)
{
    public const string PdfInfo = "pdfinfo";
    public const string PdfToText = "pdftotext";
    public const string PdfToPpm = "pdftoppm";

    private const int SmallOutputCap = 64 * 1024;

    /// <summary>The render scales tried in turn for a page over 3 MiB (§3.3 step 3).</summary>
    internal static IReadOnlyList<int> RenderScales { get; } = [1600, 1200, 800];

    private readonly object _cacheLock = new();
    private readonly LinkedList<CacheEntry> _cache = new();
    private int _unavailableLogged;

    /// <summary>The cached entries, for the tests (the bound and that no bytes are held).</summary>
    internal int CacheCount
    {
        get { lock (_cacheLock) return _cache.Count; }
    }

    public static bool IsPdf(MessageDocument document) =>
        string.Equals(document.MimeType, "application/pdf", StringComparison.OrdinalIgnoreCase)
        || (document.FileName ?? "").EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
        || (document.FilePath ?? "").EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>The name a note uses: the sender's file name, else the saved one; brackets and line breaks replaced.</summary>
    public static string DisplayName(MessageDocument document)
    {
        var name = document.FileName is { Length: > 0 } given ? given
            : document.FilePath is { Length: > 0 } path ? Path.GetFileName(path) : "attachment.pdf";
        return NoteUnsafe().Replace(name, "_");
    }

    public async Task<PiPdfResult> ExtractAsync(
        MessageDocument document, int imageSlotsLeft, bool imageInput, PiProcessingBudget budget, CancellationToken ct)
    {
        var name = DisplayName(document);
        if (document.FilePath is not { Length: > 0 } path || !File.Exists(path))
            return Failed(name, PiLocalModel.PdfReasons.FileMissing);

        if (!StartsLikePdf(path))
            return Failed(name, PiLocalModel.PdfReasons.NotAPdf);

        var key = CacheKey.Of(path);
        var cached = Lookup(key);
        var info = cached?.Info;
        if (info is null)
        {
            var analysis = await AnalyseAsync(name, path, budget, ct);
            if (analysis.Failure is { } failure)
                return failure;
            info = analysis.Info!;
            if (analysis.Cacheable)
                Store(key, info);
        }

        if (info.Reason is { } reason)
            return Failed(name, reason);

        if (info.NeedsRender is not { } why)
        {
            return new PiPdfResult
            {
                Name = name,
                Excerpt = info.Excerpt,
                MoreText = info.MoreText,
                SidecarPath = info.SidecarPath,
                PagesRead = info.PagesRead,
                PageCount = info.PageCount,
                Truncated = info.Truncated,
            };
        }

        return await RenderAsync(name, path, info.PageCount, why, imageSlotsLeft, imageInput, budget, ct);
    }

    // ── Steps 1 and 2: pdfinfo, pdftotext ────────────────────────────────────

    private sealed record Analysis(PdfFacts? Info, PiPdfResult? Failure, bool Cacheable);

    private async Task<Analysis> AnalyseAsync(string name, string path, PiProcessingBudget budget, CancellationToken ct)
    {
        // Step 1.
        var probe = await tools.RunAsync(PdfInfo, [path], budget, SmallOutputCap, ct);
        if (Transient(name, probe) is { } transient)
            return new Analysis(null, transient, false);
        if (probe.ExitCode != 0)
        {
            var reason = probe.ExitCode == 1 && probe.StderrTail.Contains("Incorrect password", StringComparison.Ordinal)
                ? PiLocalModel.PdfReasons.PasswordProtected
                : PiLocalModel.PdfReasons.Damaged;
            return new Analysis(new PdfFacts { Reason = reason }, null, true);
        }

        var facts = Encoding.UTF8.GetString(probe.Stdout);
        var pages = PagesLine().Match(facts) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var p) ? p : 0;
        var encrypted = EncryptedLine().Match(facts);
        if (encrypted.Success && encrypted.Groups[1].Value.Contains("copy:no", StringComparison.Ordinal))
            return new Analysis(new PdfFacts { PageCount = pages, NeedsRender = RenderWhy.CopyRestricted }, null, true);

        // Step 2.
        var pagesToRead = Math.Min(PiLocalModel.MaxPdfPages, Math.Max(pages, 1));
        var text = await tools.RunAsync(PdfToText,
            ["-enc", "UTF-8", "-layout", "-f", "1", "-l", PiLocalModel.MaxPdfPages.ToString(), path, "-"],
            budget, PiLocalModel.MaxPdfTextBytes, ct);
        if (!text.Capped && Transient(name, text) is { } failed)
            return new Analysis(null, failed, false);
        if (!text.Capped && text.ExitCode == 3)
            return new Analysis(new PdfFacts { PageCount = pages, NeedsRender = RenderWhy.CopyRestricted }, null, true);
        if (!text.Capped && text.ExitCode != 0)
            return new Analysis(new PdfFacts { Reason = PiLocalModel.PdfReasons.Damaged }, null, true);

        var full = DecodeUtf8Prefix(text.Stdout);
        if (full.Count(c => !char.IsWhiteSpace(c)) < PiLocalModel.MinTextLayerChars)
            return new Analysis(new PdfFacts { PageCount = pages, NeedsRender = RenderWhy.NoTextLayer }, null, true);

        var sidecar = WriteSidecar(path, full);
        var excerpt = full.Length > PiLocalModel.MaxInlinePdfChars ? full[..PiLocalModel.MaxInlinePdfChars] : full;
        return new Analysis(new PdfFacts
        {
            Excerpt = excerpt,
            MoreText = full.Length > excerpt.Length,
            SidecarPath = sidecar,
            PagesRead = pages > 0 ? Math.Min(pagesToRead, pages) : 0,
            PageCount = pages,
            Truncated = text.Capped,
        }, null, true);
    }

    /// <summary>Timeouts and a missing extractor are not properties of the file: never cached.</summary>
    private PiPdfResult? Transient(string name, PiToolResult result)
    {
        if (result.TimedOut)
            return Failed(name, PiLocalModel.PdfReasons.TimedOut);
        if (result.Unavailable)
        {
            if (Interlocked.Exchange(ref _unavailableLogged, 1) == 0)
                logger.LogError("pi pdf: extractor unavailable");
            return Failed(name, PiLocalModel.PdfReasons.ExtractorUnavailable);
        }
        return null;
    }

    /// <summary>The full text next to the attachment, through a temporary file and a rename; null on failure.</summary>
    private string? WriteSidecar(string path, string text)
    {
        var sidecar = path + ".txt";
        var tmp = sidecar + ".tmp";
        try
        {
            File.WriteAllText(tmp, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tmp, sidecar, overwrite: true);
            return sidecar;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("pi pdf: the full-text file could not be written; the inline text is still sent");
            try
            {
                File.Delete(tmp);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }
            return null;
        }
    }

    // ── Step 3: renders ──────────────────────────────────────────────────────

    private enum RenderWhy { NoTextLayer, CopyRestricted }

    private async Task<PiPdfResult> RenderAsync(
        string name, string path, int pageCount, RenderWhy why, int slots, bool imageInput,
        PiProcessingBudget budget, CancellationToken ct)
    {
        var what = why == RenderWhy.NoTextLayer ? "has no text layer" : "text copying restricted";
        var reason = why == RenderWhy.NoTextLayer
            ? PiLocalModel.PdfReasons.NoTextLayer
            : PiLocalModel.PdfReasons.CopyRestricted;

        if (!imageInput)
            return Failed(name, reason, $"[PDF {name} {what} and this model is text-only: not readable]");
        if (slots < 1)
            return Failed(name, PiLocalModel.PdfReasons.NoImageSlotLeft,
                $"[PDF {name} {what}: no image slot left in this message; send it on its own]");

        var last = Math.Min(slots, Math.Max(pageCount, 1));
        var dir = Directory.CreateTempSubdirectory("fleet-pi-pdf-").FullName;
        try
        {
            var first = await tools.RunAsync(PdfToPpm, RenderArgs(RenderScales[0], 1, last, path, Path.Combine(dir, "p")),
                budget, SmallOutputCap, ct);
            if (Transient(name, first) is { } failed)
                return failed;
            if (first.ExitCode != 0)
                return Failed(name, PiLocalModel.PdfReasons.Damaged);

            var renders = new List<byte[]>();
            var sent = new List<int>();
            var notes = new List<string>();
            foreach (var (page, file) in RenderedPages(dir, "p"))
            {
                var bytes = await File.ReadAllBytesAsync(file, ct);
                foreach (var scale in RenderScales.Skip(1))
                {
                    if (bytes.Length <= PiLocalModel.MaxImageBytes)
                        break;
                    var single = Path.Combine(dir, $"s{page}-{scale}");
                    var again = await tools.RunAsync(PdfToPpm, RenderArgs(scale, page, page, path, single, singleFile: true),
                        budget, SmallOutputCap, ct);
                    if (again.TimedOut || again.Unavailable || again.ExitCode != 0 || !File.Exists(single + ".jpg"))
                        break;
                    bytes = await File.ReadAllBytesAsync(single + ".jpg", ct);
                }

                if (bytes.Length > PiLocalModel.MaxImageBytes)
                {
                    notes.Add($"[PDF {name} page {page}: {PiLocalModel.ImageReasons.Over3MiB}, not sent]");
                    continue;
                }
                if (!PiAttachments.IsWithinBounds(bytes))
                {
                    notes.Add($"[PDF {name} page {page}: {PiLocalModel.ImageReasons.Unreadable}, not sent]");
                    continue;
                }
                renders.Add(bytes);
                sent.Add(page);
            }

            if (renders.Count == 0)
                return Failed(name, reason, notes.Count > 0 ? null : $"[PDF {name} {what}: no page could be rendered]", notes);

            notes.Insert(0, $"[PDF {name} {what}: pages {PageList(sent)} of {pageCount} sent as images]");
            return new PiPdfResult { Name = name, PageCount = pageCount, Renders = renders, Notes = notes };
        }
        finally
        {
            PiLocalTools.TryDeleteDirectory(dir);
        }
    }

    internal static IReadOnlyList<string> RenderArgs(
        int scale, int first, int last, string path, string prefix, bool singleFile = false) =>
    [
        "-jpeg", "-jpegopt", "quality=85", "-r", "96", "-scale-to", scale.ToString(),
        "-f", first.ToString(), "-l", last.ToString(), .. (singleFile ? new[] { "-singlefile" } : []), path, prefix,
    ];

    /// <summary>pdftoppm's <c>prefix-N.jpg</c> files (N zero-padded to the page count's digits), by page.</summary>
    private static IEnumerable<(int Page, string File)> RenderedPages(string dir, string prefix) =>
        Directory.EnumerateFiles(dir, $"{prefix}-*.jpg")
            .Select(file => (Page: int.TryParse(Path.GetFileNameWithoutExtension(file)[(prefix.Length + 1)..], out var n) ? n : 0, File: file))
            .Where(r => r.Page > 0)
            .OrderBy(r => r.Page)
            .ToList();

    /// <summary>"1–3" for a run, "1, 3" otherwise.</summary>
    internal static string PageList(IReadOnlyList<int> pages)
    {
        var contiguous = pages.Select((page, i) => page - i).Distinct().Count() == 1;
        return pages.Count == 1 ? $"{pages[0]}"
            : contiguous ? $"{pages[0]}–{pages[^1]}"
            : string.Join(", ", pages);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static PiPdfResult Failed(string name, string reason, string? note = null, IReadOnlyList<string>? more = null)
    {
        var notes = new List<string>();
        notes.Add(note ?? reason switch
        {
            PiLocalModel.PdfReasons.NotAPdf => $"[PDF {name}: {reason}]",
            _ => $"[PDF {name} could not be read: {reason}]",
        });
        if (more is not null)
            notes.AddRange(more);
        return new PiPdfResult { Name = name, Notes = notes, FailureReason = reason };
    }

    private static bool StartsLikePdf(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var head = new byte[1024];
            var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            return head.AsSpan(0, read).IndexOf("%PDF-"u8) >= 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>UTF-8 text from a prefix that may end inside a multi-byte sequence (the 4 MiB cut).</summary>
    private static string DecodeUtf8Prefix(byte[] bytes)
    {
        var end = bytes.Length;
        // Back off over at most three continuation bytes to the start of the last sequence.
        var back = 0;
        while (back < 3 && end - back - 1 >= 0 && (bytes[end - back - 1] & 0xC0) == 0x80)
            back++;
        if (end - back - 1 >= 0)
        {
            var lead = bytes[end - back - 1];
            var length = lead < 0x80 ? 1 : lead >= 0xF0 ? 4 : lead >= 0xE0 ? 3 : lead >= 0xC0 ? 2 : 1;
            if (length > back + 1)
                end -= back + 1;
        }
        return Encoding.UTF8.GetString(bytes, 0, end);
    }

    // ── Cache ────────────────────────────────────────────────────────────────

    private sealed record PdfFacts
    {
        public string? Excerpt { get; init; }
        public bool MoreText { get; init; }
        public string? SidecarPath { get; init; }
        public int PagesRead { get; init; }
        public int PageCount { get; init; }
        public bool Truncated { get; init; }
        public string? Reason { get; init; }
        public RenderWhy? NeedsRender { get; init; }
    }

    private readonly record struct CacheKey(string Path, long Size, long MtimeTicks)
    {
        public static CacheKey Of(string path)
        {
            var info = new FileInfo(path);
            return new CacheKey(info.FullName, info.Length, info.LastWriteTimeUtc.Ticks);
        }
    }

    private sealed record CacheEntry(CacheKey Key, PdfFacts Info);

    private CacheEntry? Lookup(CacheKey key)
    {
        lock (_cacheLock)
        {
            EvictMissing();
            for (var node = _cache.First; node is not null; node = node.Next)
            {
                if (node.Value.Key != key)
                    continue;
                _cache.Remove(node);
                _cache.AddFirst(node);
                return node.Value;
            }
            return null;
        }
    }

    private void Store(CacheKey key, PdfFacts info)
    {
        lock (_cacheLock)
        {
            for (var node = _cache.First; node is not null; node = node.Next)
            {
                if (node.Value.Key == key)
                {
                    _cache.Remove(node);
                    break;
                }
            }
            _cache.AddFirst(new CacheEntry(key, info));
            while (_cache.Count > PiLocalModel.PdfCacheEntries)
                _cache.RemoveLast();
        }
    }

    private void EvictMissing()
    {
        for (var node = _cache.First; node is not null;)
        {
            var next = node.Next;
            if (!File.Exists(node.Value.Key.Path))
                _cache.Remove(node);
            node = next;
        }
    }

    [GeneratedRegex(@"^Pages:\s+(\d+)\s*$", RegexOptions.Multiline)]
    private static partial Regex PagesLine();

    [GeneratedRegex(@"^Encrypted:\s+(.*)$", RegexOptions.Multiline)]
    private static partial Regex EncryptedLine();

    [GeneratedRegex(@"[\[\]\r\n\t]")]
    private static partial Regex NoteUnsafe();
}
