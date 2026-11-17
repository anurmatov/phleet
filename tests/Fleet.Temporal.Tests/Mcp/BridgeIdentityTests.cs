using Fleet.Temporal.Services;
using Microsoft.AspNetCore.Http;

namespace Fleet.Temporal.Tests.Mcp;

/// <summary>Which of the bridge's routes are the MCP endpoint, where a caller must be known.</summary>
public sealed class BridgeIdentityTests
{
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
