using Fleet.Orchestrator.Services;

namespace Fleet.Orchestrator.Tests.Services;

public class ContainerProvisioningServiceTests
{
    // ── WithAgentParam ────────────────────────────────────────────────────────
    // Covers the idempotency invariant: any pre-existing query string is stripped
    // before ?agent= is appended, so re-provisioning an agent whose DB URL already
    // carries ?agent= (or any other query param) never produces a double-query URL.

    [Fact]
    public void WithAgentParam_NoQuery_AppendsAgent()
    {
        var result = ContainerProvisioningService.WithAgentParam(
            "http://fleet-memory:3100/mcp", "myagent");

        Assert.Equal("http://fleet-memory:3100/mcp?agent=myagent", result);
    }

    [Fact]
    public void WithAgentParam_ExistingAgentQuery_ReplacesWithNewAgent()
    {
        // This is the actual bug: re-provisioning an agent whose DB URL already
        // had ?agent=old produced http://...?agent=old?agent=new (malformed).
        var result = ContainerProvisioningService.WithAgentParam(
            "http://fleet-memory:3100/mcp?agent=old", "new");

        Assert.Equal("http://fleet-memory:3100/mcp?agent=new", result);
    }

    [Fact]
    public void WithAgentParam_TrailingSlashAndQuery_StripsSlashAndQuery()
    {
        var result = ContainerProvisioningService.WithAgentParam(
            "http://fleet-memory:3100/mcp/?agent=old", "foo");

        Assert.Equal("http://fleet-memory:3100/mcp?agent=foo", result);
    }

    [Fact]
    public void WithAgentParam_PathSegmentsPreserved()
    {
        var result = ContainerProvisioningService.WithAgentParam(
            "http://fleet-temporal-bridge:3001/mcp", "adev");

        Assert.Equal("http://fleet-temporal-bridge:3001/mcp?agent=adev", result);
    }

    [Fact]
    public void WithAgentParam_UnrelatedQueryParams_AllDropped()
    {
        // WithAgentParam strips ALL query params, not just ?agent=.
        // Fleet-internal MCP URLs never carry other params — this is intentional.
        var result = ContainerProvisioningService.WithAgentParam(
            "http://fleet-telegram:3800/mcp?foo=bar&baz=qux", "myagent");

        Assert.Equal("http://fleet-telegram:3800/mcp?agent=myagent", result);
    }

    [Fact]
    public void WithAgentParam_IdempotentOnAlreadyCorrectUrl()
    {
        // Calling WithAgentParam twice (simulate two reprovisions) yields the same URL.
        var once = ContainerProvisioningService.WithAgentParam(
            "http://fleet-memory:3100/mcp", "foo");
        var twice = ContainerProvisioningService.WithAgentParam(once, "foo");

        Assert.Equal(once, twice);
    }
}
