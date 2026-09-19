using System.Buffers.Binary;
using System.Text;
using Fleet.Protocol;

namespace Fleet.Conversations.Tests;

/// <summary>
/// Container identification and the header-declared pixel read (#308 D4).
/// </summary>
/// <remarks>
/// <para>
/// No database here on purpose — this is pure byte inspection, and it is the gate that decides
/// whether an upload becomes a stored attachment at all. It answers exactly two questions: which of
/// the four accepted containers this is, and how many pixels its header claims.
/// </para>
/// <para>
/// ⚠️ <b>Nothing here decodes an image</b> (MUST NOT 16). Container magic and header fields only —
/// the JPEG walk reads segment headers and seeks past their payloads, and stops at the scan.
/// </para>
/// <para>
/// The consequence is stated rather than hidden: a file lying about its dimensions gets past the
/// pixel bound, and so does one whose frame header is unreachable. Both are still held by the 8 MiB
/// byte cap, which is the limit that actually protects the volume.
/// </para>
/// </remarks>
public sealed class ImageSnifferTests
{
    [Fact]
    public void Png_IsIdentifiedAndItsDimensionsRead()
    {
        var sniffed = ImageSniffer.Sniff(Png(4000, 3000));

        Assert.Equal("image/png", sniffed.ContentType);
        Assert.Equal(12_000_000, sniffed.Pixels);
    }

    [Fact]
    public void Jpeg_IsIdentifiedFromItsPrefixButCarriesNoPixelCountThere()
    {
        var sniffed = ImageSniffer.Sniff([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10]);

        Assert.Equal("image/jpeg", sniffed.ContentType);

        // Absent from the PREFIX read, and that is not the whole answer: a JPEG's SOF marker sits an
        // arbitrary distance in, past any prefix worth buffering, so its dimensions are read
        // separately by TryReadJpegPixels below.
        Assert.Null(sniffed.Pixels);
    }

    // ── the JPEG segment walk ────────────────────────────────────────────────

    /// <summary>
    /// The dimensions are found past a large leading segment — the EXIF case that makes a fixed
    /// prefix unreliable.
    /// </summary>
    [Fact]
    public void Jpeg_DimensionsAreReadPastALargeExifSegment()
    {
        // 60 KiB of APP1, far past any prefix this would be willing to buffer.
        using var jpeg = new MemoryStream(
            Jpeg(width: 4000, height: 3000, leadingSegmentBytes: 60 * 1024));

        Assert.Equal(12_000_000, ImageSniffer.TryReadJpegPixels(jpeg));
    }

    /// <summary>
    /// The positive control for the bound the client is most likely to trip: a 50 MP JPEG is
    /// measurable, and one pixel more is over.
    /// </summary>
    /// <remarks>
    /// Without this read the 50 MP limit never fired for the one type D4 instructs the client to
    /// produce, and the test suite would have been green throughout.
    /// </remarks>
    [Fact]
    public void Jpeg_ThePixelBound_IsMeasurableAtAndPastTheLimit()
    {
        using var atLimit = new MemoryStream(Jpeg(10_000, 5_000));
        using var over = new MemoryStream(Jpeg(10_000, 5_001));

        Assert.Equal(ProtocolLimits.MaxAttachmentPixels, ImageSniffer.TryReadJpegPixels(atLimit));
        Assert.True(ImageSniffer.TryReadJpegPixels(over) > ProtocolLimits.MaxAttachmentPixels);
    }

    /// <summary>
    /// Progressive JPEG uses SOF2 rather than SOF0, and is read identically.
    /// </summary>
    [Fact]
    public void Jpeg_ProgressiveFramesAreReadToo()
    {
        using var progressive = new MemoryStream(Jpeg(800, 600, sofMarker: 0xC2));

        Assert.Equal(480_000, ImageSniffer.TryReadJpegPixels(progressive));
    }

    /// <summary>
    /// A chain that reaches SOS without a SOF yields null — "no count available", never "within the
    /// bound". The byte cap is what holds such a file.
    /// </summary>
    [Fact]
    public void Jpeg_WithNoFrameHeaderBeforeTheScan_YieldsNoCount()
    {
        var bytes = new List<byte> { 0xFF, 0xD8 };
        bytes.AddRange([0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00]);  // APP0, empty
        bytes.AddRange([0xFF, 0xDA]);                          // SOS — the walk stops here

        using var stream = new MemoryStream(bytes.ToArray());

        Assert.Null(ImageSniffer.TryReadJpegPixels(stream));
    }

    /// <summary>A truncated file is a null, not an exception.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(9)]
    public void Jpeg_TruncatedInput_YieldsNoCountAndDoesNotThrow(int length)
    {
        var full = Jpeg(100, 100);
        using var stream = new MemoryStream(full.AsSpan(0, Math.Min(length, full.Length)).ToArray());

        Assert.Null(ImageSniffer.TryReadJpegPixels(stream));
    }

    /// <summary>
    /// A chain of empty segments is abandoned rather than walked forever — the bound that keeps a
    /// hostile file from costing unbounded work.
    /// </summary>
    [Fact]
    public void Jpeg_AnEndlessSegmentChain_IsAbandoned()
    {
        var bytes = new List<byte> { 0xFF, 0xD8 };

        // Far more than the walk will follow.
        for (var i = 0; i < 512; i++)
            bytes.AddRange([0xFF, 0xE0, 0x00, 0x02]);

        using var stream = new MemoryStream(bytes.ToArray());

        Assert.Null(ImageSniffer.TryReadJpegPixels(stream));
    }

