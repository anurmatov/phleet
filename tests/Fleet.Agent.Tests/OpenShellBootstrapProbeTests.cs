using System.Net;
using Fleet.Agent.Services;
using Microsoft.AspNetCore.Http;

namespace Fleet.Agent.Tests;

public sealed class OpenShellBootstrapProbeTests
{
    [Theory]
    [InlineData("success", 204, 1)]
    [InlineData("upstream_denied", 503, 1)]
    [InlineData("upstream_error", 503, 1)]
    [InlineData("redirect", 503, 1)]
    [InlineData("authorization", 401, 0)]
    [InlineData("duplicate", 401, 0)]
    [InlineData("body", 409, 0)]
    [InlineData("query", 409, 0)]
    public async Task ActualBootstrapProbe_SendsOnlyOneEmptyOutsideRequestOrDenies(string fault, int status, int arrivals)
    {
        using var fixture = new OpenShellStartupTests.OwnedConfiguration();
        var guard = new OpenShellStartupGuard(fixture.Config);
        var wire = new OutsideIngress(fault); using var client = new HttpClient(wire);
        var context = Context();
        if (fault == "authorization") context.Request.Headers.Authorization = "Bearer wrong";
        if (fault == "duplicate") context.Request.Headers.Authorization = new[] { "Bearer " + new string('x', 48), "Bearer other" };
        if (fault == "body") context.Request.Body = new MemoryStream([1]); // Unknown length still rejects bytes.
        if (fault == "query") context.Request.QueryString = new("?agent=agent2");
        await guard.ProbeAsync(context, client);
        Assert.Equal(status, context.Response.StatusCode); Assert.Equal(arrivals, wire.Arrivals);
        if (fault == "success")
        {
            var again = Context(); await guard.ProbeAsync(again, client);
            Assert.Equal(409, again.Response.StatusCode); Assert.Equal(1, wire.Arrivals);
        }
    }

    [Fact]
    public void ActualProgram_SelectsBootstrapBeforeBothCoreAndCliMode()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Fleet.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var source = File.ReadAllText(Path.Combine(root.FullName, "src/Fleet.Agent/Program.cs"));
        var bootstrap = source.IndexOf("TryAddOpenShellBootstrapServices", StringComparison.Ordinal);
        Assert.True(bootstrap >= 0 && bootstrap < source.IndexOf("AddAgentCoreServices", StringComparison.Ordinal));
        Assert.True(bootstrap < source.IndexOf("var isCliMode", StringComparison.Ordinal));
        Assert.Contains("OpenShellStartupGuard.MapBootstrap(bootstrap)", source);
    }

    private static DefaultHttpContext Context()
    {
        var context = new DefaultHttpContext(); context.Request.Method = "POST";
        context.Request.Headers.Authorization = "Bearer " + new string('x', 48); return context;
    }
    private sealed class OutsideIngress(string fault) : HttpMessageHandler
    {
        public int Arrivals;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Arrivals++; Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal("/identity/ready", request.RequestUri!.AbsolutePath);
            Assert.Empty(request.RequestUri.Query); Assert.Empty(await request.Content!.ReadAsByteArrayAsync(ct));
            Assert.False(request.Headers.Contains("Authorization")); Assert.False(request.Headers.Contains("X-Fleet-Assertion"));
            Assert.False(request.Headers.Contains("Mcp-Session-Id"));
            if (fault == "upstream_error") throw new HttpRequestException("owned fixture failure");
            return new HttpResponseMessage(fault == "upstream_denied" ? HttpStatusCode.Forbidden
                : fault == "redirect" ? HttpStatusCode.TemporaryRedirect : HttpStatusCode.NoContent);
        }
    }
}
