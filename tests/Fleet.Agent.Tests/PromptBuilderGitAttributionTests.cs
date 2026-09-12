using Fleet.Agent.Configuration;
using Fleet.Agent.Services;
using Fleet.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Tests;

/// <summary>
/// Regression coverage for the shared no-AI-attribution default (issue #269).
///
/// The policy is appended by <see cref="PromptBuilder.BuildSystemPrompt"/>, which is the
/// single prompt-construction path all three providers consume:
///   claude — <c>WriteSystemPromptFile()</c> → <c>--append-system-prompt-file</c>
///   codex  — <c>WriteSystemPromptFile()</c> → read back → <c>thread/start.baseInstructions</c>
///   gemini — <c>BuildSystemPrompt()</c> → <c>GEMINI_SYSTEM_MD</c> file
/// so both entry points are asserted below rather than a re-declared copy of the text.
/// </summary>
public class PromptBuilderGitAttributionTests
{
    private const string PolicyHeading = "## Git and Pull Request Output";

    private static PromptBuilder CreateBuilder(AgentOptions options) =>
        new(Options.Create(options), NullLogger<PromptBuilder>.Instance);

    private static AgentOptions BaseOptions(string role = "test", string? workDir = null) => new()
    {
        Name    = "test-agent",
        Role    = role,
        WorkDir = workDir ?? Path.GetTempPath(),
    };

    /// <summary>
    /// Writes an ordinary role file at the location PromptBuilder actually reads
    /// (<c>{AppContext.BaseDirectory}/roles/{role}/system.md</c>) under a unique role name,
    /// so the test exercises the real load path without colliding with other tests.
    /// </summary>
    private sealed class RoleFile : IDisposable
    {
        public string Role { get; }
        private readonly string _dir;

        public RoleFile(string content)
        {
            Role = "attr-test-" + Guid.NewGuid().ToString("N");
            _dir = Path.Combine(AppContext.BaseDirectory, "roles", Role);
            Directory.CreateDirectory(_dir);
            File.WriteAllText(Path.Combine(_dir, "system.md"), content);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }
    }

    // ── Presence across role-file states ─────────────────────────────────────

    [Fact]
    public void BuildSystemPrompt_NoRoleFiles_ContainsGitAttributionPolicy()
    {
        // An installation with no bundled role file on disk still receives the policy —
        // this is what makes the rule independent of any DB-managed role instruction.
        var prompt = CreateBuilder(BaseOptions(role: "role-that-does-not-exist")).BuildSystemPrompt();

        Assert.Contains(PolicyHeading, prompt);
    }

    [Fact]
    public void BuildSystemPrompt_OrdinaryRoleFile_ContainsPolicyAndKeepsRoleContent()
    {
        using var role = new RoleFile("# Existing role\n\nDo the existing role things.\n");

        var prompt = CreateBuilder(BaseOptions(role.Role)).BuildSystemPrompt();

        Assert.Contains("Do the existing role things.", prompt);
        Assert.Contains(PolicyHeading, prompt);
    }

    [Fact]
    public void BuildSystemPrompt_EmptyRoleFile_ContainsGitAttributionPolicy()
    {
        using var role = new RoleFile(string.Empty);

        var prompt = CreateBuilder(BaseOptions(role.Role)).BuildSystemPrompt();

        Assert.Contains(PolicyHeading, prompt);
    }

    // ── Policy content ───────────────────────────────────────────────────────

    [Fact]
    public void BuildSystemPrompt_PolicyForbidsTrailersFootersAndSessionLinks()
    {
        var prompt = CreateBuilder(BaseOptions()).BuildSystemPrompt();

        Assert.Contains("Co-Authored-By", prompt);
        Assert.Contains("generated with", prompt);
        // Session links must be covered explicitly, independently of the footer text.
        Assert.Contains("Claude session, conversation or transcript link", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_PolicyPreservesHumanCreditsAndHistory()
    {
        var prompt = CreateBuilder(BaseOptions()).BuildSystemPrompt();

        Assert.Contains("Keep the normal Git author and committer identity", prompt);
        Assert.Contains("Keep legitimate human co-authors", prompt);
        Assert.Contains("Signed-off-by", prompt);
        Assert.Contains("Do not rewrite existing commits", prompt);
        Assert.Contains("authorship acknowledgements", prompt);
    }

    // ── No collateral damage to unrelated instruction blocks ─────────────────

    [Fact]
    public void BuildSystemPrompt_RichMode_KeepsFormattingSectionAlongsidePolicy()
    {
        var options = BaseOptions();
        options.FormattingMode = FormattingMode.Rich;

        var prompt = CreateBuilder(options).BuildSystemPrompt();

        Assert.Contains("## Output Formatting", prompt);
        Assert.Contains(PolicyHeading, prompt);
    }

    [Fact]
    public void BuildSystemPrompt_MemoryTools_KeepsMemorySectionsAlongsidePolicy()
    {
        var options = BaseOptions();
        options.AllowedTools = ["mcp__fleet-memory__memory_get", "mcp__fleet-memory__memory_store"];
        options.Projects     = ["alpha"];

        var prompt = CreateBuilder(options).BuildSystemPrompt();

        Assert.Contains("## Memory Scoping", prompt);
        Assert.Contains("## Session Start: Knowledge Loading", prompt);
        Assert.Contains(PolicyHeading, prompt);
    }

    // ── Provider delivery paths ──────────────────────────────────────────────

    [Fact]
    public void WriteSystemPromptFile_ContainsPolicy_ClaudeAndCodexDeliveryPath()
    {
        // ClaudeExecutor passes this file to --append-system-prompt-file; CodexExecutor
        // reads the same file back into thread/start.baseInstructions.
        var workDir = Path.Combine(Path.GetTempPath(), "fleet-attr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);

        try
        {
            var path = CreateBuilder(BaseOptions(workDir: workDir)).WriteSystemPromptFile();

            Assert.Contains(PolicyHeading, File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void BuildSystemPrompt_ContainsPolicy_GeminiDeliveryPath()
    {
        // GeminiExecutor writes BuildSystemPrompt() straight to the GEMINI_SYSTEM_MD file.
        var prompt = CreateBuilder(BaseOptions()).BuildSystemPrompt();

        Assert.Contains(PolicyHeading, prompt);
    }
}