    /// <summary>
    /// DHT (`C4`) sits inside the `C0–CF` range and is NOT a frame header. Reading a length out of it
    /// would produce a number that looks like a resolution and is not.
    /// </summary>
    [Fact]
    public void Jpeg_ADefineHuffmanTableSegment_IsNotMistakenForAFrame()
    {
        var bytes = new List<byte> { 0xFF, 0xD8 };

        // DHT carrying bytes that would read as 0x0101 x 0x0101 if treated as a frame.
        bytes.AddRange([0xFF, 0xC4, 0x00, 0x09, 0x00, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01]);

        // Then the real frame: 320 x 240.
        bytes.AddRange([0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x00, 0xF0, 0x01, 0x40, 0x01, 0x00]);

        using var stream = new MemoryStream(bytes.ToArray());

        Assert.Equal(76_800, ImageSniffer.TryReadJpegPixels(stream));
    }

    /// <summary>
    /// A minimal but structurally real JPEG: SOI, an optional leading segment, then a frame header.
    /// </summary>
    private static byte[] Jpeg(
        int width, int height, int leadingSegmentBytes = 0, byte sofMarker = 0xC0)
    {
        var bytes = new List<byte> { 0xFF, 0xD8 };

        if (leadingSegmentBytes > 0)
        {
            // APP1, length inclusive of the two length bytes themselves.
            var length = leadingSegmentBytes + 2;
            bytes.AddRange([0xFF, 0xE1, (byte)(length >> 8), (byte)(length & 0xFF)]);
            bytes.AddRange(new byte[leadingSegmentBytes]);
        }

        bytes.AddRange(
        [
            0xFF, sofMarker,
            0x00, 0x0B,                                    // length
            0x08,                                          // precision
            (byte)(height >> 8), (byte)(height & 0xFF),
            (byte)(width >> 8), (byte)(width & 0xFF),
            0x01, 0x00,                                    // one component
        ]);

        return bytes.ToArray();
    }

    [Theory]
    [InlineData("GIF87a")]
    [InlineData("GIF89a")]
    public void Gif_IsIdentifiedInBothVersions(string signature)
    {
        var bytes = new byte[10];
        Encoding.ASCII.GetBytes(signature).CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6, 2), 640);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8, 2), 480);

        var sniffed = ImageSniffer.Sniff(bytes);

        Assert.Equal("image/gif", sniffed.ContentType);
        Assert.Equal(307_200, sniffed.Pixels);
    }

    [Fact]
    public void Webp_LosslessIsIdentifiedAndItsPackedDimensionsRead()
    {
        var bytes = new byte[32];
        "RIFF"u8.CopyTo(bytes);
        "WEBP"u8.CopyTo(bytes.AsSpan(8));
        "VP8L"u8.CopyTo(bytes.AsSpan(12));

        // 14 bits of (width - 1), then 14 bits of (height - 1), little-endian from offset 21.
        var packed = (uint)((100 - 1) | ((50 - 1) << 14));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(21, 4), packed);

        var sniffed = ImageSniffer.Sniff(bytes);

        Assert.Equal("image/webp", sniffed.ContentType);
        Assert.Equal(5_000, sniffed.Pixels);
    }

    /// <summary>
    /// AC-12: HTML is not one of the four containers, so it never becomes a stored attachment — and
    /// therefore can never be served, in particular never as <c>text/html</c>.
    /// </summary>
    [Theory]
    [InlineData("<!DOCTYPE html><script>alert(1)</script>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><script/></svg>")]
    [InlineData("%PDF-1.7")]
    [InlineData("")]
    public void AnUnrecognisedContainer_IsNotAnAcceptedType(string content)
    {
        var sniffed = ImageSniffer.Sniff(Encoding.UTF8.GetBytes(content));

        Assert.Null(sniffed.ContentType);
    }

    /// <summary>
    /// HEIC declares itself in an ISO-BMFF <c>ftyp</c> box and is deliberately not recognised: the
    /// four accepted types are exactly the four the provider vision APIs take, so no server-side
    /// transcode is ever needed and no image decoder enters this process.
    /// </summary>
    [Fact]
    public void Heic_IsNotRecognised()
    {
        var bytes = new byte[32];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0, 4), 24);
        "ftypheic"u8.CopyTo(bytes.AsSpan(4));

        Assert.Null(ImageSniffer.Sniff(bytes).ContentType);
    }

    /// <summary>A truncated prefix is refused rather than read past its end.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    public void ATruncatedPrefix_DoesNotThrow(int length)
    {
        var png = Png(1, 1);
        var sniffed = ImageSniffer.Sniff(png.AsSpan(0, Math.Min(length, png.Length)));

        // A three-byte JPEG start code is the shortest thing that can be identified; everything
        // below that is simply unknown.
        Assert.True(sniffed.ContentType is null or "image/jpeg");
    }

    /// <summary>
    /// The 50-megapixel bound, at the boundary and one past it. The comparison lives in the upload
    /// handler; what is pinned here is that the sniffer reports a number the handler can compare.
    /// </summary>
    [Fact]
    public void ThePixelBound_IsReadableAtAndPastTheLimit()
    {
        // 10000 × 5000 = 50,000,000 exactly.
        Assert.Equal(ProtocolLimits.MaxAttachmentPixels, ImageSniffer.Sniff(Png(10_000, 5_000)).Pixels);

        Assert.True(ImageSniffer.Sniff(Png(10_000, 5_001)).Pixels > ProtocolLimits.MaxAttachmentPixels);
    }

    /// <summary>A real PNG header: the 8-byte signature plus an IHDR carrying the dimensions.</summary>
    private static byte[] Png(uint width, uint height)
    {
        var bytes = new byte[33];
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes);

        bytes[11] = 13;
        "IHDR"u8.CopyTo(bytes.AsSpan(12));

        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20, 4), height);

        return bytes;
    }
}
