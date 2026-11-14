using System.Net;
using System.Net.Http.Headers;
using Fleet.Agent.Configuration;
using Fleet.Agent.Services;
using Fleet.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Tests;

/// <summary>
/// Files that are not pictures, on the agent's side: what the chat service serves becomes a file
/// on disk the agent's tools can open, named by the attachment's id and a listed extension, and a
/// line in the prompt pointing at it.
/// </summary>
public sealed class ConversationFileAttachmentTests : IDisposable
{
    private const string FileId = "01JATTACHMENTF10000000000";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fleet-conv-files-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private sealed class Served(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(answer(request));
    }

    private ConversationAttachmentFetcher Fetcher(string contentType, string? extensionHeader, string? attachmentDir = "set")
    {
        var handler = new Served(request =>
        {
            // Without this the chat service serves an agent images only.
            Assert.True(request.Headers.Contains(AttachmentFiles.AcceptsFilesHeader));

            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("the bytes"u8.ToArray()) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            if (extensionHeader is not null)
                response.Headers.TryAddWithoutValidation(AttachmentFiles.ExtensionHeader, extensionHeader);
            return response;
        });

        var south = new ConversationSouthClient(
            new HttpClient(handler),
            Options.Create(new ConversationsOptions { SouthBaseUrl = "http://south.invalid", SouthBearerToken = "t" }),
            NullLogger<ConversationSouthClient>.Instance);

        return new ConversationAttachmentFetcher(
            south,
            Options.Create(new TelegramOptions { AttachmentDir = attachmentDir == "set" ? _dir : attachmentDir ?? "" }),
            NullLogger<ConversationAttachmentFetcher>.Instance);
    }

    /// <summary>
    /// The copy is named by the attachment's id and the extension the chat service sent, and only
    /// when that extension is on the list: a header is trusted no further than that.
    /// </summary>
    [Theory]
    [InlineData("application/octet-stream", ".md", ".md")]
    [InlineData("application/octet-stream", ".bin", ".bin")]
    [InlineData("application/octet-stream", "/../../evil.sh", ".bin")]
    [InlineData("application/octet-stream", null, ".bin")]
    // A PDF is known from its bytes (the served type), whatever its name claimed.
    [InlineData("application/pdf", ".txt", ".pdf")]
    public async Task AFile_IsWrittenForTheAgentsTools_NamedByItsIdAndAListedExtension(
        string contentType, string? header, string expectedExtension)
    {
        var fetched = await Fetcher(contentType, header).FetchAsync([FileId], CancellationToken.None);

        Assert.Empty(fetched.Images);
        Assert.Equal(0, fetched.Unavailable);
        var file = Assert.Single(fetched.Documents);
        Assert.Equal(Path.Combine(_dir, $"conv-{FileId}{expectedExtension}"), file.FilePath);
        Assert.Equal("the bytes", await File.ReadAllTextAsync(file.FilePath!));
        Assert.Equal(contentType, file.MimeType);
    }

    /// <summary>One of the four image types is still a picture for the model, not a file.</summary>
    [Fact]
    public async Task AnImage_IsStillAnImage()
    {
        var fetched = await Fetcher("image/png", null).FetchAsync([FileId], CancellationToken.None);

        Assert.Single(fetched.Images);
        Assert.Empty(fetched.Documents);
    }

    /// <summary>
    /// A file has no in-memory form to hand a model, so one that cannot be written is reported as
    /// unavailable rather than passed on as nothing.
    /// </summary>
    [Fact]
    public async Task AFileThatCannotBeWritten_IsCountedUnavailable()
    {
        var fetched = await Fetcher("application/octet-stream", ".md", attachmentDir: null).FetchAsync([FileId], CancellationToken.None);

        Assert.Empty(fetched.Documents);
        Assert.Equal(1, fetched.Unavailable);
    }
}
