using Fleet.LocalEndpoint;
using Fleet.Shared;

namespace Fleet.Temporal.Tests.Services;

/// <summary>
/// The shared rules for the optional bearer token a service sends to Temporal
/// (<see cref="TemporalBearerToken"/>), which the orchestrator and the bridge both read: when one
/// is configured, which values are refused and what the refusal says, the header it becomes, and
/// what is said at startup about a token that carries its own expiry. Nothing here may ever repeat
/// the token or a part of it. Invented tokens only; none is signed by any key.
/// </summary>
public sealed class TemporalBearerTokenTests
{
    private const string Setting = "Example:BearerToken";
    private const string Opaque = "synthetic-opaque-token-0123456789abcdef";
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    // ── Not set ───────────────────────────────────────────────────────────────

    /// <summary>Absent, and empty: compose hands an unset variable over as an empty string.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoToken_IsNoFault_NoHeader_AndNothingToSay(string? token)
    {
        Assert.False(TemporalBearerToken.IsSet(token));
        Assert.Null(TemporalBearerToken.DescribeFault(token, Setting));
        Assert.Null(TemporalBearerToken.AuthorizationOrNull(token, Setting));
        Assert.Null(TemporalBearerToken.DescribeExpiry(token, Setting, Now));
    }

    // ── Usable ────────────────────────────────────────────────────────────────

    public static TheoryData<string> UsableTokens => new()
    {
        Opaque,
        "a",
        "abc.def.ghi",
        // Everything RFC 6750 allows in a token, and the rest of printable ASCII.
        "ABCxyz019-._~+/==",
        "!#$%&'()*+,-./:;<=>?@[\\]^_`{|}~",
        // Not a scheme: "Bearer" only names one when a blank follows it.
        "BearerOfNews",
        new SyntheticJwt("1800000000").Token,
    };

    [Theory]
    [MemberData(nameof(UsableTokens))]
    public void AToken_IsSentAsItIs_BehindBearer(string token)
    {
        Assert.True(TemporalBearerToken.IsSet(token));
        Assert.Null(TemporalBearerToken.DescribeFault(token, Setting));
        Assert.Equal("Bearer " + token, TemporalBearerToken.AuthorizationOrNull(token, Setting));
    }

    // ── Refused ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\r\n")]
    public void ATokenOfBlanksOnly_IsRefused(string token)
    {
        Assert.StartsWith("Example:BearerToken is set to blanks only.", Fault(token));
    }

    [Theory]
    [InlineData("Bearer " + Opaque)]
    [InlineData("bearer " + Opaque)]
    [InlineData("BEARER " + Opaque)]
    [InlineData("  Bearer " + Opaque)]
    [InlineData("Bearer  " + Opaque)]
    [InlineData("Bearer ")]
    public void ATokenThatAlreadyStartsWithBearer_IsRefused_AndToldToGiveTheTokenAlone(string token)
    {
        var fault = Fault(token);

        Assert.StartsWith("Example:BearerToken starts with \"Bearer \". Give the token alone", fault);
    }

    [Theory]
    [InlineData("synthetic token")]
    [InlineData(" " + Opaque)]
    [InlineData(Opaque + " ")]
    [InlineData(Opaque + "\n")]
    [InlineData(Opaque + "\r")]
    [InlineData("synthetic\ttoken")]
    [InlineData("synthetic\u00a0token")]
    [InlineData("synthetic\u2028token")]
    public void ATokenWithABlankOrALineBreak_IsRefused(string token)
    {
        Assert.StartsWith("Example:BearerToken has a blank or a line break in it", Fault(token));
    }

    [Theory]
    [InlineData("synthetic\0token")]
    [InlineData("synthetic\u0001token")]
    [InlineData("synthetic\u007ftoken")]
    [InlineData("synthetic\u00e9token")]
    [InlineData("synthetic\u4e2dtoken")]
    [InlineData("synthetic\ud83d\ude00token")]
    public void ATokenWithACharacterAHeaderCannotCarry_IsRefused(string token)
    {
        Assert.StartsWith("Example:BearerToken has a character in it that cannot be sent in an HTTP header", Fault(token));
    }

