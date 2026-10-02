using System.Net;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Fleet.Agent.Services.JournalFiles;
using Fleet.Conversations.Contracts;
using Fleet.Journal.Client;
using Microsoft.Extensions.Logging.Abstractions;
namespace Fleet.Agent.Tests;

public sealed class JournalFilesToolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "journal-tools-" + Guid.NewGuid().ToString("N"));
    private const string Id = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private static readonly byte[] Bytes = Encoding.UTF8.GetBytes("synthetic sentinel");
    [Fact]
    public async Task InvalidAndUnboundRequestsNeverCallComms()
    {
        var handler = new Handler(); using var http = new HttpClient(handler) { BaseAddress = new("http://journal.test") };
        var client = new JournalHttpClient(http, "ingest", readToken: "read"); using var binding = Publisher(client);
        var tool = Tools(client, binding);
        Assert.Equal(JournalAttachmentRequest.Invalid("message_id"), await tool.FetchAsync(new(), default));
        Assert.Equal(JournalAttachmentRequest.Unavailable("no_bound_conversation"), await tool.FetchAsync(new(TelegramMessageId: 5), default));
        Assert.Equal(0, handler.Calls);
    }
    [Fact]
    public async Task SuccessReturnsPrivatePathAndNoStoreMetadata()
    {
        var handler = new Handler(); using var http = new HttpClient(handler) { BaseAddress = new("http://journal.test") };
        var client = new JournalHttpClient(http, "ingest", readToken: "read"); using var binding = Publisher(client); Bind(binding);
        using var json = JsonDocument.Parse(await Tools(client, binding).FetchAsync(new(TelegramMessageId: 5), default));
        var result = json.RootElement; var path = result.GetProperty("path").GetString()!;
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(path)); Assert.Equal(Id, result.GetProperty("message_id").GetString());
        Assert.Equal(Bytes.Length, result.GetProperty("byte_size").GetInt64()); Assert.Equal(7, result.EnumerateObject().Count());
        Assert.DoesNotContain("sha256", result.GetRawText()); Assert.DoesNotContain("object", result.GetRawText());
        Assert.Equal("Bearer read", handler.Header);
    }
    [Fact]
    public async Task OtherAttachmentKindReturnsVerifiedPrivateFile()
    {
        var handler = new Handler { Kind = "other" };
        using var http = new HttpClient(handler) { BaseAddress = new("http://journal.test") };
        var client = new JournalHttpClient(http, "ingest", readToken: "read"); using var binding = Publisher(client); Bind(binding);
        using var json = JsonDocument.Parse(await Tools(client, binding).FetchAsync(new(TelegramMessageId: 5), default));
        Assert.Equal("other", json.RootElement.GetProperty("kind").GetString());
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(json.RootElement.GetProperty("path").GetString()!));
    }
    [Fact]
    public async Task RemoteLostBindingResendsIngestAndRetriesExactlyOnce()
    {
        var handler = new Handler { LostBindings = 1 }; using var http = new HttpClient(handler) { BaseAddress = new("http://journal.test") };
        var client = new JournalHttpClient(http, "ingest", readToken: "read"); using var binding = Publisher(client); Bind(binding);
        using var json = JsonDocument.Parse(await Tools(client, binding).FetchAsync(new(TelegramMessageId: 5), default));
        Assert.True(json.RootElement.TryGetProperty("path", out _)); Assert.Equal(2, handler.Fetches); Assert.Equal(1, handler.Puts);
        Assert.Equal("Bearer ingest", handler.PutHeader);
    }
    [Fact]
    public async Task PersistentlyLostBindingStopsAfterOneRetry()
    {
        var handler = new Handler { LostBindings = 2 }; using var http = new HttpClient(handler) { BaseAddress = new("http://journal.test") };
        var client = new JournalHttpClient(http, "ingest", readToken: "read"); using var binding = Publisher(client); Bind(binding);
        Assert.Equal(JournalAttachmentRequest.Unavailable("no_bound_conversation"), await Tools(client, binding).FetchAsync(new(TelegramMessageId: 5), default));
        Assert.Equal(2, handler.Fetches); Assert.Equal(1, handler.Puts);
    }
    [Theory]
    [InlineData(401, "{\"error\":\"unauthorized\"}", "{\"error\":\"unavailable\",\"reason\":\"comms_refused\"}")]
    [InlineData(404, "{\"error\":\"not_found\"}", "{\"error\":\"not_found\"}")]
    [InlineData(429, "{\"error\":\"busy\",\"retryable\":true}", "{\"error\":\"busy\",\"retryable\":true}")]
    [InlineData(409, "{\"error\":\"unavailable\",\"reason\":3}", "{\"error\":\"unavailable\",\"reason\":3}")]
    [InlineData(500, "private-secret", "{\"error\":\"unavailable\",\"reason\":\"comms_unreachable\"}")]
    public async Task FixedErrorsNeverExposeUnexpectedBodies(int status, string body, string expected)
    {
        var handler = new Handler { Status = status, Body = body }; using var http = new HttpClient(handler) { BaseAddress = new("http://journal.test") };
        var client = new JournalHttpClient(http, "ingest", readToken: "read"); using var binding = Publisher(client); Bind(binding);
        Assert.Equal(expected, await Tools(client, binding).FetchAsync(new(TelegramMessageId: 5), default));
    }
    [Fact]
    public async Task DigestFailureRemovesPartialFile()
    {
        var handler = new Handler { BadDigest = true }; using var http = new HttpClient(handler) { BaseAddress = new("http://journal.test") };
        var client = new JournalHttpClient(http, "ingest", readToken: "read"); using var binding = Publisher(client); Bind(binding);
        Assert.Equal(JournalAttachmentRequest.Unavailable("integrity_failed"), await Tools(client, binding).FetchAsync(new(TelegramMessageId: 5), default));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "journal")));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentCallsAreSerializedAndDeadlineIncludesQueueWait(bool queuedDeadlineFirst)
    {
        var clock = new Clock(); var handler = new Handler { Block = true };
        using var http = new HttpClient(handler) { BaseAddress = new("http://journal.test") };
        var client = new JournalHttpClient(http, "ingest", readToken: "read"); using var binding = Publisher(client); Bind(binding);
        var tool = new JournalFilesTools(client, binding, new JournalFileStore(_root), new JournalFilesCounter(), NullLogger<JournalFilesTools>.Instance, clock);
        var first = tool.FetchAsync(new(TelegramMessageId: 5), default);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = tool.FetchAsync(new(TelegramMessageId: 5), default); Assert.Equal(1, handler.Fetches);
        // Await cancellation of the queued call before the running handler can release
        // the semaphore. Also exercise a running-handler cancellation first, held
        // until the queued call's deadline has fired.
        handler.DelayCancellation = !queuedDeadlineFirst;
        clock.FireDeadline(queuedDeadlineFirst ? 1 : 0);
        if (!queuedDeadlineFirst) await handler.CancelObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        else Assert.Equal("{\"error\":\"timeout\",\"retryable\":true}", await second.WaitAsync(TimeSpan.FromSeconds(5)));
        clock.FireDeadline(queuedDeadlineFirst ? 0 : 1);
        handler.CancelRelease.TrySetResult();
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(results, body => Assert.Equal("{\"error\":\"timeout\",\"retryable\":true}", body));
        Assert.Equal(1, handler.Fetches);
    }
    [Fact]
    public async Task ElapsedQueuedDeadlineCannotDispatchBeforeItsTimerCallback()
    {
        var clock = new Clock(); var handler = new Handler { Block = true };
        using var http = new HttpClient(handler) { BaseAddress = new("http://journal.test") };
        var client = new JournalHttpClient(http, "ingest", readToken: "read"); using var binding = Publisher(client); Bind(binding);
        var tool = new JournalFilesTools(client, binding, new JournalFileStore(_root), new JournalFilesCounter(), NullLogger<JournalFilesTools>.Instance, clock);
        var first = tool.FetchAsync(new(TelegramMessageId: 5), default);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = tool.FetchAsync(new(TelegramMessageId: 5), default);
        clock.AdvanceWithoutCallbacks(TimeSpan.FromSeconds(50)); clock.FireDeadline(0);
        try
        {
            Assert.All(await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2)), body =>
                Assert.Equal("{\"error\":\"timeout\",\"retryable\":true}", body));
            Assert.Equal(1, handler.Fetches);
        }
        finally { clock.FireDeadline(1); await Task.WhenAll(first, second); }
    }
    [Fact]
    public async Task SuccessfulConcurrentCallsNeverOverlapHTTPOrFileWrites()
    {
        var handler = new Handler { Block = true };
        using var http = new HttpClient(handler) { BaseAddress = new("http://journal.test") };
        var client = new JournalHttpClient(http, "ingest", readToken: "read"); using var binding = Publisher(client); Bind(binding);
        var tool = Tools(client, binding); var first = tool.FetchAsync(new(TelegramMessageId: 5), default);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = tool.FetchAsync(new(TelegramMessageId: 5), default); Assert.Equal(1, handler.Fetches);
        handler.Release.TrySetResult();
        Assert.All(await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5)), result => Assert.Contains("\"path\"", result));
        Assert.Equal(2, handler.Fetches);
    }
    [Fact]
    public async Task LostBindingResendIsBoundedAtTwoSecondsBeforeSingleRetry()
    {
        var clock = new Clock(); var handler = new Handler { LostBindings = 2, BlockPuts = true };
        using var http = new HttpClient(handler) { BaseAddress = new("http://journal.test") };
        var client = new JournalHttpClient(http, "ingest", readToken: "read");
        using var binding = new TurnBindingPublisher(client, NullLogger<TurnBindingPublisher>.Instance, clock); Bind(binding);
        var tool = new JournalFilesTools(client, binding, new JournalFileStore(_root), new JournalFilesCounter(), NullLogger<JournalFilesTools>.Instance, clock);
        var call = tool.FetchAsync(new(TelegramMessageId: 5), default);
        await handler.PutStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(JournalAttachmentRequest.Unavailable("no_bound_conversation"), await call.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, handler.Puts); Assert.Equal(2, handler.Fetches);
    }
    [Fact]
    public async Task ChangedTurnNeverReturnsAnAlreadyDownloadedFile()
    {
        var handler = new Handler { Block = true };
        using var http = new HttpClient(handler) { BaseAddress = new("http://journal.test") };
        var client = new JournalHttpClient(http, "ingest", readToken: "read"); using var binding = Publisher(client); Bind(binding);
        var call = Tools(client, binding).FetchAsync(new(TelegramMessageId: 5), default);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); binding.EndTurn(binding.Current.Seq); handler.Release.TrySetResult();
        Assert.Equal(JournalAttachmentRequest.Unavailable("no_bound_conversation"), await call.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "journal")));
    }
    [Fact]
    public async Task CapturedFetchLogsContainOnlyFixedDiagnosticFields()
    {
        var handler = new Handler(); var logs = new Capture();
        using var http = new HttpClient(handler) { BaseAddress = new("http://journal.test") };
        var client = new JournalHttpClient(http, "private-ingest-token", readToken: "private-read-token");
        using var binding = Publisher(client); Bind(binding);
        var tool = new JournalFilesTools(client, binding, new JournalFileStore(_root), new JournalFilesCounter(), logs);
        using var success = JsonDocument.Parse(await tool.FetchAsync(new(TelegramMessageId: 5), default));
        handler.BadDigest = true; Assert.Contains("integrity_failed", await tool.FetchAsync(new(TelegramMessageId: 5), default));
        Assert.Equal(2, logs.Lines.Count);
        foreach (var line in logs.Lines) { Assert.Contains("result=", line); Assert.Contains("kind=", line); Assert.Contains("bytes=", line); Assert.Contains("waitedMs=", line); }
        var captured = string.Join('\n', logs.Lines);
        foreach (var forbidden in new[] { Id, _root, "synthetic sentinel", "private-ingest-token", "private-read-token",
            "http://journal.test", Convert.ToHexStringLower(SHA256.HashData(Bytes)), success.RootElement.GetProperty("path").GetString()! })
            Assert.DoesNotContain(forbidden, captured);
    }
    private sealed class Capture : ILogger<JournalFilesTools>
    {
        public ConcurrentQueue<string> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Lines.Enqueue(formatter(state, exception));
    }
    private sealed class Clock : TimeProvider
    {
        private TimeSpan _elapsed; private readonly List<Timer> _timers = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _elapsed.Ticks;
        public void AdvanceWithoutCallbacks(TimeSpan by) => _elapsed += by;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + _elapsed;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan due, TimeSpan period)
        { var timer = new Timer(this, callback, state, due); _timers.Add(timer); return timer; }
        public void FireDeadline(int index) => _timers[index].Callback(_timers[index].State);
        public void Advance(TimeSpan by) { _elapsed += by; foreach (var timer in _timers.ToArray()) if (!timer.Disposed && timer.At <= _elapsed) timer.Callback(timer.State); }
        private sealed class Timer(Clock clock, TimerCallback callback, object? state, TimeSpan due) : ITimer
        {
            public TimerCallback Callback = callback; public object? State = state; public TimeSpan At = clock._elapsed + due; public bool Disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period) { At = clock._elapsed + dueTime; return !Disposed; }
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private JournalFilesTools Tools(JournalHttpClient client, TurnBindingPublisher binding) => new(client, binding, new JournalFileStore(_root), new JournalFilesCounter(), NullLogger<JournalFilesTools>.Instance);
    private static TurnBindingPublisher Publisher(JournalHttpClient client) => new(client, NullLogger<TurnBindingPublisher>.Instance);
    private static void Bind(TurnBindingPublisher binding) { binding.ObserveChat(10, 1, "private"); binding.BeginTurn(10, TaskSource.UserMessage); }
    private sealed class Handler : HttpMessageHandler
    {
        public int Calls, Fetches, Puts, LostBindings; public bool BadDigest, Block, BlockPuts, DelayCancellation;
        public string Kind = "document";
        public TaskCompletionSource CancelObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancelRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PutStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously); public int Status = 200; public string? Body, Header, PutHeader;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; Header = request.Headers.Authorization?.ToString();
            if (request.Method == HttpMethod.Put) { Puts++; PutHeader = Header; PutStarted.TrySetResult(); if (BlockPuts) await Release.Task.WaitAsync(ct); return new HttpResponseMessage(HttpStatusCode.NoContent); }
            Fetches++; Started.TrySetResult();
            if (Block) try { await Release.Task.WaitAsync(ct); }
                catch (OperationCanceledException) { CancelObserved.TrySetResult(); if (DelayCancellation) await CancelRelease.Task; throw; }
            if (Fetches <= LostBindings) return new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent(JournalAttachmentRequest.Unavailable("no_bound_conversation")) };
            if (Status != 200) return new HttpResponseMessage((HttpStatusCode)Status) { Content = new StringContent(Body ?? "") };
            var reply = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes) };
            reply.Content.Headers.ContentType = new("text/plain");
            reply.Headers.Add("X-Journal-Message-Id", Id); reply.Headers.Add("X-Journal-Ordinal", "0"); reply.Headers.Add("X-Journal-Kind", Kind);
            reply.Headers.Add("X-Journal-Sha256", BadDigest ? new string('0', 64) : Convert.ToHexStringLower(SHA256.HashData(Bytes)));
            return reply;
        }
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
