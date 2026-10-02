using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Fleet.Comms.Routes;
using Fleet.Conversations.Contracts;
using Fleet.Conversations.Journal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;

namespace Fleet.Comms.Tests;

public sealed class JournalAttachmentContentTests
{
    private const string Id = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private static readonly byte[] Key = Enumerable.Repeat((byte)42, 32).ToArray();

    [Fact]
    public void AttachmentAndUploadUseOneSharedSizeCap() =>
        Assert.Equal(Fleet.Comms.Configuration.MediaOptions.MaxObjectBytes, JournalAttachmentRequest.MaxBytes);

    [Theory]
    [InlineData(null)]
    [InlineData("ingest")]
    [InlineData("status")]
    [InlineData("ingest-service")]
    public async Task AuthPrecedesMalformedBodyAndEveryStore(string? purpose)
    {
        await using var host = await Host.Start();
        using var reply = await host.Send("malformed", purpose: purpose);
        Assert.Equal(HttpStatusCode.Unauthorized, reply.StatusCode);
        Assert.Equal("{\"error\":\"unauthorized\"}", await reply.Content.ReadAsStringAsync());
        Assert.Equal(0, host.Source.Calls); Assert.Equal(0, host.Objects.Calls);
    }

    [Theory]
    [InlineData("{\"telegram_message_id\":5,\"telegram_chat_id\":1}", "telegram_chat_id")]
    [InlineData("{\"telegram_message_id\":0}", "telegram_message_id")]
    [InlineData("{\"telegram_message_id\":5,\"ordinal\":256}", "ordinal")]
    [InlineData("{\"message_id\":\"bad\"}", "message_id")]
    [InlineData("{}", "message_id")]
    public async Task InvalidArgumentsPrecedeMediaAndBinding(string body, string field)
    {
        await using var host = await Host.Start(media: false, bound: false);
        using var reply = await host.Send(body);
        Assert.Equal(HttpStatusCode.BadRequest, reply.StatusCode);
        Assert.Equal(JournalAttachmentRequest.Invalid(field), await reply.Content.ReadAsStringAsync());
        Assert.Equal(0, host.Source.Calls); Assert.Equal(0, host.Objects.Calls);
    }

    [Fact]
    public async Task BodyCapRejectsWithoutReadingStores()
    {
        await using var host = await Host.Start();
        using var reply = await host.Send(new string(' ', 1025));
        Assert.Equal(HttpStatusCode.BadRequest, reply.StatusCode);
        Assert.Equal(JournalAttachmentRequest.Invalid("body"), await reply.Content.ReadAsStringAsync());
        Assert.Equal(0, host.Source.Calls);
    }

    [Theory]
    [InlineData(false, false, false, "media_disabled")]
    [InlineData(true, false, false, "no_bound_conversation")]
    [InlineData(true, true, true, "conversation_not_journaled")]
    public async Task AvailabilityChecksPrecedeLookup(bool media, bool bound, bool excluded, string reason)
    {
        await using var host = await Host.Start(media, bound, excluded);
        using var reply = await host.Send();
        Assert.Equal(HttpStatusCode.Conflict, reply.StatusCode);
        Assert.Equal(JournalAttachmentRequest.Unavailable(reason), await reply.Content.ReadAsStringAsync());
        Assert.Equal(0, host.Source.Calls); Assert.Equal(0, host.Objects.Calls);
    }

    [Theory]
    [InlineData("not_archived", "committed", 422, "{\"error\":\"not_archived\",\"reason\":\"too_large\"}")]
    [InlineData("lost", "committed", 409, "{\"error\":\"unavailable\",\"reason\":\"attachment_lost\"}")]
    [InlineData("archived", "uploading", 409, "{\"error\":\"unavailable\",\"reason\":\"object_missing\"}")]
    [InlineData("archived", "deleting", 409, "{\"error\":\"unavailable\",\"reason\":\"object_missing\"}")]
    public async Task AttachmentStatesPrecedeObjectOpen(string attachment, string state, int status, string body)
    {
        await using var host = await Host.Start();
        host.Source.Row = Row() with { AttachmentState = attachment, ObjectState = state, NotArchivedReason = "too_large" };
        using var reply = await host.Send();
        Assert.Equal(status, (int)reply.StatusCode); Assert.Equal(body, await reply.Content.ReadAsStringAsync());
        Assert.Equal(0, host.Objects.Calls);
    }

