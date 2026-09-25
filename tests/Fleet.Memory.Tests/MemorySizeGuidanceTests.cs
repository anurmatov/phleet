using System.Collections.Concurrent;
using Fleet.Memory.Models;
using Fleet.Memory.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Fleet.Memory.Tests;

/// <summary>
/// The #346 memory embedding-input guidance: the UTF-8 measure, the status matrix, the warning
/// texts, the MCP lines, config parsing and the full-scan summary. Synthetic ids and text only.
/// </summary>
public sealed class MemorySizeGuidanceTests
{
    private const string Id = "0123abcd-0000-0000-0000-000000000000";

    private static MemorySizeGuidance Guidance(int limit = 10, ILogger? logger = null) =>
        new(limit, logger ?? new MemoryLogCapture());

    // ── Measure ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("", 0)]
    [InlineData("abc", 3)]
    [InlineData("д", 2)]
    [InlineData("中", 3)]
    [InlineData("😀", 4)]
    [InlineData("aд中😀", 10)]
    public void Measures_utf8_bytes_not_utf16_units(string text, int bytes)
    {
        Assert.Equal(bytes, MemorySizeGuidance.Utf8Bytes(text));
        Assert.Equal(bytes, Guidance().Evaluate(Id, null, text).Bytes);
    }

    [Fact]
    public void Embedding_text_is_title_blank_line_content()
    {
        var doc = new MemoryDocument { Id = Id, Type = "learning", Title = "t", Content = "abc" };
        Assert.Equal("t\n\nabc", MemoryService.EmbeddingText(doc));
    }

    // ── Boundary (limit 10) and status matrix ───────────────────────────────

    [Fact]
    public void Boundary_counts_bytes()
    {
        Assert.Equal("crossed", Guidance().Evaluate(Id, null, new string('a', 11)).Status);
        Assert.Equal("crossed", Guidance().Evaluate(Id, null, "aaaaaaaaaд").Status);
        Assert.Equal("under", Guidance().Evaluate(Id, null, "aaaaaaaa" + "д").Status);
    }

    [Theory]
    [InlineData(null, 5, "under")]
    [InlineData(null, 11, "crossed")]
    [InlineData(10, 11, "crossed")]
    [InlineData(11, 12, "stillOver")]
    [InlineData(11, 10, "under")]
    public void Status_matrix(int? previousBytes, int bytes, string status)
    {
        var previous = previousBytes is { } p ? new string('a', p) : null;
        var report = Guidance().Evaluate(Id, previous, new string('a', bytes));

        Assert.Equal(status, report.Status);
        Assert.Equal(previousBytes, report.PreviousBytes);
        Assert.Equal(("memory", "utf8Bytes", "Embedding:InputGuidanceBytes", 10),
            (report.Kind, report.Unit, report.LimitKey, report.LimitBytes!.Value));
        Assert.Equal(status is "crossed" or "stillOver", report.Warning is not null);
    }

    [Fact]
    public void Zero_is_disabled()
    {
        var report = Guidance(0).Evaluate(Id, "ab", new string('a', 100_000));

        Assert.Equal("disabled", report.Status);
        Assert.Null(report.LimitBytes);
        Assert.Null(report.Warning);
        Assert.Equal(2, report.PreviousBytes);
        Assert.Equal("", MemorySizeGuidance.RenderMcpLines(report));
    }

    // ── Warning texts and MCP lines ─────────────────────────────────────────

    [Fact]
    public void Crossed_warning_and_mcp_lines()
    {
        var report = Guidance(25_000).Evaluate(Id, null, "t\n\n" + new string('a', 25_000));

        const string tail = "The whole memory is embedded as one input and re-embedded on every fleet-memory restart; " +
            "text past the embedding model's input window may be cut off or rejected, leaving the memory partly or wholly unsearchable. " +
            "Consider splitting it by topic and linking the parts by id.";
        Assert.Equal(
            "Memory 0123abcd embedding input (title + content) is 25,003 UTF-8 bytes, 3 over the 25,000-byte guidance (Embedding:InputGuidanceBytes). Saved anyway. " + tail,
            report.Warning);
        Assert.Equal(
            "\nSize: 25,003 UTF-8 bytes embedded; embedding-input guidance 25,000 (Embedding:InputGuidanceBytes).\nSize warning: " + report.Warning,
            MemorySizeGuidance.RenderMcpLines(report));
    }

    [Fact]
    public void Still_over_warning_and_mcp_lines()
    {
        var report = Guidance(1_000).Evaluate(Id, new string('a', 2_000), new string('a', 1_500));

        Assert.StartsWith(
            "Memory 0123abcd embedding input is still over the 1,000-byte guidance (Embedding:InputGuidanceBytes): 2,000 → 1,500 UTF-8 bytes. Saved anyway. The whole memory",
            report.Warning);
        Assert.StartsWith("\nSize: 2,000 → 1,500 UTF-8 bytes embedded; embedding-input guidance 1,000 (Embedding:InputGuidanceBytes).\nSize warning: ",
            MemorySizeGuidance.RenderMcpLines(report));
    }

    [Fact]
    public void Under_has_a_size_line_and_no_warning()
    {
        var report = Guidance(1_000).Evaluate(Id, null, "abc");
        Assert.Equal("\nSize: 3 UTF-8 bytes embedded; embedding-input guidance 1,000 (Embedding:InputGuidanceBytes).",
            MemorySizeGuidance.RenderMcpLines(report));
    }

