using Fleet.Orchestrator.Services;

namespace Fleet.Orchestrator.Tests.Services;

/// <summary>
/// The write-time gate on a style body (#317).
/// </summary>
/// <remarks>
/// <para>
/// Every refusal here is guarding the same failure: Claude Code reports the configured style name
/// in <c>system/init</c> whether or not the file behind it resolved, so a malformed style looks
/// correct in the dashboard, in the generated <c>settings.json</c>, in the logs and in every other
/// test — while the agent runs on the default. Nothing downstream can catch that. If a rule here
/// is relaxed, the failure it was catching becomes invisible again rather than becoming loud.
/// </para>
/// </remarks>
public class OutputStyleValidatorTests
{
    private static string Body(
        string name = "my-style",
        string? description = "Chat register.",
        string? keepCoding = "true",
        string content = "# Register\n\nLowercase and short.\n")
    {
        var lines = new List<string> { "---", $"name: {name}" };
        if (description is not null) lines.Add($"description: {description}");
        if (keepCoding is not null) lines.Add($"keep-coding-instructions: {keepCoding}");
        lines.Add("---");
        return string.Join("\n", lines) + "\n\n" + content;
    }

    // ── names ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ValidateName_Blank_IsRefused(string? name)
    {
        Assert.Equal("name is required", OutputStyleValidator.ValidateName(name));
    }

    [Theory]
    [InlineData("my style")]
    [InlineData("my/style")]
    [InlineData("my.style")]
    [InlineData("../escape")]
    public void ValidateName_OutsideTheAlphabet_IsRefused(string name)
    {
        // The name becomes a file name under .generated/output-styles/ as well as a settings.json
        // value, so it is held to the same alphabet as an instruction name.
        Assert.NotNull(OutputStyleValidator.ValidateName(name));
    }

    [Fact]
    public void ValidateName_LongerThanTheColumn_IsRefused()
    {
        var name = new string('a', OutputStyleValidator.MaxNameLength + 1);

        var error = OutputStyleValidator.ValidateName(name);

        Assert.NotNull(error);
        Assert.Contains(OutputStyleValidator.MaxNameLength.ToString(), error);
    }

    [Theory]
    [InlineData("fleet-messaging")]
    [InlineData("output_style_probe")]
    [InlineData("style99")]
    public void ValidateName_Accepted(string name)
    {
        Assert.Null(OutputStyleValidator.ValidateName(name));
    }

    // ── bodies ───────────────────────────────────────────────────────────────

