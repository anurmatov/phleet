using System.Text.RegularExpressions;
using Fleet.Agent.Tests.Harness;
using Fleet.Shared;

namespace Fleet.Agent.Tests;

/// <summary>
/// #386 §6 / MUST NOT "PID 1 carries a denylisted variable": the names and prefixes
/// <c>entrypoint.sh</c> unsets for a pi agent are exactly <see cref="PiLocalModel.CredentialEnvNames"/>
/// and <see cref="PiLocalModel.CredentialEnvPrefixes"/>. A name missing from the shell list would
/// stay in <c>/proc/1/environ</c>, which the model's root shell can read.
/// </summary>
/// <remarks>
/// <para>
/// Each list is found by a fixed marker, never by searching for a loop that looks right, so the test
/// cannot pass against an empty or relocated list. The form, exactly:
/// </para>
/// <code>
/// # phleet:pi-credential-env-names
/// for _V in NAME_1 NAME_2 … NAME_61; do &lt;body&gt;; done
/// # phleet:pi-credential-env-prefixes
/// for _P in ANTHROPIC_ CLAUDE_CODE_ OPENAI_ CODEX_ GEMINI_ AWS_ GOOGLE_; do &lt;body&gt;; done
/// </code>
/// <para>
/// One line each, directly under its marker. The names body must contain <c>unset "$_V"</c>; the
/// prefixes body must enumerate the environment with <c>compgen -e</c> and <c>unset</c> each match.
/// Either body may also print the one <c>NOTE: unsetting {NAME} for a pi agent</c> line §6 allows.
/// </para>
/// </remarks>
public sealed partial class EntrypointPiCredentialEnvTests
{
    private const string NamesMarker = "# phleet:pi-credential-env-names";
    private const string PrefixesMarker = "# phleet:pi-credential-env-prefixes";

    [GeneratedRegex(@"^for _V in ([A-Z][A-Z0-9_]*(?: [A-Z][A-Z0-9_]*)*); do (.+); done$")]
    private static partial Regex NamesLoop { get; }

    [GeneratedRegex(@"^for _P in ([A-Z][A-Z0-9_]*_(?: [A-Z][A-Z0-9_]*_)*); do (.+); done$")]
    private static partial Regex PrefixesLoop { get; }

