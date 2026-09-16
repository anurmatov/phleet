using Fleet.Agent.Configuration;
using Fleet.Agent.Services;
using Fleet.Protocol;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Tests;

/// <summary>
/// AC43–45, AC49–51, AC54 and Constraints 4/5/22: the privacy boundary, the sanitation of runtime
/// control markers, the fixed error table, the bounds, and the principal binding.
/// </summary>
public class ProtocolPrivacyTests
{
    // ── AC49: runtime control markers and the paths they carry ───────────────

    /// <summary>
    /// The [IMAGE:...] marker carries a LOCAL FILESYSTEM PATH. Stripping the marker but leaving
    /// the path would leak the attachment directory layout to a client that has no way to fetch
    /// the bytes anyway, so the whole construct goes.
    /// </summary>
    [Fact]
    public void ImageMarker_IsRemovedTogetherWithItsPath()
    {
        var result = ProtocolSanitizer.Sanitize("here it is [IMAGE:/workspace/attachments/99-1-0.png] done");

        Assert.DoesNotContain("IMAGE", result);
        Assert.DoesNotContain("/workspace/attachments", result);
        Assert.DoesNotContain(".png", result);
        Assert.Contains("here it is", result);
        Assert.Contains("done", result);
    }

    [Fact]
    public void ReplyToMarker_IsRemoved()
    {
        var result = ProtocolSanitizer.Sanitize("[reply_to: 42] answering that");

        Assert.DoesNotContain("reply_to", result);
        Assert.Equal("answering that", result);
    }

    [Fact]
    public void TaskFailedMarker_IsRemoved()
    {
        var result = ProtocolSanitizer.Sanitize("[TASK_FAILED: cannot merge own PR] the rest");

        Assert.DoesNotContain("TASK_FAILED", result);
        Assert.DoesNotContain("cannot merge own PR", result);
    }

    /// <summary>AC49: all three markers in one string, none of them surviving.</summary>
    [Fact]
    public void AllThreeMarkers_AreRemovedFromOneString()
    {
        const string text = "[reply_to: 42] result [IMAGE:/some/local/path.png] and [TASK_FAILED: nope]";

        var result = ProtocolSanitizer.Sanitize(text);

        Assert.DoesNotContain("IMAGE", result);
        Assert.DoesNotContain("reply_to", result);
        Assert.DoesNotContain("TASK_FAILED", result);
        Assert.DoesNotContain("/some/local/path", result);
        Assert.DoesNotContain(".png", result);
        Assert.Contains("result", result);
    }

    [Fact]
    public void ConfiguredAttachmentDirectory_IsRedactedWhereverItAppears()
    {
        var result = ProtocolSanitizer.Sanitize(
            "I read /workspace/attachments/doc.pdf just now", "/workspace/attachments");

        Assert.DoesNotContain("/workspace/attachments", result);
        Assert.Contains("[redacted]", result);
    }

    [Fact]
    public void Sanitize_HandlesNullAndEmpty()
    {
        Assert.Equal("", ProtocolSanitizer.Sanitize(null));
        Assert.Equal("", ProtocolSanitizer.Sanitize(""));
    }

    [Fact]
    public void IdleMarker_IsRecognisedRegardlessOfCaseAndWhitespace()
    {
        Assert.True(ProtocolSanitizer.IsIdleMarker("IDLE"));
        Assert.True(ProtocolSanitizer.IsIdleMarker("  idle  "));
        Assert.False(ProtocolSanitizer.IsIdleMarker("idle hands"));
        Assert.False(ProtocolSanitizer.IsIdleMarker(null));
    }

    // ── AC54: bounds, at the boundary and one past it ─────────────────────────

    [Fact]
    public void TextAtTheBound_IsNotTruncated()
    {
        var text = new string('a', ProtocolLimits.MaxNoticeTextChars);

        var (result, truncated) = ProtocolSanitizer.SanitizeAndBound(text, ProtocolLimits.MaxNoticeTextChars);

        Assert.False(truncated);
        Assert.Equal(ProtocolLimits.MaxNoticeTextChars, result.Length);
    }

    [Fact]
    public void TextPastTheBound_IsTruncatedAndFlagged()
    {
        var text = new string('a', ProtocolLimits.MaxNoticeTextChars + 1);

        var (result, truncated) = ProtocolSanitizer.SanitizeAndBound(text, ProtocolLimits.MaxNoticeTextChars);

        Assert.True(truncated);
        Assert.Equal(ProtocolLimits.MaxNoticeTextChars, result.Length);
    }

