using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;

namespace Fleet.Orchestrator.Tests.Services;

/// <summary>
/// The built-in style shipped in the image, asserted as a product decision rather than as text
/// (#314 §4).
/// </summary>
/// <remarks>
/// Each of these was argued for and could be undone by an edit that looks like tidying. The style
/// is what an agent's chat register becomes, so the properties that make it correct are pinned
/// here — not the prose, which is free to change.
/// </remarks>
public class ShippedOutputStyleTests
{
    private static readonly string Path_ =
        System.IO.Path.Combine(AppContext.BaseDirectory, "OutputStyles", "fleet-messaging.md");

    private static string Body() => File.ReadAllText(Path_);

    [Fact]
    public void TheStyleIsShippedWithTheOrchestrator()
    {
        // It is seeded from the image, not supplied by the operator like roles/ — so if this is
        // missing, a fresh install has no style to switch anyone to.
        Assert.True(File.Exists(Path_), $"expected the built-in style at {Path_}");
    }

    /// <summary>
    /// These agents run <c>gh</c>, <c>docker</c> and <c>dotnet</c>. Dropping the coding defaults to
    /// win a tone argument is a bad trade, and it does not remove the conflicting blocks anyway.
    /// </summary>
    [Fact]
    public void KeepCodingInstructionsIsOn()
    {
        Assert.Contains("keep-coding-instructions: true", Body(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The fix for an arbitrary pick between two contradicting rules is to remove the ambiguity,
    /// not to state the same rule more loudly — so the style names what it overrides.
    /// </summary>
    [Fact]
    public void ItNamesWhatItOverrides()
    {
        var body = Body();

        Assert.Contains("override", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("# Tone and style", body, StringComparison.Ordinal);
        Assert.Contains("# Text output", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The style carries register and length, NOT permission to use markup — that belongs to the
    /// agent's formatting mode, and a style that granted it would outrank a real setting.
    /// </summary>
    [Fact]
    public void ItDefersStructureToTheFormattingGuidance()
    {
        Assert.Contains("Structure is not decided here", Body(), StringComparison.Ordinal);
    }

    /// <summary>The frontmatter parses, and the inlined form drops it.</summary>
    [Fact]
    public void ItRendersForBothProviders()
    {
        var style = new OutputStyle { Name = "fleet-messaging", Body = Body() };

        Assert.Equal(Body(), OutputStyleRenderer.ForStyleFile(style));

        var inlined = OutputStyleRenderer.ForPrompt(style);
        Assert.StartsWith("# Messaging register", inlined, StringComparison.Ordinal);
        Assert.Contains("Structure is not decided here", inlined, StringComparison.Ordinal);

        // Asserted on the frontmatter's STRUCTURE, not on a word from it. An earlier version of
        // this checked for "keep-coding-instructions" and failed on a body sentence that mentioned
        // the setting in prose — a test that cannot tell frontmatter from text about frontmatter.
        Assert.False(inlined.StartsWith("---", StringComparison.Ordinal));
        Assert.DoesNotContain("\nname: fleet-messaging", inlined, StringComparison.Ordinal);
        Assert.DoesNotContain("\ndescription:", inlined, StringComparison.Ordinal);

        Assert.NotNull(OutputStyleRenderer.ReadDescription(Body()));
    }
}
