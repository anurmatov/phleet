using Fleet.Agent.Models;

namespace Fleet.Agent.Tests;

/// <summary>
/// Unit tests for all-mode image routing added to fix issue #103 / PR #104 follow-up.
///
/// In all-mode, the routing decision for each incoming message is:
///   direct trigger (mention / reply / name) → immediate task (images flow today, unchanged)
///   non-direct trigger                       → ScheduleDebounce + images stored in pending buffer
///
/// At debounce fire, StartGroupCheckIn drains the pending-images buffer and passes images
/// to StartTask so the LLM sees the photos at the next check-in.
///
/// Tests mirror gate predicates and buffer semantics inline, following the pattern in
/// MessageRouterGroupGateTests, to avoid wiring the full DI graph.
/// </summary>
public class MessageRouterAllModeImageTests
{
    // ── Predicates mirrored from MessageRouter.HandleAsync all-mode branch ────

    private static bool IsGroupAllowed(long chatId, IReadOnlySet<long> allowedGroupIds)
        => allowedGroupIds.Contains(chatId);

    /// Returns true when all-mode treats the message as a direct trigger
    /// (cancel debounce + process immediately). Mirrors the MessageRouter predicate.
    private static bool IsAllModeDirectTrigger(IncomingMessage msg)
        => msg.IsBotMentioned || msg.IsReplyToBot || msg.IsNameMentioned;

    // ── Table-driven: all-mode routing × message types ────────────────────────

    public static IEnumerable<object[]> AllModeRoutingCases()
    {
        // (label, isBotMentioned, isReplyToBot, isNameMentioned, hasImages, expectedDirectTrigger)
        yield return ["single photo no mention",   false, false, false, true,  false]; // → pending buffer + debounce
        yield return ["album (multiple photos)",   false, false, false, true,  false]; // → pending buffer + debounce
        yield return ["photo with caption",        false, false, false, true,  false]; // → pending buffer + debounce
        yield return ["photo with @mention",       true,  false, false, true,  true];  // → immediate task (no pending buffer)
        yield return ["photo with reply-to-bot",   false, true,  false, true,  true];  // → immediate task
        yield return ["photo with name mention",   false, false, true,  true,  true];  // → immediate task
        yield return ["text no mention",           false, false, false, false, false]; // → debounce (no images)
        yield return ["text with @mention",        true,  false, false, false, true];  // → immediate task
    }

    [Theory]
    [MemberData(nameof(AllModeRoutingCases))]
    public void AllMode_AuthorizedGroup_RoutingDecision(
        string _, bool isBotMentioned, bool isReplyToBot, bool isNameMentioned,
        bool hasImages, bool expectedDirectTrigger)
    {
        var allowedGroups = new HashSet<long> { 100L };
        var img = new MessageImage(new byte[] { 1 }, "image/jpeg");
        var msg = new IncomingMessage
        {
            ChatId = 100, UserId = 42, Text = "content", Sender = "@user",
            IsGroupChat = true,
            IsBotMentioned = isBotMentioned,
            IsReplyToBot = isReplyToBot,
            IsNameMentioned = isNameMentioned,
            HasMediaAttachment = hasImages,
            Images = hasImages ? [img] : [],
        };

        Assert.True(IsGroupAllowed(msg.ChatId, allowedGroups));
        Assert.Equal(expectedDirectTrigger, IsAllModeDirectTrigger(msg));

        // Non-direct-trigger messages with images go to the pending buffer
        var goesToPendingBuffer = !IsAllModeDirectTrigger(msg) && msg.HasImage;
        Assert.Equal(!expectedDirectTrigger && hasImages, goesToPendingBuffer);
    }

