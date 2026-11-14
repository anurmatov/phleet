using System.Text;

namespace Fleet.Protocol.Tests;

public class AttachmentFilesTests
{
    /// <summary>
    /// The agent's copy of a file is named with an extension from the list, lower-cased, or `.bin`.
    /// Nothing else about the name survives, so a name cannot choose what a shell or a tool does.
    /// </summary>
    [Theory]
    [InlineData("report.PDF", ".pdf")]
    [InlineData("notes.final.md", ".md")]
    [InlineData("archive.tar.gz", ".gz")]
    [InlineData("run.exe", ".bin")]
    [InlineData("payload.sh;rm -rf", ".bin")]
    [InlineData("noextension", ".bin")]
    [InlineData("trailingdot.", ".bin")]
    [InlineData(".bashrc", ".bin")]
    [InlineData("", ".bin")]
    [InlineData(null, ".bin")]
    public void SafeExtension_IsFromTheListOrUnknown(string? fileName, string expected)
    {
        Assert.Equal(expected, AttachmentFiles.SafeExtension(fileName));
    }

    /// <summary>A header this side did not write is trusted only as far as the list goes.</summary>
    [Theory]
    [InlineData(".pdf", ".pdf")]
    [InlineData(".PDF", ".bin")]
    [InlineData("../x", ".bin")]
    [InlineData(".sh; rm", ".bin")]
    [InlineData(null, ".bin")]
    public void KnownExtensionOrUnknown_AcceptsOnlyListMembers(string? header, string expected)
    {
        Assert.Equal(expected, AttachmentFiles.KnownExtensionOrUnknown(header));
    }

    [Fact]
    public void EveryListedExtension_IsLowerCaseDotAndPlain()
    {
        Assert.All(AttachmentFiles.Extensions, extension =>
        {
            Assert.StartsWith(".", extension);
            Assert.Equal(extension.ToLowerInvariant(), extension);
            Assert.All(extension[1..], c => Assert.True(char.IsAsciiLetterOrDigit(c), extension));
        });
        Assert.DoesNotContain(AttachmentFiles.UnknownExtension, AttachmentFiles.Extensions);
    }

    [Theory]
    [InlineData("C:\\Users\\me\\a.txt", "a.txt")]
    [InlineData("/tmp/b.txt", "b.txt")]
    [InlineData("..hidden", "hidden")]
    [InlineData("   ", null)]
    [InlineData("\u0000\u0007", null)]
    [InlineData(null, null)]
    public void SafeDownloadName_KeepsTheLastSegmentAndDropsWhatCouldBreakAHeader(string? fileName, string? expected)
    {
        Assert.Equal(expected, AttachmentFiles.SafeDownloadName(fileName));
    }

    [Fact]
    public void SafeDownloadName_IsBounded()
    {
        Assert.Equal(120, AttachmentFiles.SafeDownloadName(new string('x', 500))!.Length);
    }

    [Fact]
    public void APdf_IsKnownFromItsFirstBytes()
    {
        Assert.True(AttachmentFiles.LooksLikePdf(Encoding.ASCII.GetBytes("%PDF-1.7\n")));
        Assert.False(AttachmentFiles.LooksLikePdf(Encoding.ASCII.GetBytes("<html>%PDF-")));
        Assert.False(AttachmentFiles.LooksLikePdf([]));
    }
}
