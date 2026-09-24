using System.Text.Json;
using System.Text.Json.Nodes;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fleet.Orchestrator.Tests.Services;

/// <summary>
/// Provisioning an agent with card assignments (#347 D3/D6/D8, AC 6/7/13): everything keys on the
/// EFFECTIVE mode, decided once per provision, and runs through the real
/// <see cref="ContainerProvisioningService.ProvisionAsync"/>.
/// </summary>
public class ProjectContextProvisioningTests
{
    private const string AgentName = ProjectContextGoldenTests.AgentName;
    private const string Container = ProjectContextGoldenTests.ContainerName;
    private const string Grant = "mcp__fleet-context__get_project_context";

    private const string ExactFooter =
        "[project card: project-a · card v1 · written for full v2 · full is v2]\n" +
        "The full project-a context is attached to turns routed to this project.\n" +
        "On any other turn that needs it, call get_project_context with name \"project-a\".\n";

    private static async Task<(ProvisioningHarness Harness, ProvisionResult Result)> ProvisionScenarioAsync(
        string provider, string projectAMode, Action<OrchestratorDbContext>? extra = null,
        IReadOnlyDictionary<string, string?>? config = null)
    {
        var harness = ProvisioningHarness.Create(config);
        await harness.SeedAsync(db =>
        {
            ProjectContextGoldenTests.SeedScenario(db, provider, projectAMode);
            extra?.Invoke(db);
        });
        var result = await harness.Service.ProvisionAsync(AgentName);
        return (harness, result);
    }

    private static string Read(ProvisioningHarness h, params string[] parts) =>
        File.ReadAllText(Path.Combine([h.GeneratedDir(Container), .. parts]));

    private static bool Exists(ProvisioningHarness h, params string[] parts) =>
        File.Exists(Path.Combine([h.GeneratedDir(Container), .. parts]));

    private static JsonElement Json(ProvisioningHarness h, string file) =>
        JsonDocument.Parse(Read(h, file)).RootElement.Clone();

    private static List<string> Strings(JsonElement array) =>
        array.EnumerateArray().Select(e => e.GetString()!).ToList();

