using System.Net;
using Fleet.Comms;
using Fleet.Comms.Routes;
using Fleet.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fleet.Comms.Tests;

/// <summary>
/// `/health` and `/ready` live on their own application, and the north route table does not change.
///
/// <para>The design under test is <b>two <c>WebApplication</c> instances</b>, not one application
/// filtering ops endpoints by host. That distinction is the whole point: <c>Host</c> is
/// client-supplied, so under the filtering design a caller on the public port reaches the readiness
/// oracle by sending the ops listener's host and port. A test that asks the north listener for
/// `/ready` without that header passes under <b>both</b> designs — which is why one below sends
/// it.</para>
/// </summary>
public class OpsListenerTests
{
    // ── the north surface is unchanged ───────────────────────────────────────

    [Theory]
    [InlineData("/health")]
    [InlineData("/ready")]
    [InlineData("/metrics")]
    [InlineData("/")]
    public async Task OpsAndUnknownPathsAreNotFoundOnTheNorthListener(string path)
    {
        await using var host = await NorthTestHost.StartAsync(conversations: new FakeConversationStore());

        var response = await host.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// The case that discriminates between the two designs. A north caller claiming to be the ops
    /// listener must still get `404` — otherwise the readiness oracle is reachable through the
    /// public port by anyone who can set a header.
    /// </summary>
    [Theory]
    [InlineData("/health")]
    [InlineData("/ready")]
    public async Task AForgedOpsHostHeaderDoesNotReachOpsRoutesThroughTheNorthListener(string path)
    {
        await using var host = await NorthTestHost.StartAsync(conversations: new FakeConversationStore());

        foreach (var forged in new[] { "127.0.0.1:8081", "localhost:8081", "127.0.0.1" })
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.TryAddWithoutValidation("Host", forged);

            var response = await host.Client.SendAsync(request);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    // ── the ops surface ──────────────────────────────────────────────────────

    [Fact]
    public async Task HealthAndReadyAnswerOnTheOpsListenerWhenTheStoreIsHealthy()
    {
        var store = new FakeConversationStore();
        await using var ops = BuildOps(store);
        var client = ops.GetTestClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/ready")).StatusCode);
    }

    /// <summary>
    /// F4, and the reason the healthcheck targets `/ready` rather than `/health`: a database that is
    /// unreachable produces a process that starts cleanly and then `503`s every request. Liveness
    /// calls that healthy. Readiness must not.
    /// </summary>
    [Fact]
    public async Task ReadyReports503WhenTheStoreIsUnavailable_WhileHealthStillReports200()
    {
        var store = new FakeConversationStore { Unavailable = true };
        await using var ops = BuildOps(store);
        var client = ops.GetTestClient();

        // The process is up — that is all liveness claims, and it is true.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);

        var ready = await client.GetAsync("/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);

        // The same fixed body every other store failure produces. A readiness endpoint is not a
        // debugging surface.
        using var body = System.Text.Json.JsonDocument.Parse(await ready.Content.ReadAsStringAsync());
        Assert.Equal("internal", body.RootElement.GetProperty("code").GetString());
        Assert.Equal(ProtocolErrors.Internal, body.RootElement.GetProperty("message").GetString());
    }

    /// <summary>
    /// Recovery needs no recreation: the probe reflects the store's current state, not the state it
    /// was in at startup.
    /// </summary>
    [Fact]
    public async Task ReadyRecoversWithoutRestartingWhenTheStoreComesBack()
    {
        var store = new FakeConversationStore { Unavailable = true };
        await using var ops = BuildOps(store);
        var client = ops.GetTestClient();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/ready")).StatusCode);

        store.Unavailable = false;

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/ready")).StatusCode);
    }

    [Fact]
    public async Task NoNorthOrSouthRouteIsReachableOnTheOpsListener()
    {
        await using var ops = BuildOps(new FakeConversationStore());
        var client = ops.GetTestClient();

        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Get, "/v1/session"),
                     (HttpMethod.Get, "/v1/conversations"),
                     (HttpMethod.Post, "/events:append"),
                     (HttpMethod.Post, "/conversations"),
                 })
        {
            var response = await client.SendAsync(new HttpRequestMessage(method, path));

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    /// <summary>The journal routes (#375) are not on the ops listener either.</summary>
    [Fact]
    public async Task NoJournalRouteIsReachableOnTheOpsListener()
    {
        await using var ops = BuildOps(new FakeConversationStore());
        var client = ops.GetTestClient();

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsync("/journal/v1/messages", new StringContent("{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/journal/v1/status")).StatusCode);
    }

    private static WebApplication BuildOps(Fleet.Conversations.Contracts.IConversationStore store)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        var app = CommsApp.BuildOpsApp(builder, store);
        app.StartAsync().GetAwaiter().GetResult();
        return app;
    }
}
