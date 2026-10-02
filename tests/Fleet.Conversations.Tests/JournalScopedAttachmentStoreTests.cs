using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Fleet.Comms.Routes;
using Fleet.Conversations.Contracts;
using Fleet.Conversations.Journal;
using Fleet.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;

namespace Fleet.Conversations.Tests;

/// <summary>Runs only in comms-media-store-smoke against its non-Admin runtime identity.</summary>
public sealed class JournalScopedAttachmentStoreTests
{
    [Fact]
    public async Task ScopedIdentityContentFetchPreservesBucketAndDoesNotLogPrivateData()
    {
        static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value : throw new InvalidOperationException($"{name} required: run the scoped-store smoke job, never skip.");
        var endpoint = Required("FLEET_COMMS_SCOPED_ENDPOINT"); var bucket = Required("FLEET_COMMS_SCOPED_BUCKET");
        var access = Required("FLEET_COMMS_SCOPED_ACCESS_KEY"); var secret = Required("FLEET_COMMS_SCOPED_SECRET_KEY");
        var logs = new Capture();
        using var store = new S3ObjectStore(new JournalMediaOptions
        { Endpoint = endpoint, Bucket = bucket, AccessKey = access, SecretKey = secret, Region = "us-east-1" }, logs.CreateLogger("store"));
        var bytes = Encoding.UTF8.GetBytes("synthetic scoped sentinel"); var objectKey = "fetch-test/" + Ulid.NewUlid();
        var id = Ulid.NewUlid(); var key = Enumerable.Repeat((byte)42, 32).ToArray();
        try
        {
            var written = await store.PutAsync(objectKey, new MemoryStream(bytes), bytes.Length, "text/plain");
            Assert.True(written.Succeeded);
            var before = (await store.ListAsync("")).Select(row => row.Key).Order().ToArray();
            var bindings = new JournalTurnBindings(TimeProvider.System);
            bindings.Put("agent1", new("fixture-epoch", 1, "bound", "private", 7001, 10));
            var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer(); builder.Logging.ClearProviders(); builder.Logging.AddProvider(logs);
            await using var app = builder.Build(); var stats = new JournalRuntimeStats(); JournalAuth.Use(app, [key], stats);
            var row = new JournalAttachmentLocator(id, 0, "other", "text/plain", bytes.Length, "committed", null,
                written.Sha256, objectKey, "uploaded", bytes.Length, written.Sha256);
            var source = new Source(row);
            var content = new JournalAttachmentContentEndpoint(source, store, new JournalReadGrants(new HashSet<string>()),
                new JournalBindingScope(bindings, new HashSet<long>()), stats);
            app.MapPost(JournalAttachmentRequest.ContentPath, content.HandleAsync); await app.StartAsync(); using var client = app.GetTestClient();
            for (var i = 0; i < 20; i++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, JournalAttachmentRequest.ContentPath)
                { Content = new StringContent("{\"telegram_message_id\":5}", Encoding.UTF8, "application/json") };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", JournalTokens.Mint(key, "read", "agent1"));
                using var reply = await client.SendAsync(request); Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
                Assert.Equal(bytes, await reply.Content.ReadAsByteArrayAsync());
                var headers = reply.Headers.ToString() + reply.Content.Headers;
                foreach (var forbidden in new[] { objectKey, bucket, endpoint, access, secret }) Assert.DoesNotContain(forbidden, headers);
            }
            Assert.Equal(before, (await store.ListAsync("")).Select(item => item.Key).Order().ToArray());
            Assert.Equal(20, source.Calls); Assert.Equal(0, stats.AttachmentFetchInFlight);
            Assert.NotEmpty(logs.Lines);
            var captured = string.Join('\n', logs.Lines);
            foreach (var forbidden in new[] { objectKey, bucket, endpoint, written.Sha256, access, secret, "synthetic scoped sentinel", "private-file.pdf" })
                Assert.DoesNotContain(forbidden, captured);
        }
        finally { await store.DeleteAsync(objectKey); }
    }
    private sealed class Source(JournalAttachmentLocator row) : IJournalAttachmentSource
    {
        public int Calls;
        public Task<JournalAttachmentLocator?> FindAttachmentAsync(JournalReader reader, string conversation, string? id, long? telegramId, int ordinal, CancellationToken ct = default)
        {
            Calls++;
            Assert.Equal("agent1", reader.Subject); Assert.Equal("tg:dm:7001:10", conversation);
            Assert.Null(id); Assert.Equal(5, telegramId); Assert.Equal(0, ordinal);
            return Task.FromResult<JournalAttachmentLocator?>(row);
        }
    }
    private sealed class Capture : ILoggerProvider
    {
        public ConcurrentQueue<string> Lines { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Logger(this);
        public void Dispose() { }
        private sealed class Logger(Capture capture) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                capture.Lines.Enqueue(formatter(state, exception));
        }
    }
}