    /// <summary>
    /// Every refusal names the setting and ends the same way, and none repeats the value or a
    /// stretch of it, whatever the value was.
    /// </summary>
    [Fact]
    public void NoRefusal_CarriesTheValueOrAPartOfIt()
    {
        var jwt = new SyntheticJwt("1800000000");
        string[] refused =
        [
            "   ",
            "Bearer " + jwt.Token,
            "bearer " + Opaque,
            jwt.Token + "\n",
            " " + jwt.Token,
            Opaque + " " + Opaque,
            jwt.Token + "\u00e9",
            Opaque + "\0",
        ];

        foreach (var token in refused)
        {
            var fault = TemporalBearerToken.DescribeFault(token, Setting);
            var thrown = Assert.Throws<TemporalBearerTokenException>(() => TemporalBearerToken.AuthorizationOrNull(token, Setting));

            Assert.NotNull(fault);
            Assert.Equal(fault, thrown.Message);
            Assert.StartsWith(Setting, fault);
            Assert.EndsWith("Nothing was connected.", fault);
            foreach (var text in new[] { fault, thrown.ToString() })
            {
                foreach (var secret in jwt.Secrets) Assert.DoesNotContain(secret, text);
                Assert.DoesNotContain(Opaque, text);
                Assert.DoesNotContain(Opaque[..16], text);
                Assert.DoesNotContain(jwt.Signature[..16], text);
            }
            // And nothing is said about the expiry of a token that will not be sent.
            Assert.Null(TemporalBearerToken.DescribeExpiry(token, Setting, Now));
        }
    }

    // ── What is said about expiry ─────────────────────────────────────────────

    [Fact]
    public void AnOpaqueToken_SaysNothingAboutItself()
    {
        Assert.Null(TemporalBearerToken.DescribeExpiry(Opaque, Setting, Now));
    }

    public static TheoryData<string> NotAJwtWithAnExpiry => new()
    {
        // Two parts, four parts, an empty part.
        "abc.def",
        "abc.def.ghi.jkl",
        $"{SyntheticJwt.Encode("{}")}..{SyntheticJwt.Encode("x")}",
        // A payload that is not base64url, not JSON, not an object.
        "abc.!!!.ghi",
        $"abc.{SyntheticJwt.Encode("not json")}.ghi",
        $"abc.{SyntheticJwt.Encode("[1700000000]")}.ghi",
        $"abc.{SyntheticJwt.Encode("1700000000")}.ghi",
        // No exp, or one that is not a number.
        new SyntheticJwt(null).Token,
        new SyntheticJwt("\"1700000000\"").Token,
        new SyntheticJwt("null").Token,
        new SyntheticJwt("true").Token,
        new SyntheticJwt("{\"at\":1700000000}").Token,
        // A number no date can hold.
        new SyntheticJwt("1e30").Token,
        new SyntheticJwt("-1e30").Token,
    };

    [Theory]
    [MemberData(nameof(NotAJwtWithAnExpiry))]
    public void ATokenThatDoesNotReadAsAJwtWithANumericExp_SaysNothing_AndIsStillSent(string token)
    {
        Assert.Null(TemporalBearerToken.DescribeExpiry(token, Setting, Now));
        Assert.Equal("Bearer " + token, TemporalBearerToken.AuthorizationOrNull(token, Setting));
    }

    [Fact]
    public void AnExpiryFarAway_IsStated()
    {
        var jwt = SyntheticJwt.ExpiringAt(new DateTimeOffset(2027, 3, 4, 5, 6, 7, TimeSpan.Zero));

        var notice = TemporalBearerToken.DescribeExpiry(jwt.Token, Setting, Now);

        Assert.NotNull(notice);
        Assert.Equal(TemporalTokenExpiryState.Valid, notice.State);
        Assert.Equal(new DateTimeOffset(2027, 3, 4, 5, 6, 7, TimeSpan.Zero), notice.ExpiresAt);
        Assert.Equal("The Temporal bearer token (Example:BearerToken) expires at 2027-03-04T05:06:07Z.", notice.Message);
    }

