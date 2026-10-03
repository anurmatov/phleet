using System.Security.Cryptography;
using System.Text;
using Fleet.Agent.Services.JournalFiles;
namespace Fleet.Agent.Tests;
public sealed class JournalVerifyingUploadTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidMultipart_IsKnownLengthAndOnlyDestinationAndFile(bool photo)
    {
        var bytes = Encoding.UTF8.GetBytes("synthetic bytes");
        using var file = new VerifyingUploadContent(new MemoryStream(bytes), bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        var request = new JournalUploadRequest(101, photo, file, "../../unsafe\"name.pdf");
        using var content = request.ToHttpContent();
        var body = await content.ReadAsByteArrayAsync(); var text = Encoding.UTF8.GetString(body);
        Assert.Equal(body.Length, content.Headers.ContentLength);
        Assert.Contains("name=chat_id", text); Assert.Contains(photo ? "name=photo" : "name=document", text);
        Assert.DoesNotContain("caption", text); Assert.DoesNotContain("../../", text);
        var boundary = content.Headers.ContentType!.Parameters.Single(p => p.Name == "boundary").Value!.Trim('"');
        var golden = text.Replace(boundary, "@boundary", StringComparison.Ordinal);
        Assert.Equal(System.Text.Json.JsonSerializer.Deserialize<string>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "journal-send", "413-multipart-" + (photo ? "photo" : "document") + ".json"))), golden);
        Assert.Equal(2, text.Split("Content-Disposition:").Length - 1);
        Assert.True(request.TerminatorWritten);
    }
    [Theory]
    [InlineData(-1, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    public async Task InvalidIntegrity_ThrowsBeforeTerminator(int lengthDelta, bool badDigest)
    {
        var bytes = Encoding.UTF8.GetBytes("synthetic bytes");
        using var file = new VerifyingUploadContent(new MemoryStream(bytes), bytes.Length + lengthDelta,
            badDigest ? new string('0', 64) : Convert.ToHexStringLower(SHA256.HashData(bytes)));
        var request = new JournalUploadRequest(101, false, file, "attachment.bin");
        using var content = request.ToHttpContent();
        using var sink = new MemoryStream();
        var error = await Assert.ThrowsAnyAsync<Exception>(() => content.CopyToAsync(sink));
        Assert.True(error is JournalUploadIntegrityException || error.InnerException is JournalUploadIntegrityException);
        Assert.False(request.TerminatorWritten);
        Assert.True(sink.Length < content.Headers.ContentLength);
    }
}
