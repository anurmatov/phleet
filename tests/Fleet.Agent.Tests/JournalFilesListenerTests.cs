using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Fleet.Agent.Services;
using Fleet.Agent.Services.JournalFiles;
using Fleet.Conversations.Contracts;
using Fleet.Journal.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static Fleet.Agent.Tests.JournalFilesTestDoubles;
namespace Fleet.Agent.Tests;

public sealed class JournalFilesListenerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "journal-listener-" + Guid.NewGuid().ToString("N"));

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

    [Fact]
    public async Task T1_BoundToolsCallRunsOnTheListenerInstance()
    {
        var handler = new Handler(); using var http = new HttpClient(handler) { BaseAddress = new("http://journal.test") };
        var client = new JournalHttpClient(http, "ingest", readToken: "read"); using var binding = Publisher(client); Bind(binding);
        var counter = new JournalFilesCounter();
        var tools = new JournalFilesTools(client, binding, new JournalFileStore(_root), counter, NullLogger<JournalFilesTools>.Instance);
        var listener = await StartAsync(tools, NullLoggerFactory.Instance);
        try
        {
            var result = await CallAsync(1);
            Assert.False(IsError(result));
            using var json = JsonDocument.Parse(Text(result));
            Assert.Equal(["byte_size", "expires_at", "kind", "message_id", "mime_type", "ordinal", "path"],
                json.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
            Assert.Equal(Bytes, await File.ReadAllBytesAsync(json.RootElement.GetProperty("path").GetString()!));
            Assert.Equal("Bearer read", handler.Header);
            Assert.Equal(1, counter.Count("ok"));
        }
        finally { await listener.DisposeAsync(); }
    }

    [Fact]
    public async Task T2_UnboundToolsCallRefusesWithoutCallingComms()
    {
        var handler = new Handler(); using var http = new HttpClient(handler) { BaseAddress = new("http://journal.test") };
        var client = new JournalHttpClient(http, "ingest", readToken: "read"); using var binding = Publisher(client);
        var counter = new JournalFilesCounter();
        var tools = new JournalFilesTools(client, binding, new JournalFileStore(_root), counter, NullLogger<JournalFilesTools>.Instance);
        var listener = await StartAsync(tools, NullLoggerFactory.Instance);
        try
        {
            var result = await CallAsync(1);
            Assert.False(IsError(result));
            Assert.Equal(JournalAttachmentRequest.Unavailable("no_bound_conversation"), Text(result));
            Assert.Equal(0, handler.Calls);
            Assert.Equal(1, counter.Count("no_bound_conversation"));
        }
        finally { await listener.DisposeAsync(); }
    }

    [Fact]
    public async Task T3_ToolsCallSharesSerializationWithTheInjectedInstance()
    {
        var handler = new Handler { Block = true }; using var http = new HttpClient(handler) { BaseAddress = new("http://journal.test") };
        var client = new JournalHttpClient(http, "ingest", readToken: "read"); using var binding = Publisher(client); Bind(binding);
        var tools = new JournalFilesTools(client, binding, new JournalFileStore(_root), new JournalFilesCounter(), NullLogger<JournalFilesTools>.Instance);
        var listener = await StartAsync(tools, NullLoggerFactory.Instance);
        try
        {
            var mcp = CallAsync(1);
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var direct = tools.FetchAsync(new(TelegramMessageId: 5), default);
            var held = DateTime.UtcNow + TimeSpan.FromMilliseconds(500);
            while (DateTime.UtcNow < held) { Assert.Equal(1, handler.Fetches); Assert.False(mcp.IsCompleted); Assert.False(direct.IsCompleted); await Task.Delay(25); }
            handler.Release.TrySetResult();
            Assert.False(IsError(await mcp.WaitAsync(TimeSpan.FromSeconds(10))));
            Assert.Contains("\"path\"", await direct.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(2, handler.Fetches);
        }
        finally { handler.Release.TrySetResult(); await listener.DisposeAsync(); }
    }

    [Fact]
    public async Task T4_ConcurrentToolsCallsShareOneLifecycleAndDeadline()
    {
        var clock = new Clock(); var handler = new Handler { Block = true };
        using var http = new HttpClient(handler) { BaseAddress = new("http://journal.test") };
        var client = new JournalHttpClient(http, "ingest", readToken: "read"); using var binding = Publisher(client); Bind(binding);
        var tools = new JournalFilesTools(client, binding, new JournalFileStore(_root), new JournalFilesCounter(), NullLogger<JournalFilesTools>.Instance, clock);
        var listener = await StartAsync(tools, NullLoggerFactory.Instance);
        try
        {
            var first = CallAsync(1);
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var second = CallAsync(2);
            // Call 2 creates the second deadline timer on entry; fire it once it exists.
            await FireWhenCreatedAsync(clock, 1);
            Assert.Equal("{\"error\":\"timeout\",\"retryable\":true}", Text(await second.WaitAsync(TimeSpan.FromSeconds(10))));
            Assert.False(first.IsCompleted); Assert.Equal(1, handler.Fetches);
            handler.Release.TrySetResult();
            Assert.False(IsError(await first.WaitAsync(TimeSpan.FromSeconds(10))));
            Assert.Equal(1, handler.Fetches);
        }
        finally { handler.Release.TrySetResult(); await listener.DisposeAsync(); }
    }

    [Fact]
    public async Task T5_ListenerLogsCarryNoContentDuringBoundAndUnboundCalls()
    {
        var logs = new CaptureFactory(); var handler = new Handler();
        using var http = new HttpClient(handler) { BaseAddress = new("http://journal.test") };
        var client = new JournalHttpClient(http, "ingest", readToken: "read"); using var binding = Publisher(client);
        var tools = new JournalFilesTools(client, binding, new JournalFileStore(_root), new JournalFilesCounter(), logs.CreateLogger<JournalFilesTools>());
        var listener = await StartAsync(tools, logs);
        string path;
        try
        {
            Assert.Equal(JournalAttachmentRequest.Unavailable("no_bound_conversation"), Text(await CallAsync(1)));
            Bind(binding);
            using var json = JsonDocument.Parse(Text(await CallAsync(2)));
            path = json.RootElement.GetProperty("path").GetString()!;
        }
        finally { await listener.DisposeAsync(); }
        var captured = string.Join('\n', logs.Lines.Where(l => l.Level >= LogLevel.Information).Select(l => l.Text));
        Assert.Contains("result=no_bound_conversation", captured); Assert.Contains("result=ok", captured);
        foreach (var forbidden in new[] { "synthetic sentinel", path, Id })
            Assert.DoesNotContain(forbidden, captured);
    }

    private static async Task<JournalFilesListener> StartAsync(JournalFilesTools tools, ILoggerFactory logs)
    {
        var listener = new JournalFilesListener(tools, logs);
        await listener.StartAsync(default);
        // Port 8091 is fixed: an occupied port fails the test, it is never skipped.
        if (!listener.Ready) { await listener.DisposeAsync(); Assert.Fail("journal files listener did not bind 127.0.0.1:8091"); }
        return listener;
    }

    private static async Task<JsonElement> CallAsync(int id)
    {
        using var loopback = new HttpClient { BaseAddress = new("http://127.0.0.1:8091"), Timeout = TimeSpan.FromSeconds(30) };
        var body = "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"tools/call\",\"params\":{\"name\":\"fetch_attachment\",\"arguments\":{\"telegram_message_id\":5}}}";
        using var request = new HttpRequestMessage(HttpMethod.Post, JournalFilesListener.Path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json"); request.Headers.Accept.ParseAdd("text/event-stream");
        using var reply = await loopback.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        var text = await reply.Content.ReadAsStringAsync();
        // Stateless streamable HTTP answers with either a JSON body or one SSE `data:` line.
        var payload = text.TrimStart().StartsWith('{') ? text
            : text.Split('\n').Select(l => l.TrimEnd('\r')).Single(l => l.StartsWith("data:", StringComparison.Ordinal))["data:".Length..].Trim();
        using var json = JsonDocument.Parse(payload);
        return json.RootElement.GetProperty("result").Clone();
    }

    private static bool IsError(JsonElement result) => result.TryGetProperty("isError", out var e) && e.ValueKind == JsonValueKind.True;
    private static string Text(JsonElement result) => result.GetProperty("content")[0].GetProperty("text").GetString()!;

    private static async Task FireWhenCreatedAsync(Clock clock, int index)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            try { clock.FireDeadline(index); return; }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException or NullReferenceException && DateTime.UtcNow < until) { await Task.Delay(20); }
        }
    }

    private sealed class CaptureFactory : ILoggerFactory
    {
        public ConcurrentQueue<(LogLevel Level, string Text)> Lines { get; } = new();
        public void AddProvider(ILoggerProvider provider) { }
        public ILogger CreateLogger(string categoryName) => new Logger(this);
        public void Dispose() { }
        private sealed class Logger(CaptureFactory owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Lines.Enqueue((level, formatter(state, exception) + (exception is null ? "" : "\n" + exception)));
        }
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
