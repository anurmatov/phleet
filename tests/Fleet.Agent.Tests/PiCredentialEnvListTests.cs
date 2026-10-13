using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Fleet.Shared;

namespace Fleet.Agent.Tests;

/// <summary>
/// #386 §4.2 / §15: every environment name the pinned Pi reads for a credential is covered by
/// <see cref="PiLocalModel.CredentialEnvNames"/> or <see cref="PiLocalModel.CredentialEnvPrefixes"/>.
/// </summary>
/// <remarks>
/// <para>
/// The names come from the two Pi sources vendored under <c>Fixtures/pi-source/</c> at tag
/// <c>v1.0.4</c>, never from <c>pi --help</c> at build or run time. <c>SOURCE.txt</c> pins each
/// file's SHA-256, so the vendored bytes cannot drift from what was reviewed, and a Pi bump that
/// does not replace them fails the <c>RealPi</c> version check below.
/// </para>
/// <para>
/// Extraction, exactly as §15: the <c>args.ts</c> "Environment Variables" block, one name per
/// <c>^\s{2}([A-Z][A-Z0-9_]+)\s+-</c> line, with <c>${ENV_AGENT_DIR}</c> and
/// <c>${ENV_SESSION_DIR}</c> mapped as <c>coding-agent/src/config.ts</c> maps them; and every
/// <c>env-api-keys.ts</c> literal matching <c>"([A-Z][A-Z0-9_]{2,})"</c>.
/// </para>
/// </remarks>
public sealed partial class PiCredentialEnvListTests
{
    /// <summary>The names Fleet sets in the Pi child itself.</summary>
    private static readonly string[] FleetSetNames = ["PI_CODING_AGENT_DIR", "PI_OFFLINE", "PI_TELEMETRY"];

    /// <summary><c>coding-agent/src/config.ts</c>: <c>${APP_NAME.toUpperCase()}_CODING_AGENT_DIR</c> and <c>…_SESSION_DIR</c>, APP_NAME "pi".</summary>
    private static readonly Dictionary<string, string> ConfigConstants = new(StringComparer.Ordinal)
    {
        ["ENV_AGENT_DIR"] = "PI_CODING_AGENT_DIR",
        ["ENV_SESSION_DIR"] = "PI_CODING_AGENT_SESSION_DIR",
    };

    [GeneratedRegex(@"^\s{2}([A-Z][A-Z0-9_]+)\s+-")]
    private static partial Regex HelpNameLine { get; }

    [GeneratedRegex(@"^\s{2}\$\{([A-Z_]+)(?:\.[A-Za-z]+\([^)]*\))?\}\s+-")]
    private static partial Regex HelpConstantLine { get; }

    [GeneratedRegex("\"([A-Z][A-Z0-9_]{2,})\"")]
    private static partial Regex QuotedName { get; }

