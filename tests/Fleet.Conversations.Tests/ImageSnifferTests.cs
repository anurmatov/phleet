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
/// ⚠️ <b>Nothing here decodes an image</b> (MUST NOT 16). The consequence is stated rather than
/// hidden: a file lying about its dimensions gets past the pixel bound and is still held by the
/// 8 MiB byte cap, which is the limit that actually protects the volume.
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
    public void Jpeg_IsIdentifiedAndReportsNoPixelCount()
    {
        var sniffed = ImageSniffer.Sniff([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10]);

        Assert.Equal("image/jpeg", sniffed.ContentType);

        // Deliberately absent: JPEG dimensions live in a SOF marker an arbitrary distance in, and
        // walking a segment chain to find it is the beginning of parsing the file. It is bounded by
        // bytes alone.
        Assert.Null(sniffed.Pixels);
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
