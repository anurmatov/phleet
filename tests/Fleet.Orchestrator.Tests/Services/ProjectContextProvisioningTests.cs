using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Tests.Services;

/// <summary>
/// Provisioning project contexts after the #346 card removal: every assignment renders the canonical
/// context through the real <see cref="ContainerProvisioningService.ProvisionAsync"/>, a <c>full.md</c>
/// left by a card-mode provision is deleted, and none of the removed fallback wiring is generated.
/// </summary>
public class ProjectContextProvisioningTests
{
    private const string AgentName = ProjectContextGoldenTests.AgentName;
    private const string Container = ProjectContextGoldenTests.ContainerName;

    private static string Read(ProvisioningHarness h, params string[] parts) =>
        File.ReadAllText(Path.Combine([h.GeneratedDir(Container), .. parts]));

    private static bool Exists(ProvisioningHarness h, params string[] parts) =>
        File.Exists(Path.Combine([h.GeneratedDir(Container), .. parts]));

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("gemini")]
    public async Task PreExistingFullMd_IsDeleted_AndOutputEqualsGolden(string provider)
    {
        await using var h = ProvisioningHarness.Create();
        await h.SeedAsync(db => ProjectContextGoldenTests.SeedScenario(db, provider));

        // What a card-mode provision before #346 left behind, including under a project the agent
        // is no longer assigned.
        foreach (var project in new[] { "project-a", "project-gone" })
        {
            var dir = Path.Combine(h.GeneratedDir(Container), "projects", project);
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "full.md"), "stale full\n");
        }

        var result = await h.Service.ProvisionAsync(AgentName);
        Assert.True(result.Success, result.Message);

        Assert.False(Exists(h, "projects", "project-a", "full.md"));
        Assert.False(Exists(h, "projects", "project-gone", "full.md"));

        var fixtures = ProjectContextGoldenTests.FixtureDir(provider);
        Assert.Equal(File.ReadAllText(Path.Combine(fixtures, "mcp.json")), Read(h, ".mcp.json"));
        Assert.Equal(File.ReadAllText(Path.Combine(fixtures, "settings.json")), Read(h, "settings.json"));
        Assert.Equal(File.ReadAllText(Path.Combine(fixtures, "appsettings.json")), Read(h, "appsettings.json"));
        Assert.Equal(File.ReadAllText(Path.Combine(fixtures, "project-files", "project-a", "context.md")),
            Read(h, "projects", "project-a", "context.md"));
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("gemini")]
    public async Task NoFallbackServer_NoGrant_NoRoutingBlock(string provider)
    {
        await using var h = ProvisioningHarness.Create(new Dictionary<string, string?>
        {
            // A leftover setting from the card build is ignored.
            ["Provisioning:ContextMcpUrl"] = "http://fleet-orchestrator:3600/mcp/context",
        });
        await h.SeedAsync(db => ProjectContextGoldenTests.SeedScenario(db, provider));

        var result = await h.Service.ProvisionAsync(AgentName);
        Assert.True(result.Success, result.Message);

        foreach (var file in new[] { ".mcp.json", "settings.json", "appsettings.json" })
        {
            var text = Read(h, file);
            Assert.DoesNotContain("fleet-context", text, StringComparison.Ordinal);
            Assert.DoesNotContain("mcp/context", text, StringComparison.Ordinal);
            Assert.DoesNotContain("ProjectContextRouting", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task CaseVariantAssignment_MatchesItsContext_AndKeepsTheAssignmentName()
    {
        await using var h = ProvisioningHarness.Create();
        await h.SeedAsync(db =>
        {
            ProjectContextGoldenTests.SeedScenario(db, "claude");
            db.Agents.Local.Single().Projects.Single(p => p.ProjectName == "project-a").ProjectName = "Project-A";
        });

        var result = await h.Service.ProvisionAsync(AgentName);
        Assert.True(result.Success, result.Message);

        Assert.Equal(ProjectContextGoldenTests.FullV2, Read(h, "projects", "Project-A", "context.md"));
    }

    [Fact]
    public async Task CanonicalIsTheCurrentVersion_ElseTheLatest()
    {
        await using var h = ProvisioningHarness.Create();
        await h.SeedAsync(db =>
        {
            ProjectContextGoldenTests.SeedScenario(db, "claude");
            db.ProjectContexts.Local.Single(p => p.Name == "project-a").CurrentVersion = 1;
        });
        Assert.True((await h.Service.ProvisionAsync(AgentName)).Success);
        Assert.Equal(ProjectContextGoldenTests.FullV1, Read(h, "projects", "project-a", "context.md"));

        // CurrentVersion points at no row: the latest row is the canonical content.
        await h.MutateAsync(async db =>
            (await db.ProjectContexts.SingleAsync(p => p.Name == "project-a")).CurrentVersion = 9);
        Assert.True((await h.Service.ProvisionAsync(AgentName)).Success);
        Assert.Equal(ProjectContextGoldenTests.FullV2, Read(h, "projects", "project-a", "context.md"));
    }

    [Fact]
    public void Plan_HasOneAssignmentPerProject_WithTheStubsUnchanged()
    {
        var agent = new Agent
        {
            Name = "agent-a", DisplayName = "a", Role = "developer", Model = "m", ContainerName = "fleet-agent-a",
        };
        agent.Projects.Add(new AgentProject { ProjectName = "missing" });
        agent.Projects.Add(new AgentProject { ProjectName = "empty" });

        var plan = ContainerProvisioningService.BuildProjectContextPlan(
            agent, [new ProjectContext { Name = "empty" }], Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        Assert.Equal(
            [
                new ProjectContextAssignment("missing", "# missing\n\n(No content — project context not yet seeded in DB)\n"),
                new ProjectContextAssignment("empty", "# empty\n\n(No content — no versions found)\n"),
            ],
            plan.Assignments);
    }
}
