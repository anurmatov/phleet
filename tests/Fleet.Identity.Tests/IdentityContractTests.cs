using System.Security.Cryptography;
using Fleet.Identity.Configuration;
using Fleet.Identity.Services;
using Fleet.Shared.Security;

namespace Fleet.Identity.Tests;

public sealed class IdentityContractTests
{
    [Fact]
    public void MissingCustodyConfigurationRefusesStartup()
    {
        Assert.Throws<InvalidOperationException>(() => new IdentityOptions { Enabled = true }.Validate());
    }

    [Fact]
    public void ProductionSignerMatchesExistingReceiverIncludingAbsentSession()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var options = new IdentityOptions { Issuer = "issuer1", KeyId = "key1" };
        var signer = new RequestAssertionIssuer(options, key);
        var identity = new VerifiedAgentIdentity("agent1", "gateway1", "sandbox1", Guid.NewGuid(), Guid.NewGuid(), "memory", "/mcp");
        var binding = new FleetRequestBinding("POST", "/mcp", RequestRules.Hash([]), FleetRequestBinding.AbsentSessionHash);
        var token = signer.Issue(identity, binding);
        var receiver = new FleetRequestAssertionVerifier(key.ExportSubjectPublicKeyInfo(), options.Issuer, "memory:/mcp", options.KeyId);
        Assert.Equal(identity, receiver.Verify(token, binding, "memory", "/mcp").Identity);
        Assert.Throws<CryptographicException>(() => receiver.Verify(token, binding with { RawTarget = "/other" }, "memory", "/mcp"));
    }
}
