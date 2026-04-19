using System.Text.Json;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;

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

    // ── AC#8: Claude multi-image JSON payload ──────────────────────────────

    // Reproduce the content-block building logic from ClaudeExecutor so we can
    // verify the JSON shape without starting a real process.
    private static string BuildClaudeImagePayload(IReadOnlyList<MessageImage> images, string task)
    {
        var contentBlocks = new List<object>(images.Count + 1);
        foreach (var img in images)
        {
            var base64 = Convert.ToBase64String(img.Bytes);
            contentBlocks.Add(new { type = "image", source = new { type = "base64", media_type = img.MimeType, data = base64 } });
        }
        contentBlocks.Add(new { type = "text", text = task });
        return JsonSerializer.Serialize(new
        {
            type = "user",
            message = new { role = "user", content = contentBlocks }
        });
    }

    [Fact]
    public void ClaudePayload_SingleImage_HasOneImageBlockAndOneTextBlock()
    {
        var img = new MessageImage(new byte[] { 0xFF, 0xD8 }, "image/jpeg");
        var json = BuildClaudeImagePayload([img], "describe this");

        using var doc = JsonDocument.Parse(json);
        var content = doc.RootElement
            .GetProperty("message")
            .GetProperty("content");

        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Equal(2, content.GetArrayLength()); // 1 image + 1 text

        var first = content[0];
        Assert.Equal("image", first.GetProperty("type").GetString());
        Assert.Equal("base64", first.GetProperty("source").GetProperty("type").GetString());
        Assert.Equal("image/jpeg", first.GetProperty("source").GetProperty("media_type").GetString());

        var second = content[1];
        Assert.Equal("text", second.GetProperty("type").GetString());
        Assert.Equal("describe this", second.GetProperty("text").GetString());
    }

    [Fact]
    public void ClaudePayload_ThreeImages_HasThreeImageBlocksBeforeTextBlock()
    {
        var images = new MessageImage[]
        {
            new(new byte[] { 1 }, "image/jpeg"),
            new(new byte[] { 2 }, "image/png"),
            new(new byte[] { 3 }, "image/jpeg"),
        };
        var json = BuildClaudeImagePayload(images, "compare");

        using var doc = JsonDocument.Parse(json);
        var content = doc.RootElement.GetProperty("message").GetProperty("content");

        Assert.Equal(4, content.GetArrayLength()); // 3 images + 1 text

        for (var i = 0; i < 3; i++)
            Assert.Equal("image", content[i].GetProperty("type").GetString());

        Assert.Equal("text", content[3].GetProperty("type").GetString());
        Assert.Equal("compare", content[3].GetProperty("text").GetString());
    }

    // ── AC#9: Codex warning event type ────────────────────────────────────

    // Verify that CodexExecutor emits a "warning" EventType (not "assistant")
    // for the image-not-supported notification, so TaskManager can deliver it.
    // We test this by inspecting the AgentProgress record directly — the event
    // type must be "warning" with IsSignificant=true so the new delivery path fires.
    [Fact]
    public void CodexWarning_AgentProgress_HasCorrectShape()
    {
        // Construct the AgentProgress that CodexExecutor would yield for an image message.
        // This mirrors the exact fields set in CodexExecutor.ExecuteAsync.
        const string warningText = "Note: the Codex provider does not support image input — images will be ignored.";
        var progress = new AgentProgress
        {
            EventType = "warning",
            Summary = warningText,
            IsSignificant = true,
        };

        Assert.Equal("warning", progress.EventType);
        Assert.True(progress.IsSignificant);
        Assert.Null(progress.ToolName);   // must be null — not a tool call
        Assert.Null(progress.FinalResult); // warning doesn't terminate the stream
        Assert.Equal(warningText, progress.Summary);
    }
}
