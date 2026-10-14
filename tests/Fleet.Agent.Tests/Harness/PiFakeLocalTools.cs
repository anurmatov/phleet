using System.Buffers.Binary;
using System.Text;
using Fleet.Agent.Services;
using Fleet.Shared;

namespace Fleet.Agent.Tests.Harness;

/// <summary>
/// A stand-in for <see cref="PiLocalTools"/> (#455): it records every call and answers like
/// poppler and ffmpeg would, without running either. A test replaces <see cref="Handler"/> for the
/// row it is about. Timeouts follow the real runner's rule — a call waits at most min(per-call
/// timeout, what is left of the command budget) — so the deadline rows hold.
/// </summary>
internal sealed class FakePiLocalTools : IPiLocalTools
{
    private readonly List<(string Tool, IReadOnlyList<string> Args)> _calls = [];

    /// <summary>pdfinfo's stdout for the default handler.</summary>
    public string PdfInfoText { get; set; } = "Pages:          3\nEncrypted:      no\n";

    /// <summary>pdftotext's stdout for the default handler.</summary>
    public string PdfText { get; set; } = "Synthetic page one text, long enough to count as a text layer.\n";

    /// <summary>The bytes the default pdftoppm writes for a page at a scale.</summary>
    public Func<int, int, byte[]> RenderPage { get; set; } = (_, scale) => TestImages.Jpeg(scale * 707 / 1000, scale, 4096);

    public TimeSpan CallTimeout { get; set; } = PiLocalModel.LocalToolCallTimeout;

    /// <summary>Replaces the default behaviour; return null to fall through to it.</summary>
    public Func<string, IReadOnlyList<string>, PiProcessingBudget, CancellationToken, Task<PiToolResult?>>? Handler { get; set; }

    public IReadOnlyList<(string Tool, IReadOnlyList<string> Args)> Calls
    {
        get { lock (_calls) return _calls.ToList(); }
    }

    public int Count(string tool) => Calls.Count(c => c.Tool == tool);

    public async Task<PiToolResult> RunAsync(
        string tool, IReadOnlyList<string> args, PiProcessingBudget budget, int stdoutCap, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (budget.Expired)
            return PiToolResult.Expired;
        lock (_calls)
            _calls.Add((tool, args.ToList()));

        if (Handler is { } handler && await handler(tool, args, budget, ct) is { } scripted)
            return Cap(scripted, stdoutCap);

        return tool switch
        {
            PiPdfExtractor.PdfInfo => Ok(PdfInfoText),
            PiPdfExtractor.PdfToText => Cap(Ok(PdfText), stdoutCap),
            PiPdfExtractor.PdfToPpm => Render(args),
            PiAttachments.FfmpegBinary => Downscale(args),
            _ => Exit(127, $"prlimit: failed to execute {tool}: No such file or directory"),
        };
    }

    /// <summary>Blocks like a wedged tool: until the call's timeout, then timed out (or cancelled).</summary>
    public async Task<PiToolResult> BlockAsync(PiProcessingBudget budget, CancellationToken ct)
    {
        var wait = budget.Remaining < CallTimeout ? budget.Remaining : CallTimeout;
        await Task.Delay(wait, ct);
        return PiToolResult.Expired;
    }

    public static PiToolResult Ok(string stdout) => new(0, Encoding.UTF8.GetBytes(stdout), "", false, false, false);

    public static PiToolResult Ok(byte[] stdout) => new(0, stdout, "", false, false, false);

    public static PiToolResult Exit(int code, string stderr = "") =>
        new(code, [], stderr, false, false, Unavailable: code == 127 && stderr.Contains("failed to execute"));

    private static PiToolResult Cap(PiToolResult result, int cap) =>
        result.Stdout.Length > cap ? result with { Stdout = result.Stdout[..cap], Capped = true } : result;

