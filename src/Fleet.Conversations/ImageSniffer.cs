using System.Buffers.Binary;

namespace Fleet.Conversations;

/// <summary>
/// Decides an image's real container type from its magic bytes, and reads its declared dimensions
/// from the header (#308 D4).
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>Magic bytes and header fields only — this never decodes an image</b> (MUST NOT 16). A full
/// decode is a parser attack surface in a process that holds the transcript and the auth store, and
/// the only questions here are "which of four containers is this" and "is the declared pixel count
/// within the bound". Both are answerable from header fields alone.
/// </para>
/// <para>
/// PNG, GIF and WebP declare their dimensions within the first few dozen bytes, so
/// <see cref="Sniff"/> reads them from the same prefix it identifies the container with. JPEG does
/// not — its <c>SOF</c> marker sits past a chain of segments that a single EXIF block can push 64 KiB
/// in — so <see cref="TryReadJpegPixels"/> reads it by seeking the written file. Both are header
/// reads; neither touches a scan.
/// </para>
/// <para>
/// The consequence is that the pixel check trusts the header's own numbers. That is deliberate and
/// stated: a file lying about its dimensions gets through the pixel bound and is still bounded by
/// the 8 MiB byte cap, which is the limit that actually protects the volume. Believing the header is
/// a far smaller risk than running a decoder.
/// </para>
/// </remarks>
public static class ImageSniffer
{
    /// <summary>
    /// How many leading bytes the sniffer needs. WebP's <c>VP8X</c> canvas fields are the deepest
    /// read, at offset 24; 64 gives room without pulling a page.
    /// </summary>
    public const int PrefixBytes = 64;

    /// <summary>What the bytes actually are.</summary>
    public sealed record Sniffed
    {
        /// <summary>One of the four accepted types, or null when the container is not recognised.</summary>
        public string? ContentType { get; init; }

        /// <summary>Header-declared pixel count, or null when it could not be read.</summary>
        public long? Pixels { get; init; }
    }

    /// <summary>
    /// Identify the container and read its declared dimensions.
    /// </summary>
    /// <remarks>
    /// An unrecognised container returns a null <see cref="Sniffed.ContentType"/>, which the caller
    /// turns into a failed seal. That is the case AC-12 pins: a <c>.jpg</c>-declared file whose magic
    /// bytes are HTML never becomes a stored attachment, so it can never be served — and in
    /// particular can never be served as <c>text/html</c>.
    /// </remarks>
    public static Sniffed Sniff(ReadOnlySpan<byte> prefix)
    {
        // JPEG: FF D8 FF. Its dimensions live in a SOF marker an arbitrary distance in — past the
        // fixed prefix — so they are read separately by TryReadJpegPixels, which seeks the segment
        // chain on the written file rather than buffering it.
        if (prefix.Length >= 3 && prefix[0] == 0xFF && prefix[1] == 0xD8 && prefix[2] == 0xFF)
            return new Sniffed { ContentType = "image/jpeg" };

        // PNG: the 8-byte signature, then IHDR at 16 with width and height as big-endian uint32.
        if (prefix.Length >= 24 && prefix[..8].SequenceEqual(PngSignature))
        {
            var width = BinaryPrimitives.ReadUInt32BigEndian(prefix.Slice(16, 4));
            var height = BinaryPrimitives.ReadUInt32BigEndian(prefix.Slice(20, 4));
            return new Sniffed { ContentType = "image/png", Pixels = (long)width * height };
        }

        // GIF: "GIF87a" or "GIF89a", then width and height as little-endian uint16.
        if (prefix.Length >= 10
            && (prefix[..6].SequenceEqual(Gif87a) || prefix[..6].SequenceEqual(Gif89a)))
        {
            var width = BinaryPrimitives.ReadUInt16LittleEndian(prefix.Slice(6, 2));
            var height = BinaryPrimitives.ReadUInt16LittleEndian(prefix.Slice(8, 2));
            return new Sniffed { ContentType = "image/gif", Pixels = (long)width * height };
        }

        // WebP: RIFF container whose form type is "WEBP". Three chunk layouts carry dimensions
        // differently; an unrecognised one is still WebP, just without a pixel count.
        if (prefix.Length >= 16
            && prefix[..4].SequenceEqual(Riff)
            && prefix.Slice(8, 4).SequenceEqual(Webp))
        {
            return new Sniffed { ContentType = "image/webp", Pixels = WebpPixels(prefix) };
        }

        return new Sniffed();
    }

    /// <summary>
    /// How many JPEG segment headers are walked before giving up. Sixty-four is far past what any
    /// real file needs and bounds a hostile one.
    /// </summary>
    private const int MaxJpegSegments = 64;

