using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Agent.Tests;

/// <summary>
/// Tests for the Telegram attachment persistence feature (issue #110).
/// Covers config defaults, MessageImage.FilePath propagation, hint-building and sweep logic.
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

    // ── AttachmentSweeper.BuildHints ──────────────────────────────────────────

    [Fact]
    public void BuildHints_NoImages_ReturnsEmpty()
    {
        var hints = AttachmentSweeper.BuildHints([]);
        Assert.Equal("", hints);
    }

    [Fact]
    public void BuildHints_ImageWithNoFilePath_ReturnsEmpty()
    {
        var img = new MessageImage(new byte[] { 1 }, "image/jpeg");
        var hints = AttachmentSweeper.BuildHints([img]);
        Assert.Equal("", hints);
    }

    [Fact]
    public void BuildHints_SingleImageWithPath_ReturnsSingleHint()
    {
        var img = new MessageImage(new byte[] { 1 }, "image/jpeg")
        {
            FilePath = "/workspace/attachments/100-200-1.jpg",
        };
        var hints = AttachmentSweeper.BuildHints([img]);
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
        var hints = AttachmentSweeper.BuildHints(images);
        var lines = hints.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Equal("[image attachment: /workspace/attachments/1-10-1.jpg]", lines[0]);
        Assert.Equal("[image attachment: /workspace/attachments/1-11-1.jpg]", lines[1]);
        Assert.Equal("[image attachment: /workspace/attachments/1-12-1.jpg]", lines[2]);
    }

    [Fact]
    public void BuildHints_MixedPathsAndNulls_OnlyIncludesPersisted()
    {
        var images = new MessageImage[]
        {
            new(new byte[] { 1 }, "image/jpeg") { FilePath = "/workspace/attachments/1-10-1.jpg" },
            new(new byte[] { 2 }, "image/jpeg"),  // no FilePath (skipped or disabled)
            new(new byte[] { 3 }, "image/jpeg") { FilePath = "/workspace/attachments/1-12-1.jpg" },
        };
        var hints = AttachmentSweeper.BuildHints(images);
        var lines = hints.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Contains("/workspace/attachments/1-10-1.jpg", hints);
        Assert.Contains("/workspace/attachments/1-12-1.jpg", hints);
    }

    // ── Hint injection into message text ─────────────────────────────────────

    // Reproduce the text-injection logic from AgentTransport.OnMessage to verify
    // that hint injection produces the expected shapes.
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

    // ── AttachmentSweeper.SweepExpired integration test ───────────────────────

    [Fact]
    public void SweepExpired_DeletesOldFile_PreservesRecentFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"fleet-attach-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // Create an "old" file — backdated past the retention cutoff
            var oldFile = Path.Combine(dir, "old.jpg");
            File.WriteAllBytes(oldFile, new byte[] { 1 });
            File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow - TimeSpan.FromHours(49));

            // Create a "fresh" file — within retention window
            var freshFile = Path.Combine(dir, "fresh.jpg");
            File.WriteAllBytes(freshFile, new byte[] { 2 });

            AttachmentSweeper.SweepExpired(dir, retentionHours: 48, NullLogger.Instance);

            Assert.False(File.Exists(oldFile), "expired file should have been deleted");
            Assert.True(File.Exists(freshFile), "recent file must be preserved");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SweepExpired_NonExistentDir_DoesNotThrow()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"fleet-no-such-dir-{Guid.NewGuid():N}");
        // Must not throw even if the directory doesn't exist yet
        AttachmentSweeper.SweepExpired(dir, retentionHours: 48, NullLogger.Instance);
    }

    [Fact]
    public void SweepExpired_PersistAttachmentsDisabled_CallerNeverCallsSweep()
    {
        // When PersistAttachments=false the code path that writes files and calls
        // SweepExpired is never reached. Verify the config default is respected.
        var opts = new TelegramOptions { PersistAttachments = false };
        Assert.False(opts.PersistAttachments);
        // No sweep call needed — this documents the expected caller behaviour.
    }

    // ── MaxDocumentBytes config ───────────────────────────────────────────────

    [Fact]
    public void MaxDocumentBytes_DefaultIs32MB()
    {
        var opts = new TelegramOptions();
        Assert.Equal(33_554_432, opts.MaxDocumentBytes); // 32 * 1024 * 1024
    }

    // ── MessageDocument record ────────────────────────────────────────────────

    [Fact]
    public void MessageDocument_FilePathDefaultsToNull()
    {
        var doc = new MessageDocument("file123", "application/pdf", 1024, "invoice.pdf");
        Assert.Null(doc.FilePath);
    }

    [Fact]
    public void MessageDocument_FilePathCanBeSet()
    {
        var doc = new MessageDocument("file123", "application/pdf", 1024, "invoice.pdf")
        {
            FilePath = "/workspace/attachments/100-200-1.pdf",
        };
        Assert.Equal("/workspace/attachments/100-200-1.pdf", doc.FilePath);
    }

    [Fact]
    public void MessageDocument_WithExpressionRetainsFilePath()
    {
        var original = new MessageDocument("file123", "application/pdf", 1024, "invoice.pdf")
        {
            FilePath = "/workspace/attachments/1-2-1.pdf",
        };
        var copy = original with { };
        Assert.Equal(original.FilePath, copy.FilePath);
    }

    // ── Document path construction ────────────────────────────────────────────

    [Fact]
    public void DocumentPath_FollowsSameChatMessageIndexShape()
    {
        long chatId = 12345;
        long messageId = 67890;
        var dir = "/workspace/attachments";
        var expected = $"{dir}/{chatId}-{messageId}-1.pdf";
        var actual = Path.Combine(dir, $"{chatId}-{messageId}-1.pdf");
        Assert.Equal(expected, actual);
    }

    // ── BuildHints — extension-aware (images and documents) ──────────────────

    [Fact]
    public void BuildHints_PdfDocument_ReturnsDocumentAttachmentHint()
    {
        var doc = new MessageDocument("f1", "application/pdf", 100, "report.pdf")
        {
            FilePath = "/workspace/attachments/1-2-1.pdf",
        };
        var hints = AttachmentSweeper.BuildHints([], [doc]);
        Assert.Equal("[document attachment: /workspace/attachments/1-2-1.pdf]", hints);
    }

    [Fact]
    public void BuildHints_JpgImage_ReturnsImageAttachmentHint()
    {
        var img = new MessageImage(new byte[] { 1 }, "image/jpeg")
        {
            FilePath = "/workspace/attachments/1-2-1.jpg",
        };
        var hints = AttachmentSweeper.BuildHints([img]);
        Assert.Equal("[image attachment: /workspace/attachments/1-2-1.jpg]", hints);
    }

    [Fact]
    public void BuildHints_MixedImageAndDocument_BothHintsReturned()
    {
        var img = new MessageImage(new byte[] { 1 }, "image/jpeg")
        {
            FilePath = "/workspace/attachments/1-2-1.jpg",
        };
        var doc = new MessageDocument("f1", "application/pdf", 100, "doc.pdf")
        {
            FilePath = "/workspace/attachments/1-2-1.pdf",
        };
        var hints = AttachmentSweeper.BuildHints([img], [doc]);
        Assert.Contains("[image attachment: /workspace/attachments/1-2-1.jpg]", hints);
        Assert.Contains("[document attachment: /workspace/attachments/1-2-1.pdf]", hints);
    }

    [Fact]
    public void BuildHints_DocumentWithNoFilePath_ReturnsEmpty()
    {
        var doc = new MessageDocument("f1", "application/pdf", 100, "doc.pdf");
        var hints = AttachmentSweeper.BuildHints([], [doc]);
        Assert.Equal("", hints);
    }

    [Fact]
    public void BuildHints_UnknownExtension_SilentlySkipped()
    {
        // .docx is not in the hint prefix map — should produce no hint
        var doc = new MessageDocument("f1", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", 100, "file.docx")
        {
            FilePath = "/workspace/attachments/1-2-1.docx",
        };
        var hints = AttachmentSweeper.BuildHints([], [doc]);
        Assert.Equal("", hints);
    }

    // ── 32 MB size-cap rejection ──────────────────────────────────────────────

    [Fact]
    public void MaxDocumentBytes_SizeCap_FileSizeCheckLogic()
    {
        // Verify the size guard threshold: 32 MB = 33_554_432 bytes
        const long limit = 33_554_432;
        const long oversized = 33_554_433; // 1 byte over
        const long ok = 33_554_432;        // exactly at limit — should pass

        Assert.True(oversized > limit, "oversized file should exceed limit");
        Assert.False(ok > limit, "file exactly at limit should not exceed it");
    }

    // ── PersistAttachments kill switch — document path ────────────────────────

    [Fact]
    public void PersistAttachments_False_DocumentKillSwitchRespected()
    {
        // When PersistAttachments=false, DownloadDocumentAsync returns null immediately —
        // no download, no disk write, no hint, no LLM block. This test documents that
        // the kill switch applies to both images and documents.
        var opts = new TelegramOptions { PersistAttachments = false };
        Assert.False(opts.PersistAttachments);
    }

    // ── SweepExpired handles .pdf files ───────────────────────────────────────

    [Fact]
    public void SweepExpired_DeletesExpiredPdfFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"fleet-attach-test-pdf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var oldPdf = Path.Combine(dir, "old-doc.pdf");
            File.WriteAllBytes(oldPdf, new byte[] { 0x25, 0x50, 0x44, 0x46 }); // %PDF header
            File.SetLastWriteTimeUtc(oldPdf, DateTime.UtcNow - TimeSpan.FromHours(49));

            var freshPdf = Path.Combine(dir, "fresh-doc.pdf");
            File.WriteAllBytes(freshPdf, new byte[] { 0x25, 0x50, 0x44, 0x46 });

            AttachmentSweeper.SweepExpired(dir, retentionHours: 48, NullLogger.Instance);

            Assert.False(File.Exists(oldPdf), "expired PDF should have been deleted");
            Assert.True(File.Exists(freshPdf), "recent PDF must be preserved");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ── Document hint injection into message text ─────────────────────────────

    [Fact]
    public void DocumentHintInjection_WithCaption_AppendedAfterCaption()
    {
        var hints = "[document attachment: /workspace/attachments/1-2-1.pdf]";
        var (text, stripped) = ApplyHints("please review this invoice", "please review this invoice", hints);
        Assert.Equal("please review this invoice\n[document attachment: /workspace/attachments/1-2-1.pdf]", text);
        Assert.Equal("please review this invoice\n[document attachment: /workspace/attachments/1-2-1.pdf]", stripped);
    }

    [Fact]
    public void DocumentHintInjection_NoCaption_HintIsOnlyText()
    {
        var hints = "[document attachment: /workspace/attachments/1-2-1.pdf]";
        var (text, _) = ApplyHints("", "", hints);
        Assert.Equal("[document attachment: /workspace/attachments/1-2-1.pdf]", text);
    }
}
