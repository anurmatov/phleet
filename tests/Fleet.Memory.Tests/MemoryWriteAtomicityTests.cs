using System.IO;
using Fleet.Memory.Data;
using Fleet.Memory.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Fleet.Memory.Configuration;

namespace Fleet.Memory.Tests;

/// <summary>
/// Regression tests for the write-then-deindex ghost-state bug (issue #100).
///
/// Tests work at the MemoryFileStore layer directly, which avoids needing a live
/// Qdrant instance. The key behaviors under test are:
///   A — tag escaping round-trips correctly through serialize → parse
///   B — ParseMarkdown throws InvalidDataException (not returns null) for bad YAML
///   C — SaveAsync throws InvalidDataException and writes no file when pre-write validation fails
///   D — UpdateAsync throws InvalidDataException and leaves the original file unchanged
///   E — ParseFileAsync propagates InvalidDataException for corrupt on-disk files
/// </summary>
public class MemoryWriteAtomicityTests : IDisposable
{
    private readonly string _tempDir;
    private readonly MemoryFileStore _store;

    public MemoryWriteAtomicityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"fleet-memory-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var storageOptions = Options.Create(new StorageOptions
        {
            Path = _tempDir,
            PollingIntervalSeconds = 30
        });

        _store = new MemoryFileStore(storageOptions, NullLogger<MemoryFileStore>.Instance);
        _store.EnsureDirectories();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    // ── Test A ──────────────────────────────────────────────────────────────────

    [Fact]
    public void SerializeToMarkdown_TagsWithSpecialChars_RoundTripsSuccessfully()
    {
        var doc = new MemoryDocument
        {
            Id = Guid.NewGuid().ToString(),
            Type = "learning",
            Title = "Test memory",
            Content = "Some content",
            Tags = ["valid-tag", "bad`tag[with]brackets", "tag-with-\"quotes\""]
        };

        var serialized = MemoryFileStore.SerializeToMarkdown(doc);

        // Must not throw — round-trip parse should succeed with the escaped tags
        var fakePath = Path.Combine(_tempDir, "learning", "test.md");
        var parsed = MemoryFileStore.ParseMarkdown(serialized, fakePath);

        Assert.NotNull(parsed);
        Assert.Equal(3, parsed.Tags.Count);
        Assert.Contains("valid-tag", parsed.Tags);
        Assert.Contains("bad`tag[with]brackets", parsed.Tags);
        Assert.Contains("tag-with-\"quotes\"", parsed.Tags);
    }

    // ── Test B ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseMarkdown_ThrowsInvalidDataException_OnMalformedYaml()
    {
        var malformed = """
            ---
            id: test-id-1234
            tags: [unclosed bracket
            ---
            content here
            """;

        var fakePath = Path.Combine(_tempDir, "learning", "test.md");

        var ex = Assert.Throws<InvalidDataException>(
            () => MemoryFileStore.ParseMarkdown(malformed, fakePath));

        // Exception message must include the YAML deserializer's diagnostic, not just a generic string
        Assert.Contains("YAML parse error", ex.Message);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public void ParseMarkdown_ReturnsNull_ForFileWithNoFrontmatter()
    {
        var noFrontmatter = "just some plain markdown without a frontmatter header";
        var fakePath = Path.Combine(_tempDir, "learning", "test.md");

        // Must return null (not a memory file), never throw
        var result = MemoryFileStore.ParseMarkdown(noFrontmatter, fakePath);

        Assert.Null(result);
    }

    // ── Test C ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveAsync_ThrowsInvalidDataException_BeforeWritingFile_WhenYamlIsInvalid()
    {
        // A source value starting with "[" produces `source: [unclosed` in the serialized
        // output — genuinely invalid YAML that the round-trip parse will reject.
        var doc = new MemoryDocument
        {
            Id = Guid.NewGuid().ToString(),
            Type = "learning",
            Title = "Test memory",
            Content = "Some content",
            Source = "[unclosed bracket in source"
        };

        // Verify this actually produces invalid YAML (guards the test itself)
        var serialized = MemoryFileStore.SerializeToMarkdown(doc);
        var fakePath = Path.Combine(_tempDir, "learning", "fake.md");
        Assert.Throws<InvalidDataException>(() => MemoryFileStore.ParseMarkdown(serialized, fakePath));

        // SaveAsync must throw before writing any file
        await Assert.ThrowsAsync<InvalidDataException>(() => _store.SaveAsync(doc));

        // No .md files should have been written
        var mdFiles = Directory.EnumerateFiles(Path.Combine(_tempDir, "learning"), "*.md").ToList();
        Assert.Empty(mdFiles);
    }

    [Fact]
    public async Task SaveAsync_LeavesNoTempFiles_OnValidationFailure()
    {
        var doc = new MemoryDocument
        {
            Id = Guid.NewGuid().ToString(),
            Type = "learning",
            Title = "Test memory",
            Content = "Some content",
            Source = "[unclosed"
        };

        try { await _store.SaveAsync(doc); } catch (InvalidDataException) { /* expected */ }

        // Temp files must also be cleaned up on validation failure
        var allFiles = Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories).ToList();
        Assert.DoesNotContain(allFiles, f => f.Contains(".tmp."));
    }

    // ── Test D ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAsync_LeavesFileUnchanged_OnValidationFailure()
    {
        // Write a valid memory file first
        var original = new MemoryDocument
        {
            Id = Guid.NewGuid().ToString(),
            Type = "learning",
            Title = "Original title",
            Content = "Original content"
        };
        var saved = await _store.SaveAsync(original);
        var originalBytes = await File.ReadAllBytesAsync(saved.FilePath);

        // Attempt an update that produces invalid YAML in the source field
        var ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => _store.UpdateAsync(saved.FilePath, d => { d.Source = "[unclosed"; }));

        Assert.Contains("YAML parse error", ex.Message);

        // Original file must be byte-for-byte unchanged
        var afterBytes = await File.ReadAllBytesAsync(saved.FilePath);
        Assert.Equal(originalBytes, afterBytes);
    }

    [Fact]
    public async Task UpdateAsync_LeavesNoTempFiles_OnValidationFailure()
    {
        var original = new MemoryDocument
        {
            Id = Guid.NewGuid().ToString(),
            Type = "learning",
            Title = "Original title",
            Content = "Original content"
        };
        var saved = await _store.SaveAsync(original);

        try
        {
            await _store.UpdateAsync(saved.FilePath, d => { d.Source = "[unclosed"; });
        }
        catch (InvalidDataException) { /* expected */ }

        var allFiles = Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories).ToList();
        Assert.DoesNotContain(allFiles, f => f.Contains(".tmp."));
    }

    // ── Test E ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ParseFileAsync_ThrowsInvalidDataException_ForCorruptOnDiskFile()
    {
        // Write a file with valid frontmatter delimiters but invalid YAML body
        var corruptPath = Path.Combine(_tempDir, "learning", $"corrupt-{Guid.NewGuid():N}.md");
        await File.WriteAllTextAsync(corruptPath, """
            ---
            id: corrupt-id-1234
            tags: [unclosed bracket
            ---
            content here
            """);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => _store.ParseFileAsync(corruptPath));

        Assert.Contains("YAML parse error", ex.Message);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public async Task ParseFileAsync_ReturnsNull_ForMissingFile()
    {
        var result = await _store.ParseFileAsync(Path.Combine(_tempDir, "does-not-exist.md"));
        Assert.Null(result);
    }

    // ── Tag escaping edge cases ─────────────────────────────────────────────────

    [Fact]
    public void SerializeToMarkdown_EmptyTags_ProducesNoTagsLine()
    {
        var doc = new MemoryDocument
        {
            Id = Guid.NewGuid().ToString(),
            Type = "learning",
            Title = "No tags",
            Content = "content",
            Tags = []
        };

        var serialized = MemoryFileStore.SerializeToMarkdown(doc);
        Assert.DoesNotContain("tags:", serialized);
    }

    [Fact]
    public void SerializeToMarkdown_SingleTag_RoundTripsCorrectly()
    {
        var doc = new MemoryDocument
        {
            Id = Guid.NewGuid().ToString(),
            Type = "learning",
            Title = "Single tag",
            Content = "content",
            Tags = ["my-tag"]
        };

        var serialized = MemoryFileStore.SerializeToMarkdown(doc);
        var fakePath = Path.Combine(_tempDir, "learning", "test.md");
        var parsed = MemoryFileStore.ParseMarkdown(serialized, fakePath);

        Assert.NotNull(parsed);
        Assert.Single(parsed.Tags);
        Assert.Equal("my-tag", parsed.Tags[0]);
    }

    // ── CleanStaleTempFiles ──────────────────────────────────────────────────────

    [Fact]
    public void CleanStaleTempFiles_DeletesTmpFiles_LeavesOtherFilesIntact()
    {
        var learningDir = Path.Combine(_tempDir, "learning");

        // Plant a stale temp file and a real md file
        var stale = Path.Combine(learningDir, "2026-01-01_abc123_test.md.tmp.deadbeef");
        var real = Path.Combine(learningDir, "2026-01-01_abc123_real.md");
        File.WriteAllText(stale, "stale");
        File.WriteAllText(real, "real");

        _store.CleanStaleTempFiles();

        Assert.False(File.Exists(stale), "Stale temp file should have been deleted");
        Assert.True(File.Exists(real), "Real .md file should be untouched");
    }
}
