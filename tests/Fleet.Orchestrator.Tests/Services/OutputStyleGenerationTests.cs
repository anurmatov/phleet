using System.Text.Json;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;

namespace Fleet.Orchestrator.Tests.Services;

/// <summary>
/// What provisioning emits for an agent with an output style, and — mostly — what it emits for one
/// without (#314).
/// </summary>
/// <remarks>
/// <para>
/// The column is a per-agent rollout switch, and it is only that if a styleless agent is generated
/// byte for byte as it was before styles existed. So the null case is asserted as bytes here, not
/// as behaviour: an <c>outputStyle</c> key that serialized as <c>null</c>, or a stray
/// <c>OutputStyleBody</c>, would each be a silent change to every agent in the fleet.
/// </para>
/// <para>
/// The other half is provider parity. Cloud claude resolves the style as a file; codex, gemini and
/// local-model claude (#365) get the identical text inlined. A style that reached one provider and
/// vanished for the others is the
/// failure this must not introduce, so both directions are pinned.
/// </para>
/// </remarks>
public class OutputStyleGenerationTests
{
    private const string StyleBody =
        "---\nname: fleet-messaging\ndescription: Chat register.\nkeep-coding-instructions: true\n---\n\n"
        + "# Messaging register\n\nLowercase and short.\n";

    private static OutputStyle Style(string name = "fleet-messaging") =>
        new() { Name = name, Body = StyleBody, Description = "Chat register." };

    private static Agent AgentWith(string provider, string? outputStyle)
    {
        var agent = new Agent
        {
            Name = "agent1",
            DisplayName = "Agent One",
            Role = "developer",
            Model = "model-x",
            ContainerName = "ctr-agent1",
            Provider = provider,
            OutputStyle = outputStyle,
        };
        agent.Tools.Add(new AgentTool { ToolName = "Read", IsEnabled = true });
        return agent;
    }

    // ── the null case: bytes, not behaviour ──────────────────────────────────