    [Fact]
    public void TheNamesList_EqualsPiLocalModelCredentialEnvNames()
    {
        var names = ParseNames(File.ReadAllLines(RepoPaths.Resolve("entrypoint.sh")));

        Assert.Equal(61, PiLocalModel.CredentialEnvNames.Count);
        Assert.Equal(PiLocalModel.CredentialEnvNames.Count, names.Count);
        Assert.Equal(
            PiLocalModel.CredentialEnvNames.ToHashSet(StringComparer.Ordinal),
            names.ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public void ThePrefixesLoop_EqualsPiLocalModelCredentialEnvPrefixes()
    {
        var prefixes = ParsePrefixes(File.ReadAllLines(RepoPaths.Resolve("entrypoint.sh")));

        Assert.Equal(PiLocalModel.CredentialEnvPrefixes.Count, prefixes.Count);
        Assert.Equal(
            PiLocalModel.CredentialEnvPrefixes.ToHashSet(StringComparer.Ordinal),
            prefixes.ToHashSet(StringComparer.Ordinal));
    }

    // ── The parser, held to the exact form ──────────────────────────────────

    [Fact]
    public void Parser_ReadsTheDocumentedForm_WithOrWithoutANoteLine()
    {
        Assert.Equal(["AAA_KEY", "BBB_KEY"],
            ParseNames([NamesMarker, "for _V in AAA_KEY BBB_KEY; do unset \"$_V\"; done"]));
        Assert.Equal(["AAA_KEY"],
            ParseNames([NamesMarker, "for _V in AAA_KEY; do [ -n \"${!_V+x}\" ] && echo \"NOTE: unsetting $_V for a pi agent\" >&2; unset \"$_V\"; done"]));
        Assert.Equal(["AWS_", "GOOGLE_"],
            ParsePrefixes([PrefixesMarker, "for _P in AWS_ GOOGLE_; do for _V in $(compgen -e \"$_P\"); do unset \"$_V\"; done; done"]));
    }

    [Theory]
    [InlineData("no marker", new[] { "for _V in AAA_KEY; do unset \"$_V\"; done" })]
    [InlineData("repeated marker", new[] { NamesMarker, "for _V in AAA_KEY; do unset \"$_V\"; done", NamesMarker, "for _V in AAA_KEY; do unset \"$_V\"; done" })]
    [InlineData("marker on the last line", new[] { NamesMarker })]
    [InlineData("list not directly below", new[] { NamesMarker, "", "for _V in AAA_KEY; do unset \"$_V\"; done" })]
    [InlineData("indented", new[] { NamesMarker, "  for _V in AAA_KEY; do unset \"$_V\"; done" })]
    [InlineData("multi-line body", new[] { NamesMarker, "for _V in AAA_KEY; do", "  unset \"$_V\"", "done" })]
    [InlineData("quoted name", new[] { NamesMarker, "for _V in \"AAA_KEY\"; do unset \"$_V\"; done" })]
    [InlineData("empty list", new[] { NamesMarker, "for _V in ; do unset \"$_V\"; done" })]
    [InlineData("no unset", new[] { NamesMarker, "for _V in AAA_KEY; do echo \"$_V\"; done" })]
    public void Parser_RefusesAnythingButOneMarkerAndAWellFormedNamesLine(string _, string[] lines) =>
        Assert.Throws<InvalidOperationException>(() => ParseNames(lines));

    [Theory]
    [InlineData("no compgen", new[] { PrefixesMarker, "for _P in AWS_; do unset AWS_X; done" })]
    [InlineData("a prefix without the underscore", new[] { PrefixesMarker, "for _P in AWS; do for _V in $(compgen -e \"$_P\"); do unset \"$_V\"; done; done" })]
    [InlineData("no unset", new[] { PrefixesMarker, "for _P in AWS_; do compgen -e \"$_P\"; done" })]
    [InlineData("no marker", new[] { "for _P in AWS_; do for _V in $(compgen -e \"$_P\"); do unset \"$_V\"; done; done" })]
    public void Parser_RefusesAMalformedPrefixesLine(string _, string[] lines) =>
        Assert.Throws<InvalidOperationException>(() => ParsePrefixes(lines));

    private static IReadOnlyList<string> ParseNames(IReadOnlyList<string> lines)
    {
        var match = NamesLoop.Match(LineUnder(lines, NamesMarker));
        if (!match.Success)
            throw new InvalidOperationException($"the line under {NamesMarker} is not a one-line name loop");
        if (!match.Groups[2].Value.Contains("unset \"$_V\"", StringComparison.Ordinal))
            throw new InvalidOperationException("the name loop does not unset \"$_V\"");
        return match.Groups[1].Value.Split(' ');
    }

    private static IReadOnlyList<string> ParsePrefixes(IReadOnlyList<string> lines)
    {
        var match = PrefixesLoop.Match(LineUnder(lines, PrefixesMarker));
        if (!match.Success)
            throw new InvalidOperationException($"the line under {PrefixesMarker} is not a one-line prefix loop");
        var body = match.Groups[2].Value;
        if (!body.Contains("compgen -e", StringComparison.Ordinal) || !body.Contains("unset ", StringComparison.Ordinal))
            throw new InvalidOperationException("the prefix loop does not unset what compgen -e enumerates");
        return match.Groups[1].Value.Split(' ');
    }

    private static string LineUnder(IReadOnlyList<string> lines, string marker)
    {
        var markers = lines.Select((line, index) => (line, index)).Where(l => l.line == marker).ToList();
        if (markers.Count != 1)
            throw new InvalidOperationException($"expected exactly one '{marker}' line, found {markers.Count}");

        var next = markers[0].index + 1;
        if (next >= lines.Count)
            throw new InvalidOperationException("the marker is the last line; the list must follow it");
        return lines[next];
    }
}
