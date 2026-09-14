using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Tools;

namespace Fleet.Orchestrator.Tests.Tools;

/// <summary>
/// Regression tests for PreviewAgentProvisionTool.Render.
///
/// The desired spec carries RESOLVED env-ref values (tokens, base64 private keys, credentials in
/// URLs). The rendered preview is the return value of an MCP tool, which lands in the calling
/// agent's transcript and anything downstream of it — so no environment value may appear anywhere
/// in the rendered string, on any branch.
///
/// Every assertion here checks the WHOLE rendered output rather than a single section, so a value
/// leaking through the actual-container branch or a diff line fails these tests too.
/// </summary>
public sealed class PreviewAgentProvisionRenderTests
{
    // ── Canary fixtures ───────────────────────────────────────────────────────
    // Obviously synthetic literals covering the secret shapes that actually occur in a desired
    // spec. Every value is prefixed CANARY so it can never collide with a key name or with
    // markdown emitted by the renderer.

    private const string TokenEntry    = "EXAMPLE_TOKEN=CANARY-9f3a1c7d-token";
    private const string PemEntry      = "EXAMPLE_PEM=CANARY-LS0tLS1CRUdJTg==\nCANARYLINE2";
    private const string EmptyEntry    = "EXAMPLE_EMPTY=";
    private const string EqualsEntry   = "EXAMPLE_EQUALS=CANARY=embedded=equals";
    private const string UrlEntry      = "EXAMPLE_URL=https://user:CANARYPASS@example.invalid/path";
    private const string BareEntry     = "EXAMPLE_BARE";
    private const string ExtraEntry    = "EXAMPLE_EXTRA=CANARY-extra-only-on-the-container";

    private static List<string> DesiredEnv() =>
        [TokenEntry, PemEntry, EmptyEntry, EqualsEntry, UrlEntry, BareEntry];

    // Deliberately differs from the desired set so ComputeDiff emits real "env missing" /
    // "env extra" lines — otherwise the diff branch would never be exercised.
    private static List<string> ActualEnv() =>
        [PemEntry, EmptyEntry, EqualsEntry, UrlEntry, BareEntry, ExtraEntry];

    private static List<string> AllCanaryEntries() =>
        [.. DesiredEnv(), ExtraEntry];

    private static readonly string[] ExpectedKeys =
    [
        "EXAMPLE_BARE",
        "EXAMPLE_EMPTY",
        "EXAMPLE_EQUALS",
        "EXAMPLE_PEM",
        "EXAMPLE_TOKEN",
        "EXAMPLE_URL",
    ];

    /// <summary>
    /// Asserts that no canary env entry — and no canary value — survives into the rendered output.
    /// For each entry the whole "KEY=value" string must be absent (this is what catches the empty
    /// value, which has no substring of its own), and any non-empty value must be absent too.
    /// </summary>
    private static void AssertNoCanaryValues(string rendered)
    {
        foreach (var entry in AllCanaryEntries())
        {
            var idx = entry.IndexOf('=');
            if (idx < 0) continue; // bare key — no value to leak

            Assert.DoesNotContain(entry, rendered, StringComparison.Ordinal);

            var value = entry[(idx + 1)..];
            if (value.Length > 0)
                Assert.DoesNotContain(value, rendered, StringComparison.Ordinal);
        }
    }

    private static void AssertAllKeysPresent(string rendered)
    {
        foreach (var key in ExpectedKeys)
            Assert.Contains($"  - {key}{Environment.NewLine}", rendered, StringComparison.Ordinal);
    }

    // ── Builders ──────────────────────────────────────────────────────────────

    private static ContainerSpec Spec(List<string> env) =>
        new("example/image:tag", 4L << 30, env, ["/host/path:/container/path:ro"], ["example-net"]);

    private static ProvisionPreview PreviewWithActual() =>
        new("example-agent", "example-container", "example-container",
            Spec(DesiredEnv()),
            Spec(ActualEnv()),
            // Uses the production diff so a value leaking through a diff line is covered here too.
            ContainerProvisioningService.ComputeDiff(Spec(DesiredEnv()), Spec(ActualEnv())));

    private static ProvisionPreview PreviewWithoutActual() =>
        new("example-agent", "example-container", "example-container",
            Spec(DesiredEnv()),
            null,
            ["container not found — not running"]);

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Render_DesiredEnv_ShowsKeysAndNoValues()
    {
        var rendered = PreviewAgentProvisionTool.Render(PreviewWithoutActual());

        AssertAllKeysPresent(rendered);
        AssertNoCanaryValues(rendered);
    }

    [Fact]
    public void Render_DesiredEnv_IsLabelledLikeTheActualSection()
    {
        var rendered = PreviewAgentProvisionTool.Render(PreviewWithActual());

        // Both sections use the same label, so the two key lists read as comparable.
        Assert.DoesNotContain($"- Env:{Environment.NewLine}", rendered, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(rendered, $"- Env keys:{Environment.NewLine}"));
    }

    [Fact]
    public void Render_DesiredEnv_KeysAreSorted()
    {
        var rendered = PreviewAgentProvisionTool.Render(PreviewWithoutActual());

        var desiredSection = rendered[rendered.IndexOf("### Desired Spec", StringComparison.Ordinal)..];
        var indices = ExpectedKeys
            .Select(k => desiredSection.IndexOf($"  - {k}{Environment.NewLine}", StringComparison.Ordinal))
            .ToList();

        Assert.DoesNotContain(-1, indices);
        Assert.Equal(indices.Order().ToList(), indices);
    }

    [Fact]
    public void Render_WithActualContainer_LeaksNoValueFromEitherSideOrTheDiff()
    {
        var rendered = PreviewAgentProvisionTool.Render(PreviewWithActual());

        // The diff branch really ran — otherwise "no value leaked from the diff" is vacuous.
        Assert.Contains("env missing: 'EXAMPLE_TOKEN'", rendered, StringComparison.Ordinal);
        Assert.Contains("env extra: 'EXAMPLE_EXTRA'", rendered, StringComparison.Ordinal);

        AssertAllKeysPresent(rendered);
        AssertNoCanaryValues(rendered);
    }

    [Fact]
    public void Render_ContainerNotFound_LeaksNoValue()
    {
        var rendered = PreviewAgentProvisionTool.Render(PreviewWithoutActual());

        Assert.Contains("Container not found or not running.", rendered, StringComparison.Ordinal);
        AssertNoCanaryValues(rendered);
    }

    [Fact]
    public void Render_NotFoundPreview_DoesNotThrowAndEmitsNoValue()
    {
        var rendered = PreviewAgentProvisionTool.Render(ProvisionPreview.NotFound("example-agent"));

        Assert.Contains("agent 'example-agent' not found in DB", rendered, StringComparison.Ordinal);
        AssertNoCanaryValues(rendered);
    }

    [Fact]
    public void Render_PreservesNonEnvSections()
    {
        var rendered = PreviewAgentProvisionTool.Render(PreviewWithActual());

        // Image, memory, networks and binds are unchanged by the env redaction.
        Assert.Contains("- Image: example/image:tag", rendered, StringComparison.Ordinal);
        Assert.Contains("- Memory: 4GB", rendered, StringComparison.Ordinal);
        Assert.Contains("- Networks: example-net", rendered, StringComparison.Ordinal);
        Assert.Contains("  - /host/path:/container/path:ro", rendered, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}
