using System.Text.Json;
using System.Text.Json.Nodes;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Tests.Services;

namespace Fleet.Orchestrator.Tests;

/// <summary>
/// #518: <c>memory_stats</c> as the fourth fixed managed memory read. The actual managed generation path
/// (<see cref="ContainerProvisioningService.GenerateManagedInputsAsync"/> over SQLite rows) and saved-policy admission, in
/// both auth modes under either accepted memory endpoint name, with only the stock CLI, host view and broker faked. A
/// stats read is callable only when both the saved policy and an enabled selected-name grant name it; nothing implies it.
/// </summary>
public sealed class ManagedMemoryStatsTests
{
    private static readonly string[] AllReads = ["memory_get", "memory_search", "memory_list", "memory_stats"];

    /// <summary>One auth branch over the shared adapter fixture; subscription adds its outside source and scoped CLI.</summary>
    private sealed class Branch : IDisposable
    {
        private const string Provider = "agent1-codex-sub";
        private readonly ManagedStockSubscriptionLifecycleTests.ScopedCli? _scoped;
        private readonly ManagedStockSubscriptionLifecycleTests.MutableClock _clock = new() { Now = DateTimeOffset.UtcNow };
        public readonly ManagedStockRuntimeAdapterTests T;
        public readonly bool Subscription;

        public Branch(string mode)
        {
            Subscription = mode == "subscription";
            if (!Subscription) { T = new ManagedStockRuntimeAdapterTests(); return; }
            T = new ManagedStockRuntimeAdapterTests(PrivateBase.Create("managed-stats-"));
            _scoped = new ManagedStockSubscriptionLifecycleTests.ScopedCli(_clock);
            _scoped.Providers[("ws-a", Provider)] = (StockSubscriptionBindingTests.ProviderId, "codex-subscription-native", ["CODEX_AUTH_ACCESS_TOKEN"]);
            T._store.ProfileHashes = ManagedStockRuntimeAdapterTests.FakeStore.PinnedProfiles.TrimEnd('}')
                + ",\"codex-subscription-native\":\"" + SyntheticProfile.Digest + "\"}";
            var source = StockSubscriptionBindingTests.Source(workspace: "ws-a", expires: _clock.Now.AddHours(2), profile: SyntheticProfile.Digest);
            source["gatewayGeneration"] = T._host.Generation;
            StockSubscriptionBindingTests.WriteSource(T._dir, source);
        }

        public void Dispose()
        {
            _scoped?.Dispose();
            T.Dispose();
        }

        /// <summary>A managed agent of this branch whose selected memory endpoint holds exactly these enabled grants.</summary>
        public Agent Agent(string name, params string[] reads)
        {
            var agent = T.ManagedAgentWithReads(name, reads);
            if (Subscription) { agent.NativeAuthMode = "subscription"; agent.ProviderApiKeyEnvRef = null; }
            return agent;
        }

        public StockOpenShellRuntime Runtime(ContainerProvisioningService? provisioning = null, Func<string, Task<bool>>? dockerStop = null) =>
            Subscription ? T.Runtime(dockerStop, cli: _scoped, clock: _clock, provisioning: provisioning) : T.Runtime(dockerStop, provisioning: provisioning);

        /// <summary>The fake that received this branch's sandbox calls and uploads.</summary>
        public ManagedStockRuntimeAdapterTests.FakeCli Cli => Subscription ? _scoped!.Cli("ws-a") : T._cli;

        public IReadOnlyList<List<string>> Calls => Subscription ? [.. _scoped!.Calls.Select(c => c.Args)] : [.. T._cli.Calls.Select(c => c.Args)];
    }

    private static string Grants(Agent agent) => JsonSerializer.Serialize(agent.Tools.Select(t => new { t.ToolName, t.IsEnabled }));

    private static List<string> AllowedTools(ManagedStockRuntimeAdapterTests.FakeCli cli) =>
        JsonNode.Parse(cli.Uploads.Last(u => u.Remote == "/home/agent/app/appsettings.json").Content!)!["Agent"]!["AllowedTools"]!.AsArray()
            .Select(t => t!.GetValue<string>()).ToList();

