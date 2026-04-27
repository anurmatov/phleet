using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;

namespace Fleet.Agent.Tests;

/// <summary>
/// Tests for the Telegram attachment persistence feature (issue #110).
/// Covers config defaults, MessageImage.FilePath propagation, and hint-building logic.
/// </summary>
public class AttachmentPersistenceTests
{
    // ── TelegramOptions defaults ──────────────────────────────────────────────

    [Fact]
    public void PersistAttachments_DefaultIsTrue()
    {
        var opts = new TelegramOptions();
        Assert.True(opts.PersistAttachments);
    }

    [Fact]
    public void AttachmentDir_DefaultIsWorkspaceAttachments()
    {
        var opts = new TelegramOptions();
        Assert.Equal("/workspace/attachments", opts.AttachmentDir);
    }

    [Fact]
    public void AttachmentRetentionHours_DefaultIs48()
    {
        var opts = new TelegramOptions();
        Assert.Equal(48, opts.AttachmentRetentionHours);
    }

    // ── MessageImage.FilePath ─────────────────────────────────────────────────

    [Fact]
    public void MessageImage_FilePathDefaultsToNull()
    {
        var img = new MessageImage(new byte[] { 1 }, "image/jpeg");
        Assert.Null(img.FilePath);
    }

    [Fact]
    public void MessageImage_FilePathCanBeSet()
    {
        var img = new MessageImage(new byte[] { 1 }, "image/jpeg")
        {
            FilePath = "/workspace/attachments/123-456-1.jpg",
        };
        Assert.Equal("/workspace/attachments/123-456-1.jpg", img.FilePath);
    }

    [Fact]
    public void MessageImage_WithExpressionRetainsFilePath()
    {
        var original = new MessageImage(new byte[] { 1 }, "image/jpeg")
        {
            FilePath = "/workspace/attachments/1-2-1.jpg",
        };
        var copy = original with { };
        Assert.Equal(original.FilePath, copy.FilePath);
    }

    // ── Hint-building logic ───────────────────────────────────────────────────

    // Reproduce the BuildAttachmentHints logic from AgentTransport so we can test
    // it independently without wiring up the full Telegram infrastructure.
    private static string BuildAttachmentHints(IReadOnlyList<MessageImage> images)
    {
        var paths = images
            .Where(i => i.FilePath is not null)
            .Select(i => $"[image attachment: {i.FilePath}]");
        return string.Join("\n", paths);
    }

    [Fact]
    public void BuildHints_NoImages_ReturnsEmpty()
    {
        var hints = BuildAttachmentHints([]);
        Assert.Equal("", hints);
    }

    [Fact]
    public void BuildHints_ImageWithNoFilePath_ReturnsEmpty()
    {
        var img = new MessageImage(new byte[] { 1 }, "image/jpeg");
        var hints = BuildAttachmentHints([img]);
        Assert.Equal("", hints);
    }

    [Fact]
    public void BuildHints_SingleImageWithPath_ReturnsSingleHint()
    {
        var img = new MessageImage(new byte[] { 1 }, "image/jpeg")
        {
            FilePath = "/workspace/attachments/100-200-1.jpg",
        };
        var hints = BuildAttachmentHints([img]);
        Assert.Equal("[image attachment: /workspace/attachments/100-200-1.jpg]", hints);
    }

    [Fact]
    public void BuildHints_ThreeImagesWithPaths_ReturnsThreeHints()
    {
        var images = new MessageImage[]
        {
            new(new byte[] { 1 }, "image/jpeg") { FilePath = "/workspace/attachments/1-10-1.jpg" },
            new(new byte[] { 2 }, "image/jpeg") { FilePath = "/workspace/attachments/1-11-1.jpg" },
            new(new byte[] { 3 }, "image/jpeg") { FilePath = "/workspace/attachments/1-12-1.jpg" },
        };
        var hints = BuildAttachmentHints(images);
        var lines = hints.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Equal("[image attachment: /workspace/attachments/1-10-1.jpg]", lines[0]);
        Assert.Equal("[image attachment: /workspace/attachments/1-11-1.jpg]", lines[1]);
        Assert.Equal("[image attachment: /workspace/attachments/1-12-1.jpg]", lines[2]);
    }

