using System.Text.Json;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;

namespace Fleet.Orchestrator.Tests.Services;

/// <summary>
/// Regression coverage for the generated Claude settings disabling native commit/PR
/// attribution (issue #269).
///
/// These exercise the real production helper
/// <see cref="ContainerProvisioningService.GenerateSettingsJson"/> — the same method that
/// writes <c>.generated/settings.json</c>, mounted read-only at
/// <c>/root/.claude/settings.json</c> — rather than a test-only copy of the shape, so
/// a regression in the generator fails these tests.
/// </summary>
public class ContainerProvisioningSettingsAttributionTests
{
    private static Agent AgentWithTools(string name, params string[] tools) => new()
    {
        Name          = name,
        DisplayName   = name,
        Role          = "test",
        Model         = "test-model",
        ContainerName = $"fleet-{name}",
        Provider      = "claude",
        Tools         = [.. tools.Select(t => new AgentTool { ToolName = t })],
    };

    private static JsonElement Settings(Agent agent, string ctoAgentName = "agent-cto") =>
        JsonDocument.Parse(ContainerProvisioningService.GenerateSettingsJson(agent, ctoAgentName))
            .RootElement.Clone();

    private static List<string> Allow(JsonElement settings) =>
        settings.GetProperty("permissions").GetProperty("allow")
            .EnumerateArray().Select(e => e.GetString()!).ToList();

    // ── attribution block ────────────────────────────────────────────────────

    [Fact]
    public void GenerateSettingsJson_EmitsEmptyCommitAndPrAttribution()
    {
        var attribution = Settings(AgentWithTools("agent-one", "Bash")).GetProperty("attribution");

        Assert.Equal(string.Empty, attribution.GetProperty("commit").GetString());
        Assert.Equal(string.Empty, attribution.GetProperty("pr").GetString());
    }

    [Fact]
    public void GenerateSettingsJson_DisablesSessionUrlAttribution()
    {
        // Session links are a control separate from the commit/PR attribution text in the
        // pinned CLI's settings schema, so an empty commit/pr string alone would not cover
        // the Claude-Session trailer or the PR-body session link. The schema scopes that
        // link to web and Remote Control sessions, which headless agents are not, so this
        // is defensive rather than required — it pins the behaviour regardless.
        var attribution = Settings(AgentWithTools("agent-one", "Bash")).GetProperty("attribution");

        Assert.False(attribution.GetProperty("sessionUrl").GetBoolean());
    }

    [Fact]
    public void GenerateSettingsJson_DoesNotUseDeprecatedIncludeCoAuthoredBy()
    {
        // `includeCoAuthoredBy` is marked deprecated in the pinned CLI's settings schema.
        var settings = Settings(AgentWithTools("agent-one", "Bash"));

        Assert.False(settings.TryGetProperty("includeCoAuthoredBy", out _));
    }

    [Fact]
    public void GenerateSettingsJson_DeserializesWithBothAttributionStringsEmpty()
    {
        // Round-trip through a typed shape: the file the agent container mounts must
        // deserialize cleanly, not merely contain the right substrings.
        var json = ContainerProvisioningService.GenerateSettingsJson(AgentWithTools("agent-one", "Bash"), "agent-cto");

        var parsed = JsonSerializer.Deserialize<SettingsShape>(
            json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(parsed);
        Assert.NotNull(parsed!.Attribution);
        Assert.Equal(string.Empty, parsed.Attribution!.Commit);
        Assert.Equal(string.Empty, parsed.Attribution.Pr);
        Assert.False(parsed.Attribution.SessionUrl);
        Assert.NotNull(parsed.Permissions);
        Assert.Contains("Bash", parsed.Permissions!.Allow);
    }

    // ── permissions are untouched ────────────────────────────────────────────

    [Fact]
    public void GenerateSettingsJson_PermissionContentsAndOrderingUnchanged()
    {
        var agent = AgentWithTools("agent-one", "Write", "Bash", "Read");

        var allow = Allow(Settings(agent));

        // Declared tools plus both auto-grants, sorted ordinal-ignore-case.
        Assert.Equal(
            new[]
            {
                "Bash",
                "mcp__fleet-memory__memory_get",
                "mcp__fleet-temporal__notify_cto",
                "Read",
                "Write",
            },
            allow);
    }

    [Fact]
    public void GenerateSettingsJson_DisabledToolStillExcluded()
    {
        var agent = AgentWithTools("agent-one", "Bash");
        agent.Tools.Add(new AgentTool { ToolName = "Write", IsEnabled = false });

        Assert.DoesNotContain("Write", Allow(Settings(agent)));
    }

    [Fact]
    public void GenerateSettingsJson_CtoAgent_StillHasNoNotifyCtoAndGetsAttribution()
    {
        // The CTO self-loop guard and the attribution block are independent.
        var settings = Settings(AgentWithTools("agent-cto", "Bash"), "agent-cto");

        Assert.DoesNotContain("mcp__fleet-temporal__notify_cto", Allow(settings));
        Assert.Equal(string.Empty, settings.GetProperty("attribution").GetProperty("commit").GetString());
    }

    [Fact]
    public void GenerateSettingsJson_UnsetCtoAgent_SkipsNotifyCtoAndStillGetsAttribution()
    {
        var settings = Settings(AgentWithTools("agent-one", "Bash"), ctoAgentName: "");

        Assert.DoesNotContain("mcp__fleet-temporal__notify_cto", Allow(settings));
        Assert.Equal(string.Empty, settings.GetProperty("attribution").GetProperty("pr").GetString());
    }

    private sealed class SettingsShape
    {
        public PermissionsShape? Permissions { get; set; }
        public AttributionShape? Attribution { get; set; }
    }

    private sealed class PermissionsShape
    {
        public List<string> Allow { get; set; } = [];
    }

    private sealed class AttributionShape
    {
        public string? Commit { get; set; }
        public string? Pr { get; set; }
        public bool SessionUrl { get; set; } = true;
    }
}