    /// <summary>
    /// AC54. Splitting a surrogate pair produces a lone surrogate, which is not valid text.
    /// This has already been a shipped bug once in this codebase, which is why truncation goes
    /// through the shared helper rather than a local Substring.
    /// </summary>
    [Fact]
    public void Truncation_NeverSplitsASurrogatePair()
    {
        // 🎉 is a surrogate PAIR: two UTF-16 code units. Place one exactly on the cut index.
        const int bound = 11;
        var text = new string('a', bound - 1) + "🎉" + "tail";

        var (result, truncated) = ProtocolSanitizer.SanitizeAndBound(text, bound);

        Assert.True(truncated);
        // The cut backed off one unit rather than leaving half an emoji.
        Assert.Equal(bound - 1, result.Length);
        Assert.False(char.IsHighSurrogate(result[^1]));
        Assert.False(char.IsLowSurrogate(result[^1]));
    }

    [Fact]
    public void ToolName_IsBoundedButNeverFlagged()
    {
        var name = new string('T', ProtocolLimits.MaxToolNameChars + 20);

        var result = ProtocolSanitizer.BoundToolName(name);

        Assert.Equal(ProtocolLimits.MaxToolNameChars, result.Length);
    }

    [Fact]
    public void ToolName_UnderTheBoundIsUntouched() =>
        Assert.Equal("Read", ProtocolSanitizer.BoundToolName("Read"));

    // ── AC51: the fixed error table ───────────────────────────────────────────

    /// <summary>
    /// `internal` in particular must never carry an exception message — that is where provider
    /// text, stack frames and absolute paths would otherwise reach a client.
    /// </summary>
    [Fact]
    public void InternalErrorMessage_CarriesNoExceptionDetail()
    {
        var exception = new InvalidOperationException(
            "Connection to /var/run/secret.sock failed with token sk-ABC123");

        var message = ProtocolErrors.MessageFor(ProtocolErrorCode.Internal);

        Assert.DoesNotContain("secret.sock", message);
        Assert.DoesNotContain("sk-ABC123", message);
        Assert.DoesNotContain(exception.Message, message);
        Assert.Equal(ProtocolErrors.Internal, message);
    }

    [Fact]
    public void ExecutorErrorMessage_IsFixedNotDerivedFromRuntimeText()
    {
        // The Telegram path shows "Task failed: {lastError}". The client event must not.
        var message = ProtocolErrors.MessageFor(ProtocolErrorCode.ExecutorError);

        Assert.Equal("The turn failed.", message);
        Assert.DoesNotContain("Task failed:", message);
    }

    // ── AC42–44: principal binding ────────────────────────────────────────────

    private static PrincipalBinder BuildBinder(
        string token = "operator-set-token", long ownerUserId = 4242L,
        bool allowlisted = true, string agentName = "fleet-agent1")
    {
        var telegramOpts = Options.Create(new TelegramOptions
        {
            AllowedUserIds = allowlisted ? [ownerUserId] : [],
        });
        return new PrincipalBinder(
            Options.Create(new ClientChannelOptions { OwnerPrincipalToken = token, OwnerUserId = ownerUserId }),
            Options.Create(new AgentOptions { Name = agentName, Role = "r", WorkDir = "/tmp" }),
            new AllowlistHolder(telegramOpts));
    }

    private static PrincipalBinding Binding(string value, string scheme = PrincipalBinding.LegacyOwnerScheme) =>
        new() { Scheme = scheme, Value = value };

    [Fact]
    public void ValidBinding_Succeeds()
    {
        var result = BuildBinder().Bind(Binding("operator-set-token"), PrincipalRole.Owner);

        Assert.True(result.Success);
        Assert.StartsWith("p_", result.PrincipalId);
    }

    /// <summary>AC42: each of the five rejection cases, none of which may create a registry entry.</summary>
    [Fact]
    public void EmptyToken_DisablesTheChannelEntirely()
    {
        var result = BuildBinder(token: "").Bind(Binding("anything"), PrincipalRole.Owner);

        Assert.False(result.Success);
        Assert.Equal(ProtocolErrorCode.Unauthorized, result.Error);
    }

    [Fact]
    public void ZeroOwnerUserId_DisablesTheChannelEntirely()
    {
        var result = BuildBinder(ownerUserId: 0).Bind(Binding("operator-set-token"), PrincipalRole.Owner);

        Assert.False(result.Success);
        Assert.Equal(ProtocolErrorCode.Unauthorized, result.Error);
    }

