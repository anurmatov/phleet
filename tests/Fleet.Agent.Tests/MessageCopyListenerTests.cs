using System.Net;
using System.Text;
using System.Text.Json;
using Fleet.Agent.Services.MessageCopy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static Fleet.Agent.Tests.MessageCopyToolTests;
namespace Fleet.Agent.Tests;

public sealed class MessageCopyListenerTests
{
    [Fact]
    public async Task Listener_RealToolsCalls_UseParentAndRejectExtraArguments()
    {
        using var rig = new Rig();
        using var logs = LoggerFactory.Create(b => b.AddConsole());
        await using var listener = new MessageCopyListener(rig.Tools, logs);
        await listener.StartAsync(default); Assert.True(listener.Ready);
        using var http = new HttpClient { BaseAddress = new("http://127.0.0.1:8092"), Timeout = TimeSpan.FromSeconds(10) };
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/health")).StatusCode);
        var listed = await Rpc(http, "tools/list", "{}");
        var schema = listed.GetProperty("result").GetProperty("tools")[0].GetProperty("inputSchema");
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(new[] { "message_id", "to_chat_id" }, schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order());
        var extra = await Rpc(http, "tools/call", "{\"name\":\"copy_message\",\"arguments\":{\"message_id\":77,\"to_chat_id\":2002,\"approved\":true}}");
        Assert.True(extra.TryGetProperty("error", out _) || extra.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Empty(rig.Bot.Calls);
        var success = Rpc(http, "tools/call", "{\"name\":\"copy_message\",\"arguments\":{\"message_id\":77,\"to_chat_id\":2002}}");
        await rig.Bot.Prompted.Task.WaitAsync(TimeSpan.FromSeconds(5)); await rig.Tap();
        Assert.Equal("copied", Outcome(Text(await success)));
        rig.Current = null;
        var denied = await Rpc(http, "tools/call", "{\"name\":\"copy_message\",\"arguments\":{\"message_id\":77,\"to_chat_id\":2002}}");
        Assert.Equal("not_a_human_request", Outcome(Text(denied)));
        Assert.Equal(1, rig.Counter.Count("copied"));
    }
    private static string Text(JsonElement reply) => reply.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
    private static async Task<JsonElement> Rpc(HttpClient http, string method, string parameters)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, MessageCopyListener.Path)
        { Content = new StringContent($"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"{method}\",\"params\":{parameters}}}", Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json"); request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var payload = text.TrimStart().StartsWith('{') ? text : text.Split('\n').Single(l => l.StartsWith("data:", StringComparison.Ordinal))[5..].Trim();
        return JsonDocument.Parse(payload).RootElement.Clone();
    }
}
