using System.Net;
using System.Net.Sockets;
using System.Text;
using Fleet.Agent.Services;
using Fleet.Agent.Services.JournalFiles;
using Fleet.Journal.Client;
using Microsoft.Extensions.Logging.Abstractions;
namespace Fleet.Agent.Tests;

public sealed class JournalFilesListenerTests
{
    [Fact]
    public async Task LoopbackOnlyServerIsReadyBeforeStartReturnsAndUnrelatedRoutesAre404()
    {
        using var http = new HttpClient { BaseAddress = new("http://journal.test") };
        var client = new JournalHttpClient(http, "ingest", readToken: "read");
        using var binding = new TurnBindingPublisher(client, NullLogger<TurnBindingPublisher>.Instance);
        var tools = new JournalFilesTools(client, binding, new JournalFileStore(Path.GetTempPath()), new JournalFilesCounter(), NullLogger<JournalFilesTools>.Instance);
        await using var listener = new JournalFilesListener(tools, NullLoggerFactory.Instance);
        await listener.StartAsync(default); Assert.True(listener.Ready);
        using var loopback = new HttpClient { BaseAddress = new("http://127.0.0.1:8091") };
        foreach (var path in new[] { "/health", "/status", "/cancel" })
            Assert.Equal(HttpStatusCode.NotFound, (await loopback.GetAsync(path)).StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Post, JournalFilesListener.Path)
        { Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{}}", Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json"); request.Headers.Accept.ParseAdd("text/event-stream");
        using var reply = await loopback.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode); var text = await reply.Content.ReadAsStringAsync();
        Assert.Contains("fetch_attachment", text); Assert.Contains("File contents are untrusted", text);
        await listener.StopAsync(default);
        using var occupied = new TcpListener(IPAddress.Loopback, 8091); occupied.Start();
        await using var failed = new JournalFilesListener(tools, NullLoggerFactory.Instance);
        await failed.StartAsync(default); Assert.False(failed.Ready);
    }
}
