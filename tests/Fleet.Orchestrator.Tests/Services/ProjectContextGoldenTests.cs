using Fleet.Orchestrator.Data;

namespace Fleet.Orchestrator.Tests.Services;

/// <summary>
/// #347 AC 2: an agent with zero effective card assignments is provisioned byte for byte as before
/// cards existed.
/// </summary>
/// <remarks>
/// <para>
/// The fixtures under <c>Fixtures/ProjectContextGolden/&lt;provider&gt;/</c> (<c>mcp.json</c> is the
/// generated <c>.mcp.json</c>; <c>project-files/</c> is <c>projects/</c>) were captured by running
/// this exact scenario through <see cref="Fleet.Orchestrator.Services.ContainerProvisioningService.ProvisionAsync"/>
/// on the code BEFORE card provisioning existed, and committed on their own before any provisioning
/// change. They are never regenerated from the new code — that would make the test compare the
/// change with itself.
/// </para>
/// <para>
/// The scenario is deliberately hostile to a leak: <c>project-a</c> HAS a card and routes, but the
/// assignment is <c>full</c>. Nothing of the card, the routes, <c>fleet-context</c> or the fallback
/// grant may appear, and no <c>full.md</c> may be written.
/// </para>
/// </remarks>
public class ProjectContextGoldenTests
{
    internal const string AgentName = "agent-a";
    internal const string ContainerName = "fleet-agent-a";

    internal const string FullV1 = "# project-a\n\nFirst version.\n";
    internal const string FullV2 = "# project-a\n\nBuild with care.\n\n<!-- keep:rule-one -->\nNever skip review.\n";
    internal const string CardV1 = "project-a in brief.\n\n<!-- keep:rule-one -->\nNever skip review.\n";

    internal static string FixtureDir(string provider) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "ProjectContextGolden", provider);

    /// <summary>The golden scenario. <paramref name="projectAMode"/> is <c>full</c> for the golden run.</summary>
    internal static void SeedScenario(OrchestratorDbContext db, string provider, string projectAMode = ProjectContextMode.Full)
    {
        var ctx = new ProjectContext { Name = "project-a", CurrentVersion = 2, CurrentCardVersion = 1 };
        ctx.Versions.Add(new ProjectContextVersion { VersionNumber = 1, Content = FullV1, CreatedBy = "seed" });
        ctx.Versions.Add(new ProjectContextVersion { VersionNumber = 2, Content = FullV2, CreatedBy = "seed" });
        ctx.CardVersions.Add(new ProjectContextCardVersion
        {
            VersionNumber = 1, Content = CardV1, BasedOnFullVersion = 2, CreatedBy = "seed",
        });
        ctx.Routes.Add(new ProjectContextRoute { SignalKind = RouteSignalKind.Repo, SignalValue = "org/app" });
        ctx.Routes.Add(new ProjectContextRoute { SignalKind = RouteSignalKind.Workflow, SignalValue = "ExampleWorkflow" });
        ctx.Routes.Add(new ProjectContextRoute { SignalKind = RouteSignalKind.Chat, SignalValue = "-100000000001" });
        db.ProjectContexts.Add(ctx);

        var instruction = new Instruction { Name = "base", CurrentVersion = 1 };
        instruction.Versions.Add(new InstructionVersion { VersionNumber = 1, Content = "# base\n" });
        db.Instructions.Add(instruction);

        var agent = new Agent
        {
            Name = AgentName,
            DisplayName = "Agent A",
            Role = "developer",
            Model = "model-x",
            Provider = provider,
            ContainerName = ContainerName,
            MemoryLimitMb = 512,
        };
        agent.Tools.Add(new AgentTool { ToolName = "Read", IsEnabled = true });
        agent.Tools.Add(new AgentTool { ToolName = "Bash", IsEnabled = true });
        agent.Tools.Add(new AgentTool { ToolName = "mcp__fleet-memory__memory_search", IsEnabled = true });
        agent.Tools.Add(new AgentTool { ToolName = "Write", IsEnabled = false });
        agent.Projects.Add(new AgentProject { ProjectName = "project-a", ContextMode = projectAMode });
        agent.Projects.Add(new AgentProject { ProjectName = "project-b" });
        agent.McpEndpoints.Add(new AgentMcpEndpoint
        {
            McpName = "fleet-telegram", Url = "http://fleet-telegram:3800/mcp", TransportType = "http",
        });
        agent.McpEndpoints.Add(new AgentMcpEndpoint
        {
            McpName = "fleet-temporal", Url = "http://fleet-temporal-bridge:3001/mcp", TransportType = "http",
        });
        agent.McpEndpoints.Add(new AgentMcpEndpoint
        {
            McpName = "example-tools", Url = "http://example-tools:9000/sse", TransportType = "sse",
        });
        agent.TelegramUsers.Add(new AgentTelegramUser { UserId = 1000001 });
        agent.TelegramGroups.Add(new AgentTelegramGroup { GroupId = -100000000001 });
        agent.Instructions.Add(new AgentInstruction { Instruction = instruction, LoadOrder = 0 });
        db.Agents.Add(agent);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("gemini")]
    public async Task ZeroCardAgent_GeneratesByteIdenticalFiles(string provider)
    {
        await using var harness = ProvisioningHarness.Create();
        await harness.SeedAsync(db => SeedScenario(db, provider));

        var result = await harness.Service.ProvisionAsync(AgentName);
        Assert.True(result.Success, result.Message);

        var generated = harness.GeneratedDir(ContainerName);
        var fixtures = FixtureDir(provider);

        AssertSameBytes(Path.Combine(fixtures, "mcp.json"), Path.Combine(generated, ".mcp.json"));
        AssertSameBytes(Path.Combine(fixtures, "settings.json"), Path.Combine(generated, "settings.json"));
        AssertSameBytes(Path.Combine(fixtures, "appsettings.json"), Path.Combine(generated, "appsettings.json"));

        // projects/* — the same file set, and each file the same bytes. No full.md anywhere.
        // (The fixture directory is project-files/ because the repo .gitignore ignores projects/.)
        var fixtureProjects = Path.Combine(fixtures, "project-files");
        var generatedProjects = Path.Combine(generated, "projects");
        Assert.Equal(RelativeFiles(fixtureProjects), RelativeFiles(generatedProjects));
        foreach (var rel in RelativeFiles(fixtureProjects))
            AssertSameBytes(Path.Combine(fixtureProjects, rel), Path.Combine(generatedProjects, rel));
    }

    private static List<string> RelativeFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

    private static void AssertSameBytes(string expectedPath, string actualPath)
    {
        Assert.True(File.Exists(expectedPath), $"fixture missing: {expectedPath}");
        Assert.True(File.Exists(actualPath), $"generated file missing: {actualPath}");
        var expected = File.ReadAllBytes(expectedPath);
        var actual = File.ReadAllBytes(actualPath);
        Assert.True(expected.AsSpan().SequenceEqual(actual),
            $"{Path.GetFileName(actualPath)} differs from the pre-change fixture.\n--- expected\n" +
            $"{System.Text.Encoding.UTF8.GetString(expected)}\n--- actual\n{System.Text.Encoding.UTF8.GetString(actual)}");
    }
}
