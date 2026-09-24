using System.Text.RegularExpressions;
using Fleet.Agent.Tests.Harness;

namespace Fleet.Agent.Tests;

/// <summary>
/// D8 — the structural hygiene sweep. Deliberately narrow in scope and structural in content.
///
/// <para>Two reasons for that shape, both found while writing the spec. A denylist naming real orgs
/// and hosts would publish, in a public test file, exactly the inventory the boundary exists to
/// withhold. And a blanket absolute-path rule fails on the harness's own legitimate
/// <c>/bin/cat</c> stand-in and on ordinary <c>/tmp</c> usage — a hygiene gate that fails on
/// correct code gets disabled rather than fixed.</para>
///
/// <para>The name-specific sweep (real org, host, project and agent names) runs in private Fleet
/// against the PR diff from an operator-owned list that is never committed here. Only its pass/fail
/// RESULT is stated publicly. "The public patterns passed" is not the same statement.</para>
/// </summary>
public partial class FixtureHygieneTests
{
    // ── Patterns ─────────────────────────────────────────────────────────────

    /// <summary>
    /// F1 — an absolute path outside the fixture-corpus root. FIXTURE SCOPE ONLY (MUST NOT #11).
    ///
    /// <para><b>Two corrections to the pattern as specified, both found by running it.</b></para>
    ///
    /// <para>The specified form had no left boundary, so on the allowed path
    /// <c>/workspace/example/notes.txt</c> the engine simply retried at the inner <c>/example/…</c>
    /// and matched — rejecting exactly the paths the rule is meant to permit. The lookbehind fixes
    /// that: a match must begin at a real path boundary, not in the middle of one.</para>
    ///
    /// <para>The specified lookahead also required a trailing slash, so the bare directory
    /// <c>/workspace/example</c> was rejected. <c>(?:/|\b)</c> accepts the directory itself while
    /// still rejecting a sibling such as <c>/workspace/examples/…</c>.</para>
    /// </summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9_.\-/])/(?!workspace/example(?:/|\b))[A-Za-z0-9_.\-]+(?:/[A-Za-z0-9_.\-]+)+")]
    private static partial Regex AbsolutePathOutsideCorpus { get; }

    /// <summary>F2 — Telegram bot-token shape.</summary>
    [GeneratedRegex(@"\b\d{9,10}:[A-Za-z0-9_\-]{35}\b")]
    private static partial Regex BotTokenShape { get; }

    /// <summary>F3 — bearer credential shape.</summary>
    [GeneratedRegex(@"(?i)\bbearer\s+[A-Za-z0-9._\-]{16,}")]
    private static partial Regex BearerShape { get; }

    /// <summary>F4 — dotted-quad IP.</summary>
    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b")]
    private static partial Regex DottedQuad { get; }

    /// <summary>F5 — hostname with a TLD, case-insensitive. FIXTURE SCOPE.</summary>
    [GeneratedRegex(@"(?i)\b[a-z0-9\-]+\.(com|org|net|io|dev|kg)\b")]
    private static partial Regex HostnameAnyCase { get; }

    /// <summary>
    /// F5-lower — the same pattern with the case-insensitive flag dropped. SOURCE SCOPE.
    ///
    /// <para>Dropping the flag rather than growing an allowlist is deliberate.
    /// <c>Microsoft.NET.Test.Sdk</c> in <c>Fleet.Agent.Tests.csproj</c> — a file this work must
    /// modify — matches the case-insensitive form as a <c>.net</c> hostname, so the gate could
    /// never have gone green on a correct change. An allowlist of .NET namespace fragments would
    /// have to grow with every new <c>PackageReference</c> and every new <c>using</c>, and a
    /// hygiene allowlist that grows on unrelated changes is a denylist that stopped working.</para>
    ///
    /// <para>Residual risk, stated rather than waved off: this will not catch a capitalised leak
    /// such as <c>Example.COM</c> in prose. Real hostnames in configuration, URLs and logs are
    /// lowercase, so the structural gap is narrow, and the private name-specific sweep is
    /// case-insensitive and covers exactly the capitalised-prose case.</para>
    /// </summary>
    [GeneratedRegex(@"\b[a-z0-9\-]+\.(com|org|net|io|dev|kg)\b")]
    private static partial Regex HostnameLowerOnly { get; }

    /// <summary>
    /// Applied to both scopes. Asserted to be SHORT — an allowlist that grows is a denylist that
    /// stopped working.
    /// </summary>
    private static readonly IReadOnlyList<string> Allowlist =
        ["127.0.0.1", "0.0.0.0", "::1", "example.com", "example.org", "localhost"];

