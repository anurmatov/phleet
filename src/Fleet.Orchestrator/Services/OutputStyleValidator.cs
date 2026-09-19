using System.Text.RegularExpressions;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// The one gate a style body passes through on its way into <c>output_styles</c>, shared by the
/// REST handlers and <c>manage_output_styles</c> (#317).
/// </summary>
/// <remarks>
/// <para>
/// <b>The write is the only place a broken style can be caught.</b> Claude Code reports the
/// configured style name in <c>system/init</c> whether or not the file behind it resolved, so a
/// style with no frontmatter — or one whose frontmatter <c>name:</c> disagrees with the row it is
/// stored under — looks correct in every artifact, log line and dashboard echo while the agent
/// silently runs on the default. Nothing downstream notices. Refusing at the write is what turns
/// that into an error message the operator reads immediately.
/// </para>
/// <para>
/// Parsing is delegated to <see cref="OutputStyleRenderer"/> in every case. This class decides
/// what is acceptable; it never decides what the file says.
/// </para>
/// </remarks>
public static class OutputStyleValidator
{
    /// <summary>Matches the <c>output_styles.Name</c> column.</summary>
    public const int MaxNameLength = 100;

    /// <summary>Matches the <c>output_styles.Description</c> column.</summary>
    public const int MaxDescriptionLength = 500;

    /// <summary>
    /// The name is written into <c>settings.json</c> and used as a file name under
    /// <c>.generated/output-styles/</c>, so it is kept to the same alphabet as an instruction name.
    /// </summary>
    private static readonly Regex NamePattern = new(@"^[a-zA-Z0-9_-]+$", RegexOptions.Compiled);

    /// <summary>The error message for an unusable name, or null when the name is fine.</summary>
    public static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "name is required";

        if (name.Length > MaxNameLength)
            return $"name must be at most {MaxNameLength} characters (got {name.Length})";

        return NamePattern.IsMatch(name)
            ? null
            : "name must contain only letters, digits, hyphens, or underscores";
    }

    /// <summary>
    /// The error message for a body that would not resolve as an output style under
    /// <paramref name="name"/>, or null when it is fine. Never rewrites the body.
    /// </summary>
    public static string? ValidateBody(string name, string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "body is required";

        if (!OutputStyleRenderer.HasFrontmatter(body))
            return "body must open with a YAML frontmatter block delimited by --- on its own line";

        var frontmatterName = OutputStyleRenderer.ReadFrontmatterValue(body, "name");
        if (frontmatterName is null)
            return "frontmatter is missing 'name:'";

        // Claude Code matches a style by the frontmatter name, not the file name — so a row whose
        // frontmatter names something else resolves as that something else, or as nothing at all.
        // Ordinal on purpose: a case difference is a different style to Claude Code.
        if (!string.Equals(frontmatterName, name, StringComparison.Ordinal))
            return $"frontmatter name '{frontmatterName}' does not match the style name '{name}' — "
                 + "Claude Code resolves a style by its frontmatter name, so the two must agree";

        var description = OutputStyleRenderer.ReadDescription(body);
        if (description is null)
            return "frontmatter is missing 'description:'";

        if (description.Length > MaxDescriptionLength)
            return $"frontmatter description must be at most {MaxDescriptionLength} characters (got {description.Length})";

        if (OutputStyleRenderer.BodyAfterFrontmatter(body).Length == 0)
            return "body after the frontmatter is empty — a style with no instructions changes nothing";

        var keepCoding = OutputStyleRenderer.ReadFrontmatterValue(body, "keep-coding-instructions");
        if (keepCoding is not null
            && !keepCoding.Equals("true", StringComparison.OrdinalIgnoreCase)
            && !keepCoding.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return $"frontmatter keep-coding-instructions must be 'true' or 'false' (got '{keepCoding}')";
        }

        return null;
    }

    /// <summary>Both checks, name first. Null when the style is writable as given.</summary>
    public static string? Validate(string? name, string? body) =>
        ValidateName(name) ?? ValidateBody(name!, body);
}
