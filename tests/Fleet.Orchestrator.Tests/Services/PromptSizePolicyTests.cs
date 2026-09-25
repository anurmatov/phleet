using System.Collections.Concurrent;
using Fleet.Orchestrator.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Fleet.Orchestrator.Tests.Services;

/// <summary>
/// The #346 instruction / project-context size evaluator: the UTF-8 measure, the status matrix,
/// the warning texts, the MCP lines, config parsing and the log lines. Synthetic text only.
/// </summary>
public sealed class PromptSizePolicyTests
{
    private static PromptSizePolicy Policy(int instruction = 10, int context = 10, ILogger? logger = null) =>
        new(instruction, context, logger ?? new SizeLogCapture());

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
        Assert.Equal(bytes, PromptSizePolicy.Utf8Bytes(text));
        Assert.Equal(bytes, Policy().Evaluate(PromptSizeKind.Instruction, "row", null, text).Bytes);
    }

    // ── Boundary (limit 10) ─────────────────────────────────────────────────

    [Fact]
    public void Eleven_ascii_bytes_cross_a_ten_byte_limit()
    {
        var report = Policy().Evaluate(PromptSizeKind.Instruction, "row", null, new string('a', 11));
        Assert.Equal("crossed", report.Status);
    }

    [Fact]
    public void Ten_utf16_units_that_are_eleven_bytes_cross()
    {
        var text = "aaaaaaaaaд";
        Assert.Equal(10, text.Length);

        var report = Policy().Evaluate(PromptSizeKind.Instruction, "row", null, text);

        Assert.Equal(11, report.Bytes);
        Assert.Equal("crossed", report.Status);
    }

    [Fact]
    public void Exactly_the_limit_is_under()
    {
        var report = Policy().Evaluate(PromptSizeKind.Instruction, "row", null, "aaaaaaaa" + "д");
        Assert.Equal(10, report.Bytes);
        Assert.Equal("under", report.Status);
        Assert.Null(report.Warning);
    }

    // ── Status matrix ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, 5, "under")]
    [InlineData(null, 11, "crossed")]
    [InlineData(5, 10, "under")]
    [InlineData(10, 11, "crossed")]
    [InlineData(11, 12, "stillOver")]
    [InlineData(11, 11, "stillOver")]
    [InlineData(11, 10, "under")]
    [InlineData(12, 3, "under")]
    public void Status_matrix(int? previousBytes, int bytes, string status)
    {
        var previous = previousBytes is { } p ? new string('a', p) : null;
        var report = Policy().Evaluate(PromptSizeKind.ProjectContext, "row", previous, new string('a', bytes));

        Assert.Equal(status, report.Status);
        Assert.Equal(previousBytes, report.PreviousBytes);
        Assert.Equal(bytes, report.Bytes);
        Assert.Equal(10, report.LimitBytes);
        Assert.Equal(status is "crossed" or "stillOver", report.Warning is not null);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(5)]
    [InlineData(50)]
    public void Limit_zero_is_disabled_whatever_the_previous(int? previousBytes)
    {
        var previous = previousBytes is { } p ? new string('a', p) : null;
        var report = Policy(instruction: 0).Evaluate(PromptSizeKind.Instruction, "row", previous, new string('a', 500));

        Assert.Equal("disabled", report.Status);
        Assert.Null(report.LimitBytes);
        Assert.Null(report.Warning);
        Assert.Equal("PromptSizeWarnings:InstructionBytes", report.LimitKey);
    }

    [Fact]
    public void Report_constants_and_keys_per_kind()
    {
        var instruction = Policy().Evaluate(PromptSizeKind.Instruction, "row", null, "a");
        var context = Policy().Evaluate(PromptSizeKind.ProjectContext, "row", null, "a");

        Assert.Equal(("instruction", "utf8Bytes", "PromptSizeWarnings:InstructionBytes"),
            (instruction.Kind, instruction.Unit, instruction.LimitKey));
        Assert.Equal(("projectContext", "utf8Bytes", "PromptSizeWarnings:ProjectContextBytes"),
            (context.Kind, context.Unit, context.LimitKey));
    }

    // ── Warning texts ───────────────────────────────────────────────────────

    [Fact]
    public void Crossed_warning_text()
    {
        var report = Policy(instruction: 10_000).Evaluate(PromptSizeKind.Instruction, "row-a", null, new string('a', 10_234));

        Assert.Equal(
            "Instruction 'row-a' is 10,234 UTF-8 bytes, 234 over the 10,000-byte soft limit (PromptSizeWarnings:InstructionBytes). " +
            "Saved anyway. Every assigned agent loads this text on every turn; move reference detail into memories and link them by id.",
            report.Warning);
    }

    [Fact]
    public void Still_over_warning_text()
    {
        var report = Policy(context: 10_000).Evaluate(PromptSizeKind.ProjectContext, "row-b",
            new string('a', 12_000), new string('a', 11_500));

        Assert.Equal(
            "Project context 'row-b' is still over the 10,000-byte soft limit (PromptSizeWarnings:ProjectContextBytes): " +
            "12,000 → 11,500 UTF-8 bytes. Saved anyway. Every assigned agent loads this text on every turn; move reference detail into memories and link them by id.",
            report.Warning);
    }

    [Fact]
    public void Warnings_never_contain_content()
    {
        var content = "SECRET-SENTINEL " + new string('x', 20);
        var report = Policy().Evaluate(PromptSizeKind.Instruction, "row", null, content);
        Assert.DoesNotContain("SECRET-SENTINEL", report.Warning);
    }

    // ── MCP lines ───────────────────────────────────────────────────────────

    [Fact]
    public void Mcp_lines_on_create_under()
    {
        var report = Policy(instruction: 10_000).Evaluate(PromptSizeKind.Instruction, "row", null, "abc");
        Assert.Equal("\nSize: 3 UTF-8 bytes; instruction soft limit 10,000 (PromptSizeWarnings:InstructionBytes).",
            PromptSizePolicy.RenderMcpLines(report));
    }

    [Fact]
    public void Mcp_lines_with_previous_and_warning()
    {
        var report = Policy(context: 10).Evaluate(PromptSizeKind.ProjectContext, "row", "abc", new string('a', 1_200));
        var lines = PromptSizePolicy.RenderMcpLines(report);

        Assert.StartsWith("\nSize: 3 → 1,200 UTF-8 bytes; project-context soft limit 10 (PromptSizeWarnings:ProjectContextBytes).\nSize warning: Project context 'row' is 1,200", lines);
        Assert.Equal(2, lines.Split('\n').Length - 1);
    }

    [Fact]
    public void Mcp_lines_when_disabled()
    {
        var report = Policy(instruction: 0).Evaluate(PromptSizeKind.Instruction, "row", "ab", new string('a', 20_000));
        Assert.Equal("\nSize: 2 → 20,000 UTF-8 bytes; instruction soft limit disabled (PromptSizeWarnings:InstructionBytes=0).",
            PromptSizePolicy.RenderMcpLines(report));
    }

    // ── Config ──────────────────────────────────────────────────────────────

    private static (PromptSizePolicy Policy, SizeLogCapture Logs) FromConfig(params (string Key, string? Value)[] values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();
        var logs = new SizeLogCapture();
        return (PromptSizePolicy.FromConfiguration(configuration, logs), logs);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Unset_empty_or_whitespace_is_the_default_with_no_log(string? value)
    {
        var (policy, logs) = FromConfig((PromptSizePolicy.InstructionKey, value));

        Assert.Equal(10_000, policy.InstructionLimitBytes);
        Assert.Equal(10_000, policy.ProjectContextLimitBytes);
        Assert.Empty(logs.Entries);
    }

    [Fact]
    public void Zero_disables_one_key_only()
    {
        var (policy, logs) = FromConfig((PromptSizePolicy.InstructionKey, "0"));

        Assert.Equal(0, policy.InstructionLimitBytes);
        Assert.Equal(10_000, policy.ProjectContextLimitBytes);
        Assert.Empty(logs.Entries);
        Assert.Equal("PromptSizeWarnings: instruction=disabled projectContext=10,000", policy.StartupLine);
    }

    [Theory]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("99999999999")]
    public void Invalid_value_is_the_default_plus_exactly_one_warning_naming_the_key(string value)
    {
        var (policy, logs) = FromConfig((PromptSizePolicy.ProjectContextKey, value), (PromptSizePolicy.InstructionKey, "1000"));

        Assert.Equal(10_000, policy.ProjectContextLimitBytes);
        Assert.Equal(1_000, policy.InstructionLimitBytes);
        var warning = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("PromptSizeWarnings:ProjectContextBytes", warning.Message);
        Assert.DoesNotContain("PromptSizeWarnings:InstructionBytes", warning.Message);
    }

    [Fact]
    public void Startup_line_renders_n0()
    {
        var (policy, logs) = FromConfig((PromptSizePolicy.InstructionKey, "1000"));
        policy.LogStartup();

        var line = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Equal("PromptSizeWarnings: instruction=1,000 projectContext=10,000", line.Message);
    }

    // ── Per-write log line ──────────────────────────────────────────────────

    [Fact]
    public void Logs_only_crossed_and_still_over_with_n0_and_no_content()
    {
        var logs = new SizeLogCapture();
        var policy = Policy(instruction: 1_000, logger: logs);

        policy.LogWrite(policy.Evaluate(PromptSizeKind.Instruction, "row", null, "tiny"), "rest", "row");
        Assert.Empty(logs.Entries);

        var content = "SECRET-SENTINEL" + new string('a', 1_500);
        policy.LogWrite(policy.Evaluate(PromptSizeKind.Instruction, "row", "x", content), "mcp", "row");

        var entry = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("PromptSize crossed: surface=mcp kind=instruction name=row bytes=1,515 previousBytes=1 limitBytes=1,000", entry.Message);
    }
}

/// <summary>Captures rendered log lines for the #346 size tests.</summary>
internal sealed class SizeLogCapture : ILogger
{
    public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Enqueue((logLevel, formatter(state, exception)));
}
