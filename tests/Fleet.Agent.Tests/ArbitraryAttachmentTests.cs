using Fleet.Agent.Interfaces;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Agent.Tests;

/// <summary>
/// Tests for arbitrary file attachment support (issue #179).
/// Covers BuildHints extension dispatch, path-traversal sanitisation,
/// MimeType inference, ClaudeExecutor PDF-only filter, and SweepExpired extension agnosticism.
/// </summary>
public class ArbitraryAttachmentTests
{
    // ── BuildHints extension dispatch ─────────────────────────────────────────

    [Theory]
    [InlineData("/att/1-2-1.jpg",  "[image attachment: /att/1-2-1.jpg]")]
    [InlineData("/att/1-2-1.jpeg", "[image attachment: /att/1-2-1.jpeg]")]
    [InlineData("/att/1-2-1.png",  "[image attachment: /att/1-2-1.png]")]
    [InlineData("/att/1-2-1.pdf",  "[document attachment: /att/1-2-1.pdf]")]
    [InlineData("/att/1-2-1.json", "[file attachment: /att/1-2-1.json]")]
    [InlineData("/att/1-2-1.sh",   "[file attachment: /att/1-2-1.sh]")]
    [InlineData("/att/1-2-1.zip",  "[file attachment: /att/1-2-1.zip]")]
    [InlineData("/att/1-2-1.txt",  "[file attachment: /att/1-2-1.txt]")]
    [InlineData("/att/1-2-1.pub",  "[file attachment: /att/1-2-1.pub]")]
    public void BuildHints_ExtensionDispatch(string filePath, string expected)
    {
        var doc = new MessageDocument("fid", "application/octet-stream", 100, "file")
        {
            FilePath = filePath,
        };
        var hints = AttachmentSweeper.BuildHints([], [doc]);
        Assert.Equal(expected, hints);
    }

    [Fact]
    public void BuildHints_BinFallback_EmitsFileAttachmentHint()
    {
        // A file persisted as .bin (no original extension) → [file attachment: ...]
        var doc = new MessageDocument("fid", "application/octet-stream", 100, "somefile")
        {
            FilePath = "/workspace/attachments/1-2-1.bin",
        };
        var hints = AttachmentSweeper.BuildHints([], [doc]);
        Assert.Equal("[file attachment: /workspace/attachments/1-2-1.bin]", hints);
    }

    [Fact]
    public void BuildHints_Idempotent_CallingTwiceDoesNotDoubleEmit()
    {
        var docs = new MessageDocument[]
        {
            new("f1", "application/pdf", 100, "a.pdf") { FilePath = "/att/1-1-1.pdf" },
            new("f2", "application/octet-stream", 100, "b.json") { FilePath = "/att/1-2-1.json" },
        };
        var hints1 = AttachmentSweeper.BuildHints([], docs);
        var hints2 = AttachmentSweeper.BuildHints([], docs);
        Assert.Equal(hints1, hints2);
        Assert.Equal(2, hints1.Split('\n').Length);
    }

    // ── SweepExpired extension agnostic ───────────────────────────────────────

    [Fact]
    public void SweepExpired_DeletesExpiredJsonFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"fleet-attach-json-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var oldJson = Path.Combine(dir, "old.json");
            File.WriteAllText(oldJson, "{}");
            File.SetLastWriteTimeUtc(oldJson, DateTime.UtcNow - TimeSpan.FromHours(49));

            var freshJson = Path.Combine(dir, "fresh.json");
            File.WriteAllText(freshJson, "{}");

            AttachmentSweeper.SweepExpired(dir, retentionHours: 48, NullLogger.Instance);

            Assert.False(File.Exists(oldJson), "expired .json file should have been deleted");
            Assert.True(File.Exists(freshJson), "recent .json file must be preserved");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ── ExtractSafeExtension path-traversal sanitisation ─────────────────────

    [Fact]
    public void ExtractSafeExtension_NormalFilename_ExtractsExtension()
    {
        Assert.Equal(".json", AgentTransport.ExtractSafeExtension("config.json"));
    }

