using System.Net;
using Fleet.Comms.Configuration;
using Fleet.Contracts;
using Fleet.Conversations;
using Fleet.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Comms.Tests;

/// <summary>
/// The agent-facing download of a file that is not an image: who is served it, and what is said
/// about it.
/// </summary>
public sealed class SouthFileAttachmentTests : IDisposable
{
    private const string Key = "south-file-test-key-0123456789abcdef0123456789abcdef";
    private const string FileId = "01JSOUTHFILE00000000000001";
    private const string ImageId = "01JSOUTHIMAGE0000000000001";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "fleet-south-files-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private async Task<(WebApplication App, HttpClient Client)> StartAsync(FakeConversationStore store)
    {
        var files = new AttachmentStore(_root, NullLogger.Instance);
        foreach (var id in store.Attachments.Keys)
        {
            using var bytes = new MemoryStream("the bytes"u8.ToArray());
            await files.WriteAsync(id, bytes, 9, CancellationToken.None);
        }

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, string.Empty);
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        var app = CommsApp.BuildSouthApp(builder, store, new CommsOptions
        {
            ConversationConnectionString = "configured",
            Journal = new() { TokenKeys = Key },
        }, attachments: files);

        await app.StartAsync();
        return (app, app.GetTestClient());
    }

    private static HttpRequestMessage Fetch(FakeConversationStore store, string id, bool acceptsFiles)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/attachments/{id}/content");
        request.Headers.TryAddWithoutValidation(
            "Authorization",
            "Bearer " + JournalTokens.Mint(JournalTokens.ParseKeys(Key)[0], Fleet.Shared.SignIn.AgentTokenPurpose, store.AgentName));
        if (acceptsFiles) request.Headers.TryAddWithoutValidation(AttachmentFiles.AcceptsFilesHeader, "1");
        return request;
    }

    private static FakeConversationStore StoreWithAFileAndAnImage(string fileName)
    {
        var store = new FakeConversationStore();
        store.Attachments[FileId] = FakeConversationStore.Attachment(FileId, contentType: AttachmentFiles.GenericContentType, byteSize: 9)
            with { Kind = AttachmentKind.Other, FileName = fileName };
        store.Attachments[ImageId] = FakeConversationStore.Attachment(ImageId, byteSize: 9);
        return store;
    }

    /// <summary>
    /// An agent that says it takes files is served one, with the extension to name its copy by:
    /// the file name's own when it is on the list, `.bin` otherwise, and never the name itself.
    /// </summary>
    [Theory]
    [InlineData("Q3 report.CSV", ".csv")]
    [InlineData("run.exe", ".bin")]
    [InlineData("../../evil.sh", ".sh")]
    public async Task AnAgentThatTakesFiles_IsServedTheFile_WithAListedExtension(string fileName, string expectedExtension)
    {
        var store = StoreWithAFileAndAnImage(fileName);
        var (app, client) = await StartAsync(store);
        await using var _ = app;

        var response = await client.SendAsync(Fetch(store, FileId, acceptsFiles: true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(AttachmentFiles.GenericContentType, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(expectedExtension, Assert.Single(response.Headers.GetValues(AttachmentFiles.ExtensionHeader)));
        Assert.Equal("the bytes", await response.Content.ReadAsStringAsync());
        // The name the person's device gave the file goes no further than its extension.
        Assert.DoesNotContain(response.Headers, h => h.Value.Any(v => v.Contains("evil", StringComparison.Ordinal) || v.Contains("report", StringComparison.Ordinal)));
    }

    /// <summary>
    /// An agent built before files hands whatever it fetches to the model as a picture. It is told
    /// the file is not available, which it already reports to the person, and is still served images.
    /// </summary>
    [Fact]
    public async Task AnAgentFromBeforeFiles_IsToldAFileIsNotAvailable_AndStillGetsImages()
    {
        var store = StoreWithAFileAndAnImage("notes.md");
        var (app, client) = await StartAsync(store);
        await using var _ = app;

        var file = await client.SendAsync(Fetch(store, FileId, acceptsFiles: false));
        Assert.Equal(HttpStatusCode.NotFound, file.StatusCode);

        var image = await client.SendAsync(Fetch(store, ImageId, acceptsFiles: false));
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.False(image.Headers.Contains(AttachmentFiles.ExtensionHeader));
    }
}