    [Fact]
    public void BuildHints_MixedPathsAndNulls_OnlyIncludesPersisted()
    {
        // Images where some were skipped (size limit) — those have null FilePath
        var images = new MessageImage[]
        {
            new(new byte[] { 1 }, "image/jpeg") { FilePath = "/workspace/attachments/1-10-1.jpg" },
            new(new byte[] { 2 }, "image/jpeg"),  // no FilePath (persistence disabled or skipped)
            new(new byte[] { 3 }, "image/jpeg") { FilePath = "/workspace/attachments/1-12-1.jpg" },
        };
        var hints = BuildAttachmentHints(images);
        var lines = hints.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Contains("/workspace/attachments/1-10-1.jpg", hints);
        Assert.Contains("/workspace/attachments/1-12-1.jpg", hints);
    }

    // ── Hint injection into message text ─────────────────────────────────────

    // Reproduce the text-injection logic from AgentTransport.OnMessage so we can
    // verify hint injection without the full Telegram DI graph.
    private static (string Text, string StrippedText) ApplyHints(string text, string strippedText, string hints)
    {
        if (hints.Length == 0)
            return (text, strippedText);

        return (
            text.Length > 0 ? $"{text}\n{hints}" : hints,
            strippedText.Length > 0 ? $"{strippedText}\n{hints}" : hints
        );
    }

    [Fact]
    public void HintInjection_NoPersistence_TextUnchanged()
    {
        var (text, stripped) = ApplyHints("analyze this", "analyze this", "");
        Assert.Equal("analyze this", text);
        Assert.Equal("analyze this", stripped);
    }

    [Fact]
    public void HintInjection_WithCaption_AppendedAfterCaption()
    {
        var hints = "[image attachment: /workspace/attachments/1-2-1.jpg]";
        var (text, stripped) = ApplyHints("check my receipt", "check my receipt", hints);
        Assert.Equal("check my receipt\n[image attachment: /workspace/attachments/1-2-1.jpg]", text);
        Assert.Equal("check my receipt\n[image attachment: /workspace/attachments/1-2-1.jpg]", stripped);
    }

    [Fact]
    public void HintInjection_EmptyCaption_HintIsOnlyText()
    {
        var hints = "[image attachment: /workspace/attachments/1-2-1.jpg]";
        var (text, stripped) = ApplyHints("", "", hints);
        Assert.Equal("[image attachment: /workspace/attachments/1-2-1.jpg]", text);
        Assert.Equal("[image attachment: /workspace/attachments/1-2-1.jpg]", stripped);
    }

    [Fact]
    public void HintInjection_MultipleImages_AllHintsAppended()
    {
        var hints = "[image attachment: /workspace/attachments/1-10-1.jpg]\n[image attachment: /workspace/attachments/1-11-1.jpg]";
        var (text, _) = ApplyHints("here are the photos", "here are the photos", hints);
        Assert.Contains("[image attachment: /workspace/attachments/1-10-1.jpg]", text);
        Assert.Contains("[image attachment: /workspace/attachments/1-11-1.jpg]", text);
        Assert.StartsWith("here are the photos\n", text);
    }

    // ── AttachmentJanitorService: no-ops when PersistAttachments=false ────────

    [Fact]
    public async Task Janitor_PersistAttachmentsDisabled_DoesNotThrow()
    {
        var opts = Microsoft.Extensions.Options.Options.Create(new TelegramOptions
        {
            PersistAttachments = false,
        });
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<AttachmentJanitorService>.Instance;
        var janitor = new AttachmentJanitorService(opts, logger);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Should return immediately without touching any files
        await janitor.StartAsync(cts.Token);
    }
}