    [Fact]
    public void WrongScheme_IsRejected()
    {
        var result = BuildBinder().Bind(Binding("operator-set-token", scheme: "device"), PrincipalRole.Owner);

        Assert.False(result.Success);
        Assert.Equal(ProtocolErrorCode.Unauthorized, result.Error);
    }

    [Fact]
    public void NonMatchingValue_IsRejected()
    {
        var result = BuildBinder().Bind(Binding("wrong-token"), PrincipalRole.Owner);

        Assert.False(result.Success);
        Assert.Equal(ProtocolErrorCode.Unauthorized, result.Error);
    }

    /// <summary>
    /// The allowlist is re-checked at OPEN time, so revoking the owner in the live allowlist also
    /// closes the client channel — with no reprovision.
    /// </summary>
    [Fact]
    public void ConfiguredButDeAllowlistedOwner_IsRejected()
    {
        var result = BuildBinder(allowlisted: false).Bind(Binding("operator-set-token"), PrincipalRole.Owner);

        Assert.False(result.Success);
        Assert.Equal(ProtocolErrorCode.Unauthorized, result.Error);
    }

    /// <summary>AC45: v1 is owner-only; member and guest are reserved values the runtime rejects.</summary>
    [Theory]
    [InlineData(PrincipalRole.Member)]
    [InlineData(PrincipalRole.Guest)]
    public void NonOwnerRole_IsRejectedAsUnsupportedRole(PrincipalRole role)
    {
        var result = BuildBinder().Bind(Binding("operator-set-token"), role);

        Assert.False(result.Success);
        Assert.Equal(ProtocolErrorCode.UnsupportedRole, result.Error);
    }

    /// <summary>
    /// AC43. A length-varying or `==` comparison leaks the token a character at a time. Both
    /// sides are hashed to a fixed 32 bytes first so the comparison is always over equal lengths —
    /// returning early on a length mismatch would itself be a (weaker) length oracle.
    /// </summary>
    [Fact]
    public void TokenComparison_RejectsAValueDifferingOnlyInLength()
    {
        var binder = BuildBinder(token: "operator-set-token");

        Assert.False(binder.Bind(Binding("operator-set-toke"), PrincipalRole.Owner).Success);
        Assert.False(binder.Bind(Binding("operator-set-tokenX"), PrincipalRole.Owner).Success);
        Assert.True(binder.Bind(Binding("operator-set-token"), PrincipalRole.Owner).Success);
    }

    [Fact]
    public void FixedTimeTokenEquals_HandlesUnequalLengths()
    {
        Assert.True(PrincipalBinder.FixedTimeTokenEquals("abc", "abc"));
        Assert.False(PrincipalBinder.FixedTimeTokenEquals("abc", "abcdef"));
        Assert.False(PrincipalBinder.FixedTimeTokenEquals("", "abc"));
    }

    /// <summary>AC44: deterministic across restarts, so no random salt.</summary>
    [Fact]
    public void PrincipalId_IsDeterministicAcrossIndependentBinders()
    {
        var a = BuildBinder().DerivePrincipalId();
        var b = BuildBinder().DerivePrincipalId();

        Assert.Equal(a, b);
    }

    [Fact]
    public void PrincipalId_DiffersWhenTheAgentNameDiffers()
    {
        var a = BuildBinder(agentName: "fleet-agent1").DerivePrincipalId();
        var b = BuildBinder(agentName: "fleet-agent2").DerivePrincipalId();

        Assert.NotEqual(a, b);
    }

    /// <summary>AC44: neither the numeric owner id nor any token material appears in the output.</summary>
    [Fact]
    public void PrincipalId_ContainsNeitherTheNumericIdNorTheToken()
    {
        const string token = "operator-set-token";
        const long ownerUserId = 4242L;

        var principalId = BuildBinder(token, ownerUserId).DerivePrincipalId();

        Assert.DoesNotContain("4242", principalId);
        Assert.DoesNotContain(token, principalId);
        // Nor any meaningful substring of it.
        Assert.DoesNotContain("operator", principalId);
        Assert.DoesNotContain("token", principalId);
    }

    [Fact]
    public void Binder_IsDisabledWhenEitherValueIsAbsent()
    {
        Assert.False(BuildBinder(token: "").IsEnabled);
        Assert.False(BuildBinder(ownerUserId: 0).IsEnabled);
        Assert.True(BuildBinder().IsEnabled);
    }
}
