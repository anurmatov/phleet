using Fleet.Agent.Configuration;
using Fleet.Agent.Models;

namespace Fleet.Agent.Tests;

/// <summary>
/// Tests for image-related message model and config defaults used by MessageRouter.
/// </summary>
public class MessageRouterImageTests
{
    // ── IncomingMessage.HasImage ───────────────────────────────────────────────

    [Fact]
    public void HasImage_NoImages_ReturnsFalse()
    {
        var msg = new IncomingMessage
        {
            ChatId = 1, UserId = 1, Text = "hi", Sender = "@u", IsGroupChat = false,
        };
        Assert.False(msg.HasImage);
    }

    [Fact]
    public void HasImage_WithOneImage_ReturnsTrue()
    {
        var img = new MessageImage(new byte[] { 1 }, "image/jpeg");
        var msg = new IncomingMessage
        {
            ChatId = 1, UserId = 1, Text = "", Sender = "@u", IsGroupChat = false,
            Images = [img],
        };
        Assert.True(msg.HasImage);
        Assert.Single(msg.Images);
    }

    [Fact]
    public void HasImage_WithMultipleImages_ReturnsTrue()
    {
        var img = new MessageImage(new byte[] { 1 }, "image/jpeg");
        var msg = new IncomingMessage
        {
            ChatId = 1, UserId = 1, Text = "", Sender = "@u", IsGroupChat = false,
            Images = [img, img, img],
        };
        Assert.True(msg.HasImage);
        Assert.Equal(3, msg.Images.Count);
    }

    // ── TelegramOptions defaults ──────────────────────────────────────────────

    [Fact]
    public void DefaultImagePrompt_HasExpectedDefault()
    {
        var opts = new TelegramOptions();
        Assert.Equal("(image attached — please analyze)", opts.DefaultImagePrompt);
    }

    [Fact]
    public void MaxImagesPerGroup_DefaultIsTen()
    {
        var opts = new TelegramOptions();
        Assert.Equal(10, opts.MaxImagesPerGroup);
    }

    [Fact]
    public void MaxImageBytes_DefaultIsTenMb()
    {
        var opts = new TelegramOptions();
        Assert.Equal(10 * 1024 * 1024, opts.MaxImageBytes);
    }

    [Fact]
    public void MaxGroupBufferMs_DefaultIsTenSeconds()
    {
        var opts = new TelegramOptions();
        Assert.Equal(10_000, opts.MaxGroupBufferMs);
    }

    // ── Display text format helper logic (inline mirrors of MessageRouter) ────

    // Reproduce the display-text logic from MessageRouter so we can unit-test the
    // branch outcomes without wiring up the full DI graph.
    private static string BuildDisplayText(IncomingMessage msg)
    {
        var trimmed = msg.StrippedText;
        if (msg.Images.Count > 1)
        {
            return string.IsNullOrEmpty(trimmed)
                ? $"[{msg.Images.Count} images]"
                : $"[{msg.Images.Count} images + caption: {trimmed}]";
        }
        return trimmed;
    }

    [Fact]
    public void DisplayText_MultiImageNoCaption_IsNImages()
    {
        var img = new MessageImage(new byte[] { 1 }, "image/jpeg");
        var msg = new IncomingMessage
        {
            ChatId = 1, UserId = 1, Text = "", Sender = "@u", IsGroupChat = false,
            StrippedText = "",
            Images = [img, img, img],
        };
        Assert.Equal("[3 images]", BuildDisplayText(msg));
    }

    [Fact]
    public void DisplayText_MultiImageWithCaption_IncludesCaption()
    {
        var img = new MessageImage(new byte[] { 1 }, "image/jpeg");
        var msg = new IncomingMessage
        {
            ChatId = 1, UserId = 1, Text = "check these", Sender = "@u", IsGroupChat = false,
            StrippedText = "check these",
            Images = [img, img],
        };
        Assert.Equal("[2 images + caption: check these]", BuildDisplayText(msg));
    }

    [Fact]
    public void DisplayText_SingleImage_IsJustTrimmed()
    {
        var img = new MessageImage(new byte[] { 1 }, "image/jpeg");
        var msg = new IncomingMessage
        {
            ChatId = 1, UserId = 1, Text = "hello", Sender = "@u", IsGroupChat = false,
            StrippedText = "hello",
            Images = [img],
        };
        // Single image: display text is the caption as-is (no "[1 image]" annotation)
        Assert.Equal("hello", BuildDisplayText(msg));
    }

    [Fact]
    public void DisplayText_TextOnly_IsJustTrimmed()
    {
        var msg = new IncomingMessage
        {
            ChatId = 1, UserId = 1, Text = "hello world", Sender = "@u", IsGroupChat = false,
            StrippedText = "hello world",
        };
        Assert.Equal("hello world", BuildDisplayText(msg));
    }

    // ── Image-only prompt substitution logic ──────────────────────────────────

    // Reproduce the image-only substitution from MessageRouter so we can assert the
    // decision path independently of the full routing infrastructure.
    private static string ResolvePrompt(IncomingMessage msg, string defaultImagePrompt)
    {
        var trimmed = msg.StrippedText;
        if (string.IsNullOrEmpty(trimmed) && msg.HasImage)
            trimmed = defaultImagePrompt;
        return trimmed;
    }

    [Fact]
    public void ImageOnly_Substitutes_DefaultPrompt()
    {
        var img = new MessageImage(new byte[] { 1 }, "image/jpeg");
        var msg = new IncomingMessage
        {
            ChatId = 1, UserId = 1, Text = "", Sender = "@u", IsGroupChat = false,
            StrippedText = "",
            Images = [img],
        };
        Assert.Equal("(image attached — please analyze)", ResolvePrompt(msg, "(image attached — please analyze)"));
    }

    [Fact]
    public void ImageWithCaption_PreservesCaption()
    {
        var img = new MessageImage(new byte[] { 1 }, "image/jpeg");
        var msg = new IncomingMessage
        {
            ChatId = 1, UserId = 1, Text = "check this", Sender = "@u", IsGroupChat = false,
            StrippedText = "check this",
            Images = [img],
        };
        Assert.Equal("check this", ResolvePrompt(msg, "(image attached — please analyze)"));
    }

    [Fact]
    public void TextOnly_NoSubstitution()
    {
        var msg = new IncomingMessage
        {
            ChatId = 1, UserId = 1, Text = "hello", Sender = "@u", IsGroupChat = false,
            StrippedText = "hello",
        };
        // No images → prompt stays as-is, default is never used
        Assert.Equal("hello", ResolvePrompt(msg, "(image attached — please analyze)"));
    }
}
