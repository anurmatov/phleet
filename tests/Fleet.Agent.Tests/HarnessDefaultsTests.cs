using Fleet.Shared;

namespace Fleet.Agent.Tests;

/// <summary>
/// #460 Part 1 and 3b: <see cref="HarnessDefaults.Build"/> returns exactly the per-mode table values
/// (AC-U1), the clamp rule (AC-U2), the <c>--tools</c> kept set and name rule (AC-U4), the env
/// additions (AC-U5), and merges <c>harnessExtra</c> by the precedence rules (AC-X2). The snapshot
/// cases list every entry, so any added or removed default fails here.
/// </summary>
public sealed class HarnessDefaultsTests
{
    private const string Url = "http://inference-host:11434";
    private static readonly string[] SixFileTools = ["Read", "Grep", "Glob", "Write", "Edit", "Bash"];

    private static HarnessPlan Codex(bool local, int? window = null, string? extra = null) =>
        HarnessDefaults.Build(new HarnessInput
        {
            Provider = "codex", Local = local, ContextWindow = window, HarnessExtra = extra,
            AllowedTools = ["mcp__fleet-memory__memory_get"],
        });

    private static HarnessPlan Claude(bool local, IReadOnlyList<string>? tools = null, int? window = null,
        string? extra = null, string? effort = null) =>
        HarnessDefaults.Build(new HarnessInput
        {
            Provider = "claude", Local = local, ContextWindow = window, HarnessExtra = extra,
            AllowedTools = tools ?? SixFileTools,
            LocalBaseUrl = local ? Url : null, Model = local ? "qwen3.8:27b" : "claude-opus-5-5", Effort = effort,
        });

    private static (string, object)[] Thread(HarnessPlan plan) =>
        plan.CodexThreadConfig!.Select(s => (s.Name, s.Value)).ToArray();

    private const string RootToml =
        "check_for_update_on_startup = false\n\n[analytics]\nenabled = false\n\n[history]\npersistence = \"none\"\n";

    // ── AC-U1: the table values, per harness and mode ─────────────────────────────────────────────

    [Fact]
    public void CodexCloud_IsExactlyTheTable()
    {
        var plan = Codex(local: false, window: 131_072);

        Assert.Equal(("codex", "cloud"), (plan.Provider, plan.Mode));
        Assert.Equal(RootToml, plan.CodexRootToml);
        Assert.Equal(
            [("features.apps", false), ("features.remote_plugin", false), ("features.skill_mcp_dependency_install", false)],
            Thread(plan));
        Assert.Null(plan.ClaudeArgs);
        Assert.Null(plan.ClaudeEnv);
        Assert.Equal((HarnessDefaults.ExtraNone, null), (plan.ExtraState, plan.ExtraReason));
    }

    [Fact]
    public void CodexLocal_IsExactlyTheTable()
    {
        var plan = Codex(local: true, window: 131_072);

        Assert.Equal("local", plan.Mode);
        Assert.Equal(RootToml, plan.CodexRootToml);
        Assert.Equal(
        [
            ("web_search", "disabled"), ("tools.view_image", false), ("model_reasoning_summary", "none"),
            ("tool_output_token_limit", 8192), ("features.apps", false), ("features.remote_plugin", false),
            ("features.skill_mcp_dependency_install", false),
        ], Thread(plan));
    }

    [Fact]
    public void CodexHosted_IsCloud()
    {
        // A hosted provider (zai/) has no local server URL, so the caller passes Local = false.
        Assert.Equal(Codex(local: false).PlanSha256, Codex(local: false, window: 65_536).PlanSha256);
        Assert.Equal("cloud", Codex(local: false).Mode);
    }

    [Fact]
    public void ClaudeCloud_IsExactlyTheTable()
    {
        var plan = Claude(local: false, window: 131_072);

        Assert.Equal(("claude", "cloud"), (plan.Provider, plan.Mode));
        Assert.Null(plan.CodexRootToml);
        Assert.Null(plan.CodexThreadConfig);
        Assert.Equal(
        [
            "--tools", "Agent,Task,ToolSearch,Skill,TodoWrite,WebFetch,WebSearch,Bash,Edit,Glob,Grep,Read,Write",
            "--exclude-dynamic-system-prompt-sections", "--strict-mcp-config",
        ], plan.ClaudeArgs);
        Assert.Equal([new("CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC", "1")], plan.ClaudeEnv);
    }

    [Fact]
    public void ClaudeLocal_IsExactlyTheTable()
    {
        var plan = Claude(local: true, window: 131_072, effort: "off");

        Assert.Equal("local", plan.Mode);
        Assert.Equal(
        [
            "--tools", "Agent,Task,ToolSearch,Skill,TodoWrite,Bash,Edit,Glob,Grep,Read,Write",
            "--disallowedTools", "WebSearch",
            "--exclude-dynamic-system-prompt-sections", "--strict-mcp-config",
        ], plan.ClaudeArgs);
    }

    // ── AC-U2: the clamp, and absence when the window is unset ─────────────────────────────────────

