using System.Net;
using Openshell.Middleware.V1;

namespace Fleet.Identity.Tests;

public sealed class BootstrapIdentityTests
{
    [Theory]
    [InlineData("ready", 204)]
    [InlineData("phase", 403)]
    [InlineData("revoked", 403)]
    [InlineData("lease", 403)]
    [InlineData("deadline", 403)]
    [InlineData("session", 409)]
    [InlineData("desired", 409)]
    [InlineData("gateway", 503)]
    [InlineData("certificate", 401)]
    public async Task GeneratedHookThroughOutsideIngressAndActualReceiver_UsesFreshAuthority(string fault, int expected)
    {
        await using var f = new BootstrapIdentityFixture(); await f.StartAsync();
        var proof = await f.ProofAsync();
        Assert.Equal(Decision.Allow, proof.Decision);
        await f.FaultAsync(fault);
        using var result = await f.SendAsync(proof.HeaderMutations.Single().Write.Value);
        Assert.Equal((HttpStatusCode)expected, result.StatusCode);
        Assert.Equal(0, f.NormalMemoryAdmitted);
        if (fault == "ready")
        {
            using var replay = await f.SendAsync(proof.HeaderMutations.Single().Write.Value);
            Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
            Assert.True(f.CurrentSupervisorReads > 0);
            Assert.Equal(1, f.Accepted);
            await f.AssertMemoryDeniedAsync(proof.HeaderMutations.Single().Write.Value);
        }
    }
}