    /// <summary>A line ending in this marker is exempt. Source scope only — JSONL carries no comments.</summary>
    private const string LineMarker = "// hygiene-ok:";

    // ── Scopes ───────────────────────────────────────────────────────────────

    private const string FixtureRoot = "tests/Fleet.Agent.Tests/Fixtures";

    /// <summary>
    /// Every non-fixture file this work adds or modifies. Committed explicitly rather than derived
    /// from git, so the sweep runs offline with no process launch and a rename fails loudly.
    ///
    /// <para><c>Fleet.Agent.Tests.csproj</c> is deliberately in this list: the
    /// <c>Microsoft.NET.Test.Sdk</c> case is covered by the LIVE file, not only by a synthetic
    /// string (AC13).</para>
    /// </summary>
    private static readonly IReadOnlyList<string> SourceScope =
    [
        "tests/Fleet.Agent.Tests/Fleet.Agent.Tests.csproj",
        "tests/Fleet.Agent.Tests/ConversationTurnLifecycleTests.cs",
        "tests/Fleet.Agent.Tests/ConversationEventPumpTests.cs",
        "tests/Fleet.Agent.Tests/ClaudeExecutorTerminalResultTests.cs",
        "tests/Fleet.Agent.Tests/CodexExecutorTests.cs",
        "tests/Fleet.Agent.Tests/RealAdapterMappingTests.cs",
        "tests/Fleet.Agent.Tests/RuntimeScenarioTests.cs",
        "tests/Fleet.Agent.Tests/ProviderCapabilityMatrixTests.cs",
        "tests/Fleet.Agent.Tests/FixtureHygieneTests.cs",
        "tests/Fleet.Agent.Tests/VoiceEpochFenceTests.cs",
        "tests/Fleet.Agent.Tests/Harness/CapabilityMatrix.cs",
        "tests/Fleet.Agent.Tests/Harness/ConversationHarness.cs",
        "tests/Fleet.Agent.Tests/Harness/LoopbackChannelAdapter.cs",
        "tests/Fleet.Agent.Tests/Harness/MatrixCells.cs",
        "tests/Fleet.Agent.Tests/Harness/ProviderFrameReplay.cs",
        "tests/Fleet.Agent.Tests/Harness/ScenarioRunner.cs",
        "tests/Fleet.Agent.Tests/Harness/ScriptedExecutor.cs",
        "tests/Fleet.Agent.Tests/Harness/StandInProcess.cs",
        "tests/Fleet.Agent.Tests/Harness/UtteranceEpochFence.cs",
        "docs/conversation-protocol.md",
        "docs/spikes/real-adapter-capability-matrix.md",
        "docs/spikes/session-continuity-scenarios.md",
        "docs/spikes/voice-feasibility-protocol.md",
        "docs/spikes/results-public-summary.md",
    ];

    private static IEnumerable<string> FixtureFiles() =>
        Directory.EnumerateFiles(RepoPaths.Resolve(FixtureRoot), "*", SearchOption.AllDirectories);

    // ── The sweep ────────────────────────────────────────────────────────────

    private static IReadOnlyList<string> Sweep(string path, string content, bool fixtureScope)
    {
        var findings = new List<string>();
        var lineNumber = 0;

        foreach (var rawLine in content.Split('\n'))
        {
            lineNumber++;
            var line = rawLine.TrimEnd('\r');

            // F1 does NOT apply outside Fixtures/**. The marker is source-scope only, because a
            // JSONL or NDJSON fixture cannot carry a line comment.
            if (!fixtureScope && line.TrimEnd().Contains(LineMarker, StringComparison.Ordinal))
                continue;

            void Check(Regex pattern, string id)
            {
                foreach (Match match in pattern.Matches(line))
                {
                    if (Allowlist.Contains(match.Value, StringComparer.OrdinalIgnoreCase))
                        continue;
                    findings.Add($"{path}:{lineNumber} [{id}] {match.Value}");
                }
            }

            if (fixtureScope)
            {
                Check(AbsolutePathOutsideCorpus, "F1");
                Check(HostnameAnyCase, "F5");
            }
            else
            {
                Check(HostnameLowerOnly, "F5-lower");
            }

            Check(BotTokenShape, "F2");
            Check(BearerShape, "F3");
            Check(DottedQuad, "F4");
        }

        return findings;
    }

    // ── AC18: the sweep over the real files returns nothing ──────────────────

    [Fact]
    public void FixtureScopeIsClean()
    {
        var findings = FixtureFiles()
            .SelectMany(path => Sweep(
                Path.GetRelativePath(RepoPaths.Root, path), File.ReadAllText(path), fixtureScope: true))
            .ToList();

        Assert.Empty(findings);
    }

