using System.Text.Json;
using Fleet.Orchestrator.Application;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Services.McpTools;
using Fleet.Orchestrator.Tests.Services;
using Fleet.Shared;

namespace Fleet.Orchestrator.Tests.Identity;

/// <summary>
/// What provisioning writes for an agent to prove who it is, through the real
/// <see cref="ContainerProvisioningService"/>: its token as a header on the fleet's own MCP
/// servers and nowhere else, and nothing at all for a deployment without the key.
/// </summary>
public sealed class AgentIdentityProvisioningTests
{
    private static readonly byte[] Key = AgentIdentityTokensTests.Key;
    private static readonly Dictionary<string, string?> WithKey = new() { ["AgentIdentity:Key"] = AgentIdentityTokensTests.Encode(Key) };

    private static Agent NewAgent(string provider = "claude", string name = "agent1") => new()
    {
        Name = name,
        DisplayName = name,
        Role = "developer",
        Model = provider == "codex" ? "gpt-5.5" : provider == "gemini" ? "gemini-2.5-pro" : "claude-sonnet-4-6",
        Provider = provider,
        ContainerName = "fleet-" + name,
        MemoryLimitMb = 512,
        Tools = [new() { ToolName = "mcp__fleet-telegram__send_message" }, new() { ToolName = "mcp__example__ping" }],
        McpEndpoints =
        [
            new() { McpName = "fleet-telegram", Url = "http://fleet-telegram:3800", TransportType = "http" },
            new() { McpName = "fleet-temporal", Url = "http://fleet-temporal-bridge:3001", TransportType = "http" },
            new() { McpName = "fleet-orchestrator", Url = "http://fleet-orchestrator:3600/mcp", TransportType = "http" },
            new() { McpName = "example", Url = "https://mcp.example.test/mcp", TransportType = "http" },
        ],
    };

    private static async Task<ProvisioningHarness> StartAsync(Agent agent, bool withKey = true)
    {
        var harness = ProvisioningHarness.Create(withKey ? WithKey : null);
        await harness.SeedAsync(db => db.Agents.Add(agent));
        return harness;
    }

