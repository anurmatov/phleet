using System.Text.RegularExpressions;
using Fleet.Agent.Tests.Harness;
using Fleet.Shared;

namespace Fleet.Agent.Tests;

/// <summary>
/// #335 AC9 / MUST NOT 13: the names <c>entrypoint.sh</c> unsets on every agent are exactly
/// <see cref="HostedModelProviders.KeyEnvVars"/>. A name missing from the shell list would reach
/// PID 1 and every child, including the model's shell.
/// </summary>
/// <remarks>
/// The list is found by a fixed marker, never by searching for a loop that looks right, so the test
/// cannot pass against an empty or relocated list.
/// </remarks>
public sealed partial class EntrypointReservedKeysTests
{
    private const string Marker = "# phleet:reserved-key-names";

    [GeneratedRegex("^for _HOSTED_VAR in ([A-Z][A-Z0-9_]*( [A-Z][A-Z0-9_]*)*); do$")]
    private static partial Regex ReservedLoopLine { get; }

    [Fact]
    public void ReservedList_EqualsTheRegistryKeyEnvVars()
    {
        var names = ParseReservedNames(File.ReadAllLines(RepoPaths.Resolve("entrypoint.sh")));

        Assert.NotEmpty(HostedModelProviders.KeyEnvVars);
        Assert.Equal(
            HostedModelProviders.KeyEnvVars.ToHashSet(StringComparer.Ordinal),
            names.ToHashSet(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("no marker", new[] { "for _HOSTED_VAR in ZAI_CODING_PLAN_API_KEY; do" })]
    [InlineData("repeated marker", new[] { Marker, "for _HOSTED_VAR in ZAI_CODING_PLAN_API_KEY; do", Marker, "for _HOSTED_VAR in ZAI_CODING_PLAN_API_KEY; do" })]
    [InlineData("marker on the last line", new[] { Marker })]
    [InlineData("empty list", new[] { Marker, "for _HOSTED_VAR in ; do" })]
    [InlineData("list not directly below", new[] { Marker, "", "for _HOSTED_VAR in ZAI_CODING_PLAN_API_KEY; do" })]
    [InlineData("indented or reformatted", new[] { Marker, "  for _HOSTED_VAR in ZAI_CODING_PLAN_API_KEY; do" })]
    [InlineData("quoted name", new[] { Marker, "for _HOSTED_VAR in \"ZAI_CODING_PLAN_API_KEY\"; do" })]
    public void Parser_RejectsAnythingButOneMarkerAndAWellFormedLine(string _, string[] lines)
    {
        Assert.Throws<InvalidOperationException>(() => ParseReservedNames(lines));
    }

    [Fact]
    public void Parser_ReturnsEveryNameOnTheLine()
    {
        var names = ParseReservedNames([Marker, "for _HOSTED_VAR in AAA_KEY BBB_KEY; do"]);

        Assert.Equal(["AAA_KEY", "BBB_KEY"], names);
    }

    private static IReadOnlyList<string> ParseReservedNames(IReadOnlyList<string> lines)
    {
        var markers = lines.Select((line, index) => (line, index)).Where(l => l.line == Marker).ToList();
        if (markers.Count != 1)
            throw new InvalidOperationException($"expected exactly one '{Marker}' line, found {markers.Count}");

        var next = markers[0].index + 1;
        if (next >= lines.Count)
            throw new InvalidOperationException("the marker is the last line; the reserved list must follow it");

        var match = ReservedLoopLine.Match(lines[next]);
        if (!match.Success)
            throw new InvalidOperationException($"the line under the marker is not a reserved-name loop: '{lines[next]}'");

        var names = match.Groups[1].Value.Split(' ');
        if (names.Length == 0)
            throw new InvalidOperationException("the reserved list is empty");
        return names;
    }
}
