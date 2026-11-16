using System.ComponentModel;
using System.Net;
using Fleet.Orchestrator.Services.McpTools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tests.Services.McpTools;

/// <summary>
/// The MCP client side of the tool listing, against a real MCP server hosted in this process: the
/// handshake, <c>tools/list</c>, the headers that reach the server, and every way it can fail.
/// </summary>
/// <remarks>In-process only. Nothing here is evidence against a third-party MCP server.</remarks>
public sealed class McpToolListerTests : IAsyncLifetime
{
    private const string HeaderValue = "value-from-env-file";

    private WebApplication _app = null!;
    private readonly List<string?> _seenHeaders = [];
    private int _refuseWith;

    [McpServerToolType]
    public sealed class SampleTools
    {
        [McpServerTool(Name = "zeta_lookup"), Description("Looks one thing up.\nSecond line.")]
        public static string ZetaLookup(string query) => query;

        [McpServerTool(Name = "alpha_list"), Description("Lists things.")]
        public static string AlphaList() => "";

        [McpServerTool(Name = "undescribed")]
        public static string Undescribed() => "";
    }

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddMcpServer().WithHttpTransport().WithTools<SampleTools>();

        _app = builder.Build();
        _app.Use(async (context, next) =>
        {
            lock (_seenHeaders) _seenHeaders.Add(context.Request.Headers["X-Example-Key"].FirstOrDefault());
            if (_refuseWith != 0)
            {
                context.Response.StatusCode = _refuseWith;
                return;
            }
            await next(context);
        });
        _app.MapMcp("/mcp");
        _app.MapGet("/plain", () => Results.Text("not an MCP server"));
        await _app.StartAsync();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private McpToolLister Lister() => new(() => _app.GetTestServer().CreateHandler());

    private static McpListTarget Target(string path = "/mcp", Dictionary<string, string>? headers = null) =>
        new("example", new Uri("http://localhost" + path), Sse: false, headers ?? []);

    [Fact]
    public async Task AServersTools_AreListedByName_WithTheirDescriptions()
    {
        var answer = await Lister().ListAsync(Target(), CancellationToken.None);

        Assert.Equal(McpServerTools.Ok, answer.Status);
        Assert.Null(answer.Reason);
        Assert.False(answer.Truncated);
        Assert.Equal(["alpha_list", "undescribed", "zeta_lookup"], answer.Tools.Select(t => t.Name));
        Assert.Equal("Lists things.", answer.Tools[0].Description);
        Assert.Null(answer.Tools[1].Description);
        Assert.Equal("Looks one thing up.\nSecond line.", answer.Tools[2].Description);
    }

