using System.Net;
using Fleet.Comms;
using Fleet.Comms.Auth;
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
        await using var host = await NorthTestHost.StartAsync();

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
        await using var host = await NorthTestHost.StartAsync();

        foreach (var forged in new[] { "127.0.0.1:8081", "localhost:8081", "127.0.0.1" })
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.TryAddWithoutValidation("Host", forged);

            var response = await host.Client.SendAsync(request);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public async Task TheNorthRouteTableIsStillExactlyFourRoutes()
    {
        await using var host = await NorthTestHost.StartAsync();

        var registered = host.Services.GetServices<EndpointDataSource>()
            .SelectMany(s => s.Endpoints)
            .OfType<RouteEndpoint>()
            .Count();

        Assert.Equal(4, registered);
        Assert.Equal(4, NorthEndpoints.Routes.Count);
    }

    // ── the ops surface ──────────────────────────────────────────────────────

    [Fact]
    public async Task HealthAndReadyAnswerOnTheOpsListenerWhenTheStoreIsHealthy()
    {
        var store = new InMemoryAuthStore();
        await using var ops = BuildOps(store);
        var client = ops.GetTestClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/ready")).StatusCode);
    }

    /// <summary>
    /// F4, and the reason the healthcheck targets `/ready` rather than `/health`: the store
    /// initialises lazily, so a broken path produces a process that starts cleanly and then `503`s
    /// every request. Liveness calls that healthy. Readiness must not.
    /// </summary>
    [Fact]
    public async Task ReadyReports503WhenTheStoreIsUnavailable_WhileHealthStillReports200()
    {
        var store = new InMemoryAuthStore { FailEveryOperation = true };
        await using var ops = BuildOps(store);
        var client = ops.GetTestClient();

        // The process is up — that is all liveness claims, and it is true.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);

        var ready = await client.GetAsync("/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);

        // The same fixed body every other store failure produces. A readiness endpoint is not a
        // debugging surface.
        var body = await AuthLifecycleTests.Body<AuthLifecycleTests.ErrorBody>(ready);
        Assert.Equal("internal", body.Code);
        Assert.Equal(ProtocolErrors.Internal, body.Message);
    }

    /// <summary>
    /// Recovery needs no recreation: the probe reflects the store's current state, not the state it
    /// was in at startup.
    /// </summary>
    [Fact]
    public async Task ReadyRecoversWithoutRestartingWhenTheStoreComesBack()
    {
        var store = new InMemoryAuthStore { FailEveryOperation = true };
        await using var ops = BuildOps(store);
        var client = ops.GetTestClient();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/ready")).StatusCode);

        store.FailEveryOperation = false;

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/ready")).StatusCode);
    }

    [Fact]
    public async Task NoNorthOrSouthRouteIsReachableOnTheOpsListener()
    {
        await using var ops = BuildOps(new InMemoryAuthStore());
        var client = ops.GetTestClient();

        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Post, "/v1/auth/devices"),
                     (HttpMethod.Post, "/v1/auth/token"),
                     (HttpMethod.Get, "/v1/session"),
                     (HttpMethod.Post, "/events:append"),
                     (HttpMethod.Post, "/conversations"),
                 })
        {
            var response = await client.SendAsync(new HttpRequestMessage(method, path));

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    private static WebApplication BuildOps(IAuthStore store)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        var app = CommsApp.BuildOpsApp(builder, store);
        app.StartAsync().GetAwaiter().GetResult();
        return app;
    }
}
