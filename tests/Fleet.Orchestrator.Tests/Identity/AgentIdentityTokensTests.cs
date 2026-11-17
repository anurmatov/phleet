using Fleet.Shared;

namespace Fleet.Orchestrator.Tests.Identity;

/// <summary>
/// The agent identity token and the one rule for who a caller is. Pure: no server, no request.
/// </summary>
public sealed class AgentIdentityTokensTests
{
    internal static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    internal static readonly byte[] OtherKey = Enumerable.Range(101, 32).Select(i => (byte)i).ToArray();

    internal static string Encode(byte[] key) => Convert.ToBase64String(key).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Agent(string name, byte[]? key = null) =>
        AgentIdentityTokens.Mint(key ?? Key, AgentIdentityTokens.PurposeAgent, name);

    // ── the token ────────────────────────────────────────────────────────────

    [Fact]
    public void AMintedToken_Verifies_AndSaysWhoseItIs()
    {
        var token = Agent("agent-1");

        Assert.StartsWith("fa1.agent.agent-1.", token);
        Assert.True(AgentIdentityTokens.TryVerify(token, [Key], out var purpose, out var subject));
        Assert.Equal("agent", purpose);
        Assert.Equal("agent-1", subject);
    }

    [Fact]
    public void TheFormat_IsPinned_BecauseTheOperatorsScriptAndEveryServerMustAgreeOnIt()
    {
        // Computed outside this code base (HMAC-SHA256 over "fa1|service|operator" with bytes 1..32).
        // scripts/mint-identity-token.sh prints the same for the same key.
        Assert.Equal(
            "fa1.service.operator.TqvsOTZUHJj9KL5szXr1pmb0sWamSYPtypubHVPapmo",
            AgentIdentityTokens.Mint(Key, AgentIdentityTokens.PurposeService, "operator"));
    }

    [Fact]
    public void TheSameAgentAlwaysGetsTheSameToken_AndAnotherAgentAnother()
    {
        Assert.Equal(Agent("agent-1"), Agent("agent-1"));
        Assert.NotEqual(Agent("agent-1"), Agent("agent-2"));
        Assert.NotEqual(Agent("agent-1"), AgentIdentityTokens.Mint(Key, AgentIdentityTokens.PurposeService, "agent-1"));
    }

    [Fact]
    public void ATokenUnderAnotherKey_DoesNotVerify()
    {
        Assert.False(AgentIdentityTokens.TryVerify(Agent("agent-1", OtherKey), [Key], out _, out var subject));
        Assert.Equal("", subject);
    }

