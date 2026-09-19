using Fleet.Agent.Configuration;
using Fleet.Agent.Services;
using Fleet.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Tests;

/// <summary>
/// How the output style reaches a provider that has no output-style mechanism (#314).
/// </summary>
/// <remarks>
/// <para>
/// Claude resolves the style as a file, so the orchestrator leaves <c>OutputStyleBody</c> empty for
/// it and there is nothing to assert here for that provider. Codex and gemini get the identical
/// text inlined, and what matters is where in the prompt it lands: the style carries register and
/// length, while the per-agent formatting mode still decides structure.
/// </para>
/// <para>
/// Which is why heading suppression is asserted on a <b>LegacyHtml</b> agent. On that tier a
/// heading really does arrive as a literal <c>#</c>, so suppressing it is the contract. Asserting
/// the same thing on a Rich agent would be asserting that it violates its own — Rich exists
/// precisely to permit headings, lists and tables.
/// </para>
/// </remarks>
public class PromptBuilderOutputStyleTests
{
    /// <summary>A style shaped like the shipped one: register rules, and structure deferred.</summary>
    private const string StyleBody =
        "# Messaging register\n\n"
        + "Lowercase and conversational. Lead with the answer.\n\n"
        + "## Structure and markup\n\n"
        + "Structure is not decided here. Do not add headings, bullet lists or tables that the "
        + "formatting guidance does not allow.\n";

    private static PromptBuilder Builder(string styleBody, FormattingMode mode = FormattingMode.LegacyHtml) =>
        new(
            Options.Create(new AgentOptions
            {
                Name            = "test-agent",
                Role            = "test",
                WorkDir         = Path.GetTempPath(),
                FormattingMode  = mode,
                OutputStyleBody = styleBody,
            }),
            NullLogger<PromptBuilder>.Instance);

    [Fact]
    public void StyleBody_IsInlinedIntoThePrompt()
    {
        var prompt = Builder(StyleBody).BuildSystemPrompt();

        Assert.Contains("Lowercase and conversational.", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// The empty case is the whole rollout switch: an agent with no style must assemble exactly
    /// what it assembled before styles existed.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void NoStyleBody_LeavesThePromptByteIdentical(string body)
    {
        var withStyleField = Builder(body).BuildSystemPrompt();
        var asBefore = new PromptBuilder(
            Options.Create(new AgentOptions
            {
                Name = "test-agent", Role = "test", WorkDir = Path.GetTempPath(),
                FormattingMode = FormattingMode.LegacyHtml,
            }),
            NullLogger<PromptBuilder>.Instance).BuildSystemPrompt();

        Assert.Equal(asBefore, withStyleField);
    }

    /// <summary>
    /// AC5. On the legacy-HTML tier, banning headings IS the contract, and nothing follows the
    /// style that would license them.
    /// </summary>
    [Fact]
    public void LegacyHtmlAgent_HeadingSuppressionStands()
    {
        var prompt = Builder(StyleBody, FormattingMode.LegacyHtml).BuildSystemPrompt();

        Assert.Contains("Do not add headings", prompt, StringComparison.Ordinal);
        // Nothing grants markup back on this tier.
        Assert.DoesNotContain("## Output Formatting", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// A Rich agent keeps its own contract. The style is still inlined — dropping it for one
    /// formatting mode would be the same failure as dropping it for one provider — but the
    /// formatting block comes after it and has the last word on structure.
    /// </summary>
    [Fact]
    public void RichAgent_FormattingBlockFollowsTheStyleAndOverridesIt()
    {
        var prompt = Builder(StyleBody, FormattingMode.Rich).BuildSystemPrompt();

        var styleAt = prompt.IndexOf("Structure is not decided here.", StringComparison.Ordinal);
        var formattingAt = prompt.IndexOf("## Output Formatting", StringComparison.Ordinal);

        Assert.True(styleAt >= 0, "the style is inlined for a Rich agent too");
        Assert.True(formattingAt > styleAt,
            "the formatting block must follow the style, so structure is decided by the mode");
        Assert.Contains("use them when they aid clarity", prompt, StringComparison.Ordinal);
    }
}
