using System.Text;
using Fleet.Agent.Services;

namespace Fleet.Agent.Tests;

/// <summary>
/// How a gemini agent's instructions are laid out for the Antigravity CLI: rule files that sort in
/// order, each under the CLI's per-file cut, and a refusal instead of anything the CLI would cut or
/// leave out. The limits themselves are measured against the real binary by
/// <c>tests/antigravity-cli/run.sh</c>; these tests hold the arithmetic.
/// </summary>
public sealed class GeminiInstructionFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gemini-rules-{Guid.NewGuid():N}");
    private readonly string _home;
    private readonly string _work;

    public GeminiInstructionFilesTests()
    {
        _home = Path.Combine(_root, "home", ".gemini");
        _work = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(_work);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Rules => GeminiInstructionFiles.RulesDirectory(_home);

    private static string Lines(int bytes, string unit = "A synthetic rule line used only to fill space.\n")
    {
        var text = new StringBuilder();
        while (Encoding.UTF8.GetByteCount(text.ToString()) + Encoding.UTF8.GetByteCount(unit) <= bytes)
            text.Append(unit);
        return text.ToString();
    }

    [Theory]
    [InlineData("A synthetic rule line used only to fill space.\n")]
    [InlineData("Синтетическая строка правила, только чтобы занять место.\n")]
    [InlineData("\n\n  indented, after blank lines 🙂\r\n")]
    public void Split_KeepsEveryByte_InPiecesThatEndOnLineBoundaries_AndFitTheFile(string unit)
    {
        var text = "FIRST\n" + Lines(50_000, unit) + "LAST LINE WITHOUT A NEWLINE";

        var chunks = GeminiInstructionFiles.Split(text, out var fault)!;

        Assert.Equal("", fault);
        Assert.Equal(text, string.Concat(chunks));
        Assert.True(chunks.Count >= 3);
        Assert.All(chunks, c => Assert.InRange(Encoding.UTF8.GetByteCount(c), 1, GeminiInstructionFiles.ChunkBytes));
        Assert.All(chunks.SkipLast(1), c => Assert.EndsWith("\n", c));
    }

    [Fact]
    public void Split_OfAShortText_IsOnePiece_AndOfAnEmptyOneIsNone()
    {
        Assert.Equal(["one line\nand another"], GeminiInstructionFiles.Split("one line\nand another", out _));
        Assert.Empty(GeminiInstructionFiles.Split("", out _)!);
    }

    [Fact]
    public void ALineLongerThanAFile_CannotBeDelivered_AndIsRefused()
    {
        var text = "short\n" + new string('x', GeminiInstructionFiles.ChunkBytes + 1) + "\nshort\n";

        Assert.Null(GeminiInstructionFiles.Split(text, out var fault));
        Assert.Equal(
            $"one line of the agent's instructions is {GeminiInstructionFiles.ChunkBytes + 2} bytes; the Antigravity CLI takes lines of at most {GeminiInstructionFiles.ChunkBytes} bytes",
            fault);

        Assert.Equal(fault, GeminiInstructionFiles.Prepare(_home, _work, text));
        Assert.False(Directory.Exists(Rules));
    }

    [Fact]
    public void Prepare_WritesThePieces_InFilesThatSortInOrder_AlwaysOn_ForTheirOwnerOnly()
    {
        var text = Lines(50_000);

        Assert.Null(GeminiInstructionFiles.Prepare(_home, _work, text));

        var files = Directory.GetFiles(Rules).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["fleet-instructions-001.md", "fleet-instructions-002.md", "fleet-instructions-003.md"], files.Select(Path.GetFileName));
        Assert.All(files, f => Assert.StartsWith("---\ntrigger: always_on\n---\n", File.ReadAllText(f)));
        Assert.Equal(text, string.Concat(files.Select(f => File.ReadAllText(f)[GeminiInstructionFiles.FrontMatter.Length..])));
        if (!OperatingSystem.IsWindows())
            Assert.All(files, f => Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(f)));
    }

    [Fact]
    public void Prepare_RemovesTheFilesOfOursThatAShorterTextNoLongerNeeds_AndLeavesOthersAlone()
    {
        Assert.Null(GeminiInstructionFiles.Prepare(_home, _work, Lines(50_000)));
        File.WriteAllText(Path.Combine(Rules, "operator-note.md"), "---\ntrigger: always_on\n---\nkept\n");

        Assert.Null(GeminiInstructionFiles.Prepare(_home, _work, "now one short piece\n"));

        Assert.Equal(
            ["fleet-instructions-001.md", "operator-note.md"],
            Directory.GetFiles(Rules).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(GeminiInstructionFiles.FrontMatter + "now one short piece\n", File.ReadAllText(Path.Combine(Rules, "fleet-instructions-001.md")));
    }

    [Fact]
    public void InstructionsOverTheBudget_AreRefused_AndAtTheBudgetTheyAreWritten()
    {
        var atBudget = Lines(GeminiInstructionFiles.BudgetBytes - 1) ;
        atBudget += new string('x', GeminiInstructionFiles.BudgetBytes - Encoding.UTF8.GetByteCount(atBudget));

        Assert.Null(GeminiInstructionFiles.Prepare(_home, _work, atBudget));
        Assert.Equal(
            $"the agent's instructions are {GeminiInstructionFiles.BudgetBytes + 1} bytes; the Antigravity CLI delivers at most {GeminiInstructionFiles.BudgetBytes} bytes of instructions",
            GeminiInstructionFiles.Prepare(_home, _work, atBudget + "y"));
    }

    [Fact]
    public void RuleFilesOfOthers_CountAgainstTheSameBudget_BecauseTheCliWouldDropOursForThem()
    {
        // Each of these is added by the CLI beside ours and shares its limit.
        var workspaceRule = Path.Combine(_work, "AGENTS.md");
        var homeRule = Path.Combine(_home, "GEMINI.md");
        var dotAgentsRule = Path.Combine(_work, ".agents", "rules", "team.md");
        var above = Path.Combine(_root, "GEMINI.md");
        foreach (var path in new[] { workspaceRule, homeRule, dotAgentsRule, above })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, new string('r', 1_000));
        }
        // A file larger than the CLI's per-file cut takes only the cut.
        File.WriteAllText(Path.Combine(_home, "AGENTS.md"), new string('r', 50_000));

        var others = GeminiInstructionFiles.OtherRuleFiles(_home, _work);

        Assert.Equal(4 * 1_000 + GeminiInstructionFiles.CliFileCapBytes, others.Sum(f => f.CountedBytes));
        Assert.Contains(others, f => f.Path == above);

        var fits = Lines(GeminiInstructionFiles.BudgetBytes - 28_000 - 100);
        Assert.Null(GeminiInstructionFiles.Prepare(_home, _work, fits));
        // Our own files are never counted as someone else's.
        Assert.Equal(others.Count, GeminiInstructionFiles.OtherRuleFiles(_home, _work).Count);

        var fault = GeminiInstructionFiles.Prepare(_home, _work, fits + Lines(200));
        Assert.NotNull(fault);
        Assert.Contains("5 other rule file(s)", fault);
        Assert.Contains(workspaceRule, fault);
        Assert.Contains($"together they may be at most {GeminiInstructionFiles.BudgetBytes} bytes", fault);
    }
}