    [Fact]
    public void SourceScopeIsClean()
    {
        var findings = SourceScope
            .SelectMany(relative => Sweep(
                relative, File.ReadAllText(RepoPaths.Resolve(relative)), fixtureScope: false))
            .ToList();

        Assert.Empty(findings);
    }

    // ── AC13: four regression tests proving the SCOPING, not just the patterns ──

    [Fact]
    public void StandInPathPassesInSourceScopeAndFailsInsideAFixture()
    {
        const string line = "FileName = \"/bin/cat\",";

        Assert.Empty(Sweep("harness.cs", line, fixtureScope: false));
        Assert.NotEmpty(Sweep("fixture.jsonl", line, fixtureScope: true));
    }

    [Fact]
    public void BotTokenShapeFailsInBothScopes()
    {
        // The literal is a deliberate negative control, so the sweep of THIS file exempts it.
        const string line = "\"token\":\"1234567890:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\""; // hygiene-ok: negative control

        Assert.NotEmpty(Sweep("harness.cs", line, fixtureScope: false));
        Assert.NotEmpty(Sweep("fixture.jsonl", line, fixtureScope: true));
    }

    /// <summary>
    /// The case that would have made AC18 unpassable. Asserted against the LIVE csproj, not a
    /// synthetic string, so the rule is proven where it actually has to hold.
    /// </summary>
    [Fact]
    public void MicrosoftNetTestSdkPassesSourceScopeAndFailsFixtureScope()
    {
        const string relative = "tests/Fleet.Agent.Tests/Fleet.Agent.Tests.csproj";
        var content = File.ReadAllText(RepoPaths.Resolve(relative));

        Assert.Contains("Microsoft.NET.Test.Sdk", content, StringComparison.Ordinal);
        Assert.Contains(relative, SourceScope);

        Assert.Empty(Sweep(relative, content, fixtureScope: false));
        Assert.NotEmpty(Sweep(relative, content, fixtureScope: true));
    }

    [Fact]
    public void LowercaseHostnameFailsInBothScopes()
    {
        const string line = "connect to some-host.com now"; // hygiene-ok: negative control

        Assert.NotEmpty(Sweep("harness.cs", line, fixtureScope: false));
        Assert.NotEmpty(Sweep("fixture.jsonl", line, fixtureScope: true));
    }

    // ── Pattern-level guards ─────────────────────────────────────────────────

    [Theory]
    [InlineData("/workspace/example/notes.txt")]
    [InlineData("/workspace/example")]
    [InlineData("/workspace/example/src/deeply/nested/file.cs")]
    public void CorpusPathsArePermittedInsideFixtures(string path) =>
        Assert.Empty(Sweep("fixture.jsonl", $"{{\"path\":\"{path}\"}}", fixtureScope: true));

    [Theory]
    [InlineData("/workspace/examples/notes.txt")]
    [InlineData("/home/someone/secret.txt")]
    [InlineData("/etc/passwd")]
    public void NonCorpusPathsAreRejectedInsideFixtures(string path) =>
        Assert.NotEmpty(Sweep("fixture.jsonl", $"{{\"path\":\"{path}\"}}", fixtureScope: true));

    [Fact]
    public void TheAllowlistIsShort() => Assert.True(
        Allowlist.Count <= 8,
        "An allowlist that grows is a denylist that stopped working. Fix the pattern instead.");

    [Fact]
    public void TheHygieneOkMarkerIsSourceScopeOnly()
    {
        var line = $"var p = \"/etc/passwd\"; {LineMarker} deliberate";

        Assert.Empty(Sweep("harness.cs", line, fixtureScope: false));
        Assert.NotEmpty(Sweep("fixture.jsonl", line, fixtureScope: true));
    }

    // ── The corpus is actually present ───────────────────────────────────────

    /// <summary>
    /// If the csproj glob were not widened to <c>Fixtures\**\*</c>, the per-provider directories
    /// would be absent at runtime and every fixture-backed test would pass vacuously against an
    /// empty corpus. Asserted against the TEST OUTPUT tree, which is where that failure shows up.
    /// </summary>
    [Theory]
    [InlineData(ProviderFrameReplay.ClaudeDirectory, 6)]  // + compact-boundary (#347)
    [InlineData(ProviderFrameReplay.CodexDirectory, 6)]   // + context-compaction (#347)
    [InlineData(ProviderFrameReplay.GeminiDirectory, 4)]
    public void EveryProviderCorpusIsCopiedToTheTestOutput(string provider, int expectedFiles)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", provider);

        Assert.True(Directory.Exists(directory), $"Fixture directory '{provider}' is missing from the test output.");
        Assert.Equal(expectedFiles, Directory.GetFiles(directory).Length);
    }
}