    /// <summary>
    /// Read a JPEG's declared dimensions by walking its segment chain, or null when they are not
    /// reachable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>This walks segment HEADERS; it never decodes a scan.</b> Each step reads a two-byte
    /// marker and a two-byte length and then <i>seeks</i> past the payload — so the work is a handful
    /// of four-byte reads regardless of file size, and no image data is ever parsed (MUST NOT 16).
    /// It stops at <c>SOS</c>, because entropy-coded data follows and there is no length to trust
    /// after it.
    /// </para>
    /// <para>
    /// Why a stream rather than the fixed prefix <see cref="Sniff"/> uses: a single EXIF
    /// <c>APP1</c> segment may be 64 KiB on its own, so a prefix large enough to be reliable would
    /// have to be buffered in memory for every concurrent upload. Seeking costs nothing and is exact.
    /// </para>
    /// <para>
    /// <b>Null is not "within the bound".</b> A truncated file, a chain that ends at <c>SOS</c>
    /// without a <c>SOF</c>, or more than <see cref="MaxJpegSegments"/> segments all return null, and
    /// the caller treats that as "no pixel count available" — the attachment is then bounded by the
    /// byte cap alone. That residual is documented in the limits table rather than hidden.
    /// </para>
    /// </remarks>
    public static long? TryReadJpegPixels(Stream stream)
    {
        if (!stream.CanSeek) return null;

        try
        {
            stream.Position = 0;

            Span<byte> header = stackalloc byte[4];

            // SOI.
            if (!ReadExactly(stream, header[..2]) || header[0] != 0xFF || header[1] != 0xD8)
                return null;

            for (var segment = 0; segment < MaxJpegSegments; segment++)
            {
                // Markers may be preceded by any number of 0xFF fill bytes.
                int marker;
                do
                {
                    if (!ReadExactly(stream, header[..1])) return null;
                }
                while (header[0] != 0xFF);

                do
                {
                    if (!ReadExactly(stream, header[..1])) return null;
                    marker = header[0];
                }
                while (marker == 0xFF);

                // Standalone markers carry no length: RSTn, SOI, EOI, TEM.
                if (marker is >= 0xD0 and <= 0xD9 or 0x01) continue;

                // Start of scan — entropy-coded data follows and the chain is no longer walkable.
                if (marker == 0xDA) return null;

                if (!ReadExactly(stream, header[..2])) return null;
                var length = (header[0] << 8) | header[1];
                if (length < 2) return null;

                if (IsStartOfFrame(marker))
                {
                    // [precision:1][height:2][width:2][components:1]
                    Span<byte> frame = stackalloc byte[5];
                    if (!ReadExactly(stream, frame)) return null;

                    long height = (frame[1] << 8) | frame[2];
                    long width = (frame[3] << 8) | frame[4];

                    return height > 0 && width > 0 ? height * width : null;
                }

                stream.Position += length - 2;
            }

            return null;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or ObjectDisposedException)
        {
            // A pixel count that cannot be read is not a verification failure. The byte cap still
            // applies, and failing the seal here would reject a valid image over a read error.
            return null;
        }
    }

    /// <summary>
    /// The SOF markers that carry dimensions.
    /// </summary>
    /// <remarks>
    /// <c>C0–CF</c> minus the three that are not frame headers: <c>C4</c> (DHT), <c>C8</c> (JPG
    /// extension) and <c>CC</c> (DAC). Reading a length field out of one of those would produce a
    /// number that looks like a resolution and is not.
    /// </remarks>
    private static bool IsStartOfFrame(int marker) =>
        marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);

    private static bool ReadExactly(Stream stream, Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var got = stream.Read(buffer[read..]);
            if (got == 0) return false;
            read += got;
        }

        return true;
    }

    private static long? WebpPixels(ReadOnlySpan<byte> prefix)
    {
        var chunk = prefix.Slice(12, 4);

        // VP8X: 24-bit canvas width-1 and height-1, little-endian, at offset 24.
        if (chunk.SequenceEqual(Vp8x) && prefix.Length >= 30)
        {
            long width = prefix[24] | (prefix[25] << 8) | (prefix[26] << 16);
            long height = prefix[27] | (prefix[28] << 8) | (prefix[29] << 16);
            return (width + 1) * (height + 1);
        }

        // VP8 (lossy): a 14-bit width and height at offset 26, after the 3-byte start code.
        if (chunk.SequenceEqual(Vp8Lossy) && prefix.Length >= 30)
        {
            var width = BinaryPrimitives.ReadUInt16LittleEndian(prefix.Slice(26, 2)) & 0x3FFF;
            var height = BinaryPrimitives.ReadUInt16LittleEndian(prefix.Slice(28, 2)) & 0x3FFF;
            return (long)width * height;
        }

        // VP8L (lossless): 14-bit width-1 and height-1 packed across the four bytes at offset 21.
        if (chunk.SequenceEqual(Vp8Lossless) && prefix.Length >= 25)
        {
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(prefix.Slice(21, 4));
            var width = (int)(bits & 0x3FFF) + 1;
            var height = (int)((bits >> 14) & 0x3FFF) + 1;
            return (long)width * height;
        }

        return null;
    }

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static ReadOnlySpan<byte> Gif87a => "GIF87a"u8;
    private static ReadOnlySpan<byte> Gif89a => "GIF89a"u8;
    private static ReadOnlySpan<byte> Riff => "RIFF"u8;
    private static ReadOnlySpan<byte> Webp => "WEBP"u8;
    private static ReadOnlySpan<byte> Vp8x => "VP8X"u8;
    private static ReadOnlySpan<byte> Vp8Lossy => "VP8 "u8;
    private static ReadOnlySpan<byte> Vp8Lossless => "VP8L"u8;
}
