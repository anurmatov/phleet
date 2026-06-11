using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Tests;

/// <summary>
/// Unit tests for CodexExecutor image-forwarding logic (issue #164).
/// Tests exercise <c>CollectImagePaths</c> directly — the method is internal
/// and accessible via InternalsVisibleTo("Fleet.Agent.Tests").
///
/// The wire-format path (msgDict["input"] built from CollectImagePaths output
/// and passed to runStreamed) is validated structurally here but not end-to-end,
/// as that would require spawning the real bridge process.
/// </summary>
public class CodexExecutorImageTests
{
    private static CodexExecutor CreateExecutor(string attachmentDir = "/workspace/attachments")
    {
        var agentOptions = Options.Create(new AgentOptions
        {
            Name = "test",
            Role = "test",
            WorkDir = "/workspace",
        });
        var telegramOptions = Options.Create(new TelegramOptions
        {
            AttachmentDir = attachmentDir,
        });
        var promptBuilder = new PromptBuilder(agentOptions, NullLogger<PromptBuilder>.Instance);
        return new CodexExecutor(agentOptions, telegramOptions, promptBuilder, NullLogger<CodexExecutor>.Instance);
    }

    // ── Null / empty input ────────────────────────────────────────────────────

    [Fact]
    public void CollectImagePaths_NullImages_ReturnsEmpty()
    {
        var executor = CreateExecutor();
        var (paths, skipped) = executor.CollectImagePaths(null);
        Assert.Empty(paths);
        Assert.Equal(0, skipped);
    }

    [Fact]
    public void CollectImagePaths_EmptyList_ReturnsEmpty()
    {
        var executor = CreateExecutor();
        var (paths, skipped) = executor.CollectImagePaths([]);
        Assert.Empty(paths);
        Assert.Equal(0, skipped);
    }

    // ── No FilePath (PersistAttachments=false or size-gate exceeded) ──────────

    [Fact]
    public void CollectImagePaths_ImageWithNoFilePath_SkipsWithCount()
    {
        var executor = CreateExecutor();
        var images = new[] { new MessageImage([], "image/jpeg") { FilePath = null } };

        var (paths, skipped) = executor.CollectImagePaths(images);

        Assert.Empty(paths);
        Assert.Equal(1, skipped);
    }

    [Fact]
    public void CollectImagePaths_ImageWithEmptyFilePath_SkipsWithCount()
    {
        var executor = CreateExecutor();
        var images = new[] { new MessageImage([], "image/jpeg") { FilePath = "" } };

        var (paths, skipped) = executor.CollectImagePaths(images);

        Assert.Empty(paths);
        Assert.Equal(1, skipped);
    }

    // ── File not on disk (swept between download and dispatch) ────────────────

    [Fact]
    public void CollectImagePaths_FileNotFound_SkipsWithCount()
    {
        var executor = CreateExecutor("/workspace/attachments");
        // Path is inside AttachmentDir but the file does not exist on disk.
        var images = new[] { new MessageImage([], "image/jpeg") { FilePath = "/workspace/attachments/nonexistent-12345.jpg" } };

        var (paths, skipped) = executor.CollectImagePaths(images);

        Assert.Empty(paths);
        Assert.Equal(1, skipped);
    }

    // ── Path outside AttachmentDir ────────────────────────────────────────────

    [Fact]
    public void CollectImagePaths_PathOutsideAttachmentDir_SkipsWithCount()
    {
        var executor = CreateExecutor("/workspace/attachments");
        var images = new[] { new MessageImage([], "image/jpeg") { FilePath = "/etc/passwd" } };

        var (paths, skipped) = executor.CollectImagePaths(images);

        Assert.Empty(paths);
        Assert.Equal(1, skipped);
    }

    [Fact]
    public void CollectImagePaths_PathTraversalAttempt_SkipsWithCount()
    {
        // Path that begins with the dir prefix but escapes via traversal
        var executor = CreateExecutor("/workspace/attachments");
        var images = new[] { new MessageImage([], "image/jpeg") { FilePath = "/workspace/attachments/../secrets.txt" } };

        var (paths, skipped) = executor.CollectImagePaths(images);

        // Path.GetFullPath normalizes the traversal; /workspace/secrets.txt is outside attachments/.
        Assert.Empty(paths);
        Assert.Equal(1, skipped);
    }

    // ── Valid path (file exists inside AttachmentDir) ─────────────────────────