    private static string SourcePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "pi-source", name);

    private static IReadOnlyList<string[]> SourceLines() =>
        File.ReadAllLines(SourcePath("SOURCE.txt"))
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line => line.Split(' '))
            .ToList();

    [Fact]
    public void Source_RecordsThePinnedTagAndCommit()
    {
        var lines = SourceLines();

        Assert.Equal(["tag", $"v{PiLocalModel.PinnedVersion}"], lines.Single(l => l[0] == "tag"));
        Assert.Equal(["commit", "7c10bd4337495ee613f2224843ecdf349b80d1df"], lines.Single(l => l[0] == "commit"));
    }

    [Fact]
    public void Source_EachVendoredFileMatchesItsRecordedSha256()
    {
        var files = SourceLines().Where(l => l[0] == "file").ToList();

        Assert.Equal(["args.ts", "env-api-keys.ts"], files.Select(f => f[1]));
        Assert.Equal(["packages/coding-agent/src/cli/args.ts", "packages/ai/src/env-api-keys.ts"], files.Select(f => f[2]));
        foreach (var file in files)
        {
            Assert.Equal("sha256", file[3]);
            var actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(SourcePath(file[1]))));
            Assert.Equal(file[4], actual);
        }
    }

    /// <summary>The "Environment Variables" help block of <c>args.ts</c>.</summary>
    internal static IReadOnlyList<string> ExtractArgsNames(IReadOnlyList<string> lines)
    {
        var start = lines.ToList().FindIndex(l => l.Contains("Environment Variables", StringComparison.Ordinal));
        if (start < 0)
            throw new InvalidOperationException("args.ts has no Environment Variables block");

        var names = new List<string>();
        foreach (var line in lines.Skip(start + 1).TakeWhile(l => l.Trim().Length > 0))
        {
            if (HelpNameLine.Match(line) is { Success: true } name)
                names.Add(name.Groups[1].Value);
            else if (HelpConstantLine.Match(line) is { Success: true } constant)
                names.Add(ConfigConstants.TryGetValue(constant.Groups[1].Value, out var mapped)
                    ? mapped
                    : throw new InvalidOperationException($"args.ts names an unmapped constant {constant.Groups[1].Value}"));
            else
                throw new InvalidOperationException($"args.ts has an Environment Variables line no rule reads: '{line.Trim()}'");
        }
        return names;
    }

    internal static IReadOnlyList<string> ExtractEnvApiKeyNames(string source) =>
        QuotedName.Matches(source).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToList();

    [Fact]
    public void Extraction_FindsTheHelpBlockAndTheLiterals()
    {
        var args = ExtractArgsNames(File.ReadAllLines(SourcePath("args.ts")));
        var keys = ExtractEnvApiKeyNames(File.ReadAllText(SourcePath("env-api-keys.ts")));

        // 1.0.4 help: 43 provider and cloud names, the two directory constants and four more PI_ names.
        Assert.Equal(49, args.Count);
        Assert.Contains("PI_CODING_AGENT_DIR", args);
        Assert.Contains("PI_CODING_AGENT_SESSION_DIR", args);
        Assert.Contains("AWS_REGION", args);
        Assert.Contains("GOOGLE_CLOUD_LOCATION", keys);
        Assert.Contains("AWS_WEB_IDENTITY_TOKEN_FILE", keys);
        Assert.Contains("GCLOUD_PROJECT", keys);
    }

    [Fact]
    public void EveryExtractedName_IsCoveredByTheDenylist_ExceptTheNamesFleetSets()
    {
        var extracted = ExtractArgsNames(File.ReadAllLines(SourcePath("args.ts")))
            .Concat(ExtractEnvApiKeyNames(File.ReadAllText(SourcePath("env-api-keys.ts"))))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var uncovered = extracted.Where(n => !PiLocalModel.IsCredentialEnvName(n)).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(FleetSetNames, uncovered);
    }

    [Fact]
    public void TheFleetSetNames_AreNeverDenylisted_AndAreWhatTheChildGets()
    {
        Assert.All(FleetSetNames, n => Assert.False(PiLocalModel.IsCredentialEnvName(n)));
        Assert.Equal(FleetSetNames, PiLocalModel.ChildEnvironment.Select(kv => kv.Key).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("  FOO_KEY        - description")]
    [InlineData("  ${ENV_AGENT_DIR.padEnd(32)} - Config directory")]
    public void ExtractArgsNames_ReadsBothLineShapes(string line)
    {
        var names = ExtractArgsNames(["Environment Variables:", line, ""]);

        Assert.Single(names);
    }

    [Fact]
    public void ExtractArgsNames_RefusesALineNoRuleReads()
    {
        Assert.Throws<InvalidOperationException>(() => ExtractArgsNames(["Environment Variables:", "  lowercase - x", ""]));
        Assert.Throws<InvalidOperationException>(() => ExtractArgsNames(["Environment Variables:", "  ${ENV_OTHER} - x", ""]));
    }

    /// <summary>A Pi bump without fresh vendored sources fails here.</summary>
    [Fact]
    [Trait("Category", "RealPi")]
    public async Task RealPi_TheInstalledPiIsTheVendoredTag()
    {
        var bin = Environment.GetEnvironmentVariable("FLEET_PI_BIN") is { Length: > 0 } configured ? configured : "pi";
        var psi = new ProcessStartInfo(bin) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("--version");
        psi.Environment["PI_OFFLINE"] = "1";

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"'{bin}' did not start; a missing pi is a hard failure.");
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        var tag = SourceLines().Single(l => l[0] == "tag")[1];
        Assert.Equal(tag, "v" + output.Trim());
    }
}