    // ── AC 6: an effective card agent ─────────────────────────────────────────

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("gemini")]
    public async Task EffectiveCard_WritesCardWithExactFooter_AndFullMdByteForByte(string provider)
    {
        var (h, result) = await ProvisionScenarioAsync(provider, ProjectContextMode.Card);
        await using var _ = h;
        Assert.True(result.Success, result.Message);

        Assert.Equal(ProjectContextGoldenTests.CardV1.TrimEnd() + "\n\n" + ExactFooter, Read(h, "projects", "project-a", "context.md"));
        Assert.Equal(ProjectContextGoldenTests.FullV2, Read(h, "projects", "project-a", "full.md"));

        // project-b is full mode and has no context row: today's stub, no full.md.
        Assert.Equal(
            File.ReadAllText(Path.Combine(ProjectContextGoldenTests.FixtureDir(provider), "project-files", "project-b", "context.md")),
            Read(h, "projects", "project-b", "context.md"));
        Assert.False(Exists(h, "projects", "project-b", "full.md"));

        Assert.Contains(h.Logs.Entries, e => e.Level == LogLevel.Information && e.Message ==
            "ProjectContext assignment agent=agent-a project=project-a mode=card effective=card card=1 basedOn=2 full=2 " +
            "stale=false missingKeeps=- invalidKeeps=0");
        Assert.DoesNotContain(h.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.StartsWith("ProjectContext", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("gemini")]
    public async Task EffectiveCard_InjectsFleetContextWithAgentParam(string provider)
    {
        var (h, _) = await ProvisionScenarioAsync(provider, ProjectContextMode.Card);
        await using var _h = h;

        var server = Json(h, ".mcp.json").GetProperty("mcpServers").GetProperty("fleet-context");

        Assert.Equal("http", server.GetProperty("type").GetString());
        Assert.Equal("http://fleet-orchestrator:3600/mcp/context?agent=agent-a", server.GetProperty("url").GetString());
        // Never the admin route.
        Assert.DoesNotContain(Json(h, ".mcp.json").GetProperty("mcpServers").EnumerateObject(),
            s => s.Value.GetProperty("url").GetString()!.Split('?')[0].EndsWith("/mcp", StringComparison.Ordinal)
                 && s.Value.GetProperty("url").GetString()!.Contains("fleet-orchestrator", StringComparison.Ordinal));
    }

    /// <summary>The grant in each provider's shape: settings.json allow (claude/gemini), AllowedTools (codex).</summary>
    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("gemini")]
    public async Task EffectiveCard_GrantsTheFallbackTool_InTheProviderShape(string provider)
    {
        var (h, _) = await ProvisionScenarioAsync(provider, ProjectContextMode.Card);
        await using var _h = h;

        var allow = Strings(Json(h, "settings.json").GetProperty("permissions").GetProperty("allow"));
        var allowedTools = Strings(Json(h, "appsettings.json").GetProperty("Agent").GetProperty("AllowedTools"));

        Assert.Contains(Grant, allow);
        Assert.Equal(allow.OrderBy(t => t, StringComparer.OrdinalIgnoreCase), allow);
        if (provider == "codex")
            Assert.Contains(Grant, allowedTools);
        else
            Assert.DoesNotContain(Grant, allowedTools);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("gemini")]
    public async Task EffectiveCard_AddsTheRoutingBlock_AndNothingElseToAppsettings(string provider)
    {
        var (h, _) = await ProvisionScenarioAsync(provider, ProjectContextMode.Card);
        await using var _h = h;

        var generated = JsonNode.Parse(Read(h, "appsettings.json"))!.AsObject();
        var agentNode = generated["Agent"]!.AsObject();
        var routing = agentNode["ProjectContextRouting"]!;

        Assert.Equal("""
            {"cardProjects":["project-a"],"fullVersions":{"project-a":2},"routes":[{"kind":"chat","value":"-100000000001","project":"project-a"},{"kind":"repo","value":"org/app","project":"project-a"},{"kind":"workflow","value":"ExampleWorkflow","project":"project-a"}]}
            """, routing.ToJsonString());

        // Take the block (and codex's grant) back out, and what is left is the pre-change file.
        agentNode.Remove("ProjectContextRouting");
        var tools = agentNode["AllowedTools"]!.AsArray();
        var grant = tools.FirstOrDefault(t => t!.GetValue<string>() == Grant);
        if (grant is not null) tools.Remove(grant);

        var golden = File.ReadAllText(Path.Combine(ProjectContextGoldenTests.FixtureDir(provider), "appsettings.json"));
        Assert.Equal(golden, generated.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public async Task ContextMcpUrl_ComesFromConfiguration()
    {
        var (h, _) = await ProvisionScenarioAsync("claude", ProjectContextMode.Card,
            config: new Dictionary<string, string?> { ["Provisioning:ContextMcpUrl"] = "http://orchestrator.internal:3600/mcp/context/" });
        await using var _h = h;

        var url = Json(h, ".mcp.json").GetProperty("mcpServers").GetProperty("fleet-context").GetProperty("url").GetString();
        Assert.Equal("http://orchestrator.internal:3600/mcp/context?agent=agent-a", url);
    }

    [Fact]
    public async Task ExplicitFleetContextRow_Wins_ButStillGetsTheAgentParam()
    {
        var (h, _) = await ProvisionScenarioAsync("claude", ProjectContextMode.Card, db =>
            db.Agents.Local.Single().McpEndpoints.Add(new AgentMcpEndpoint
            {
                McpName = "fleet-context", Url = "http://custom-orchestrator:3600/mcp/context?agent=someone-else", TransportType = "http",
            }));
        await using var _h = h;

        var url = Json(h, ".mcp.json").GetProperty("mcpServers").GetProperty("fleet-context").GetProperty("url").GetString();
        Assert.Equal("http://custom-orchestrator:3600/mcp/context?agent=agent-a", url);
    }

    [Fact]
    public async Task ExplicitFleetContextRow_OnAZeroCardAgent_IsLeftUntouched()
    {
        const string raw = "http://custom-orchestrator:3600/mcp/context";
        var (h, _) = await ProvisionScenarioAsync("claude", ProjectContextMode.Full, db =>
            db.Agents.Local.Single().McpEndpoints.Add(new AgentMcpEndpoint
            {
                McpName = "fleet-context", Url = raw, TransportType = "http",
            }));
        await using var _h = h;

        var url = Json(h, ".mcp.json").GetProperty("mcpServers").GetProperty("fleet-context").GetProperty("url").GetString();
        Assert.Equal(raw, url);
        Assert.DoesNotContain(Grant, Read(h, "settings.json"));
    }

    [Fact]
    public async Task StaleCard_FooterSaysSo_AndWarns()
    {
        var (h, _) = await ProvisionScenarioAsync("claude", ProjectContextMode.Card, db =>
        {
            var ctx = db.ProjectContexts.Local.Single(p => p.Name == "project-a");
            ctx.CardVersions.Single().BasedOnFullVersion = 1;
        });
        await using var _h = h;

        Assert.Contains(
            "[project card: project-a · card v1 · written for full v1 · full is v2 · may be stale]",
            Read(h, "projects", "project-a", "context.md"));
        Assert.True(Exists(h, "projects", "project-a", "full.md"));
        Assert.Contains(h.Logs.Entries, e => e.Level == LogLevel.Warning &&
            e.Message.StartsWith("ProjectContext card_stale agent=agent-a project=project-a", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CaseVariantAssignment_MatchesItsContext_AndKeepsTheAssignmentName()
    {
        var (h, result) = await ProvisionScenarioAsync("claude", ProjectContextMode.Card, db =>
            db.Agents.Local.Single().Projects.Single(p => p.ProjectName == "project-a").ProjectName = "Project-A");
        await using var _h = h;
        Assert.True(result.Success, result.Message);

        Assert.Contains("call get_project_context with name \"Project-A\"", Read(h, "projects", "Project-A", "context.md"));
        var routing = Json(h, "appsettings.json").GetProperty("Agent").GetProperty("ProjectContextRouting");
        Assert.Equal(["Project-A"], Strings(routing.GetProperty("cardProjects")));
        Assert.All(routing.GetProperty("routes").EnumerateArray(),
            r => Assert.Equal("Project-A", r.GetProperty("project").GetString()));
    }

    // ── AC 7 / D8: a card missing a keep marker falls back to full ───────────

    private static void AddKeepToFull(OrchestratorDbContext db)
    {
        var ctx = db.ProjectContexts.Local.Single(p => p.Name == "project-a");
        ctx.Versions.Add(new ProjectContextVersion
        {
            VersionNumber = 3, Content = ProjectContextGoldenTests.FullV2 + "\n<!-- keep:y -->\nAlso this.\n",
        });
        ctx.CurrentVersion = 3;
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    public async Task MissingKeep_RendersFull_NoFullMd_NoFallbackWiring_AndTheResponseNamesIt(string provider)
    {
        await using var h = ProvisioningHarness.Create();
        await h.SeedAsync(db =>
        {
            ProjectContextGoldenTests.SeedScenario(db, provider, ProjectContextMode.Card);
            AddKeepToFull(db);
        });

        // Through reprovision, as an operator would run it (no container exists → straight to provision).
        var result = await h.Service.ReprovisionAsync(AgentName);

        Assert.True(result.Success, result.Message);
        Assert.Contains("project 'project-a'", result.Message, StringComparison.Ordinal);
        Assert.Contains("keep marker(s) y", result.Message, StringComparison.Ordinal);

        Assert.Equal(ProjectContextGoldenTests.FullV2 + "\n<!-- keep:y -->\nAlso this.\n", Read(h, "projects", "project-a", "context.md"));
        Assert.False(Exists(h, "projects", "project-a", "full.md"));

        // Zero EFFECTIVE card assignments: no fleet-context, no grant, no routing block.
        Assert.False(Json(h, ".mcp.json").GetProperty("mcpServers").TryGetProperty("fleet-context", out _));
        Assert.DoesNotContain(Grant, Read(h, "settings.json"));
        Assert.DoesNotContain("ProjectContextRouting", Read(h, "appsettings.json"));
        foreach (var file in new[] { ("mcp.json", ".mcp.json"), ("settings.json", "settings.json") })
            Assert.Equal(File.ReadAllText(Path.Combine(ProjectContextGoldenTests.FixtureDir(provider), file.Item1)), Read(h, file.Item2));

        Assert.Contains(h.Logs.Entries, e => e.Level == LogLevel.Warning &&
            e.Message.StartsWith("ProjectContext card_fallback_full agent=agent-a project=project-a card=1 missingKeeps=y", StringComparison.Ordinal));
        Assert.Contains(h.Logs.Entries, e => e.Message ==
            "ProjectContext assignment agent=agent-a project=project-a mode=card effective=full card=1 basedOn=2 full=3 " +
            "stale=true missingKeeps=y invalidKeeps=0");
    }

    [Fact]
    public async Task CardProjects_AreEffectiveCardsOnly_WhileRoutesCoverEveryAssignedProject()
    {
        await using var h = ProvisioningHarness.Create();
        await h.SeedAsync(db =>
        {
            ProjectContextGoldenTests.SeedScenario(db, "claude", ProjectContextMode.Card);

            // project-b: card mode, but its full context carries a keep marker the card lacks.
            var b = new ProjectContext { Name = "project-b", CurrentVersion = 1, CurrentCardVersion = 1 };
            b.Versions.Add(new ProjectContextVersion { VersionNumber = 1, Content = "# project-b\n\n<!-- keep:z -->\nRule.\n" });
            b.CardVersions.Add(new ProjectContextCardVersion { VersionNumber = 1, Content = "b card\n", BasedOnFullVersion = 1 });
            b.Routes.Add(new ProjectContextRoute { SignalKind = RouteSignalKind.Repo, SignalValue = "org/app" });
            db.ProjectContexts.Add(b);
            db.Agents.Local.Single().Projects.Single(p => p.ProjectName == "project-b").ContextMode = ProjectContextMode.Card;

            // A route on a project agent-a is NOT assigned never reaches it.
            var other = new ProjectContext { Name = "project-other", CurrentVersion = 1 };
            other.Versions.Add(new ProjectContextVersion { VersionNumber = 1, Content = "x" });
            other.Routes.Add(new ProjectContextRoute { SignalKind = RouteSignalKind.Workflow, SignalValue = "ExampleWorkflow" });
            db.ProjectContexts.Add(other);
        });

        var result = await h.Service.ProvisionAsync(AgentName);

        Assert.True(result.Success, result.Message);
        Assert.Contains("project 'project-b'", result.Message, StringComparison.Ordinal);
        Assert.Contains("keep marker(s) z", result.Message, StringComparison.Ordinal);

        var routing = Json(h, "appsettings.json").GetProperty("Agent").GetProperty("ProjectContextRouting");
        Assert.Equal(["project-a"], Strings(routing.GetProperty("cardProjects")));
        Assert.Equal(["project-a"], routing.GetProperty("fullVersions").EnumerateObject().Select(p => p.Name).ToList());
        Assert.Equal(
            ["chat|-100000000001|project-a", "repo|org/app|project-a", "repo|org/app|project-b", "workflow|ExampleWorkflow|project-a"],
            routing.GetProperty("routes").EnumerateArray()
                .Select(r => $"{r.GetProperty("kind").GetString()}|{r.GetProperty("value").GetString()}|{r.GetProperty("project").GetString()}")
                .ToList());

        Assert.Equal("# project-b\n\n<!-- keep:z -->\nRule.\n", Read(h, "projects", "project-b", "context.md"));
        Assert.False(Exists(h, "projects", "project-b", "full.md"));
        Assert.True(Exists(h, "projects", "project-a", "full.md"));
    }

    // ── missing rows throw; the agent is not started ─────────────────────────

    [Fact]
    public async Task CardMode_WithNoContextRow_Throws_NamingAgentAndProject()
    {
        await using var h = ProvisioningHarness.Create();
        await h.SeedAsync(db =>
        {
            ProjectContextGoldenTests.SeedScenario(db, "claude");
            db.Agents.Local.Single().Projects.Single(p => p.ProjectName == "project-b").ContextMode = ProjectContextMode.Card;
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.ProvisionAsync(AgentName));

        Assert.Contains("'agent-a'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'project-b'", ex.Message, StringComparison.Ordinal);
        Assert.False(Exists(h, ".mcp.json"), "no file may be written before the refusal");
    }

    [Fact]
    public async Task CardMode_WithNoCard_Throws_NamingAgentAndProject()
    {
        await using var h = ProvisioningHarness.Create();
        await h.SeedAsync(db =>
        {
            ProjectContextGoldenTests.SeedScenario(db, "claude", ProjectContextMode.Card);
            db.ProjectContexts.Local.Single(p => p.Name == "project-a").CurrentCardVersion = null;
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.ProvisionAsync(AgentName));

        Assert.Contains("'agent-a'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'project-a'", ex.Message, StringComparison.Ordinal);
    }

    // ── AC 13: flipping back to full restores the pre-change bytes ───────────

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("gemini")]
    public async Task FlipBackToFull_RemovesFleetContextAndFullMd_AndRestoresTheGoldenFiles(string provider)
    {
        var (h, first) = await ProvisionScenarioAsync(provider, ProjectContextMode.Card);
        await using var _h = h;
        Assert.True(first.Success, first.Message);
        Assert.True(Exists(h, "projects", "project-a", "full.md"));

        await h.MutateAsync(async db =>
        {
            var assignment = await db.AgentProjects.SingleAsync(p => p.ProjectName == "project-a");
            assignment.ContextMode = ProjectContextMode.Full;
        });
        var second = await h.Service.ProvisionAsync(AgentName);
        Assert.True(second.Success, second.Message);

        Assert.False(Exists(h, "projects", "project-a", "full.md"));
        var fixtures = ProjectContextGoldenTests.FixtureDir(provider);
        Assert.Equal(File.ReadAllText(Path.Combine(fixtures, "mcp.json")), Read(h, ".mcp.json"));
        Assert.Equal(File.ReadAllText(Path.Combine(fixtures, "settings.json")), Read(h, "settings.json"));
        Assert.Equal(File.ReadAllText(Path.Combine(fixtures, "appsettings.json")), Read(h, "appsettings.json"));
        Assert.Equal(File.ReadAllText(Path.Combine(fixtures, "project-files", "project-a", "context.md")),
            Read(h, "projects", "project-a", "context.md"));
    }

    // ── the pure pieces ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, "http://fleet-orchestrator:3600/mcp/context")]
    [InlineData("", "http://fleet-orchestrator:3600/mcp/context")]
    [InlineData("not-a-url", "http://fleet-orchestrator:3600/mcp/context")]
    [InlineData("http://orchestrator:3600/mcp/context", "http://orchestrator:3600/mcp/context")]
    public void ResolveContextMcpUrl_DefaultsWhenUnsetOrInvalid(string? raw, string expected)
    {
        Assert.Equal(expected, ContainerProvisioningService.ResolveContextMcpUrl(raw));
    }

    [Fact]
    public void Generators_WithoutTheNewArguments_EqualTheirZeroCardOutput()
    {
        var agent = new Agent
        {
            Name = "agent-a", DisplayName = "a", Role = "developer", Model = "m", ContainerName = "fleet-agent-a", Provider = "codex",
        };

        Assert.Equal(
            ContainerProvisioningService.GenerateMcpJson(agent, "http://fleet-memory:3100"),
            ContainerProvisioningService.GenerateMcpJson(agent, "http://fleet-memory:3100", contextMcpUrl: null));
        Assert.Equal(
            ContainerProvisioningService.GenerateSettingsJson(agent, "acto"),
            ContainerProvisioningService.GenerateSettingsJson(agent, "acto", grantContextFallback: false));
        Assert.Equal(
            ContainerProvisioningService.GenerateAppsettingsJson(agent, "acto"),
            ContainerProvisioningService.GenerateAppsettingsJson(agent, "acto", routing: null));
    }
}