    [Fact]
    public void AnExpiryLessThanThirtyDaysAway_IsAWarning()
    {
        var jwt = SyntheticJwt.ExpiringAt(Now.AddDays(30).AddSeconds(-1));

        var notice = TemporalBearerToken.DescribeExpiry(jwt.Token, Setting, Now);

        Assert.NotNull(notice);
        Assert.Equal(TemporalTokenExpiryState.ExpiresSoon, notice.State);
        Assert.Equal(
            "The Temporal bearer token (Example:BearerToken) expires at 2026-07-01T11:59:59Z, less than 30 days from now. "
            + "Replace it and restart this service before then: after that every Temporal call is refused.",
            notice.Message);
    }

    [Fact]
    public void AnExpiryExactlyThirtyDaysAway_IsNotYetAWarning()
    {
        var notice = TemporalBearerToken.DescribeExpiry(SyntheticJwt.ExpiringAt(Now.AddDays(30)).Token, Setting, Now);

        Assert.Equal(TemporalTokenExpiryState.Valid, notice!.State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-86400 * 400)]
    public void AnExpiryThatIsPast_IsAnError_ThatSaysTheServiceStartsAnyway(int secondsFromNow)
    {
        var jwt = SyntheticJwt.ExpiringAt(Now.AddSeconds(secondsFromNow));

        var notice = TemporalBearerToken.DescribeExpiry(jwt.Token, Setting, Now);

        Assert.NotNull(notice);
        Assert.Equal(TemporalTokenExpiryState.Expired, notice.State);
        Assert.StartsWith("The Temporal bearer token (Example:BearerToken) expired at ", notice.Message);
        Assert.EndsWith("Starting anyway: the server decides, and clocks differ.", notice.Message);
        // Past its date, the token is still what the service sends: the server is the authority.
        Assert.Equal("Bearer " + jwt.Token, TemporalBearerToken.AuthorizationOrNull(jwt.Token, Setting));
    }

    /// <summary>NumericDate may carry a fraction; the instant is said in whole seconds, in UTC.</summary>
    [Fact]
    public void AFractionalExp_IsRead()
    {
        var notice = TemporalBearerToken.DescribeExpiry(new SyntheticJwt("1800000000.75").Token, Setting, Now);

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1800000000), notice!.ExpiresAt);
        Assert.Contains("expires at 2027-01-15T08:00:00Z", notice.Message);
    }

    /// <summary>
    /// What is said carries the instant and the setting, and no part of the token: not the token,
    /// not its header, payload or signature, not a claim's value.
    /// </summary>
    [Theory]
    [InlineData(400)]
    [InlineData(10)]
    [InlineData(-10)]
    public void NoExpiryNotice_CarriesAnyPartOfTheToken(int daysFromNow)
    {
        var jwt = SyntheticJwt.ExpiringAt(Now.AddDays(daysFromNow));

        var notice = TemporalBearerToken.DescribeExpiry(jwt.Token, Setting, Now)!;

        foreach (var text in new[] { notice.Message, notice.ToString() })
        {
            foreach (var secret in jwt.Secrets) Assert.DoesNotContain(secret, text);
            Assert.DoesNotContain(jwt.Signature[..16], text);
            Assert.DoesNotContain(jwt.Payload[..16], text);
            Assert.DoesNotContain("permissions", text);
            Assert.DoesNotContain("example:read", text);
            Assert.DoesNotContain("ES256", text);
        }
    }

    private static string Fault(string token)
    {
        var fault = TemporalBearerToken.DescribeFault(token, Setting);
        Assert.NotNull(fault);
        var thrown = Assert.Throws<TemporalBearerTokenException>(() => TemporalBearerToken.AuthorizationOrNull(token, Setting));
        Assert.Equal(fault, thrown.Message);
        Assert.EndsWith("Nothing was connected.", fault);
        return fault;
    }
}