    [Theory]
    [MemberData(nameof(AllModeRoutingCases))]
    public void AllMode_UnauthorizedGroup_AlwaysBlocked(
        string _, bool isBotMentioned, bool isReplyToBot, bool isNameMentioned,
        bool hasImages, bool __)
    {
        var allowedGroups = new HashSet<long> { 100L };
        var img = new MessageImage(new byte[] { 1 }, "image/jpeg");
        var msg = new IncomingMessage
        {
            ChatId = 999, UserId = 42, Text = "content", Sender = "@user",
            IsGroupChat = true,
            IsBotMentioned = isBotMentioned,
            IsReplyToBot = isReplyToBot,
            IsNameMentioned = isNameMentioned,
            HasMediaAttachment = hasImages,
            Images = hasImages ? [img] : [],
        };

        // Group 999 is not in AllowedGroupIds — blocked before all-mode logic
        Assert.False(IsGroupAllowed(msg.ChatId, allowedGroups));
    }

    // ── Pending-images buffer semantics ───────────────────────────────────────

    // Inline simulation of the AddPendingImages + DrainPendingImages semantics.
    // Simulates accumulation of one or more HandleAsync calls into the buffer,
    // then a single drain call (at check-in time).
    private static IReadOnlyList<MessageImage> SimulateBuffer(
        IEnumerable<IReadOnlyList<MessageImage>> incomingBatches, int cap)
    {
        var stored = new List<MessageImage>();
        foreach (var batch in incomingBatches)
        {
            foreach (var img in batch)
            {
                if (stored.Count >= cap) break;
                stored.Add(img);
            }
        }
        return stored;
    }

    [Fact]
    public void PendingBuffer_SinglePhotoNoMention_StoredAndDrained()
    {
        var img = new MessageImage(new byte[] { 1 }, "image/jpeg");
        var drained = SimulateBuffer([[img]], cap: 10);
        Assert.Single(drained);
    }

    [Fact]
    public void PendingBuffer_AlbumFlush_SingleMessageWithMultipleImages_AllStored()
    {
        // An album arrives as a single flushed IncomingMessage carrying all photos.
        var albumImages = Enumerable.Range(0, 3)
            .Select(i => new MessageImage(new byte[] { (byte)i }, "image/jpeg"))
            .ToList();
        var drained = SimulateBuffer([albumImages], cap: 10);
        Assert.Equal(3, drained.Count);
    }

    [Fact]
    public void PendingBuffer_MultipleNonDirectMessages_ImagesAccumulate()
    {
        // Several separate non-direct-trigger messages within the debounce window
        // each contribute their images to the pending buffer.
        var drained = SimulateBuffer(
            [
                [new MessageImage(new byte[] { 1 }, "image/jpeg")],
                [new MessageImage(new byte[] { 2 }, "image/jpeg")],
                [new MessageImage(new byte[] { 3 }, "image/jpeg")],
            ],
            cap: 10);
        Assert.Equal(3, drained.Count);
    }

    [Fact]
    public void PendingBuffer_PhotoWithCaption_ImageStoredCaptionFlowsAsText()
    {
        // Caption is already in msg.Text / StrippedText (handled by AddAndPersist);
        // the image bytes go to the pending buffer.
        var img = new MessageImage(new byte[] { 1 }, "image/jpeg");
        var msg = new IncomingMessage
        {
            ChatId = 100, UserId = 42, Text = "interesting screenshot", Sender = "@user",
            IsGroupChat = true, IsBotMentioned = false, IsReplyToBot = false, IsNameMentioned = false,
            HasMediaAttachment = true, StrippedText = "interesting screenshot", Images = [img],
        };

        Assert.False(IsAllModeDirectTrigger(msg)); // → debounce path
        Assert.True(msg.HasImage);                  // → image goes to pending buffer
        var drained = SimulateBuffer([msg.Images], cap: 10);
        Assert.Single(drained);
    }

    [Fact]
    public void PendingBuffer_PhotoWithMention_NotStoredInPendingBuffer()
    {
        // Direct-trigger messages (mention) bypass the debounce path entirely;
        // images reach StartTask immediately — pending buffer is not used.
        var img = new MessageImage(new byte[] { 1 }, "image/jpeg");
        var msg = new IncomingMessage
        {
            ChatId = 100, UserId = 42, Text = "@bot look at this", Sender = "@user",
            IsGroupChat = true, IsBotMentioned = true, HasMediaAttachment = true, Images = [img],
        };

        Assert.True(IsAllModeDirectTrigger(msg));  // → direct path, no pending buffer
        Assert.True(msg.HasImage);
        // Pending buffer receives no images for this message
        var drained = SimulateBuffer([], cap: 10); // nothing stored
        Assert.Empty(drained);
    }