    [Fact]
    public void ExtractSafeExtension_UnixTraversal_OnlyExtensionReturned()
    {
        // ../../etc/passwd.txt → Path.GetFileName → "passwd.txt" → ext = ".txt"
        var ext = AgentTransport.ExtractSafeExtension("../../etc/passwd.txt");
        Assert.Equal(".txt", ext);
    }

    [Fact]
    public void ExtractSafeExtension_WindowsTraversal_FallsBackToBin()
    {
        // ..\\..\\etc\\passwd (no extension after sanitise) → ".bin"
        var ext = AgentTransport.ExtractSafeExtension("..\\..\\etc\\passwd");
        Assert.Equal(".bin", ext);
    }

    [Fact]
    public void ExtractSafeExtension_NullFileName_FallsBackToBin()
    {
        Assert.Equal(".bin", AgentTransport.ExtractSafeExtension(null));
    }

    [Fact]
    public void ExtractSafeExtension_ControlCharInName_PreservesExtension()
    {
        // foo\x01.json — control char stripped, extension preserved
        var ext = AgentTransport.ExtractSafeExtension("foo\x01.json");
        Assert.Equal(".json", ext);
    }

    // ── InferMimeType ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(".pdf",  "application/pdf")]
    [InlineData(".jpg",  "image/jpeg")]
    [InlineData(".jpeg", "image/jpeg")]
    [InlineData(".png",  "image/png")]
    [InlineData(".json", "application/octet-stream")]
    [InlineData(".pub",  "application/octet-stream")]
    [InlineData(".bin",  "application/octet-stream")]
    [InlineData(".zip",  "application/octet-stream")]
    public void InferMimeType_ReturnsExpectedMime(string ext, string expected)
    {
        Assert.Equal(expected, AgentTransport.InferMimeType(ext));
    }

    [Fact]
    public void InferMimeType_UnknownExtension_NeverReturnsPdf()
    {
        // The critical invariant: unknown/null-derived ext must not default to PDF
        var mime = AgentTransport.InferMimeType(".json");
        Assert.NotEqual("application/pdf", mime);
    }

    // ── ClaudeExecutor PDF-only filter ────────────────────────────────────────

    [Fact]
    public void ShouldEmitDocumentBlock_PdfWithPath_ReturnsTrue()
    {
        var doc = new MessageDocument("fid", "application/pdf", 100, "doc.pdf")
        {
            FilePath = "/workspace/attachments/1-2-1.pdf",
        };
        Assert.True(ClaudeExecutor.ShouldEmitDocumentBlock(doc));
    }

    [Fact]
    public void ShouldEmitDocumentBlock_OctetStreamJson_ReturnsFalse()
    {
        // Non-PDF MIME → no document block (would cause Anthropic 400)
        var doc = new MessageDocument("fid", "application/octet-stream", 100, "config.json")
        {
            FilePath = "/workspace/attachments/1-2-1.json",
        };
        Assert.False(ClaudeExecutor.ShouldEmitDocumentBlock(doc));
    }

    [Fact]
    public void ShouldEmitDocumentBlock_NullMime_ReturnsFalse()
    {
        // Null MimeType (AgentTransport now resolves to octet-stream via InferMimeType,
        // but test the filter independently to guard the invariant)
        var doc = new MessageDocument("fid", null!, 100, "config.json")
        {
            FilePath = "/workspace/attachments/1-2-1.json",
        };
        Assert.False(ClaudeExecutor.ShouldEmitDocumentBlock(doc));
    }

    [Fact]
    public void ShouldEmitDocumentBlock_PdfWithNullPath_ReturnsFalse()
    {
        // PDF MIME but no file on disk → nothing to send
        var doc = new MessageDocument("fid", "application/pdf", 100, "doc.pdf");
        Assert.False(ClaudeExecutor.ShouldEmitDocumentBlock(doc));
    }
}