    [Fact]
    public void ValidateBody_WellFormed_IsAccepted()
    {
        Assert.Null(OutputStyleValidator.ValidateBody("my-style", Body()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n  ")]
    [InlineData(null)]
    public void ValidateBody_Blank_IsRefused(string? body)
    {
        Assert.Equal("body is required", OutputStyleValidator.ValidateBody("my-style", body));
    }

    [Fact]
    public void ValidateBody_NoFrontmatter_IsRefused()
    {
        // Without frontmatter Claude Code has no name to match the style by, so it resolves to
        // nothing at all — the exact case system/init cannot report.
        var error = OutputStyleValidator.ValidateBody("my-style", "# Register\n\nLowercase.\n");

        Assert.NotNull(error);
        Assert.Contains("frontmatter", error);
    }

    [Fact]
    public void ValidateBody_UnterminatedFrontmatter_IsRefused()
    {
        var error = OutputStyleValidator.ValidateBody(
            "my-style", "---\nname: my-style\ndescription: x\n\n# Register\n");

        Assert.NotNull(error);
        Assert.Contains("frontmatter", error);
    }

    [Fact]
    public void ValidateBody_MissingFrontmatterName_IsRefused()
    {
        var body = "---\ndescription: Chat register.\n---\n\n# Register\n";

        var error = OutputStyleValidator.ValidateBody("my-style", body);

        Assert.NotNull(error);
        Assert.Contains("'name:'", error);
    }

    [Fact]
    public void ValidateBody_FrontmatterNameDisagreesWithTheRow_IsRefused()
    {
        // Nothing reconciles the two at provision time: the row name goes into settings.json and
        // the file name, and the frontmatter name is what Claude Code actually matches on. A
        // disagreement between them is a style that silently does not load.
        var error = OutputStyleValidator.ValidateBody("my-style", Body(name: "other-style"));

        Assert.NotNull(error);
        Assert.Contains("other-style", error);
        Assert.Contains("my-style", error);
    }

    [Fact]
    public void ValidateBody_FrontmatterNameDiffersOnlyByCase_IsRefused()
    {
        // Ordinal on purpose — a case difference is a different style to Claude Code, and MySQL's
        // default collation would not catch it on the row side either.
        Assert.NotNull(OutputStyleValidator.ValidateBody("my-style", Body(name: "My-Style")));
    }

    [Fact]
    public void ValidateBody_MissingDescription_IsRefused()
    {
        var error = OutputStyleValidator.ValidateBody("my-style", Body(description: null));

        Assert.NotNull(error);
        Assert.Contains("'description:'", error);
    }

    [Fact]
    public void ValidateBody_DescriptionLongerThanTheColumn_IsRefused()
    {
        var body = Body(description: new string('d', OutputStyleValidator.MaxDescriptionLength + 1));

        var error = OutputStyleValidator.ValidateBody("my-style", body);

        Assert.NotNull(error);
        Assert.Contains(OutputStyleValidator.MaxDescriptionLength.ToString(), error);
    }

    [Fact]
    public void ValidateBody_NothingAfterTheFrontmatter_IsRefused()
    {
        var error = OutputStyleValidator.ValidateBody("my-style", Body(content: "   \n\n"));

        Assert.NotNull(error);
        Assert.Contains("empty", error);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("TRUE")]
    [InlineData(null)]  // absent is fine — Claude Code has its own default
    public void ValidateBody_KeepCodingInstructions_Accepted(string? value)
    {
        Assert.Null(OutputStyleValidator.ValidateBody("my-style", Body(keepCoding: value)));
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("maybe")]
    public void ValidateBody_KeepCodingInstructionsNotABoolean_IsRefused(string value)
    {
        var error = OutputStyleValidator.ValidateBody("my-style", Body(keepCoding: value));

        Assert.NotNull(error);
        Assert.Contains("keep-coding-instructions", error);
    }

    // ── non-ASCII ────────────────────────────────────────────────────────────

    /// <summary>
    /// A style body may be written in a language other than English. A charset fault on the write
    /// path would pass every check above, all of which are ASCII.
    /// </summary>
    [Fact]
    public void ValidateBody_NonAscii_IsAcceptedAndNotRewritten()
    {
        var body = Body(
            description: "Регистр общения — строчными и коротко.",
            content: "# Регистр\n\nПиши строчными. Без заголовков — 简短一点。\n");

        Assert.Null(OutputStyleValidator.ValidateBody("my-style", body));

        // Validation reads; it never rewrites. The bytes the caller sent are the bytes stored,
        // and the derived description is the frontmatter's, character for character.
        Assert.Equal("Регистр общения — строчными и коротко.", OutputStyleRenderer.ReadDescription(body));
        Assert.Contains("简短一点。", OutputStyleRenderer.BodyAfterFrontmatter(body), StringComparison.Ordinal);
    }

    // ── the combined entry point ─────────────────────────────────────────────

    [Fact]
    public void Validate_ReportsTheNameProblemFirst()
    {
        // A bad name and a bad body at once: the name is what the body is validated against, so
        // complaining about a mismatch against a name we already rejected would mislead.
        var error = OutputStyleValidator.Validate("bad name", body: null);

        Assert.NotNull(error);
        Assert.Contains("name", error);
    }

    [Fact]
    public void Validate_BothGood_IsAccepted()
    {
        Assert.Null(OutputStyleValidator.Validate("my-style", Body()));
    }
}