    /// <summary>
    /// The golden assertion for constraint 1: with no style, both generated documents are exactly
    /// what they were before styles existed.
    /// </summary>
    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("gemini")]
    public void NoStyle_GeneratesByteIdenticalConfig(string provider)
    {
        var agent = AgentWith(provider, outputStyle: null);

        var settings = ContainerProvisioningService.GenerateSettingsJson(agent, "acto", style: null);
        var appsettings = ContainerProvisioningService.GenerateAppsettingsJson(agent, "acto", style: null);

        // Byte-for-byte against the same call made without the parameter at all — the shape a
        // pre-#314 orchestrator produced.
        Assert.Equal(ContainerProvisioningService.GenerateSettingsJson(agent, "acto"), settings);
        Assert.Equal(ContainerProvisioningService.GenerateAppsettingsJson(agent, "acto"), appsettings);

        // And the keys are ABSENT, not null — a null would resolve to nothing useful and would
        // still have changed the bytes.
        Assert.DoesNotContain("outputStyle", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("OutputStyleBody", appsettings, StringComparison.Ordinal);
    }

    /// <summary>A styleless agent gets no mount, so its container config is untouched too.</summary>
    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    public void NoStyle_AddsNoOutputStyleMount(string provider)
    {
        var binds = ContainerProvisioningService.BuildBinds(AgentWith(provider, null), "/fleet");

        Assert.DoesNotContain(binds, b => b.Contains("output-styles", StringComparison.Ordinal));
    }

    // ── claude: style file + settings key ────────────────────────────────────

    [Fact]
    public void ClaudeAgentWithStyle_NamesItInSettingsJson()
    {
        var settings = ContainerProvisioningService.GenerateSettingsJson(
            AgentWith("claude", "fleet-messaging"), "acto", Style());

        using var doc = JsonDocument.Parse(settings);
        Assert.Equal("fleet-messaging", doc.RootElement.GetProperty("outputStyle").GetString());
        // The permissions block is still there — the style is additive, not a replacement.
        Assert.True(doc.RootElement.TryGetProperty("permissions", out _));
    }

    /// <summary>
    /// The project-level directory beside the user-level settings.json, which is the combination
    /// that was verified on the pinned CLI.
    /// </summary>
    [Fact]
    public void ClaudeAgentWithStyle_MountsTheProjectLevelStyleDirectory()
    {
        var binds = ContainerProvisioningService.BuildBinds(AgentWith("claude", "fleet-messaging"), "/fleet");

        Assert.Contains(
            "./workspaces/ctr-agent1/.generated/output-styles:/workspace/.claude/output-styles:ro",
            binds);
    }

    /// <summary>Claude reads the file; inlining the same text would assert the rules twice.</summary>
    [Fact]
    public void ClaudeAgentWithStyle_DoesNotAlsoInlineTheBody()
    {
        var appsettings = ContainerProvisioningService.GenerateAppsettingsJson(
            AgentWith("claude", "fleet-messaging"), "acto", Style());

        Assert.DoesNotContain("OutputStyleBody", appsettings, StringComparison.Ordinal);
    }

    // ── codex and gemini: inlined, no file ───────────────────────────────────

    [Theory]
    [InlineData("codex")]
    [InlineData("gemini")]
    public void NonClaudeAgentWithStyle_GetsTheBodyInlined(string provider)
    {
        var appsettings = ContainerProvisioningService.GenerateAppsettingsJson(
            AgentWith(provider, "fleet-messaging"), "acto", Style());

        using var doc = JsonDocument.Parse(appsettings);
        var body = doc.RootElement.GetProperty("Agent").GetProperty("OutputStyleBody").GetString();

        Assert.NotNull(body);
        Assert.Contains("Lowercase and short.", body, StringComparison.Ordinal);
        // Frontmatter is Claude Code's file format, not text for a model to obey.
        Assert.DoesNotContain("keep-coding-instructions", body, StringComparison.Ordinal);
        Assert.StartsWith("# Messaging register", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("gemini")]
    public void NonClaudeAgentWithStyle_GetsNoStyleFileOrSettingsKey(string provider)
    {
        var agent = AgentWith(provider, "fleet-messaging");

        var settings = ContainerProvisioningService.GenerateSettingsJson(agent, "acto", Style());
        var binds = ContainerProvisioningService.BuildBinds(agent, "/fleet");

        Assert.DoesNotContain("outputStyle", settings, StringComparison.Ordinal);
        Assert.DoesNotContain(binds, b => b.Contains("output-styles", StringComparison.Ordinal));
        Assert.False(ContainerProvisioningService.HasStyleFile(agent));
    }

    /// <summary>Both providers render from one row, so they receive the same text.</summary>
    [Fact]
    public void CodexAndGemini_ReceiveTheSameTextFromTheSameRow()
    {
        var style = Style();

        static string BodyOf(string json) =>
            JsonDocument.Parse(json).RootElement.GetProperty("Agent").GetProperty("OutputStyleBody").GetString()!;

        var codex = BodyOf(ContainerProvisioningService.GenerateAppsettingsJson(AgentWith("codex", style.Name), "acto", style));
        var gemini = BodyOf(ContainerProvisioningService.GenerateAppsettingsJson(AgentWith("gemini", style.Name), "acto", style));

        Assert.Equal(codex, gemini);
    }

    // ── claude in local-model mode: inlined, no file (#365) ──────────────────

    private const string LocalUrl = "http://inference-host:11434";

    private static Agent LocalClaudeAgentWith(string? outputStyle)
    {
        var agent = AgentWith("claude", outputStyle);
        agent.Model = "qwen3.8:27b-agent";
        agent.AnthropicBaseUrl = LocalUrl;
        return agent;
    }

    /// <summary>
    /// AC1. Claude Code re-asserts an active style with a new system-role message every turn. A
    /// local server that folds every system message into the top block then changes the prompt
    /// prefix on every turn and never reuses its KV cache, so local mode takes the codex/gemini path.
    /// </summary>
    [Fact]
    public void LocalClaudeAgentWithStyle_GetsNoSettingsKeyFileOrMount()
    {
        var agent = LocalClaudeAgentWith("fleet-messaging");

        var settings = ContainerProvisioningService.GenerateSettingsJson(agent, "acto", Style());
        var binds = ContainerProvisioningService.BuildBinds(agent, "/fleet");

        Assert.DoesNotContain("outputStyle", settings, StringComparison.Ordinal);
        Assert.DoesNotContain(binds, b => b.Contains("output-styles", StringComparison.Ordinal));
        Assert.False(ContainerProvisioningService.HasStyleFile(agent));
    }

    /// <summary>AC1. The text is not dropped: it is the same text codex and gemini receive.</summary>
    [Fact]
    public void LocalClaudeAgentWithStyle_GetsTheBodyInlined()
    {
        var style = Style();

        var appsettings = ContainerProvisioningService.GenerateAppsettingsJson(
            LocalClaudeAgentWith(style.Name), "acto", style);
        var codex = ContainerProvisioningService.GenerateAppsettingsJson(AgentWith("codex", style.Name), "acto", style);

        static string BodyOf(string json) =>
            JsonDocument.Parse(json).RootElement.GetProperty("Agent").GetProperty("OutputStyleBody").GetString()!;

        Assert.Equal(OutputStyleRenderer.ForPrompt(style), BodyOf(appsettings));
        Assert.Equal(BodyOf(codex), BodyOf(appsettings));
    }

    [Fact]
    public void LocalClaudeAgentWithoutStyle_GetsNoStyleAnywhere()
    {
        var agent = LocalClaudeAgentWith(outputStyle: null);

        Assert.DoesNotContain("outputStyle",
            ContainerProvisioningService.GenerateSettingsJson(agent, "acto"), StringComparison.Ordinal);
        Assert.DoesNotContain("OutputStyleBody",
            ContainerProvisioningService.GenerateAppsettingsJson(agent, "acto"), StringComparison.Ordinal);
    }

    /// <summary>
    /// AC2. A cloud claude agent is untouched: settings.json bytes captured from main at 479fb58,
    /// and an appsettings.json equal to the styleless one, so the prompt the agent assembles from it
    /// is byte-identical to the one it assembles today.
    /// </summary>
    [Fact]
    public void CloudClaudeAgentWithStyle_ConfigIsUnchanged()
    {
        var agent = AgentWith("claude", "fleet-messaging");

        var settings = ContainerProvisioningService.GenerateSettingsJson(agent, "acto", Style());
        var appsettings = ContainerProvisioningService.GenerateAppsettingsJson(agent, "acto", Style());

        Assert.Equal(CloudClaudeSettingsOnMain, settings);
        Assert.Equal(
            ContainerProvisioningService.GenerateAppsettingsJson(AgentWith("claude", null), "acto"),
            appsettings);
    }

    private const string CloudClaudeSettingsOnMain =
        "{\n"
        + "  \"permissions\": {\n"
        + "    \"allow\": [\n"
        + "      \"mcp__fleet-memory__memory_get\",\n"
        + "      \"mcp__fleet-temporal__notify_cto\",\n"
        + "      \"Read\"\n"
        + "    ]\n"
        + "  },\n"
        + "  \"outputStyle\": \"fleet-messaging\"\n"
        + "}";

    // ── the dangling reference ───────────────────────────────────────────────

    /// <summary>
    /// A name with no row would leave the agent silently on <c>default</c> — indistinguishable from
    /// the style not working. Both generators refuse rather than emit it.
    /// </summary>
    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("gemini")]
    public void StyleNameWithNoRow_RefusesToGenerate(string provider)
    {
        var agent = AgentWith(provider, "does-not-exist");

        var settingsEx = Assert.Throws<InvalidOperationException>(
            () => ContainerProvisioningService.GenerateSettingsJson(agent, "acto", style: null));
        var appsettingsEx = Assert.Throws<InvalidOperationException>(
            () => ContainerProvisioningService.GenerateAppsettingsJson(agent, "acto", style: null));

        Assert.Contains("does-not-exist", settingsEx.Message, StringComparison.Ordinal);
        Assert.Contains("does-not-exist", appsettingsEx.Message, StringComparison.Ordinal);
    }

    // ── the renderer ─────────────────────────────────────────────────────────

    [Fact]
    public void StyleFileKeepsFrontmatterVerbatim()
    {
        // Claude Code reads name, description and keep-coding-instructions out of it.
        Assert.Equal(StyleBody, OutputStyleRenderer.ForStyleFile(Style()));
    }

    [Fact]
    public void BodyWithNoFrontmatter_IsInlinedWhole()
    {
        var style = new OutputStyle { Name = "plain", Body = "# Just a body\n\ntext\n" };

        Assert.Equal("# Just a body\n\ntext", OutputStyleRenderer.ForPrompt(style));
    }

    /// <summary>A horizontal rule mid-body must not be mistaken for the closing delimiter.</summary>
    [Fact]
    public void HorizontalRuleInsideTheBody_DoesNotTruncateIt()
    {
        var style = new OutputStyle
        {
            Name = "ruled",
            Body = "---\nname: ruled\n---\n\nfirst\n\n---\n\nsecond\n",
        };

        var rendered = OutputStyleRenderer.ForPrompt(style);

        Assert.Contains("first", rendered, StringComparison.Ordinal);
        Assert.Contains("second", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// Unterminated frontmatter returns the body untouched. Swallowing the whole style because its
    /// author forgot a delimiter would be a silent loss of every rule in it.
    /// </summary>
    [Fact]
    public void UnterminatedFrontmatter_ReturnsTheBodyUntouched()
    {
        var style = new OutputStyle { Name = "broken", Body = "---\nname: broken\n\nstill here\n" };

        Assert.Contains("still here", OutputStyleRenderer.ForPrompt(style), StringComparison.Ordinal);
    }

    [Fact]
    public void ReadDescription_TakesItFromTheFrontmatter()
    {
        Assert.Equal("Chat register.", OutputStyleRenderer.ReadDescription(StyleBody));
        Assert.Null(OutputStyleRenderer.ReadDescription("# no frontmatter\n"));
    }
}