    [Theory]
    [InlineData(32_768, 4_000)]
    [InlineData(131_072, 8_192)]
    [InlineData(1_048_576, 25_000)]
    public void OutputLimits_Clamp(int window, int expected)
    {
        Assert.Contains(("tool_output_token_limit", (object)expected), Thread(Codex(local: true, window: window)));
        Assert.Contains(new KeyValuePair<string, string>("MAX_MCP_OUTPUT_TOKENS", expected.ToString()),
            Claude(local: true, window: window).ClaudeEnv!);
    }

    [Fact]
    public void OutputLimits_AreAbsentWhenTheWindowIsUnset()
    {
        Assert.DoesNotContain(Thread(Codex(local: true)), s => s.Item1 == "tool_output_token_limit");
        Assert.DoesNotContain(Claude(local: true).ClaudeEnv!, e => e.Key == "MAX_MCP_OUTPUT_TOKENS");
        Assert.Null(HarnessDefaults.OutputTokenLimit(null));
    }

    // ── AC-U4: --tools ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Tools_BaseNames_NoMcp_Deduplicated()
    {
        var plan = Claude(local: true, tools: ["Bash(git *)", "Bash(npm test)", "Read", "mcp__fleet-memory__*", "Read", "Agent"]);

        Assert.Equal("Agent,Task,ToolSearch,Skill,TodoWrite,Bash,Read", plan.ClaudeArgs![1]);
    }

    [Theory]
    [InlineData]
    [InlineData("mcp__fleet-memory__memory_search", "mcp__fleet-temporal__notify_cto")]
    public void Tools_AnAllowListWithNoBuiltIn_OmitsTheFlag(params string[] tools)
    {
        var plan = Claude(local: false, tools: tools);

        Assert.DoesNotContain("--tools", plan.ClaudeArgs!);
        Assert.Equal(["--exclude-dynamic-system-prompt-sections", "--strict-mcp-config"], plan.ClaudeArgs);
    }

    [Fact]
    public void Tools_EveryKeptAndAllowedNameIsPresent_InEitherMode()
    {
        foreach (var local in new[] { false, true })
        {
            var names = Claude(local, SixFileTools).ClaudeArgs![1].Split(',');
            Assert.Superset(HarnessDefaults.KeptTools.ToHashSet(), names.ToHashSet());
            Assert.Superset(SixFileTools.ToHashSet(), names.ToHashSet());
            Assert.Equal(!local, names.Contains("WebFetch") && names.Contains("WebSearch"));
        }
    }

    [Fact]
    public void Tools_InputOrderDoesNotChangeThePlan()
    {
        Assert.Equal(Claude(false, ["Read", "Bash"]).PlanSha256, Claude(false, ["Bash", "Read"]).PlanSha256);
    }

    // ── AC-U5: env ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LocalEnv_KeepsEveryCurrentEntry_ThenAddsTheTwoDefaults()
    {
        var current = ClaudeLocalModel.BuildEnvironment(Url, "qwen3.8:27b", "off");

        var env = Claude(local: true, window: 131_072, effort: "off").ClaudeEnv!;

        Assert.Equal(
            [.. current, new("CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC", "1"), new("MAX_MCP_OUTPUT_TOKENS", "8192")],
            env);
    }

    // ── AC-X2: precedence ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CodexExtra_ReplacesTheDefault_AndLandsInTheRightPart()
    {
        var plan = Codex(local: true, window: 131_072,
            extra: "tool_output_token_limit = 12000\nhistory.persistence = \"save-all\"\n# note\n\nweb_search = \"disabled\"");

        Assert.Contains(("tool_output_token_limit", (object)12_000), Thread(plan));
        Assert.Contains("[history]\npersistence = \"save-all\"\n", plan.CodexRootToml);
        Assert.Equal(["tool_output_token_limit", "history.persistence", "web_search"], plan.Replaced);
        Assert.Equal(HarnessDefaults.ExtraApplied, plan.ExtraState);
    }

    [Fact]
    public void CodexExtra_ANewKeyIsAdded()
    {
        var plan = Codex(local: false, extra: "web_search = \"disabled\"\nmodel_reasoning_summary = \"concise\"");

        Assert.Equal(
        [
            ("features.apps", false), ("features.remote_plugin", false), ("features.skill_mcp_dependency_install", false),
            ("web_search", "disabled"), ("model_reasoning_summary", "concise"),
        ], Thread(plan));
        Assert.Empty(plan.Replaced);
    }

    [Fact]
    public void RootToml_KeepsRootKeysBeforeAnyTable()
    {
        var plan = Codex(local: false, extra: "analytics.enabled = true\ncheck_for_update_on_startup = true");

        Assert.Equal(
            "check_for_update_on_startup = true\n\n[analytics]\nenabled = true\n\n[history]\npersistence = \"none\"\n",
            plan.CodexRootToml);
    }