    [Fact]
    public async Task TheHeaders_ReachTheServer_AndAreNotInTheAnswer()
    {
        var answer = await Lister().ListAsync(
            Target(headers: new() { ["X-Example-Key"] = HeaderValue }), CancellationToken.None);

        Assert.Equal(McpServerTools.Ok, answer.Status);
        lock (_seenHeaders)
        {
            Assert.NotEmpty(_seenHeaders);
            Assert.All(_seenHeaders, seen => Assert.Equal(HeaderValue, seen));
        }
        Assert.DoesNotContain(HeaderValue, System.Text.Json.JsonSerializer.Serialize(answer));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task AServerThatRefuses_IsSaidToHaveRefused(int status)
    {
        _refuseWith = status;

        var answer = await Lister().ListAsync(
            Target(headers: new() { ["X-Example-Key"] = HeaderValue }), CancellationToken.None);

        Assert.Equal(McpServerTools.Refused, answer.Status);
        Assert.Contains(status.ToString(), answer.Reason);
        Assert.DoesNotContain(HeaderValue, answer.Reason);
        Assert.Empty(answer.Tools);
    }

    [Fact]
    public async Task AServerError_IsReportedWithItsStatusCode()
    {
        _refuseWith = 502;

        var answer = await Lister().ListAsync(Target(), CancellationToken.None);

        Assert.Equal(McpServerTools.Error, answer.Status);
        Assert.Contains("502", answer.Reason);
    }

    [Fact]
    public async Task AnAddressThatIsNotAnMcpServer_IsAnError_NotAnEmptyList()
    {
        var answer = await Lister().ListAsync(Target("/plain"), CancellationToken.None);

        Assert.NotEqual(McpServerTools.Ok, answer.Status);
        Assert.Empty(answer.Tools);
        Assert.NotNull(answer.Reason);
    }

    [Fact]
    public async Task NothingAtTheAddress_IsUnreachable_AndTheExceptionsTextIsNotReturned()
    {
        var lister = new McpToolLister(() => new ThrowingHandler(new HttpRequestException("connect to secret-host.internal failed")));

        var answer = await lister.ListAsync(Target(), CancellationToken.None);

        Assert.Equal(McpServerTools.Unreachable, answer.Status);
        Assert.DoesNotContain("secret-host", answer.Reason);
    }

    [Fact]
    public async Task AServerThatNeverAnswers_TimesOut()
    {
        var lister = new McpToolLister(() => new HangingHandler(), TimeSpan.FromMilliseconds(200));

        var answer = await lister.ListAsync(Target(), CancellationToken.None);

        Assert.Equal(McpServerTools.TimedOut, answer.Status);
        Assert.Empty(answer.Tools);
    }

    [Fact]
    public async Task TheCallersOwnCancellation_IsNotATimeout()
    {
        var lister = new McpToolLister(() => new HangingHandler());
        using var soon = new CancellationTokenSource();
        soon.CancelAfter(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lister.ListAsync(Target(), soon.Token));
    }

    [Theory]
    [InlineData("http://other.test/messages")]
    [InlineData("https://localhost/messages")]
    [InlineData("http://localhost:8081/messages")]
    public async Task Sse_OffOriginMessageEndpoint_NeverReceivesHeaders(string endpoint)
    {
        var handler = new SseEndpointHandler(endpoint);
        var lister = new McpToolLister(() => handler, TimeSpan.FromMilliseconds(500));
        var target = Target(headers: new() { ["X-Example-Key"] = HeaderValue }) with { Sse = true };

        var answer = await lister.ListAsync(target, CancellationToken.None);

        Assert.NotEqual(McpServerTools.Ok, answer.Status);
        Assert.True(handler.SawSavedOriginHeader);
        Assert.Equal(0, handler.OffOriginRequests);
        Assert.DoesNotContain(HeaderValue, System.Text.Json.JsonSerializer.Serialize(answer));
    }

    [Fact]
    public async Task SessionDelete_Stalls_IsCancelledWithinTheTotalBudget()
    {
        var handler = new StalledDeleteHandler(_app.GetTestServer().CreateHandler());
        var budget = TimeSpan.FromSeconds(1);
        var lister = new McpToolLister(() => handler, budget);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var answer = await lister.ListAsync(Target(), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(3));

        Assert.True(handler.DeleteStarted);
        Assert.True(handler.DeleteCancelled);
        Assert.Equal(McpServerTools.TimedOut, answer.Status);
        Assert.Empty(answer.Tools);
        Assert.InRange(watch.Elapsed, budget, TimeSpan.FromSeconds(2.5));
    }

    [Fact]
    public async Task SessionDelete_CallerCancels_CancellationIsNotATimeout()
    {
        var handler = new StalledDeleteHandler(_app.GetTestServer().CreateHandler());
        var lister = new McpToolLister(() => handler);
        using var caller = new CancellationTokenSource();
        var listing = lister.ListAsync(Target(), caller.Token);
        await handler.DeleteReached.Task.WaitAsync(TimeSpan.FromSeconds(3));

        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listing);
        Assert.True(handler.DeleteCancelled);
    }

    [Theory]
    [InlineData("/messages")]
    [InlineData("http://localhost/messages")]
    public async Task Sse_SameOriginMessageEndpoint_ReceivesSavedHeaders(string endpoint)
    {
        var handler = new SseEndpointHandler(endpoint);
        var lister = new McpToolLister(() => handler, TimeSpan.FromSeconds(1));
        await lister.ListAsync(
            Target(headers: new() { ["X-Example-Key"] = HeaderValue }) with { Sse = true },
            CancellationToken.None);

        Assert.True(handler.SawMessageHeader);
        Assert.Equal(0, handler.OffOriginRequests);
    }

    private sealed class SseEndpointHandler(string endpoint) : HttpMessageHandler
    {
        public bool SawSavedOriginHeader { get; private set; }
        public bool SawMessageHeader { get; private set; }
        public int OffOriginRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.GetLeftPart(UriPartial.Authority) != "http://localhost")
            {
                OffOriginRequests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            }
            if (request.Method == HttpMethod.Post)
            {
                SawMessageHeader = request.Headers.TryGetValues("X-Example-Key", out var posted)
                    && posted.Contains(HeaderValue);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
            }
            SawSavedOriginHeader |= request.Headers.TryGetValues("X-Example-Key", out var values)
                && values.Contains(HeaderValue);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"event: endpoint\ndata: {endpoint}\n\n",
                    System.Text.Encoding.UTF8, "text/event-stream"),
            });
        }
    }

    private sealed class StalledDeleteHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public TaskCompletionSource DeleteReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool DeleteStarted { get; private set; }
        public bool DeleteCancelled { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method != HttpMethod.Delete)
                return await base.SendAsync(request, ct);
            DeleteStarted = true;
            DeleteReached.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { DeleteCancelled = true; throw; }
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class ThrowingHandler(Exception error) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromException<HttpResponseMessage>(error);
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