    /// <summary>pdftoppm: <c>prefix-N.jpg</c> per page, or <c>prefix.jpg</c> with <c>-singlefile</c>.</summary>
    private PiToolResult Render(IReadOnlyList<string> args)
    {
        var scale = int.Parse(args[args.ToList().IndexOf("-scale-to") + 1]);
        var first = int.Parse(args[args.ToList().IndexOf("-f") + 1]);
        var last = int.Parse(args[args.ToList().IndexOf("-l") + 1]);
        var prefix = args[^1];
        if (args.Contains("-singlefile"))
        {
            File.WriteAllBytes(prefix + ".jpg", RenderPage(first, scale));
        }
        else
        {
            for (var page = first; page <= last; page++)
                File.WriteAllBytes($"{prefix}-{page}.jpg", RenderPage(page, scale));
        }
        return Ok("");
    }

    /// <summary>ffmpeg: the input's header clamped to 2000 px with its aspect, in the output's format.</summary>
    private static PiToolResult Downscale(IReadOnlyList<string> args)
    {
        var input = File.ReadAllBytes(args[args.ToList().IndexOf("-i") + 1]);
        var (w, h) = PiAttachments.ReadDimensions(input, PiAttachments.Sniff(input)!)!.Value;
        var scale = Math.Min(1.0, Math.Min(2000.0 / w, 2000.0 / h));
        var (ow, oh) = ((int)(w * scale), (int)(h * scale));
        var output = args[^1];
        File.WriteAllBytes(output, output.EndsWith(".png") ? TestImages.Png(ow, oh, 2048) : TestImages.Jpeg(ow, oh, 2048));
        return Ok("");
    }
}

/// <summary>
/// Header-only images for the §3.2 rows: valid magic bytes and dimensions, padded to a size. Nothing
/// decodes them; only <see cref="PiAttachments.Sniff"/> and <see cref="PiAttachments.ReadDimensions"/> read them.
/// </summary>
internal static class TestImages
{
    public static byte[] Png(int width, int height, int size = 64)
    {
        var bytes = new byte[Math.Max(size, 33)];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), (uint)height);
        bytes[24] = 8;
        bytes[25] = 2;
        return bytes;
    }

    public static byte[] Jpeg(int width, int height, int size = 64)
    {
        var bytes = new byte[Math.Max(size, 40)];
        var header = new byte[]
        {
            0xFF, 0xD8,
            0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0,
            0xFF, 0xC0, 0x00, 0x11, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 0x03,
        };
        header.CopyTo(bytes, 0);
        bytes[^2] = 0xFF;
        bytes[^1] = 0xD9;
        return bytes;
    }

    public static byte[] Gif(int width, int height)
    {
        var bytes = new byte[32];
        "GIF89a"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), (ushort)height);
        return bytes;
    }

    public static byte[] WebpVp8(int width, int height) => Webp("VP8 "u8, chunk =>
    {
        chunk[3] = 0x9D;
        chunk[4] = 0x01;
        chunk[5] = 0x2A;
        BinaryPrimitives.WriteUInt16LittleEndian(chunk[6..], (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(chunk[8..], (ushort)height);
    });

    public static byte[] WebpVp8L(int width, int height) => Webp("VP8L"u8, chunk =>
    {
        chunk[0] = 0x2F;
        var bits = (uint)(width - 1) | ((uint)(height - 1) << 14);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk[1..], bits);
    });

    public static byte[] WebpVp8X(int width, int height) => Webp("VP8X"u8, chunk =>
    {
        var w = width - 1;
        var h = height - 1;
        chunk[4] = (byte)w;
        chunk[5] = (byte)(w >> 8);
        chunk[6] = (byte)(w >> 16);
        chunk[7] = (byte)h;
        chunk[8] = (byte)(h >> 8);
        chunk[9] = (byte)(h >> 16);
    });

    private delegate void ChunkWriter(Span<byte> chunk);

    private static byte[] Webp(ReadOnlySpan<byte> fourCc, ChunkWriter write)
    {
        var bytes = new byte[48];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(bytes.Length - 8));
        "WEBP"u8.CopyTo(bytes.AsSpan(8));
        fourCc.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 20);
        write(bytes.AsSpan(20));
        return bytes;
    }
}