    private static JsonElement Servers(ProvisioningHarness harness, string container = "fleet-agent1") =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(harness.GeneratedDir(container), ".mcp.json")))
            .RootElement.GetProperty("mcpServers");

    private static string? AuthorizationOf(JsonElement servers, string server) =>
        servers.GetProperty(server).TryGetProperty("headers", out var headers)
        && headers.TryGetProperty("Authorization", out var value) ? value.GetString() : null;

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    public async Task AnAgent_IsGivenItsToken_OnEachOfTheFleetsOwnServers(string provider)
    {
        await using var harness = await StartAsync(NewAgent(provider));

        var result = await harness.Service.ProvisionAsync("agent1");

        Assert.True(result.Success, result.Message);
        var servers = Servers(harness);
        var expected = "Bearer " + AgentIdentityTokens.Mint(Key, AgentIdentityTokens.PurposeAgent, "agent1");
        // fleet-memory is given to every agent, row or no row, and carries the identity too.
        foreach (var server in new[] { "fleet-memory", "fleet-telegram", "fleet-temporal", "fleet-orchestrator" })
            Assert.Equal(expected, AuthorizationOf(servers, server));

        Assert.True(AgentIdentityTokens.TryVerify(expected["Bearer ".Length..], [Key], out var purpose, out var subject));
        Assert.Equal(("agent", "agent1"), (purpose, subject));
    }

    [Fact]
    public async Task TheToken_GoesToNoServerThatIsNotTheFleetsOwn()
    {
        await using var harness = await StartAsync(NewAgent());

        Assert.True((await harness.Service.ProvisionAsync("agent1")).Success);

        var example = Servers(harness).GetProperty("example");
        Assert.False(example.TryGetProperty("headers", out _));
    }

    [Fact]
    public async Task AnAgentThatSendsHeaders_IsToldSo_OrItWouldSkipTheServers()
    {
        // The agent leaves out a server that has headers unless Agent.McpHeaderSupport is set.
        await using var harness = await StartAsync(NewAgent("codex"));

        Assert.True((await harness.Service.ProvisionAsync("agent1")).Success);

        var appsettings = JsonDocument.Parse(File.ReadAllText(Path.Combine(harness.GeneratedDir("fleet-agent1"), "appsettings.json")));
        Assert.True(appsettings.RootElement.GetProperty("Agent").GetProperty("McpHeaderSupport").GetBoolean());
    }

    [Fact]
    public async Task AGeminiAgent_GetsNoHeader_BecauseItWouldSkipEveryServerThatHasOne()
    {
        await using var harness = await StartAsync(NewAgent("gemini"));

        Assert.True((await harness.Service.ProvisionAsync("agent1")).Success);

        var servers = Servers(harness);
        foreach (var server in new[] { "fleet-memory", "fleet-telegram", "fleet-temporal", "fleet-orchestrator" })
            Assert.Null(AuthorizationOf(servers, server));
    }

    [Fact]
    public async Task WithoutTheKey_AnAgentsFilesAreWhatTheyAlwaysWere()
    {
        await using var harness = await StartAsync(NewAgent(), withKey: false);

        Assert.True((await harness.Service.ProvisionAsync("agent1")).Success);

        var text = File.ReadAllText(Path.Combine(harness.GeneratedDir("fleet-agent1"), ".mcp.json"));
        Assert.DoesNotContain("headers", text);
        Assert.DoesNotContain("fa1.", text);
        var appsettings = File.ReadAllText(Path.Combine(harness.GeneratedDir("fleet-agent1"), "appsettings.json"));
        Assert.DoesNotContain("McpHeaderSupport", appsettings);
    }

    [Fact]
    public async Task TheUrlStillNamesTheAgent_SoAServerFromBeforeTokensStillKnowsIt()
    {
        await using var harness = await StartAsync(NewAgent());

        Assert.True((await harness.Service.ProvisionAsync("agent1")).Success);

        Assert.EndsWith("?agent=agent1", Servers(harness).GetProperty("fleet-memory").GetProperty("url").GetString());
        Assert.Contains("?agent=agent1", Servers(harness).GetProperty("fleet-telegram").GetProperty("url").GetString());
    }

    [Fact]
    public async Task TheKeyItself_NeverEntersAnAgent_EvenWhereARowAsksForIt()
    {
        var agent = NewAgent();
        agent.EnvRefs.Add(new AgentEnvRef { EnvKeyName = ContainerSpecBuilder.AgentIdentityKeyName });
        await using var harness = await StartAsync(agent);
        File.AppendAllText(harness.EnvFile, $"{ContainerSpecBuilder.AgentIdentityKeyName}={AgentIdentityTokensTests.Encode(Key)}\n");

        Assert.True((await harness.Service.ProvisionAsync("agent1")).Success);

        var body = harness.CreateBodies.Single();
        Assert.DoesNotContain(ContainerSpecBuilder.AgentIdentityKeyName, body);
        Assert.DoesNotContain(AgentIdentityTokensTests.Encode(Key), body);
    }

    // ── the pieces ───────────────────────────────────────────────────────────

    [Fact]
    public void TheIdentityHeader_IsAddedBesideAnOperatorsMappedHeaders_AndWinsOverAMappedAuthorization()
    {
        var mapped = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["fleet-telegram"] = new Dictionary<string, string> { ["X-Extra"] = "kept", ["authorization"] = "Bearer mapped-by-operator" },
            ["example"] = new Dictionary<string, string> { ["X-Example-Key"] = "value" },
        };

        var merged = AgentConfigFiles.WithIdentityHeaders(NewAgent(), mapped, "fa1.agent.agent1.sig")!;

        Assert.Equal("kept", merged["fleet-telegram"]["X-Extra"]);
        Assert.Equal("Bearer fa1.agent.agent1.sig", merged["fleet-telegram"]["Authorization"]);
        Assert.DoesNotContain("authorization", merged["fleet-telegram"].Keys);
        // A third-party endpoint keeps exactly what the operator mapped.
        Assert.Equal(new Dictionary<string, string> { ["X-Example-Key"] = "value" }, merged["example"]);
        Assert.Same(mapped, AgentConfigFiles.WithIdentityHeaders(NewAgent(), mapped, identityToken: null));
    }

    [Fact]
    public void AnAgentWhoseNameCannotBeSigned_GetsNoToken_RatherThanFailingToProvision()
    {
        Assert.Null(AgentConfigFiles.IdentityTokenFor(NewAgent(name: "agent.with.dots"), [Key]));
        Assert.Null(AgentConfigFiles.IdentityTokenFor(NewAgent(), []));
        Assert.NotNull(AgentConfigFiles.IdentityTokenFor(NewAgent(), [Key]));
    }

    [Fact]
    public void AnAgentsGrants_AreItsEnabledToolsOnTheFleetsServers_AndTheTwoEveryAgentHas()
    {
        var agent = NewAgent();
        agent.Tools.Add(new AgentTool { ToolName = "mcp__fleet-orchestrator__create_agent", IsEnabled = false });
        agent.Tools.Add(new AgentTool { ToolName = "mcp__fleet-temporal", IsEnabled = true });
        agent.Tools.Add(new AgentTool { ToolName = "Read", IsEnabled = true });

        var grants = AgentConfigFiles.EffectiveMcpGrants(agent, "cto-agent");

        Assert.Equal(
            ["mcp__fleet-memory__memory_get", "mcp__fleet-telegram__send_message", "mcp__fleet-temporal", "mcp__fleet-temporal__notify_cto"],
            grants);
    }

    [Fact]
    public void TheCto_IsNotGrantedNotifyCto_AndNobodyIsWhenNoCtoIsConfigured()
    {
        Assert.DoesNotContain("mcp__fleet-temporal__notify_cto", AgentConfigFiles.EffectiveMcpGrants(NewAgent(name: "cto-agent"), "CTO-AGENT"));
        Assert.DoesNotContain("mcp__fleet-temporal__notify_cto", AgentConfigFiles.EffectiveMcpGrants(NewAgent(), ""));
        Assert.Contains("mcp__fleet-memory__memory_get", AgentConfigFiles.EffectiveMcpGrants(NewAgent(), null));
    }

    [Fact]
    public void TheToolListing_AsksTheFleetsOwnServersAsTheOrchestrator_AndSendsItsTokenNowhereElse()
    {
        var service = AgentIdentityTokens.Mint(Key, AgentIdentityTokens.PurposeService, "fleet-orchestrator");

        var plan = McpToolPlan.Build(NewAgent(), new Dictionary<string, string>(), [], "http://fleet-memory:3100", () => null, service);

        foreach (var name in new[] { "fleet-memory", "fleet-telegram", "fleet-temporal", "fleet-orchestrator" })
            Assert.Equal("Bearer " + service, plan.Single(e => e.Name == name).Target!.Headers["Authorization"]);
        Assert.Empty(plan.Single(e => e.Name == "example").Target!.Headers);

        var without = McpToolPlan.Build(NewAgent(), new Dictionary<string, string>(), [], "http://fleet-memory:3100", () => null);
        Assert.All(without, entry => Assert.Empty(entry.Target!.Headers));
    }
}
