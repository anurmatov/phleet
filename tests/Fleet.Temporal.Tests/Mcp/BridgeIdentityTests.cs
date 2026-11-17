using System.Net;
using Fleet.Shared.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Fleet.Temporal.Services;
using Microsoft.AspNetCore.Http;

namespace Fleet.Temporal.Tests.Mcp;

/// <summary>Which of the bridge's routes are the MCP endpoint, where a caller must be known.</summary>
public sealed class BridgeIdentityTests
{
    [Fact]
    public async Task RequiredIdentity_HttpHealthAndExactStatusExclusionsRemainUsable()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["AgentIdentity:Key"] = new string('A', 43) });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAgentIdentity(builder.Configuration);
        await using var app = builder.Build();
        app.UseAgentIdentity(c => BridgeIdentity.IsMcp(c.Request.Path));
        foreach (var path in new[] { "/health", "/workflow-failures", "/pending-delegations" })
            app.MapGet(path, () => "ok");
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(5) };
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/healthz")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/workflow-failures")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/pending-delegations")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/workflow-failures/extra")).StatusCode);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/mcp")]
    [InlineData("/sse")]
    [InlineData("/healthz")]
    [InlineData("/workflow-failures/extra")]
    public void TheMcpEndpoint_IsEveryRouteButTheStatusRoutes(string path)
    {
        Assert.True(BridgeIdentity.IsMcp(new PathString(path)));
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/HEALTH")]
    [InlineData("/workflow-failures")]
    [InlineData("/pending-delegations")]
    public void TheStatusRoutes_AreNotAskedForAToken(string path)
    {
        Assert.False(BridgeIdentity.IsMcp(new PathString(path)));
    }
}