    [Fact]
    public async Task MissingLookupAndMissingObjectHaveFixedDistinctBodies()
    {
        await using var host = await Host.Start();
        host.Source.Row = null;
        using var absent = await host.Send();
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        Assert.Equal("{\"error\":\"not_found\"}", await absent.Content.ReadAsStringAsync());
        Assert.Equal(0, host.Objects.Calls);
        host.Source.Row = Row(); host.Objects.Missing = true;
        using var missing = await host.Send();
        Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);
        Assert.Equal(JournalAttachmentRequest.Unavailable("object_missing"), await missing.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SuccessStreamsOnlyBytesAndAuthorizedMetadata()
    {
        await using var host = await Host.Start();
        using var reply = await host.Send();
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        Assert.Equal(Encoding.UTF8.GetBytes("synthetic"), await reply.Content.ReadAsByteArrayAsync());
        Assert.Equal(Id, reply.Headers.GetValues("X-Journal-Message-Id").Single());
        Assert.Equal("application/pdf", reply.Content.Headers.ContentType!.MediaType);
        Assert.Equal(9, reply.Content.Headers.ContentLength);
        Assert.Equal("agent1", host.Source.Reader!.Subject);
        Assert.NotNull(host.Source.ConversationKey);
        Assert.DoesNotContain("private-key", string.Join(" ", reply.Headers));
        Assert.Equal(0, host.Stats.AttachmentFetchInFlight);
    }

    [Fact]
    public async Task LengthMismatchBeforeStreamingReturnsIntegrityFailure()
    {
        await using var host = await Host.Start();
        host.Objects.Length = 10;
        using var reply = await host.Send();
        Assert.Equal(HttpStatusCode.Conflict, reply.StatusCode);
        Assert.Equal(JournalAttachmentRequest.Unavailable("integrity_failed"), await reply.Content.ReadAsStringAsync());
        Assert.Equal(0, host.Stats.AttachmentFetchInFlight);
    }

    [Fact]
    public async Task DigestMismatchAbortsAndNeverCompletesBytes()
    {
        await using var host = await Host.Start(); host.Objects.Bytes = Encoding.UTF8.GetBytes("different");
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        { using var reply = await host.Send(); await reply.Content.ReadAsByteArrayAsync(); });
        Assert.Equal(0, host.Stats.AttachmentFetchInFlight);
    }

