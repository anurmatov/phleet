using System.Text.Json;
using System.Text.RegularExpressions;
using Fleet.Agent.Services;
using Fleet.Agent.Tests.Harness;

namespace Fleet.Agent.Tests;

/// <summary>
/// <c>tests/antigravity-cli/checks.py</c> drives the real Antigravity CLI with the flags and the
/// limits written in it. They must be the executor's own, or the harness proves something about a
/// command line and a layout the agent does not use.
/// </summary>
public sealed class AntigravityHarnessParityTests
{
    private static readonly string Checks = File.ReadAllText(RepoPaths.Resolve("tests/antigravity-cli/checks.py"));

    private static string Value(string name)
    {
        var match = Regex.Match(Checks, $@"(?m)^{Regex.Escape(name)} = (.+)$");
        Assert.True(match.Success, $"checks.py has no line '{name} = …'");
        return match.Groups[1].Value.Trim();
    }

    [Fact]
    public void TheHarness_StartsTheCli_WithTheExecutorsFlags()
    {
        // The Python list is JSON apart from the quotes around the settings object.
        var flags = Regex.Replace(Value("FLAGS"), @"'(\{.*\})'", m => JsonSerializer.Serialize(m.Groups[1].Value));

        Assert.Equal(GeminiExecutor.BuildArgs(model: null, resumeId: null), JsonSerializer.Deserialize<string[]>(flags));
    }

    [Fact]
    public void TheHarness_ChecksTheLimitsTheExecutorEnforces()
    {
        Assert.Equal(GeminiExecutor.MaxPromptBytes.ToString(), Value("MAX_PROMPT_BYTES"));
        Assert.Equal(GeminiInstructionFiles.ChunkBytes.ToString(), Value("CHUNK_BYTES"));
        Assert.Equal(GeminiInstructionFiles.BudgetBytes.ToString(), Value("BUDGET_BYTES"));
        Assert.Equal(JsonSerializer.Serialize(GeminiInstructionFiles.FrontMatter), Value("FRONT_MATTER"));
        Assert.Equal(JsonSerializer.Serialize(GeminiInstructionFiles.FilePrefix), Value("FILE_PREFIX"));
        Assert.Equal(JsonSerializer.Serialize(GeminiExecutor.DisableAutoUpdateVariable), Value("DISABLE_AUTO_UPDATE"));
    }

    [Fact]
    public void TheHarness_WritesTheInstructionFilesWhereTheExecutorDoes()
    {
        Assert.Contains("RULES = GEMINI / \"config\" / \"rules\"\n", Checks);
        Assert.Equal(Path.Combine("home", "config", "rules"), GeminiInstructionFiles.RulesDirectory("home"));
        Assert.Contains("(RULES / (\"%s%03d.md\" % (FILE_PREFIX, number)))", Checks);
        Assert.Equal("fleet-instructions-007.md", GeminiInstructionFiles.FileName(7));
    }
}
