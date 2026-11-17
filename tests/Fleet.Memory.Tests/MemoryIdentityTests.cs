using Fleet.Memory.Services;
using Microsoft.AspNetCore.Http;

namespace Fleet.Memory.Tests;

/// <summary>Which of fleet-memory's routes are an agent's (the MCP endpoint) and which a service's.</summary>
public sealed class MemoryIdentityTests
{
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