    [Fact]
    public void CollectImagePaths_ValidImage_ReturnsPath()
    {
        // Write a real temp file under a temp attachment dir so File.Exists passes.
        var attachDir = Path.Combine(Path.GetTempPath(), $"codex-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(attachDir);
        var imgPath = Path.Combine(attachDir, "test.jpg");
        File.WriteAllBytes(imgPath, [0xFF, 0xD8, 0xFF]); // minimal JPEG header

        try
        {
            var executor = CreateExecutor(attachDir);
            var images = new[] { new MessageImage([], "image/jpeg") { FilePath = imgPath } };

            var (paths, skipped) = executor.CollectImagePaths(images);

            Assert.Single(paths);
            Assert.Equal(imgPath, paths[0]);
            Assert.Equal(0, skipped);
        }
        finally
        {
            Directory.Delete(attachDir, recursive: true);
        }
    }

    // ── Mixed: some valid, some not ───────────────────────────────────────────

    [Fact]
    public void CollectImagePaths_MixedImages_ForwardsOnlyValid()
    {
        var attachDir = Path.Combine(Path.GetTempPath(), $"codex-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(attachDir);
        var validPath = Path.Combine(attachDir, "valid.jpg");
        File.WriteAllBytes(validPath, [0xFF, 0xD8, 0xFF]);

        try
        {
            var executor = CreateExecutor(attachDir);
            var images = new MessageImage[]
            {
                new([], "image/jpeg") { FilePath = null },                                          // no path → skip
                new([], "image/jpeg") { FilePath = validPath },                                     // valid → forward
                new([], "image/jpeg") { FilePath = Path.Combine(attachDir, "missing.jpg") },        // missing → skip
                new([], "image/jpeg") { FilePath = "/etc/passwd" },                                 // outside dir → skip
            };

            var (paths, skipped) = executor.CollectImagePaths(images);

            Assert.Single(paths);
            Assert.Equal(validPath, paths[0]);
            Assert.Equal(3, skipped);
        }
        finally
        {
            Directory.Delete(attachDir, recursive: true);
        }
    }

    // ── All dropped → forwarded list is empty ─────────────────────────────────

    [Fact]
    public void CollectImagePaths_AllDropped_ReturnsEmptyForwarded()
    {
        var executor = CreateExecutor("/workspace/attachments");
        var images = new MessageImage[]
        {
            new([], "image/jpeg") { FilePath = null },
            new([], "image/png")  { FilePath = "/etc/hosts" },
        };

        var (paths, skipped) = executor.CollectImagePaths(images);

        Assert.Empty(paths);
        Assert.Equal(2, skipped);
    }

    // ── Multiple valid images — ordering: images first, then text ─────────────

    [Fact]
    public void CollectImagePaths_MultipleValidImages_AllForwarded()
    {
        var attachDir = Path.Combine(Path.GetTempPath(), $"codex-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(attachDir);
        var path1 = Path.Combine(attachDir, "a.jpg");
        var path2 = Path.Combine(attachDir, "b.png");
        File.WriteAllBytes(path1, [0x01]);
        File.WriteAllBytes(path2, [0x02]);

        try
        {
            var executor = CreateExecutor(attachDir);
            var images = new MessageImage[]
            {
                new([], "image/jpeg") { FilePath = path1 },
                new([], "image/png")  { FilePath = path2 },
            };

            var (paths, skipped) = executor.CollectImagePaths(images);

            // Both forwarded, order preserved.
            Assert.Equal(2, paths.Count);
            Assert.Equal(path1, paths[0]);
            Assert.Equal(path2, paths[1]);
            Assert.Equal(0, skipped);
        }
        finally
        {
            Directory.Delete(attachDir, recursive: true);
        }
    }

    [Fact]
    public void BuildUserInputs_ImagesFirst_TextLast()
    {
        var payload = CodexExecutor.BuildUserInputs("analyze this", ["/tmp/a.png", "/tmp/b.png"]);

        Assert.Equal(3, payload.Count);
        Assert.Equal("localImage", payload[0]!["type"]!.GetValue<string>());
        Assert.Equal("/tmp/a.png", payload[0]!["path"]!.GetValue<string>());
        Assert.Equal("localImage", payload[1]!["type"]!.GetValue<string>());
        Assert.Equal("/tmp/b.png", payload[1]!["path"]!.GetValue<string>());
        Assert.Equal("text", payload[2]!["type"]!.GetValue<string>());
        Assert.Equal("analyze this", payload[2]!["text"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(null, "danger-full-access")]
    [InlineData("", "danger-full-access")]
    [InlineData("read-only", "read-only")]
    [InlineData("workspace-write", "workspace-write")]
    [InlineData("danger-full-access", "danger-full-access")]
    [InlineData("unexpected", "danger-full-access")]
    public void NormalizeSandboxMode_MapsExpectedValues(string? input, string expected)
    {
        Assert.Equal(expected, CodexExecutor.NormalizeSandboxMode(input));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("low", "low")]
    [InlineData("medium", "medium")]
    [InlineData("high", "high")]
    [InlineData("xhigh", "xhigh")]
    [InlineData("max", "xhigh")]   // fleet "max" collapses to codex ceiling "xhigh"
    [InlineData("unknown", null)]
    public void MapEffortToCodex_MapsExpectedValues(string? input, string? expected)
    {
        Assert.Equal(expected, CodexExecutor.MapEffortToCodex(input));
    }
}
