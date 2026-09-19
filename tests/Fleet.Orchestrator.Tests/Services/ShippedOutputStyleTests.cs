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

    private static readonly string ProbePath =
        System.IO.Path.Combine(AppContext.BaseDirectory, "OutputStyles", "output-style-probe.md");

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

    // ── the verification probe ───────────────────────────────────────────────

    /// <summary>
    /// The probe style is the only thing that can tell whether a style actually RESOLVED.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>system/init</c>'s <c>output_style</c> echoes the name configured in <c>settings.json</c>
    /// and reports it whether or not Claude Code found a file behind it — measured on 2.1.259 with
    /// the style file moved to a wrong path: the field still named the style and the reply did not
    /// follow it. So resolution has to be observed in the reply, and the probe is what makes the
    /// reply observable.
    /// </para>
    /// <para>
    /// Pinned here because the forcing sentence is the entire mechanism. Softened to a suggestion,
    /// or with the token changed to something a real answer might contain, the probe still looks
    /// like a check and can no longer go red — which is the failure mode it exists to close.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheProbeForcesAnObservableToken()
    {
        Assert.True(File.Exists(ProbePath), $"expected the verification probe at {ProbePath}");
        var body = File.ReadAllText(ProbePath);

        // Verbatim the body that was run against a real container, so the shipped probe and the
        // evidence are the same check.
        Assert.Contains(
            "You MUST begin every single reply with the exact token ZEBRA7 and nothing before it.",
            body, StringComparison.Ordinal);

        // A token a genuine answer could produce would make a passing run meaningless.
        Assert.DoesNotContain("ZEBRA7", OutputStyleRenderer.ForPrompt(
            new OutputStyle { Name = "fleet-messaging", Body = Body() }), StringComparison.Ordinal);
    }

    /// <summary>The probe says loudly what it is, so nobody assigns it to a production agent.</summary>
    [Fact]
    public void TheProbeIsMarkedAsAProbe()
    {
        var description = OutputStyleRenderer.ReadDescription(File.ReadAllText(ProbePath));

        Assert.NotNull(description);
        Assert.Contains("probe", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never", description, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The trap is documented where the next person will look, not only in a PR comment.
    /// </summary>
    [Fact]
    public void TheConfigurationEchoIsDocumented()
    {
        var docs = System.IO.Path.Combine(RepoRoot(), "docs", "output-styles.md");
        Assert.True(File.Exists(docs), $"expected {docs}");

        var text = File.ReadAllText(docs);
        Assert.Contains("configuration echo", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ZEBRA7", text, StringComparison.Ordinal);
        // Both controls, because only the pair shows the check can go red.
        Assert.Contains("Positive control", text, StringComparison.Ordinal);
        Assert.Contains("Negative control", text, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(System.IO.Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
