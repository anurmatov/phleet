using System.Text.Json;
using Fleet.Orchestrator.Application;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Services.McpTools;
using Fleet.Orchestrator.Tests.Services;
using Fleet.Shared;
using Microsoft.Extensions.Configuration;

namespace Fleet.Orchestrator.Tests.Identity;

/// <summary>
/// What provisioning writes for an agent to prove who it is, through the real
/// <see cref="ContainerProvisioningService"/>: its token as a header on the fleet's own MCP
/// servers and nowhere else; missing keys and unsupported callers refuse provisioning.
/// </summary>
public sealed class AgentIdentityProvisioningTests
{
    private static readonly byte[] Key = AgentIdentityTokensTests.Key;
    private static readonly Dictionary<string, string?> WithKey = new() { ["AgentIdentity:Key"] = AgentIdentityTokensTests.Encode(Key) };

    private const string MemoryUrl = "http://fleet-memory:3100";
    /// <summary>Where the fleet's own servers answer by default: the origins a token may be sent to.</summary>
    private static readonly IReadOnlyCollection<string> Origins =
        AgentConfigFiles.IdentityOrigins(new ConfigurationBuilder().Build());
    /// <summary>A fleet server's name on an address that is not the fleet's.</summary>
    private const string Collector = "https://collector.example.test/mcp";

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
        // A gemini agent signs in with its API key, an Env Ref like any other.
        EnvRefs = provider == "gemini" ? [new() { EnvKeyName = ProvisioningFaults.GeminiApiKeyEnvRef }] : [],
    };

    private static async Task<ProvisioningHarness> StartAsync(Agent agent, bool withKey = true)
    {
        var harness = ProvisioningHarness.Create(withKey ? WithKey : new Dictionary<string,string?> { ["AgentIdentity:Key"] = "" });
        File.AppendAllText(harness.EnvFile, $"{ProvisioningFaults.GeminiApiKeyEnvRef}=synthetic-gemini-api-key\n");
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
    [InlineData("gemini")]
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

    [Theory]
    [InlineData("codex")]
    [InlineData("gemini")]
    public async Task AnAgentThatSendsHeaders_IsToldSo_OrItWouldSkipTheServers(string provider)
    {
        // The agent leaves out a server that has headers unless Agent.McpHeaderSupport is set.
        await using var harness = await StartAsync(NewAgent(provider));

        Assert.True((await harness.Service.ProvisionAsync("agent1")).Success);

        var appsettings = JsonDocument.Parse(File.ReadAllText(Path.Combine(harness.GeneratedDir("fleet-agent1"), "appsettings.json")));
        Assert.True(appsettings.RootElement.GetProperty("Agent").GetProperty("McpHeaderSupport").GetBoolean());
    }

    /// <summary>
    /// Every known provider's pinned CLI sends headers now, so the refusal is reached only for a
    /// provider outside the table. A provision refuses such a provider earlier, by name (PV1); the
    /// rule itself still stands for whatever is added to the list of providers without a transcript.
    /// </summary>
    [Fact]
    public async Task AProviderThatSendsNoHeaders_CannotProveWhoItIs_AndIsRefusedBeforeFilesAreWritten()
    {
        var agent = NewAgent("other-harness");
        var config = new ConfigurationBuilder().AddInMemoryCollection(WithKey).Build();

        Assert.StartsWith("mcp_headers_unsupported", ProvisioningFaults.DescribeIdentityFault(agent, config));
        foreach (var known in AgentProviders.All)
            Assert.Null(ProvisioningFaults.DescribeIdentityFault(NewAgent(known), config));

        await using var harness = await StartAsync(agent);
        var result = await harness.Service.ProvisionAsync("agent1");
        Assert.False(result.Success);
        Assert.False(Directory.Exists(harness.GeneratedDir("fleet-agent1")));
        Assert.Empty(harness.CreateBodies);
    }

    [Fact]
    public async Task WithoutTheKey_ProvisioningIsRefused()
    {
        await using var harness = await StartAsync(NewAgent(), withKey: false);
        var result = await harness.Service.ProvisionAsync("agent1");
        Assert.False(result.Success);
        Assert.Contains("agent_identity_misconfigured", result.Message);
    }

    [Fact]
    public async Task TheUrlStillNamesTheAgent_SoAServerFromBeforeTokensStillKnowsIt()
    {
        await using var harness = await StartAsync(NewAgent());

        Assert.True((await harness.Service.ProvisionAsync("agent1")).Success);

        Assert.DoesNotContain("agent=", Servers(harness).GetProperty("fleet-memory").GetProperty("url").GetString());
        Assert.DoesNotContain("agent=", Servers(harness).GetProperty("fleet-telegram").GetProperty("url").GetString());
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

    /// <summary>A stored header mapping is the other way a value reaches an agent's files: it is refused there too.</summary>
    [Fact]
    public async Task TheKeyItself_IsNeverWrittenIntoAHeader_EvenWhereAStoredMappingAsksForIt()
    {
        var agent = NewAgent();
        agent.EnvRefs.Add(new AgentEnvRef { EnvKeyName = ContainerSpecBuilder.AgentIdentityKeyName });
        agent.McpEndpoints.Single(e => e.McpName == "example").HeaderEnvRefsJson =
            McpHeaderMappings.Serialize([new McpHeaderEnvRef("X-Example-Key", ContainerSpecBuilder.AgentIdentityKeyName)]);
        await using var harness = await StartAsync(agent);
        File.AppendAllText(harness.EnvFile, $"{ContainerSpecBuilder.AgentIdentityKeyName}={AgentIdentityTokensTests.Encode(Key)}\n");

        var result = await harness.Service.ProvisionAsync("agent1");

        Assert.False(result.Success);
        Assert.StartsWith("env_ref_reserved", result.Message);
        Assert.Empty(harness.CreateBodies);
        var generated = harness.GeneratedDir("fleet-agent1");
        if (Directory.Exists(generated))
            foreach (var file in Directory.EnumerateFiles(generated, "*", SearchOption.AllDirectories))
                Assert.DoesNotContain(AgentIdentityTokensTests.Encode(Key), File.ReadAllText(file));
    }

    [Fact]
    public void TheToolListing_DoesNotSendTheKey_EvenWhereAStoredMappingAsksForIt()
    {
        var agent = NewAgent();
        agent.EnvRefs.Add(new AgentEnvRef { EnvKeyName = ContainerSpecBuilder.AgentIdentityKeyName });
        agent.McpEndpoints.Single(e => e.McpName == "example").HeaderEnvRefsJson =
            McpHeaderMappings.Serialize([new McpHeaderEnvRef("X-Example-Key", ContainerSpecBuilder.AgentIdentityKeyName)]);
        var env = new Dictionary<string, string> { [ContainerSpecBuilder.AgentIdentityKeyName] = AgentIdentityTokensTests.Encode(Key) };

        var entry = McpToolPlan.Build(agent, env, [], "http://fleet-memory:3100", () => null).Single(e => e.Name == "example");

        Assert.Null(entry.Target);
        Assert.NotNull(entry.NotAsked);
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

        var merged = AgentConfigFiles.WithIdentityHeaders(NewAgent(), mapped, "fa1.agent.agent1.sig", Origins, MemoryUrl)!;

        Assert.Equal("kept", merged["fleet-telegram"]["X-Extra"]);
        Assert.Equal("Bearer fa1.agent.agent1.sig", merged["fleet-telegram"]["Authorization"]);
        Assert.DoesNotContain("authorization", merged["fleet-telegram"].Keys);
        // A third-party endpoint keeps exactly what the operator mapped.
        Assert.Equal(new Dictionary<string, string> { ["X-Example-Key"] = "value" }, merged["example"]);
        Assert.Throws<ArgumentNullException>(() => AgentConfigFiles.WithIdentityHeaders(NewAgent(), mapped, identityToken: null!, Origins, MemoryUrl));
    }

    [Fact]
    public void InvalidSubject_CannotBeSigned()
    {
        Assert.Throws<ArgumentException>(() => AgentConfigFiles.IdentityTokenFor(NewAgent(name: "agent.with.dots"), [Key]));
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

        var plan = McpToolPlan.Build(NewAgent(), new Dictionary<string, string>(), [], MemoryUrl, () => null, service, Origins);

        foreach (var name in new[] { "fleet-memory", "fleet-telegram", "fleet-temporal", "fleet-orchestrator" })
            Assert.Equal("Bearer " + service, plan.Single(e => e.Name == name).Target!.Headers["Authorization"]);
        Assert.Empty(plan.Single(e => e.Name == "example").Target!.Headers);

        var without = McpToolPlan.Build(NewAgent(), new Dictionary<string, string>(), [], "http://fleet-memory:3100", () => null);
        Assert.All(without, entry => Assert.Empty(entry.Target!.Headers));
    }

    [Theory]
    [InlineData("gemini", "agent.with.dots", "agent_identity_subject_invalid")]
    [InlineData("claude", "agent.with.dots", "agent_identity_subject_invalid")]
    public async Task MandatoryIdentity_UnsignableCallerKeepsOldContainer(string provider, string name, string fault)
    {
        var agent = NewAgent(provider, name);
        await using var harness = await StartAsync(agent);
        harness.ContainerExists = true;
        var result = await harness.Service.ReprovisionAsync(name);
        Assert.False(result.Success);
        Assert.Contains(fault, result.Message);
        Assert.DoesNotContain(harness.DockerRequests, r => r.StartsWith("DELETE ") || r.EndsWith("/stop"));
        Assert.False(Directory.Exists(harness.GeneratedDir(agent.ContainerName)));
    }

    [Theory]
    [InlineData("fleet-orchestrator")]
    [InlineData("FLEET-ORCHESTRATOR")]
    [InlineData("FLEET-MEMORY")]
    [InlineData("Fleet-Telegram")]
    [InlineData("Fleet-Temporal")]
    public async Task MandatoryIdentity_UntrustedBuiltinKeepsOldContainer(string server)
    {
        var agent = NewAgent();
        agent.McpEndpoints.RemoveAll(e => e.McpName.Equals(server, StringComparison.OrdinalIgnoreCase));
        agent.McpEndpoints.Add(new() { McpName = server, Url = Collector, TransportType = "http" });
        await using var harness = await StartAsync(agent);
        harness.ContainerExists = true;
        var existingDir = harness.GeneratedDir(agent.ContainerName);
        Directory.CreateDirectory(existingDir);
        File.WriteAllText(Path.Combine(existingDir, "sentinel"), "preserve");
        var preview = await harness.Service.PreviewAsync(agent.Name);
        Assert.Contains(preview.Diffs, d => d.StartsWith("agent_identity_origin_untrusted"));
        var result = await harness.Service.ReprovisionAsync(agent.Name);
        Assert.False(result.Success);
        Assert.Contains("agent_identity_origin_untrusted", result.Message);
        Assert.DoesNotContain(harness.DockerRequests, r => r.StartsWith("DELETE ") || r.EndsWith("/stop"));
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(existingDir, "sentinel")));
        Assert.Single(Directory.GetFiles(existingDir));
    }

    [Theory]
    [InlineData("file:///secret")]
    [InlineData("https://user:password@example.test")]
    [InlineData("not-a-url")]
    public async Task MandatoryIdentity_InvalidImplicitMemoryKeepsOldFilesAndContainer(string url)
    {
        var agent = NewAgent();
        await using var h = ProvisioningHarness.Create(new Dictionary<string, string?>
            { ["FleetMemory:McpUrl"] = url });
        await h.SeedAsync(db => db.Agents.Add(agent));
        h.ContainerExists = true;
        var dir = h.GeneratedDir(agent.ContainerName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "sentinel"), "preserve");
        var result = await h.Service.ReprovisionAsync(agent.Name);
        Assert.False(result.Success);
        Assert.StartsWith("agent_identity_origin_untrusted", result.Message);
        Assert.DoesNotContain(url, result.Message);
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(dir, "sentinel")));
        Assert.Single(Directory.GetFiles(dir));
        Assert.DoesNotContain(h.DockerRequests, r => r.StartsWith("DELETE ") || r.EndsWith("/stop"));
    }

    [Theory]
    [InlineData("fleet-memory")]
    [InlineData("FLEET-MEMORY")]
    [InlineData("fleet-temporal")]
    [InlineData("FLEET-ORCHESTRATOR")]
    [InlineData("fleet-telegram")]
    public void MandatoryIdentity_BuiltinUrlsKeepUnrelatedOptions(string name)
    {
        var agent = NewAgent();
        var url = AgentConfigFiles.AgentMcpUrl(agent, new() { McpName = name, TransportType = "http",
            Url = "https://mcp.example.test/mcp?tag=a%20b&AGENT=stale&flag=1#section" });
        Assert.Contains("tag=a%20b", url);
        Assert.Contains("flag=1", url);
        Assert.EndsWith("#section", url);
        Assert.DoesNotContain("AGENT=", url);
        if (name == "fleet-telegram") Assert.Contains("formatting_mode=0", url);
        // Third-party `agent` is an application argument, not a caller credential.
        Assert.Equal("https://example.test/?agent=author", AgentConfigFiles.AgentMcpUrl(agent,
            new() { McpName = "example", Url = "https://example.test/?agent=author", TransportType = "http" }));
    }

    // ── where a token may go ─────────────────────────────────────────────────

    /// <summary>
    /// A token follows the address, not the name: a fleet server's name on another address is
    /// given nothing, so whoever can edit an endpoint cannot collect an agent's identity with it.
    /// </summary>
    [Fact]
    public async Task AFleetServersName_OnAnotherAddress_IsGivenNoToken()
    {
        var agent = NewAgent();
        agent.McpEndpoints.Single(e => e.McpName == "fleet-orchestrator").Url = Collector;
        await using var harness = await StartAsync(agent);
        var result = await harness.Service.ProvisionAsync("agent1");
        Assert.False(result.Success);
        Assert.Contains("agent_identity_origin_untrusted", result.Message);
        Assert.False(Directory.Exists(harness.GeneratedDir("fleet-agent1")));
    }

    /// <summary>
    /// The tool listing's service token is not held to any grant: it goes only to the fleet's own
    /// addresses, whatever an endpoint is called, and nowhere without the trusted origins.
    /// </summary>
    [Fact]
    public void TheToolListing_SendsTheServiceTokenToNoOtherAddress_WhateverTheEndpointIsCalled()
    {
        var service = AgentIdentityTokens.Mint(Key, AgentIdentityTokens.PurposeService, "fleet-orchestrator");
        var agent = NewAgent();
        agent.McpEndpoints.Single(e => e.McpName == "fleet-orchestrator").Url = Collector;

        var plan = McpToolPlan.Build(agent, new Dictionary<string, string>(), [], MemoryUrl, () => null, service, Origins);

        Assert.False(plan.Single(e => e.Name == "fleet-orchestrator").Target!.Headers.ContainsKey("Authorization"));
        Assert.Equal("Bearer " + service, plan.Single(e => e.Name == "fleet-telegram").Target!.Headers["Authorization"]);

        var noOrigins = McpToolPlan.Build(NewAgent(), new Dictionary<string, string>(), [], MemoryUrl, () => null, service);
        Assert.All(noOrigins, entry => Assert.False(entry.Target!.Headers.ContainsKey("Authorization")));
    }

    [Theory]
    [InlineData("http://fleet-orchestrator:3600/mcp", true)]
    [InlineData("http://FLEET-ORCHESTRATOR:3600/mcp?agent=agent1", true)]
    [InlineData("http://fleet-memory:3100/mcp", true)]
    [InlineData("http://fleet-orchestrator:3601/mcp", false)]
    [InlineData("https://fleet-orchestrator:3600/mcp", false)]
    [InlineData("http://user:pass@fleet-orchestrator:3600/mcp", false)]
    [InlineData("http://fleet-orchestrator.example.test:3600/mcp", false)]
    [InlineData("https://collector.example.test/mcp", false)]
    [InlineData("not a url", false)]
    public void ATokensDestination_IsTheFleetsOwnOrigin_Exactly(string url, bool trusted)
    {
        Assert.Equal(trusted, AgentConfigFiles.IsIdentityDestination(url, Origins));
    }

    [Fact]
    public void TheTrustedOrigins_CanBeSetForADeploymentWithOtherNames_AndAlwaysHoldFleetMemory()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AgentIdentity:TrustedOrigins"] = "http://orchestrator.internal:9000, http://bad host",
            ["FleetMemory:McpUrl"] = "http://memory.internal:3100/mcp",
        }).Build();

        var origins = AgentConfigFiles.IdentityOrigins(config);

        Assert.True(AgentConfigFiles.IsIdentityDestination("http://orchestrator.internal:9000/mcp", origins));
        Assert.True(AgentConfigFiles.IsIdentityDestination("http://memory.internal:3100", origins));
        // The setting replaces the defaults; it does not add to them.
        Assert.False(AgentConfigFiles.IsIdentityDestination("http://fleet-orchestrator:3600/mcp", origins));
    }

    /// <summary>The config token an agent sends for set_config_values keeps its own header beside the identity.</summary>
    [Fact]
    public void AConfigTokenHeader_IsKeptBesideTheIdentity_OnTheOrchestrator()
    {
        var mapped = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["fleet-orchestrator"] = new Dictionary<string, string> { ["X-Fleet-Config-Token"] = "config-token" },
        };

        var merged = AgentConfigFiles.WithIdentityHeaders(NewAgent(), mapped, "fa1.agent.agent1.sig", Origins, MemoryUrl)!;

        Assert.Equal("config-token", merged["fleet-orchestrator"]["X-Fleet-Config-Token"]);
        Assert.Equal("Bearer fa1.agent.agent1.sig", merged["fleet-orchestrator"]["Authorization"]);
    }
}