    // ── Config ──────────────────────────────────────────────────────────────

    private static (MemorySizeGuidance Guidance, MemoryLogCapture Logs) FromConfig(string? value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>(MemorySizeGuidance.Key, value)])
            .Build();
        var logs = new MemoryLogCapture();
        return (MemorySizeGuidance.FromConfiguration(configuration, logs), logs);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Unset_empty_or_whitespace_is_off_with_no_log(string? value)
    {
        var (guidance, logs) = FromConfig(value);

        Assert.False(guidance.Enabled);
        Assert.Empty(logs.Entries);
        Assert.Equal("Embedding input guidance: disabled (Embedding:InputGuidanceBytes); provider=onnx model=all-MiniLM-L6-v2",
            guidance.StartupLine("onnx", "all-MiniLM-L6-v2"));
    }

    [Fact]
    public void Valid_value_is_taken_and_rendered_n0()
    {
        var (guidance, logs) = FromConfig("25000");

        Assert.Equal(25_000, guidance.LimitBytes);
        Assert.Empty(logs.Entries);
        guidance.LogStartup("ollama", "bge-m3");
        Assert.Equal("Embedding input guidance: 25,000 UTF-8 bytes (Embedding:InputGuidanceBytes); provider=ollama model=bge-m3",
            Assert.Single(logs.Entries).Message);
    }

    [Theory]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("99999999999")]
    public void Invalid_value_is_off_plus_exactly_one_warning_naming_the_key(string value)
    {
        var (guidance, logs) = FromConfig(value);

        Assert.False(guidance.Enabled);
        var warning = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("Embedding:InputGuidanceBytes", warning.Message);
        Assert.StartsWith("Embedding input guidance: disabled", guidance.StartupLine("onnx", "m"));
    }

    // ── Per-write log line ──────────────────────────────────────────────────

    [Fact]
    public void Logs_only_crossed_and_still_over_with_ids_never_content()
    {
        var logs = new MemoryLogCapture();
        var guidance = Guidance(1_000, logs);

        guidance.LogWrite(guidance.Evaluate(Id, null, "tiny"), "mcp", "store", Id);
        Assert.Empty(logs.Entries);

        guidance.LogWrite(guidance.Evaluate(Id, "ab", "SECRET-SENTINEL" + new string('a', 1_500)), "rest", "update", Id);

        var entry = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal($"MemorySize crossed: surface=rest op=update id={Id} bytes=1,515 previousBytes=2 limitBytes=1,000", entry.Message);
    }

    // ── Full-scan summary ───────────────────────────────────────────────────

    private static MemoryDocument Doc(int n, int contentBytes) => new()
    {
        Id = $"{n:D8}-0000-0000-0000-000000000000",
        Type = "learning",
        Title = "TITLE-SENTINEL",
        Content = new string('a', contentBytes),
    };

    [Fact]
    public void Summary_counts_add_up_and_is_information_when_clean()
    {
        var summary = Guidance(1_000).SummarizeFullScan(indexed: 2, indexFailures: 0, corrupt: 0, [Doc(1, 100), Doc(2, 200)]);

        Assert.Equal(summary.Scanned, summary.Indexed + summary.IndexFailures + summary.Corrupt);
        var line = Assert.Single(summary.Lines);
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Equal("Full index size check: scanned=2 indexed=2 indexFailures=0 corrupt=0 overGuidance=0 limitBytes=1,000", line.Message);
    }

    [Fact]
    public void Summary_is_a_warning_on_failures_even_when_disabled()
    {
        var summary = Guidance(0).SummarizeFullScan(indexed: 0, indexFailures: 2, corrupt: 1, [Doc(1, 50_000), Doc(2, 1)]);

        var line = Assert.Single(summary.Lines);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Equal("Full index size check: scanned=3 indexed=0 indexFailures=2 corrupt=1 overGuidance=0 limitBytes=disabled", line.Message);
    }

    [Fact]
    public void Summary_lists_largest_first_capped_at_twenty_with_no_titles()
    {
        var docs = Enumerable.Range(1, 23).Select(n => Doc(n, 1_000 + n)).Append(Doc(99, 10)).ToList();

        var summary = Guidance(1_000).SummarizeFullScan(indexed: 24, indexFailures: 0, corrupt: 0, docs);

        Assert.Equal(23, summary.OverGuidance);
        Assert.Equal(1 + 20 + 1, summary.Lines.Count);
        Assert.All(summary.Lines, l => Assert.Equal(LogLevel.Warning, l.Level));
        Assert.Equal("Full index size check: scanned=24 indexed=24 indexFailures=0 corrupt=0 overGuidance=23 limitBytes=1,000", summary.Lines[0].Message);
        // Largest first: doc 23 has 1,023 content bytes + "TITLE-SENTINEL\n\n" (16) = 1,039.
        Assert.Equal("Memory over embedding-input guidance: id=00000023-0000-0000-0000-000000000000 bytes=1,039 limitBytes=1,000", summary.Lines[1].Message);
        Assert.Equal("…and 3 more", summary.Lines[^1].Message);
        Assert.DoesNotContain(summary.Lines, l => l.Message.Contains("TITLE-SENTINEL"));
    }
}

/// <summary>Captures rendered log lines for the #346 memory size tests.</summary>
internal sealed class MemoryLogCapture : ILogger
{
    public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Enqueue((logLevel, formatter(state, exception)));
}