    [Fact]
    public void ClaudeExtra_EnvReplacesTheDefault_AndANewNameIsAdded()
    {
        var local = Claude(local: true, window: 131_072, extra: "MAX_MCP_OUTPUT_TOKENS=12000\nCLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=0");
        Assert.Contains(new KeyValuePair<string, string>("MAX_MCP_OUTPUT_TOKENS", "12000"), local.ClaudeEnv!);
        Assert.Contains(new KeyValuePair<string, string>("CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC", "0"), local.ClaudeEnv!);
        Assert.Equal(["MAX_MCP_OUTPUT_TOKENS", "CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"], local.Replaced);

        var cloud = Claude(local: false, extra: "MAX_MCP_OUTPUT_TOKENS=12000");
        Assert.Equal([new("CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC", "1"), new("MAX_MCP_OUTPUT_TOKENS", "12000")], cloud.ClaudeEnv);
        Assert.Empty(cloud.Replaced);
    }

    [Fact]
    public void ClaudeExtra_CloudMayDisableWebSearch_AndRemoveTheDynamicSectionsFlag()
    {
        var plan = Claude(local: false, extra: "--disallowedTools WebSearch\n!--exclude-dynamic-system-prompt-sections");

        Assert.Equal(
        [
            "--tools", "Agent,Task,ToolSearch,Skill,TodoWrite,WebFetch,WebSearch,Bash,Edit,Glob,Grep,Read,Write",
            "--disallowedTools", "WebSearch", "--strict-mcp-config",
        ], plan.ClaudeArgs);
        Assert.Equal(["--exclude-dynamic-system-prompt-sections"], plan.Replaced);
    }

    [Fact]
    public void ClaudeExtra_ToolsOnlyAdds_AndNeverDropsAKeptOrAllowedName()
    {
        var plan = Claude(local: true, tools: ["Read"], extra: "--tools NotebookEdit,Read,Agent");

        Assert.Equal("Agent,Task,ToolSearch,Skill,TodoWrite,Read,NotebookEdit", plan.ClaudeArgs![1]);
        Assert.Equal(["--tools"], plan.Replaced);
    }

    [Fact]
    public void ClaudeExtra_ToolsOnAnAllToolsAgent_IsANoOp_SoAllBuiltInsStay()
    {
        var withExtra = Claude(local: false, tools: ["mcp__fleet-memory__memory_search"], extra: "--tools Edit");
        var without = Claude(local: false, tools: ["mcp__fleet-memory__memory_search"]);

        Assert.DoesNotContain("--tools", withExtra.ClaudeArgs!);
        Assert.Equal(without.ClaudeArgs, withExtra.ClaudeArgs);
        Assert.Empty(withExtra.Replaced);
    }

    // ── AC-X4 (helper part): a stored value the validator refuses is skipped as a whole ─────────────

    [Fact]
    public void AnInvalidExtra_IsIgnoredAsAWhole_AndTheDefaultsApply()
    {
        var plan = Codex(local: true, window: 131_072, extra: "tool_output_token_limit = 12000\nnotify = [\"x\"]");

        Assert.Equal(HarnessDefaults.ExtraIgnored, plan.ExtraState);
        Assert.StartsWith("harnessExtra line 2:", plan.ExtraReason);
        Assert.Equal(Codex(local: true, window: 131_072).CodexThreadConfig, plan.CodexThreadConfig);
        Assert.Empty(plan.Replaced);
    }

    // ── sha, masking, startup line ─────────────────────────────────────────────────────────────────

    [Fact]
    public void PlanSha_IsStable_AndChangesWithAnEffectiveSetting()
    {
        var a = Codex(local: true, window: 131_072);
        Assert.Equal(a.PlanSha256, Codex(local: true, window: 131_072).PlanSha256);
        Assert.Matches("^[0-9a-f]{64}$", a.PlanSha256);
        Assert.NotEqual(a.PlanSha256, Codex(local: true, window: 262_144).PlanSha256);
        Assert.NotEqual(a.PlanSha256, Codex(local: true, window: 131_072, extra: "history.persistence = \"save-all\"").PlanSha256);
        Assert.Equal($"harness: mode=local plan={a.PlanSha256} extra=none", a.StartupLine);
    }

    [Fact]
    public void CredentialEnvValues_AreMaskedForDisplay()
    {
        var env = Claude(local: true).ClaudeEnv!;

        Assert.All(env.Where(e => e.Key is "ANTHROPIC_API_KEY" or "ANTHROPIC_AUTH_TOKEN"),
            e => Assert.Equal("***", HarnessDefaults.DisplayValue(e.Key, e.Value)));
        Assert.Equal(Url, HarnessDefaults.DisplayValue("ANTHROPIC_BASE_URL", Url));
    }

    [Theory]
    [InlineData("pi")]
    [InlineData("gemini")]
    public void OtherProviders_AreRefused(string provider)
    {
        Assert.Throws<ArgumentException>(() => HarnessDefaults.Build(new HarnessInput { Provider = provider, Local = false }));
    }

    [Fact]
    public void TheCodexRootTomlPath_IsPinned()
    {
        Assert.Equal("/app/codex-root.toml", HarnessDefaults.CodexRootTomlContainerPath);
    }
}
