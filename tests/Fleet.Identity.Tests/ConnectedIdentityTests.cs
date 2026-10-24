using System.Data.Common;
using System.Net;
using Fleet.Identity.Services;
using Fleet.Shared.Security;
using Grpc.Core;
using Openshell.Extension.V1;
using Openshell.Middleware.V1;

namespace Fleet.Identity.Tests;

public sealed class ConnectedIdentityTests
{
    [Fact]
    public async Task GeneratedHookThroughIngressAndRealMapMcp_UsesDurableSessionAndFreshAclOnEachCall()
    {
        await using var f = new IdentityFixture(); await f.StartAsync();
        var manifest = await f.Hook.DescribeAsync(new MiddlewareDescribeRequest { Gateway = new PeerMetadata {
            ProtocolVersion = new ProtocolVersion { Major = 1 }, SupportedCapabilities = { "openshell.supervisor-middleware.contract" } } }, new Metadata { { "authorization", "Bearer " + f.Token("gateway", null) } });
        Assert.Equal(f.Options.GatewayAudience, manifest.ExpectedAudience);
        Assert.Equal(SupervisorMiddlewarePhase.PreCredentials, manifest.Bindings.Single().Phase);
        var session = await f.InitializeAsync();
        Assert.Equal(1, await f.Count("agent_identity_sessions"));
        using (var initialized = await f.Send("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", session))
            Assert.True(initialized.IsSuccessStatusCode);
        using (var list = await f.Send("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", session))
            Assert.Contains("memory_get", await list.Content.ReadAsStringAsync());
        using (var call = await f.Send(f.Call, session)) Assert.Contains("owned secret content", await call.Content.ReadAsStringAsync());
        f.Grants = false;
        using (var denied = await f.Send(f.Call, session))
        { var text = await denied.Content.ReadAsStringAsync(); Assert.Contains("access denied", text); Assert.DoesNotContain("owned secret content", text); }
        f.AclUnavailable = true;
        using (var denied = await f.Send(f.Call, session)) Assert.Equal(HttpStatusCode.ServiceUnavailable, denied.StatusCode);
        Assert.Equal(6, await f.Count("identity_request_nonces"));
    }

    [Theory]
    [InlineData("signature")]
    [InlineData("kid")]
    [InlineData("type")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("expired")]
    [InlineData("lifetime")]
    [InlineData("kind")]
    [InlineData("sandbox")]
    public async Task ActualGeneratedHook_RejectsUntrustedGatewayClaimsBeforeIssuerOrReceiver(string fault)
    {
        await using var f = new IdentityFixture(); await f.StartAsync();
        var token = f.Token(fault == "kind" ? "gateway" : "supervisor", fault == "sandbox" ? "foreign" : "sandbox1", fault);
        var response = await f.Evaluate(f.Evaluation(IdentityFixture.Initialize), token);
        Assert.Equal(Decision.Deny, response.Decision); Assert.Empty(response.HeaderMutations);
        Assert.Equal(0, f.ReceiverRequests); Assert.Equal(0, await f.Count("identity_request_nonces"));
    }

    [Theory]
    [InlineData("phase")]
    [InlineData("revoked")]
    [InlineData("unbound")]
    [InlineData("epoch")]
    [InlineData("retired")]
    public async Task IssuedProof_DoesNotSurviveAuthoritativeMappingChange(string fault)
    {
        await using var f = new IdentityFixture(); await f.StartAsync();
        var proof = (await f.Evaluate(f.Evaluation(IdentityFixture.Initialize))).HeaderMutations.Single().Write.Value;
        await f.Sql(fault switch {
            "phase" => "UPDATE agent_open_shell_launches SET Phase='security_ready'",
            "revoked" => "UPDATE agent_open_shell_launches SET RevokedAt='2020-01-01'",
            "unbound" => "UPDATE agent_open_shell_launches SET ActiveAgentId=NULL",
            "epoch" => $"UPDATE agent_open_shell_launches SET IdentityEpoch='{Guid.NewGuid():D}'",
            _ => "INSERT INTO open_shell_retired_ids VALUES ('gateway1','sandbox1')" });
        using var response = await f.Send(IdentityFixture.Initialize, assertion: proof);
        Assert.False(response.IsSuccessStatusCode); Assert.Equal(0, f.ReceiverRequests);
        Assert.Equal(0, await f.Count("identity_request_nonces"));
        if (fault != "epoch") Assert.Equal(Decision.Deny, (await f.Evaluate(f.Evaluation(IdentityFixture.Initialize))).Decision);
    }