    [Fact]
    public async Task ObjectFailureDoesNotExposeExceptionText()
    {
        await using var host = await Host.Start(); host.Objects.Fails = true;
        using var reply = await host.Send();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, reply.StatusCode);
        Assert.Equal("{\"error\":\"store_unavailable\",\"retryable\":true}", await reply.Content.ReadAsStringAsync());
        Assert.Equal(0, host.Stats.AttachmentFetchInFlight);
    }

    [Fact]
    public async Task SubjectAndGlobalSlotsAreReleasedAfterCompletion()
    {
        await using var host = await Host.Start();
        host.Objects.Wait = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = host.Send(subject: "agent1");
        await host.Objects.Opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var same = await host.Send(subject: "agent1");
        Assert.Equal(HttpStatusCode.TooManyRequests, same.StatusCode);
        var others = Enumerable.Range(2, 3).Select(i => host.Send(subject: $"agent{i}")).ToArray();
        await host.Objects.FourOpened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var fifth = await host.Send(subject: "agent5");
        Assert.Equal(HttpStatusCode.TooManyRequests, fifth.StatusCode);
        Assert.Equal(4, host.Stats.AttachmentFetchInFlight);
        host.Objects.Wait.SetResult();
        foreach (var reply in await Task.WhenAll(others.Append(first))) { Assert.Equal(HttpStatusCode.OK, reply.StatusCode); reply.Dispose(); }
        Assert.Equal(0, host.Stats.AttachmentFetchInFlight);
        using var again = await host.Send(); Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [Fact]
    public async Task StoreOpenDeadlineIsThirtySecondsAndReleasesSlots()
    {
        var clock = new Clock(); await using var host = await Host.Start(time: clock);
        host.Objects.Wait = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = host.Send(); await host.Objects.Opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(30));
        using var reply = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, reply.StatusCode);
        Assert.Equal("{\"error\":\"store_unavailable\",\"retryable\":true}", await reply.Content.ReadAsStringAsync());
        Assert.Equal(0, host.Stats.AttachmentFetchInFlight);
    }

    [Fact]
    public async Task WholeStreamDeadlineIsFortySecondsAndAbortsResponse()
    {
        var clock = new Clock(); await using var host = await Host.Start(time: clock);
        var stream = new StalledStream(); host.Objects.Stream = stream;
        var pending = host.Send(); await stream.Reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(40));
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        { using var reply = await pending.WaitAsync(TimeSpan.FromSeconds(5)); await reply.Content.ReadAsByteArrayAsync(); });
        Assert.Equal(0, host.Stats.AttachmentFetchInFlight);
    }

    private sealed class StalledStream : MemoryStream
    {
        public TaskCompletionSource Reading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { Reading.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return 0; }
    }
    private sealed class Clock : TimeProvider
    {
        private readonly List<Timer> _timers = []; private TimeSpan _elapsed;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + _elapsed;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_timers) { var timer = new Timer(this, callback, state, dueTime); _timers.Add(timer); return timer; }
        }
        public void Advance(TimeSpan by)
        {
            Timer[] ready;
            lock (_timers) { _elapsed += by; ready = _timers.Where(t => !t.Disposed && t.At <= _elapsed).ToArray(); }
            foreach (var timer in ready) timer.Callback(timer.State);
        }
        private sealed class Timer(Clock clock, TimerCallback callback, object? state, TimeSpan due) : ITimer
        {
            public TimerCallback Callback = callback; public object? State = state;
            public TimeSpan At = clock._elapsed + due; public bool Disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period) { lock (clock._timers) At = clock._elapsed + dueTime; return !Disposed; }
            public void Dispose() { lock (clock._timers) Disposed = true; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private static JournalAttachmentLocator Row() => new(Id, 0, "document", "application/pdf", 9,
        "archived", null, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("synthetic"))),
        "private-key", "committed", 9, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("synthetic"))));

    private sealed class Source : IJournalAttachmentSource
    {
        public int Calls; public JournalAttachmentLocator? Row = JournalAttachmentContentTests.Row();
        public JournalReader? Reader; public string? ConversationKey;
        public Task<JournalAttachmentLocator?> FindAttachmentAsync(JournalReader reader, string key, string? id, long? telegramId, int ordinal, CancellationToken ct = default)
        { Interlocked.Increment(ref Calls); Reader = reader; ConversationKey = key; return Task.FromResult(Row); }
    }
    private sealed class Objects : IJournalObjectStore
    {
        public int Calls; public bool Missing, Fails; public long Length = 9;
        public byte[] Bytes = Encoding.UTF8.GetBytes("synthetic"); public Stream? Stream;
        public TaskCompletionSource? Wait; public TaskCompletionSource Opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FourOpened = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<JournalObjectReadResult?> GetAsync(string key, CancellationToken ct = default)
        {
            var n = Interlocked.Increment(ref Calls); Opened.TrySetResult(); if (n == 4) FourOpened.TrySetResult();
            if (Wait is not null) await Wait.Task.WaitAsync(ct);
            if (Fails) throw new JournalObjectStoreUnavailableException(new IOException("private-key-secret"));
            return Missing ? null : new() { Content = Stream ?? new MemoryStream(Bytes), ByteSize = Length };
        }
        public Task<JournalObjectWriteResult> PutAsync(string key, Stream body, long size, string type, CancellationToken ct = default, long? contentLength = null) => throw new NotSupportedException();
        public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<JournalObjectListing>> ListAsync(string prefix, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> ProbeAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }
    private sealed class Host(WebApplication app, HttpClient client, Source source, Objects objects, JournalRuntimeStats stats) : IAsyncDisposable
    {
        public Source Source = source; public Objects Objects = objects; public JournalRuntimeStats Stats = stats;
        public static async Task<Host> Start(bool media = true, bool bound = true, bool excluded = false, TimeProvider? time = null)
        {
            var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer(); builder.Logging.ClearProviders();
            var app = builder.Build(); var stats = new JournalRuntimeStats();
            JournalAuth.Use(app, [Key], stats);
            var bindings = new JournalTurnBindings(time ?? TimeProvider.System);
            if (bound) for (var i = 1; i <= 5; i++) bindings.Put($"agent{i}", new("synthetic-epoch", 1, "bound", "private", 1, 10));
            var source = new Source(); var objects = new Objects();
            var endpoint = new JournalAttachmentContentEndpoint(source, media ? objects : null,
                new JournalReadGrants(new HashSet<string>()), new JournalBindingScope(bindings, excluded ? new HashSet<long> { 10 } : new HashSet<long>()), stats, time);
            app.MapPost(JournalAttachmentRequest.ContentPath, endpoint.HandleAsync); await app.StartAsync();
            return new(app, app.GetTestClient(), source, objects, stats);
        }
        public Task<HttpResponseMessage> Send(string body = "{\"telegram_message_id\":5}", string? purpose = "read", string subject = "agent1")
        {
            var request = new HttpRequestMessage(HttpMethod.Post, JournalAttachmentRequest.ContentPath) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (purpose is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", JournalTokens.Mint(Key, purpose, subject));
            return client.SendAsync(request);
        }
        public async ValueTask DisposeAsync() { client.Dispose(); await app.DisposeAsync(); }
    }
}
