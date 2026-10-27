using System.Net;
using System.Text;
using Fleet.Identity.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Openshell.Middleware.V1;

namespace Fleet.Identity.Tests;

public sealed class StreamingTests
{
    [Fact]
    public async Task AuthenticatedGetStreamsPastHeaderDeadline_UntilCallerDisconnect()
    {
        await using var f = new IdentityFixture(); await f.StartAsync();
        var session = await f.InitializeAsync();
        var evaluation = f.Evaluation("", session); evaluation.Target.Method = "GET";
        var result = await f.Evaluate(evaluation); Assert.Equal(Decision.Allow, result.Decision);
        var context = new DefaultHttpContext(); context.Request.Method = "GET"; context.Request.Path = "/mcp";
        context.Request.Scheme = "https"; context.Request.Host = new(f.Options.IngressHost, f.Options.IngressPort);
        context.Features.Get<IHttpRequestFeature>()!.RawTarget = "/mcp";
        context.Request.Headers["X-Fleet-Assertion"] = result.HeaderMutations.Single().Write.Value;
        context.Request.Headers["Mcp-Session-Id"] = session; context.Response.Body = new MemoryStream();
        using var cancellation = new CancellationTokenSource(); context.RequestAborted = cancellation.Token;
        var stream = new OwnedEventStream(); using var client = new HttpClient(new StreamReceiver(stream));
        var pending = f.CreateIngress(client).HandleAsync(context);
        try
        {
            await stream.Ready.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(2200); // Bounded fixture, no watcher or external stream.
            Assert.False(pending.IsCompleted); Assert.True(context.Response.Body.Length > 0);
            Assert.Equal("text/event-stream", context.Response.ContentType);
        }
        finally { cancellation.Cancel(); await pending.WaitAsync(TimeSpan.FromSeconds(2)); }
        Assert.True(stream.Cancelled);
    }

    private sealed class StreamReceiver(Stream stream) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Get, request.Method); Assert.Null(request.Headers.Authorization);
            Assert.Equal("https://memory.example.invalid/mcp", request.RequestUri!.ToString());
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
            response.Content.Headers.ContentType = new("text/event-stream"); return Task.FromResult(response);
        }
    }
    private sealed class OwnedEventStream : Stream
    {
        private int _read;
        public readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _read) == 1)
            {
                var frame = Encoding.UTF8.GetBytes("event: message\ndata: {}\n\n"); frame.CopyTo(buffer); return frame.Length;
            }
            Ready.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
