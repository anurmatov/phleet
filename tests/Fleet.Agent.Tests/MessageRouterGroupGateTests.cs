using Fleet.Agent.Models;

namespace Fleet.Agent.Tests;

/// <summary>
/// Unit tests for the group mention-gate decision introduced by issue #103.
///
/// The gate logic lives in MessageRouter.HandleAsync (mention mode):
///   - Authorized user required (AllowedUserIds check).
///   - Mention or reply-to-bot OR HasMediaAttachment bypasses the mention requirement.
///
/// Tests mirror the gate predicate inline to avoid wiring the full DI graph,
/// matching the approach used in MessageRouterImageTests.
/// </summary>
public class MessageRouterGroupGateTests
{
    // ── Gate predicate mirrored from MessageRouter.HandleAsync ────────────────

    private static bool PassesMentionGate(IncomingMessage msg, IReadOnlySet<long> allowedUserIds)
    {
        if (!allowedUserIds.Contains(msg.UserId)) return false;
        return msg.IsBotMentioned || msg.IsReplyToBot || msg.HasMediaAttachment;
    }

    // ── HasMediaAttachment on IncomingMessage ─────────────────────────────────

    [Fact]
    public void HasMediaAttachment_DefaultFalse()
    {
        var msg = new IncomingMessage
        {
            ChatId = 1, UserId = 1, Text = "hi", Sender = "@u", IsGroupChat = true,
        };
        Assert.False(msg.HasMediaAttachment);
    }

    [Fact]
    public void HasMediaAttachment_SetTrue_ReturnsTrue()
    {
        var msg = new IncomingMessage
        {
            ChatId = 1, UserId = 1, Text = "(photo)", Sender = "@u", IsGroupChat = true,
            HasMediaAttachment = true,
        };
        Assert.True(msg.HasMediaAttachment);
    }

    // ── Table-driven gate: message types × group context ─────────────────────

    public static IEnumerable<object[]> GateCases()
    {
        // (label, isBotMentioned, isReplyToBot, hasMediaAttachment, expectPass)
        yield return ["text no mention",           false, false, false, false];
        yield return ["text with @mention",        true,  false, false, true];
        yield return ["text reply-to-bot",         false, true,  false, true];
        yield return ["photo no mention",          false, false, true,  true]; // NEW: photo bypasses gate
        yield return ["voice no mention",          false, false, true,  true]; // NEW: voice bypasses gate
        yield return ["audio no mention",          false, false, true,  true]; // NEW
        yield return ["video no mention",          false, false, true,  true]; // NEW
        yield return ["video_note no mention",     false, false, true,  true]; // NEW
        yield return ["document no mention",       false, false, true,  true]; // NEW
        yield return ["media + mention",           true,  false, true,  true]; // mention + media both true
        yield return ["media + reply",             false, true,  true,  true]; // reply + media both true
    }

    [Theory]
    [MemberData(nameof(GateCases))]
    public void MentionGate_AuthorizedUser(string _, bool isBotMentioned, bool isReplyToBot, bool hasMediaAttachment, bool expectPass)
    {
        var allowed = new HashSet<long> { 42L };
        var msg = new IncomingMessage
        {
            ChatId = 100, UserId = 42, Text = "content", Sender = "@user",
            IsGroupChat = true,
            IsBotMentioned = isBotMentioned,
            IsReplyToBot = isReplyToBot,
            HasMediaAttachment = hasMediaAttachment,
        };
        Assert.Equal(expectPass, PassesMentionGate(msg, allowed));
    }

    [Theory]
    [MemberData(nameof(GateCases))]
    public void MentionGate_UnauthorizedUser_AlwaysBlocked(string _, bool isBotMentioned, bool isReplyToBot, bool hasMediaAttachment, bool __)
    {
        var allowed = new HashSet<long> { 42L };
        var msg = new IncomingMessage
        {
            ChatId = 100, UserId = 99, Text = "content", Sender = "@stranger",
            IsGroupChat = true,
            IsBotMentioned = isBotMentioned,
            IsReplyToBot = isReplyToBot,
            HasMediaAttachment = hasMediaAttachment,
        };
        // User 99 is not in AllowedUserIds — always blocked regardless of media/mention
        Assert.False(PassesMentionGate(msg, allowed));
    }

    // ── DM: no mention gate (separate path) ───────────────────────────────────

    // Reproduce the DM authorization check (AllowedUserIds only, no mention required)
    private static bool PassesDmGate(IncomingMessage msg, IReadOnlySet<long> allowedUserIds)
        => allowedUserIds.Contains(msg.UserId);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DmGate_AuthorizedUser_AlwaysPass(bool hasMediaAttachment)
    {
        var allowed = new HashSet<long> { 42L };
        var msg = new IncomingMessage
        {
            ChatId = 1, UserId = 42, Text = "hi", Sender = "@u", IsGroupChat = false,
            HasMediaAttachment = hasMediaAttachment,
        };
        Assert.True(PassesDmGate(msg, allowed));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DmGate_UnauthorizedUser_AlwaysBlocked(bool hasMediaAttachment)
    {
        var allowed = new HashSet<long> { 42L };
        var msg = new IncomingMessage
        {
            ChatId = 1, UserId = 99, Text = "hi", Sender = "@stranger", IsGroupChat = false,
            HasMediaAttachment = hasMediaAttachment,
        };
        Assert.False(PassesDmGate(msg, allowed));
    }

    // ── Excluded types: sticker/GIF should NOT set HasMediaAttachment ─────────

    // These are not set in AgentTransport; we verify the field stays false
    // (tests document the contract that excluded types never bypass the gate).

    [Fact]
    public void ExcludedType_Sticker_HasMediaAttachmentFalse()
    {
        // AgentTransport never sets HasMediaAttachment for stickers/animations/etc.
        // A message without the flag is correctly gated.
        var msg = new IncomingMessage
        {
            ChatId = 1, UserId = 42, Text = "", Sender = "@u", IsGroupChat = true,
            HasMediaAttachment = false, // sticker: not set
        };
        var allowed = new HashSet<long> { 42L };
        Assert.False(PassesMentionGate(msg, allowed));
    }
}