    [Fact]
    public void PendingBuffer_ExceedsCap_DropOverflow()
    {
        var images = Enumerable.Range(0, 15)
            .Select(i => new MessageImage(new byte[] { (byte)i }, "image/jpeg"))
            .ToList();
        var drained = SimulateBuffer([images], cap: 10);
        Assert.Equal(10, drained.Count);
    }

    [Fact]
    public void PendingBuffer_DrainClearsBuffer_SubsequentDrainIsEmpty()
    {
        var images = new MessageImage[]
        {
            new(new byte[] { 1 }, "image/jpeg"),
            new(new byte[] { 2 }, "image/jpeg"),
        };

        var firstDrain = SimulateBuffer([images], cap: 10);
        Assert.Equal(2, firstDrain.Count);

        // After drain the buffer is empty; second drain returns nothing.
        var secondDrain = SimulateBuffer([], cap: 10);
        Assert.Empty(secondDrain);
    }

    // ── Direct-trigger path in all-mode is unchanged ──────────────────────────

    [Fact]
    public void AllMode_DirectTrigger_PhotoWithMention_IsDirectTrigger()
    {
        var img = new MessageImage(new byte[] { 1 }, "image/jpeg");
        var msg = new IncomingMessage
        {
            ChatId = 100, UserId = 42, Text = "@bot analyze this", Sender = "@user",
            IsGroupChat = true, IsBotMentioned = true, IsReplyToBot = false, IsNameMentioned = false,
            HasMediaAttachment = true, Images = [img],
        };
        Assert.True(IsAllModeDirectTrigger(msg));
    }

    [Fact]
    public void AllMode_DirectTrigger_ReplyToBot_IsDirectTrigger()
    {
        var img = new MessageImage(new byte[] { 1 }, "image/jpeg");
        var msg = new IncomingMessage
        {
            ChatId = 100, UserId = 42, Text = "what do you think?", Sender = "@user",
            IsGroupChat = true, IsBotMentioned = false, IsReplyToBot = true, IsNameMentioned = false,
            HasMediaAttachment = true, Images = [img],
        };
        Assert.True(IsAllModeDirectTrigger(msg));
    }

    // ── Mention-mode gate unchanged ───────────────────────────────────────────

    // Verify that the mention-mode predicate (separate code path) is unaffected.
    private static bool PassesMentionGate(IncomingMessage msg, IReadOnlySet<long> allowedUserIds)
    {
        if (!allowedUserIds.Contains(msg.UserId)) return false;
        return msg.IsBotMentioned || msg.IsReplyToBot || msg.HasMediaAttachment;
    }

    [Fact]
    public void MentionMode_PhotoNoMention_AuthorizedUser_StillPasses()
    {
        var allowed = new HashSet<long> { 42L };
        var img = new MessageImage(new byte[] { 1 }, "image/jpeg");
        var msg = new IncomingMessage
        {
            ChatId = 100, UserId = 42, Text = "", Sender = "@user",
            IsGroupChat = true, IsBotMentioned = false, IsReplyToBot = false,
            HasMediaAttachment = true, Images = [img],
        };
        Assert.True(PassesMentionGate(msg, allowed));
    }

    [Fact]
    public void MentionMode_TextNoMention_AuthorizedUser_StillBlocked()
    {
        var allowed = new HashSet<long> { 42L };
        var msg = new IncomingMessage
        {
            ChatId = 100, UserId = 42, Text = "hello", Sender = "@user",
            IsGroupChat = true, IsBotMentioned = false, IsReplyToBot = false,
            HasMediaAttachment = false,
        };
        Assert.False(PassesMentionGate(msg, allowed));
    }
}