    // A5: stats alone, or all four reads, apply through real generation in both auth modes under either name. The bundle
    // lists exactly the saved enabled memory grants (no implicit get), and the CLI receives the exact saved policy bytes.
    [Theory]
    [InlineData("api_key", "memory", new[] { "memory_stats" })]
    [InlineData("api_key", "fleet-memory", new[] { "memory_stats" })]
    [InlineData("api_key", "memory", new[] { "memory_get", "memory_search", "memory_list", "memory_stats" })]
    [InlineData("api_key", "fleet-memory", new[] { "memory_get", "memory_search", "memory_list", "memory_stats" })]
    [InlineData("subscription", "memory", new[] { "memory_stats" })]
    [InlineData("subscription", "fleet-memory", new[] { "memory_stats" })]
    [InlineData("subscription", "memory", new[] { "memory_get", "memory_search", "memory_list", "memory_stats" })]
    [InlineData("subscription", "fleet-memory", new[] { "memory_get", "memory_search", "memory_list", "memory_stats" })]
    public async Task ManagedMemoryStats_actual_generation_bundles_exactly_the_explicit_reads_in_both_modes(string mode, string name, string[] reads)
    {
        using var branch = new Branch(mode);
        await using var harness = ProvisioningHarness.Create();
        await harness.SeedAsync(db => db.Agents.Add(ManagedStockRuntimeAdapterTests.GeneratorRow(branch.Agent(name, reads))));
        branch.T._store.Add(branch.Agent(name, reads));
        branch.T._store.PolicyYaml = SignedPolicy.MemoryReads(reads);
        branch.T._host.DockerStates = ["exited"];

        var applied = await branch.Runtime(harness.Service).ApplyAsync("agent1", default);

        Assert.True(applied.Ok, applied.Message);
        Assert.Equal(reads.Select(r => $"mcp__{name}__{r}").Order(StringComparer.Ordinal),
            ManagedStockRuntimeAdapterTests.BundledMemoryTools(branch.Cli).Order(StringComparer.Ordinal));
        var create = Assert.Single(branch.Calls, c => c.Take(2).SequenceEqual(["sandbox", "create"]));
        Assert.Equal(branch.T._store.PolicyYaml, File.ReadAllText(create[create.IndexOf("--policy") + 1]));
    }

    // A5/A6: a retained stats grant beside an old three-read saved version, or an unknown memory grant beside a four-read
    // version, refuses before any old-runtime Stop or provider/broker/create/policy-set effect; grant rows stay as saved.
    [Theory]
    [InlineData("api_key", "memory", "memory_stats", false)]
    [InlineData("api_key", "fleet-memory", "memory_stats", false)]
    [InlineData("api_key", "memory", "memory_unsupported", true)]
    [InlineData("subscription", "memory", "memory_stats", false)]
    [InlineData("subscription", "fleet-memory", "memory_stats", false)]
    [InlineData("subscription", "fleet-memory", "memory_unsupported", true)]
    public async Task ManagedMemoryStats_an_omitted_stats_or_unknown_grant_refuses_before_any_effect(string mode, string name, string extra, bool fourReadPolicy)
    {
        using var branch = new Branch(mode);
        var agent = branch.T._store.Add(branch.Agent(name, "memory_get", extra));
        branch.T._store.PolicyYaml = fourReadPolicy ? SignedPolicy.MemoryReads(AllReads) : SignedPolicy.MemoryReads("memory_get", "memory_search", "memory_list");
        var grants = Grants(agent);
        var dockerStops = 0;
        branch.T._host.DockerStates = ["running"];

        var result = await branch.Runtime(dockerStop: _ => { dockerStops++; return Task.FromResult(true); }).ApplyAsync("agent1", default);

        Assert.Equal((409, "managed_route_unsupported:mcp:" + name), (result.Status, result.Code));
        Assert.Equal(0, dockerStops);
        Assert.Empty(branch.T._broker.Puts);
        Assert.DoesNotContain(branch.Calls, c => c[0] is "sandbox" or "provider" or "policy");
        Assert.False(File.Exists(branch.T.AuthorityPath()));
        var stored = branch.T._store.Get("agent1");
        Assert.Equal(grants, Grants(stored));
        Assert.Equal((mode, "none", "docker", (int?)null), (stored.NativeAuthMode, ManagedRecord.Parse(stored.AppliedRuntimeBinding).State,
            stored.AppliedRuntimeKind, stored.AppliedSandboxPolicyVersionId));
    }

    // A5: policy permission alone grants nothing. A four-read version with no stats grant applies, and neither the bundle nor
    // the native inputs gain stats; a stats grant under the other accepted name never counts for the selected endpoint.
    [Theory]
    [InlineData("api_key", "memory", "fleet-memory")]
    [InlineData("subscription", "fleet-memory", "memory")]
    public async Task ManagedMemoryStats_policy_listing_stats_without_a_selected_name_grant_adds_nothing(string mode, string name, string other)
    {
        using var branch = new Branch(mode);
        await using var harness = ProvisioningHarness.Create();
        Agent Row()
        {
            var agent = branch.Agent(name, "memory_get");
            agent.Tools.Add(new AgentTool { ToolName = $"mcp__{other}__memory_stats" });
            agent.Tools.Add(new AgentTool { ToolName = $"mcp__{name}__memory_stats", IsEnabled = false });
            return agent;
        }
        await harness.SeedAsync(db => db.Agents.Add(ManagedStockRuntimeAdapterTests.GeneratorRow(Row())));
        branch.T._store.Add(Row());
        branch.T._store.PolicyYaml = SignedPolicy.MemoryReads(AllReads);
        branch.T._host.DockerStates = ["exited"];

        var applied = await branch.Runtime(harness.Service).ApplyAsync("agent1", default);

        Assert.True(applied.Ok, applied.Message);
        Assert.Equal([$"mcp__{name}__memory_get"],
            ManagedStockRuntimeAdapterTests.BundledMemoryTools(branch.Cli).Where(t => t.StartsWith($"mcp__{name}__", StringComparison.Ordinal)));
        Assert.DoesNotContain($"mcp__{name}__memory_stats", AllowedTools(branch.Cli));
    }