    [Fact]
    public void OneAgentsToken_CannotBeTurnedIntoAnothers()
    {
        var parts = Agent("agent-1").Split('.');

        // The same signature under another name, and under the other purpose.
        Assert.False(AgentIdentityTokens.TryVerify($"fa1.agent.agent-2.{parts[3]}", [Key], out _, out _));
        Assert.False(AgentIdentityTokens.TryVerify($"fa1.service.agent-1.{parts[3]}", [Key], out _, out _));
        // A name that only starts like the signed one.
        Assert.False(AgentIdentityTokens.TryVerify($"fa1.agent.agent-10.{parts[3]}", [Key], out _, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("fa1")]
    [InlineData("fa1.agent.agent-1")]
    [InlineData("fa1.agent.agent-1.")]
    [InlineData("fa1.agent.agent-1.not base64")]
    [InlineData("fa1.agent.agent-1.AAAA")]
    [InlineData("fa1.agent..AAAA")]
    [InlineData("fa1.admin.agent-1.AAAA")]
    [InlineData("fa2.agent.agent-1.AAAA")]
    [InlineData("fa1.agent.agent 1.AAAA")]
    [InlineData("fa1.agent.agent-1.AAAA.extra")]
    public void AnythingThatIsNotAToken_DoesNotVerify_AndNeverThrows(string? token)
    {
        Assert.False(AgentIdentityTokens.TryVerify(token, [Key], out _, out _));
    }

    [Fact]
    public void WithNoKey_NothingVerifies()
    {
        Assert.False(AgentIdentityTokens.TryVerify(Agent("agent-1"), [], out _, out _));
    }

    [Fact]
    public void Rotation_TheFirstKeyMints_AndEveryKeyVerifies()
    {
        var keys = AgentIdentityTokens.ParseKeys($"{Encode(OtherKey)}, {Encode(Key)}");

        Assert.Equal(2, keys.Count);
        Assert.Equal(Agent("agent-1", OtherKey), AgentIdentityTokens.Mint(keys[0], AgentIdentityTokens.PurposeAgent, "agent-1"));
        // A token from before the new key was prepended still verifies.
        Assert.True(AgentIdentityTokens.TryVerify(Agent("agent-1", Key), keys, out _, out _));
        Assert.True(AgentIdentityTokens.TryVerify(Agent("agent-1", OtherKey), keys, out _, out _));
    }

    [Fact]
    public void ParseKeys_NoKeyIsADeploymentWithoutOne_NotAnError()
    {
        Assert.Empty(AgentIdentityTokens.ParseKeys(null));
        Assert.Empty(AgentIdentityTokens.ParseKeys(""));
        Assert.Empty(AgentIdentityTokens.ParseKeys(" , "));
    }

    [Fact]
    public void ParseKeys_ABadKeyIsRefused_ByPosition_NeverByValue()
    {
        var shortKey = Encode(new byte[16]);
        var tooShort = Assert.Throws<FormatException>(() => AgentIdentityTokens.ParseKeys($"{Encode(Key)},{shortKey}"));
        Assert.Contains("key 2", tooShort.Message);
        Assert.DoesNotContain(shortKey, tooShort.Message);

        var notBase64 = Assert.Throws<FormatException>(() => AgentIdentityTokens.ParseKeys("not*a*key"));
        Assert.Contains("key 1", notBase64.Message);
        Assert.DoesNotContain("not*a*key", notBase64.Message);
    }

    [Theory]
    [InlineData("admin", "agent-1")]
    [InlineData("agent", "")]
    [InlineData("agent", "agent 1")]
    [InlineData("agent", "agent.1")]
    [InlineData("agent", "agent|1")]
    public void Mint_RefusesAPurposeOrANameItCannotSign(string purpose, string subject)
    {
        Assert.Throws<ArgumentException>(() => AgentIdentityTokens.Mint(Key, purpose, subject));
    }

    [Theory]
    [InlineData("Bearer fa1.agent.a.b", "fa1.agent.a.b")]
    [InlineData("bearer   fa1.agent.a.b  ", "fa1.agent.a.b")]
    [InlineData("  Bearer something-else", "something-else")]
    [InlineData("Basic abc", null)]
    [InlineData("Bearer", null)]
    [InlineData("Bearer ", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void BearerOf_ReadsOnlyABearerHeader(string? header, string? expected)
    {
        Assert.Equal(expected, AgentIdentityTokens.BearerOf(header));
    }

    // ── who a caller is ──────────────────────────────────────────────────────

    private static (AgentIdentityOutcome Outcome, AgentCaller Caller) Resolve(
        string? header, string? claimed, bool enforce, params byte[][] keys) =>
        AgentIdentityRules.Resolve(header, claimed, keys, enforce);

    [Fact]
    public void AValidAgentToken_IsThatAgent_WhateverTheUrlClaims()
    {
        foreach (var enforce in new[] { false, true })
        {
            var (outcome, caller) = Resolve($"Bearer {Agent("agent-1")}", "someone-else", enforce, Key);

            Assert.Equal(AgentIdentityOutcome.Accepted, outcome);
            Assert.Equal(new AgentCaller("agent-1", AgentCallerKind.Agent), caller);
            Assert.True(caller.IsVerified);
            Assert.Equal("agent-1", caller.AgentName);
        }
    }

    [Fact]
    public void AServiceToken_IsAService_AndNotAnAgent()
    {
        var token = AgentIdentityTokens.Mint(Key, AgentIdentityTokens.PurposeService, "fleet-orchestrator");

        var (outcome, caller) = Resolve($"Bearer {token}", "agent-1", true, Key);

        Assert.Equal(AgentIdentityOutcome.Accepted, outcome);
        Assert.Equal(AgentCallerKind.Service, caller.Kind);
        Assert.True(caller.IsVerified);
        Assert.Equal("", caller.AgentName);
    }

    [Fact]
    public void AWrongToken_IsRefusedInEveryMode_NeverTreatedAsNoToken()
    {
        foreach (var enforce in new[] { false, true })
        {
            var (outcome, caller) = Resolve($"Bearer {Agent("agent-1", OtherKey)}", "agent-1", enforce, Key);

            Assert.Equal(AgentIdentityOutcome.InvalidToken, outcome);
            Assert.Equal(AgentCaller.None, caller);
        }
    }

    [Fact]
    public void WithoutAToken_ACallerIsBelievedOnlyWhileTokensAreNotRequired()
    {
        var (served, caller) = Resolve(null, " agent-1 ", enforce: false, Key);
        Assert.Equal(AgentIdentityOutcome.Accepted, served);
        Assert.Equal(new AgentCaller("agent-1", AgentCallerKind.Unverified), caller);
        Assert.False(caller.IsVerified);
        Assert.Equal("agent-1", caller.AgentName);

        var (refused, nobody) = Resolve(null, "agent-1", enforce: true, Key);
        Assert.Equal(AgentIdentityOutcome.TokenRequired, refused);
        Assert.Equal(AgentCaller.None, nobody);
    }

    [Fact]
    public void ABearerThatIsNotThisFleetsToken_IsNoToken()
    {
        // The orchestrator's own operator check reads such a bearer; this rule does not.
        var (observed, caller) = Resolve("Bearer operator-secret", "agent-1", enforce: false, Key);
        Assert.Equal(AgentIdentityOutcome.Accepted, observed);
        Assert.Equal(AgentCallerKind.Unverified, caller.Kind);

        Assert.Equal(AgentIdentityOutcome.TokenRequired, Resolve("Bearer operator-secret", "agent-1", enforce: true, Key).Outcome);
    }

    [Fact]
    public void WithNoKey_CallersAreBelievedAsBefore_EvenOnesThatSendAToken()
    {
        var (outcome, caller) = Resolve($"Bearer {Agent("agent-9")}", "agent-1", enforce: false);

        Assert.Equal(AgentIdentityOutcome.Accepted, outcome);
        // Nothing can be verified, so the token says nothing: the claim stands, unverified.
        Assert.Equal(new AgentCaller("agent-1", AgentCallerKind.Unverified), caller);
    }

    [Fact]
    public void NobodyIsNobody()
    {
        var (outcome, caller) = Resolve(null, "  ", enforce: false, Key);

        Assert.Equal(AgentIdentityOutcome.Accepted, outcome);
        Assert.Equal(AgentCaller.None, caller);
        Assert.Equal("", caller.AgentName);
    }

    // ── grants ───────────────────────────────────────────────────────────────

    [Fact]
    public void AToolIsGranted_ByItsOwnName_OrByAGrantOfTheWholeServer()
    {
        string[] grants = ["Read", "mcp__fleet-memory__memory_get", "mcp__fleet-telegram", "mcp__fleet-temporal__*"];

        Assert.True(AgentToolGrants.Allows(grants, "fleet-memory", "memory_get"));
        Assert.True(AgentToolGrants.Allows(grants, "fleet-memory", "MEMORY_GET"));
        Assert.False(AgentToolGrants.Allows(grants, "fleet-memory", "memory_delete"));
        Assert.True(AgentToolGrants.Allows(grants, "fleet-telegram", "send_message"));
        Assert.True(AgentToolGrants.Allows(grants, "fleet-temporal", "notify_cto"));
        Assert.False(AgentToolGrants.Allows(grants, "fleet-orchestrator", "create_agent"));
        Assert.False(AgentToolGrants.Allows([], "fleet-memory", "memory_get"));
    }

    [Fact]
    public void AGrantForOneServer_IsNotAGrantForAServerWhoseNameStartsTheSame()
    {
        Assert.False(AgentToolGrants.Allows(["mcp__fleet-memory__memory_get"], "fleet-memory-2", "memory_get"));
        Assert.False(AgentToolGrants.Allows(["mcp__fleet-memory-2"], "fleet-memory", "memory_get"));
        Assert.False(AgentToolGrants.Allows(["mcp__fleet-memory__memory_get_all"], "fleet-memory", "memory_get"));
    }
}
