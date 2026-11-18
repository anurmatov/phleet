using System.Net;
using Fleet.Shared.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Fleet.Memory.Services;
using Microsoft.AspNetCore.Http;

namespace Fleet.Memory.Tests;

/// <summary>Which of fleet-memory's routes are an agent's (the MCP endpoint) and which a service's.</summary>
public sealed class MemoryIdentityTests
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
        app.UseAgentIdentity(c => MemoryIdentity.IsMcp(c.Request.Path));
        app.UseServiceIdentity(c => MemoryIdentity.IsInternal(c.Request.Path));
        app.MapGet("/health", () => "ok");
        app.MapGet("/internal/memory", () => "synthetic");
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(5) };
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/healthz")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/internal/memory")).StatusCode);
        http.DefaultRequestHeaders.Authorization = new("Bearer", Fleet.Shared.AgentIdentityTokens.Mint(new byte[32], "agent", "agent1"));
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/internal/memory")).StatusCode);
        http.DefaultRequestHeaders.Authorization = new("Bearer", Fleet.Shared.AgentIdentityTokens.Mint(new byte[32], "service", "operator"));
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/internal/memory")).StatusCode);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/sse")]
    [InlineData("/message")]
    [InlineData("/mcp")]
    public void TheMcpEndpoint_IsEveryRouteThatIsNotTheHealthCheckOrAServiceRoute(string path)
    {
        Assert.True(MemoryIdentity.IsMcp(new PathString(path)));
        Assert.False(MemoryIdentity.IsInternal(new PathString(path)));
    }

    [Theory]
    [InlineData("/internal/memory")]
    [InlineData("/internal/memory/abc")]
    [InlineData("/internal/memory/search")]
    [InlineData("/internal/stats/reads")]
    [InlineData("/INTERNAL/memory")]
    public void TheRestApi_IsAServices_NotAnAgents(string path)
    {
        Assert.True(MemoryIdentity.IsInternal(new PathString(path)));
        Assert.False(MemoryIdentity.IsMcp(new PathString(path)));
    }

    [Fact]
    public void TheHealthCheck_IsNeither_SoComposeCanReadItWithoutACredential()
    {
        Assert.False(MemoryIdentity.IsMcp(new PathString("/health")));
        Assert.False(MemoryIdentity.IsInternal(new PathString("/health")));
        // A route that only starts like it is the MCP endpoint's.
        Assert.True(MemoryIdentity.IsMcp(new PathString("/healthz")));
        Assert.True(MemoryIdentity.IsMcp(new PathString("/internalx")));
    }
}