    // A5: a stats grant beside existing Temporal grants keeps every non-memory grant and route; nothing is pruned to pass.
    [Theory]
    [InlineData("memory")]
    [InlineData("fleet-memory")]
    public async Task ManagedMemoryStats_non_memory_grants_and_routes_are_retained(string name)
    {
        using var branch = new Branch("api_key");
        await using var harness = ProvisioningHarness.Create();
        Agent Row()
        {
            var agent = branch.Agent(name, "memory_stats");
            agent.McpEndpoints.Add(new AgentMcpEndpoint { McpName = "fleet-temporal", Url = "https://temporal.example.test/mcp", TransportType = "http" });
            agent.Tools.Add(new AgentTool { ToolName = "mcp__fleet-temporal__temporal_start_workflow" });
            return agent;
        }
        await harness.SeedAsync(db => db.Agents.Add(ManagedStockRuntimeAdapterTests.GeneratorRow(Row())));
        var agent = branch.T._store.Add(Row());
        var grants = Grants(agent);
        branch.T._store.PolicyYaml = SignedPolicy.Yaml(SignedPolicy.Memory, SignedPolicy.Temporal)
            .Replace(SignedPolicy.Rules("memory_get"), SignedPolicy.Rules("memory_stats"));

        var applied = await branch.Runtime(harness.Service).ApplyAsync("agent1", default);

        Assert.True(applied.Ok, applied.Message);
        Assert.Equal([$"mcp__{name}__memory_stats"], ManagedStockRuntimeAdapterTests.BundledMemoryTools(branch.Cli));
        Assert.Contains("mcp__fleet-temporal__temporal_start_workflow", AllowedTools(branch.Cli));
        var authority = JsonNode.Parse(File.ReadAllText(branch.T.AuthorityPath()))!;
        Assert.Equal(["temporal_start_workflow"], authority["Services"]!["temporal"]!["Tools"]!.AsArray().Select(t => t!.GetValue<string>()));
        Assert.Equal(grants, Grants(branch.T._store.Get("agent1")));

        // The same grants under the old memory_get-only version refuse; the Temporal grant is not dropped to make it pass.
        branch.T._store.PolicyYaml2 = SignedPolicy.Yaml(SignedPolicy.Memory, SignedPolicy.Temporal);
        branch.T._store.Get("agent1").SandboxPolicyVersionId = 2;
        Assert.True((await branch.Runtime(harness.Service).StopAsync("agent1", default)).Ok);
        var refused = await branch.Runtime(harness.Service).ApplyAsync("agent1", default);
        Assert.Equal((409, "managed_route_unsupported:mcp:" + name), (refused.Status, refused.Code));
        Assert.Equal(grants, Grants(branch.T._store.Get("agent1")));
    }

    // A5: ordinary (non-managed) generation is unchanged: it keeps its automatic memory_get, carries a stats grant only when
    // one is saved, and never adds stats implicitly; managed generation drops only the automatic get.
    [Theory]
    [InlineData("docker")]
    [InlineData("openshell")]
    public void ManagedMemoryStats_ordinary_generation_keeps_its_baseline_and_adds_no_implicit_stats(string desiredRuntime)
    {
        var plain = ManagedStockRuntimeTests.Agent();
        plain.RuntimeKind = desiredRuntime;
        var ordinary = ContainerProvisioningService.AllowedToolsFor(plain, "cto-agent");
        Assert.Contains("mcp__fleet-memory__memory_get", ordinary);
        Assert.DoesNotContain(ordinary, t => t.EndsWith("__memory_stats", StringComparison.Ordinal));
        Assert.DoesNotContain(ContainerProvisioningService.AllowedToolsFor(plain, "cto-agent", managed: true),
            t => t.EndsWith("__memory_stats", StringComparison.Ordinal) || t.EndsWith("__memory_get", StringComparison.Ordinal));

        var granted = ManagedStockRuntimeTests.Agent();
        granted.RuntimeKind = desiredRuntime;
        granted.Tools.Add(new AgentTool { ToolName = "mcp__fleet-memory__memory_stats" });
        Assert.Equal(ContainerProvisioningService.GenerateAppsettingsJson(granted, "cto-agent"),
            ContainerProvisioningService.GenerateAppsettingsJson(granted, "cto-agent", managed: false));
        Assert.Equal(["mcp__fleet-memory__memory_get", "mcp__fleet-memory__memory_stats"],
            ContainerProvisioningService.AllowedToolsFor(granted, "cto-agent").Where(t => t.StartsWith("mcp__fleet-memory__", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        Assert.Equal(["mcp__fleet-memory__memory_stats"],
            ContainerProvisioningService.AllowedToolsFor(granted, "cto-agent", managed: true).Where(t => t.StartsWith("mcp__fleet-memory__", StringComparison.Ordinal)));
    }
}
