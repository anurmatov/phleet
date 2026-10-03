using Fleet.Orchestrator.Services;
using Microsoft.Extensions.Configuration;

namespace Fleet.Orchestrator.Tests;

public sealed class JournalTokenVerificationTests
{
    private static string Key(byte b) => Convert.ToBase64String(Enumerable.Repeat(b, 32).ToArray());
    private static JournalTokenService Service(string? key) => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Journal:TokenKey"] = key }).Build());

    [Fact]
    public void Rotation_VerifiesEveryConfiguredKey_ButPinsPurpose()
    {
        var older = Service(Key(42)).Mint(JournalTokenService.PurposeCrossChatAuthz, "fleet-comms");
        var rotated = Service($"{Key(43)},{Key(42)}");
        Assert.True(rotated.TryVerify(older, JournalTokenService.PurposeCrossChatAuthz, out var caller));
        Assert.Equal("fleet-comms", caller);
        Assert.False(rotated.TryVerify(older, JournalTokenService.PurposeReadCrossChat, out _));
        Assert.False(Service(Key(43)).TryVerify(older, JournalTokenService.PurposeCrossChatAuthz, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("cj1.cross-chat-authz.fleet-comms.invalid")]
    [InlineData("cj1.cross-chat-authz.fleet-comms.")]
    [InlineData("cj1.cross-chat-authz.fleet-comms.AAAAA")]
    [InlineData("cj1.cross-chat-authz.fleet-comms.AA..")]
    public void MalformedCredential_Denied(string? token) =>
        Assert.False(Service(Key(42)).TryVerify(token, JournalTokenService.PurposeCrossChatAuthz, out _));
}