    [Theory]
    [InlineData("body")]
    [InlineData("session")]
    [InlineData("query")]
    [InlineData("duplicate")]
    [InlineData("override")]
    [InlineData("oversize")]
    [InlineData("binary")]
    [InlineData("websocket")]
    public async Task HookRejectsShapeAndIdentityOverrides(string fault)
    {
        await using var f = new IdentityFixture(); await f.StartAsync();
        var evaluation = f.Evaluation(IdentityFixture.Initialize);
        switch (fault)
        {
            case "body": evaluation.Body = Google.Protobuf.ByteString.CopyFromUtf8("not-json"); break;
            case "session": evaluation.Headers.Add(new HttpHeader { Name = "mcp-session-id", Value = "foreign" }); break;
            case "query": evaluation.Target.Query = "AgEnT=agent2"; break;
            case "duplicate": evaluation.Headers.Add(new HttpHeader { Name = "Content-Type", Value = "application/json" }); break;
            case "override": evaluation.Headers.Add(new HttpHeader { Name = "x-FLEET-agent", Value = "agent2" }); break;
            case "oversize": evaluation.Body = Google.Protobuf.ByteString.CopyFrom(new byte[262145]); break;
            case "binary": evaluation.Headers[0].Value = "application/octet-stream"; break;
            case "websocket": evaluation.Headers.Add(new HttpHeader { Name = "upgrade", Value = "websocket" }); break;
        }
        var result = await f.Evaluate(evaluation);
        Assert.Equal(Decision.Deny, result.Decision); Assert.Empty(result.HeaderMutations); Assert.Equal(0, f.ReceiverRequests);
    }

    [Fact]
    public async Task SameProofConcurrentIngressAndLaterReplay_OnlyOneDurableConsumptionAndReceiverDispatch()
    {
        await using var f = new IdentityFixture(); await f.StartAsync();
        var proof = (await f.Evaluate(f.Evaluation(IdentityFixture.Initialize))).HeaderMutations.Single().Write.Value;
        var responses = await Task.WhenAll(f.Send(IdentityFixture.Initialize, assertion: proof), f.Send(IdentityFixture.Initialize, assertion: proof));
        Assert.Single(responses, r => r.IsSuccessStatusCode);
        foreach (var response in responses) response.Dispose();
        using var replay = await f.Send(IdentityFixture.Initialize, assertion: proof); Assert.False(replay.IsSuccessStatusCode);
        Assert.Equal(1, await f.Count("identity_request_nonces")); Assert.Equal(1, f.ReceiverRequests);
    }

    [Fact]
    public async Task DurableSessionsRefuseForeignRebindAndExpireIndependently()
    {
        await using var f = new IdentityFixture(); await f.StartAsync();
        var first = await f.InitializeAsync(); var second = await f.InitializeAsync(); Assert.NotEqual(first, second);
        await Assert.ThrowsAnyAsync<DbException>(() => f.Store.BindSessionAsync(f.Identity, first, default));
        await f.Store.EndSessionAsync(f.Identity, first, default);
        Assert.Equal(Decision.Deny, (await f.Evaluate(f.Evaluation(f.Call, first))).Decision);
        using var valid = await f.Send(f.Call, second); Assert.Contains("owned secret content", await valid.Content.ReadAsStringAsync());
        await f.Sql("UPDATE agent_identity_sessions SET IdentityEpoch='00000000-0000-0000-0000-000000000001'");
        Assert.Equal(Decision.Deny, (await f.Evaluate(f.Evaluation(f.Call, second))).Decision);
    }

    [Fact]
    public async Task ReceiverRestartAndAclFailureRemainClosed_NotDurableSessionFallback()
    {
        await using var f = new IdentityFixture(); await f.StartAsync();
        var session = await f.InitializeAsync();
        f.Current = false; // Authoritative receiver state changes between outside issuance and dispatch.
        using var response = await f.Send(f.Call, session);
        Assert.False(response.IsSuccessStatusCode); Assert.Equal(1, f.ReceiverAdmitted);
    }

    [Fact]
    public async Task IngressRejectsBodyTamperingBeforeNonceOrReceiver()
    {
        await using var f = new IdentityFixture(); await f.StartAsync();
        var proof = (await f.Evaluate(f.Evaluation(IdentityFixture.Initialize))).HeaderMutations.Single().Write.Value;
        using var response = await f.Send(IdentityFixture.Initialize + " ", assertion: proof);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); Assert.Equal(0, await f.Count("identity_request_nonces"));
        Assert.Equal(0, f.ReceiverRequests);
    }

    [Fact]
    public async Task GatewayOnlyDiscoveryAndExactProtocolAndConfigAreRequired()
    {
        await using var f = new IdentityFixture(); await f.StartAsync();
        var exception = await Assert.ThrowsAsync<RpcException>(async () => await f.Hook.DescribeAsync(new(), new Metadata { { "authorization", "Bearer " + f.Token() } }));
        Assert.Equal(StatusCode.Unauthenticated, exception.StatusCode);
        var valid = await f.Hook.ValidateConfigAsync(new() { Config = f.Evaluation(IdentityFixture.Initialize).Config }, new Metadata { { "authorization", "Bearer " + f.Token("gateway", null) } });
        Assert.True(valid.Valid);
        var invalid = await f.Hook.ValidateConfigAsync(new() { Config = new() }, new Metadata { { "authorization", "Bearer " + f.Token("gateway", null) } });
        Assert.False(invalid.Valid);
    }
}
