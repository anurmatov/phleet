using Fleet.Orchestrator.Auth;
using Fleet.Orchestrator.Endpoints;
using Fleet.Orchestrator.Services;

namespace Fleet.Orchestrator.Tests.Auth;

/// <summary>
/// The gate as a table: <see cref="OrchestratorAuth.Decide"/> is one pure function, and this is
/// every row of it that matters.
/// </summary>
public class OrchestratorAuthDecisionTests
{
    private static AccessDecision Decide(
        string method, string path, bool edge, CallerKind caller = CallerKind.None,
        bool header = false, bool required = true) =>
        OrchestratorAuth.Decide(required, method, path, edge, caller, header);

    [Theory]
    [InlineData("GET", "/api/agents")]
    [InlineData("GET", "/api/workflows")]
    [InlineData("POST", "/api/agents/example/restart")]
    [InlineData("GET", "/ws")]
    [InlineData("GET", "/ws/logs/example")]
    [InlineData("GET", "/api/auth/devices")]
    [InlineData("POST", "/api/auth/enrollments")]
    public void FromOutside_EverythingUnderApiAndWsNeedsACredential(string method, string path)
    {
        Assert.Equal(AccessDecision.Unauthorized, Decide(method, path, edge: true));
        Assert.Equal(AccessDecision.Allow, Decide(method, path, edge: true, CallerKind.Service));
        Assert.Equal(AccessDecision.Allow, Decide(method, path, edge: true, CallerKind.App));
        Assert.Equal(AccessDecision.Allow, Decide(method, path, edge: true, CallerKind.Browser, header: true));
    }

    [Theory]
    [InlineData("GET", "/api/agents")]
    [InlineData("GET", "/api/schedules")]
    [InlineData("GET", "/ws")]
    [InlineData("POST", "/mcp")]
    [InlineData("GET", "/health")]
    [InlineData("POST", "/internal/auth/check")]
    public void FromTheInternalNetwork_TheOlderRuleStands(string method, string path) =>
        Assert.Equal(AccessDecision.Allow, Decide(method, path, edge: false));

    [Theory]
    [InlineData("POST", "/api/agents/example/restart")]
    [InlineData("PUT", "/api/agents/example/config")]
    [InlineData("DELETE", "/api/push/devices/abcdefgh")]
    public void FromTheInternalNetwork_AWriteStillNeedsACredential(string method, string path)
    {
        Assert.Equal(AccessDecision.Unauthorized, Decide(method, path, edge: false));
        Assert.Equal(AccessDecision.Allow, Decide(method, path, edge: false, CallerKind.Service));
    }

    [Theory]
    [InlineData("POST", "/api/auth/browser-session")]
    [InlineData("POST", "/api/auth/devices")]
    [InlineData("POST", "/api/auth/token")]
    [InlineData("GET", "/api/auth/session")]
    public void TheRoutesThatHandOutACredentialNeedNone(string method, string path)
    {
        Assert.Equal(AccessDecision.Allow, Decide(method, path, edge: true));
        Assert.Equal(AccessDecision.Allow, Decide(method, path, edge: false));
    }

    [Theory]
    [InlineData("GET", "/api/auth/devices")]
    [InlineData("DELETE", "/api/auth/browser-session")]
    [InlineData("GET", "/api/auth/token")]
    [InlineData("POST", "/api/auth/devices/abc/revoke")]
    [InlineData("POST", "/api/auth/session")]
    public void NothingElseUnderAuthIsPublic(string method, string path) =>
        Assert.Equal(AccessDecision.Unauthorized, Decide(method, path, edge: true));

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public void ABrowsersWriteNeedsTheRequestHeader(string method)
    {
        Assert.Equal(AccessDecision.RequestHeaderRequired, Decide(method, "/api/agents/x", edge: true, CallerKind.Browser));
        Assert.Equal(AccessDecision.Allow, Decide(method, "/api/agents/x", edge: true, CallerKind.Browser, header: true));
        // A bearer is put on a request on purpose, so it is never asked for the header.
        Assert.Equal(AccessDecision.Allow, Decide(method, "/api/agents/x", edge: true, CallerKind.App));
        Assert.Equal(AccessDecision.Allow, Decide(method, "/api/agents/x", edge: true, CallerKind.Service));
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public void ABrowsersReadDoesNot(string method) =>
        Assert.Equal(AccessDecision.Allow, Decide(method, "/api/agents", edge: true, CallerKind.Browser));

    [Fact]
    public void TheBrowserRuleHoldsOnTheInternalNetworkToo() =>
        Assert.Equal(AccessDecision.RequestHeaderRequired,
            Decide("POST", "/api/agents/x", edge: false, CallerKind.Browser));

    [Theory]
    [InlineData("GET", "/api/config/values")]
    [InlineData("PUT", "/api/config/values")]
    public void TheConfigApiIsLeftToItsOwnGate(string method, string path) =>
        Assert.Equal(AccessDecision.Allow, Decide(method, path, edge: true));

    [Theory]
    [InlineData("GET", "/api/agents", true)]
    [InlineData("POST", "/api/agents/x", true)]
    [InlineData("POST", "/api/agents/x", false)]
    public void WithNoServiceToken_NothingIsGated(string method, string path, bool edge) =>
        Assert.Equal(AccessDecision.Allow, Decide(method, path, edge, required: false));

    [Fact]
    public void TheCheckPathIsExemptFromTheOlderRuleByItsExactName()
    {
        Assert.False(OrchestratorAuth.RequiresBearerToken("POST", AuthEndpoints.CheckPath));
        Assert.True(OrchestratorAuth.RequiresBearerToken("POST", AuthEndpoints.CheckPath + "/more"));
    }
}
