using System.Text.Json;
using Fleet.Agent;
using Microsoft.Extensions.Configuration;

namespace Fleet.Agent.Tests;

/// <summary>
/// #518: <c>memory_stats</c> as the fourth fixed read in the real workload enrollment, native preparation and registration
/// (<see cref="StockOpenShellEnrollment"/>), in both managed auth modes under either accepted memory endpoint name. The
/// native memory table is exactly the saved selected-name reads; stats is never added or dropped implicitly.
/// </summary>
public sealed class ManagedMemoryStatsTests
{
    private const string ApiKeyHandle = "openshell:resolve:env:v7_OPENAI_API_KEY";
    private const string SubscriptionHandle = "openshell:resolve:env:v3_CODEX_AUTH_ACCESS_TOKEN";

    private static ManagedStockAgentTests.Fixture ManagedFixture(string mode, string name)
    {
        var f = new ManagedStockAgentTests.Fixture(memoryName: name);
        if (mode != "subscription") return f;
        f.Settings["Agent:NativeAuthMode"] = "subscription";
        f.Settings["Agent:ProviderApiKeyEnvRef"] = null;
        f.Settings["StockOpenShell:SubscriptionAccountId"] = "synthetic-account_01";
        f.Settings["StockOpenShell:SubscriptionProvider"] = "agent1-codex-sub";
        f.Settings["StockOpenShell:SubscriptionOwnerGeneration"] = "4f0c6c8e-6f39-4f5b-9d50-6b1b4b7e2a11";
        return f;
    }

    private static Func<string, string?> ManagedEnv(string mode) =>
        mode == "subscription" ? name => name == "CODEX_AUTH_ACCESS_TOKEN" ? SubscriptionHandle : null : _ => ApiKeyHandle;

    /// <summary>Replaces the configured AllowedTools with exactly these entries.</summary>
    private static void Grants(ManagedStockAgentTests.Fixture f, params string[] grants)
    {
        foreach (var key in f.Settings.Keys.Where(k => k.StartsWith("Agent:AllowedTools:", StringComparison.Ordinal)).ToList()) f.Settings.Remove(key);
        for (var i = 0; i < grants.Length; i++) f.Settings[$"Agent:AllowedTools:{i}"] = grants[i];
    }

    private static string MemoryTable(string name, string tools) =>
        $"\n[mcp_servers.{name}]\nurl = \"https://memory.example.test/mcp\"\nenabled_tools = {tools}\n";

    private static int McpTables(string config) =>
        System.Text.RegularExpressions.Regex.Matches(config, @"^\[mcp_servers\.", System.Text.RegularExpressions.RegexOptions.Multiline).Count;

    // A5: stats alone, stats beside get, or all four saved reads register in both modes under either name; the native
    // table is identical at preparation and registration and the other accepted name's grants never count.
    [Theory]
    [InlineData("api_key", "memory", "memory_stats")]
    [InlineData("api_key", "fleet-memory", "memory_stats")]
    [InlineData("api_key", "memory", "memory_get", "memory_stats")]
    [InlineData("api_key", "fleet-memory", "memory_get", "memory_search", "memory_list", "memory_stats")]
    [InlineData("subscription", "memory", "memory_stats")]
    [InlineData("subscription", "fleet-memory", "memory_stats")]
    [InlineData("subscription", "fleet-memory", "memory_get", "memory_stats")]
    [InlineData("subscription", "memory", "memory_get", "memory_search", "memory_list", "memory_stats")]
    public void ManagedMemoryStats_native_table_is_exactly_the_saved_reads_including_stats(string mode, string name, params string[] reads)
    {
        using var f = ManagedFixture(mode, name);
        var other = name == "memory" ? "fleet-memory" : "memory";
        Grants(f, [.. reads.Select(r => $"mcp__{name}__{r}"), $"mcp__{other}__memory_stats"]);
        var env = ManagedEnv(mode);
        var c = f.Config();

        var b = StockOpenShellEnrollment.Validate(c, f.Home, () => 10001, f.Root, env)!;
        StockOpenShellEnrollment.PrepareNative(c, f.Home, b, f.Root, env);
        var prepared = File.ReadAllText(Path.Combine(f.NativeDirectory, "config.toml"));
        var registered = StockOpenShellEnrollment.Validate(c, f.Home, () => 10001, f.Root, env)!;
        StockOpenShellEnrollment.RequirePrepared(c, registered, f.Home, env);

        Assert.EndsWith(MemoryTable(name, JsonSerializer.Serialize(reads)), prepared);
        Assert.Equal(1, McpTables(prepared));
        Assert.Equal(prepared, File.ReadAllText(Path.Combine(f.NativeDirectory, "config.toml")));
    }

    // A5: no implicit stats. Without a stats grant the table omits it; a stats grant added after preparation is stale
    // until preparation runs again, and dropping it narrows the table on the next preparation.
    [Theory]
    [InlineData("api_key")]
    [InlineData("subscription")]
    public void ManagedMemoryStats_stats_appears_only_with_its_grant_and_drift_refuses_registration(string mode)
    {
        using var f = ManagedFixture(mode, "fleet-memory");
        Grants(f, "mcp__fleet-memory__memory_get");
        var env = ManagedEnv(mode);
        var c = f.Config();
        var b = StockOpenShellEnrollment.Validate(c, f.Home, () => 10001, f.Root, env)!;
        StockOpenShellEnrollment.PrepareNative(c, f.Home, b, f.Root, env);
        var path = Path.Combine(f.NativeDirectory, "config.toml");
        Assert.EndsWith(MemoryTable("fleet-memory", "[\"memory_get\"]"), File.ReadAllText(path));

        f.Settings["Agent:AllowedTools:1"] = "mcp__fleet-memory__memory_stats";
        var widened = f.Config();
        Assert.Equal("stock_enrollment_native_not_prepared",
            Assert.Throws<InvalidOperationException>(() => StockOpenShellEnrollment.RequirePrepared(widened, b, f.Home, env)).Message);
        StockOpenShellEnrollment.PrepareNative(widened, f.Home, b, f.Root, env);
        Assert.EndsWith(MemoryTable("fleet-memory", "[\"memory_get\",\"memory_stats\"]"), File.ReadAllText(path));
        StockOpenShellEnrollment.RequirePrepared(widened, b, f.Home, env);
    }

    // A5: a stats grant only under the other accepted name, or an unknown memory grant beside stats, refuses before any
    // native write instead of being filtered away, in both auth modes.
    [Theory]
    [InlineData("api_key", "stock_enrollment_memory_tools_missing", "mcp__fleet-memory__memory_stats")]
    [InlineData("api_key", "stock_enrollment_mcp_route_invalid", "mcp__memory__memory_stats", "mcp__memory__memory_unsupported")]
    [InlineData("api_key", "stock_enrollment_mcp_route_invalid", "mcp__memory__memory_stats", "mcp__memory__memory_store")]
    [InlineData("subscription", "stock_enrollment_memory_tools_missing", "mcp__fleet-memory__memory_stats")]
    [InlineData("subscription", "stock_enrollment_mcp_route_invalid", "mcp__memory__memory_stats", "mcp__memory__Memory_stats")]
    [InlineData("subscription", "stock_enrollment_mcp_route_invalid", "mcp__memory__memory_get", "mcp__memory__memory_stats_all")]
    public void ManagedMemoryStats_missing_or_unknown_grants_refuse_before_any_native_write(string mode, string code, params string[] grants)
    {
        using var f = ManagedFixture(mode, "memory");
        Grants(f, grants);
        var env = ManagedEnv(mode);

        var refusal = Assert.Throws<InvalidOperationException>(() => StockOpenShellEnrollment.Validate(f.Config(), f.Home, () => 10001, f.Root, env));

        Assert.Equal(code, refusal.Message);
        Assert.False(Directory.Exists(Path.Combine(f.Home, ".codex")));
    }

    // A5: ordinary agents are unchanged: without the stock opt-in nothing is validated or written, whatever memory grants
    // the agent holds. The manual stock profile keeps its fixed-read intersection, which now admits a granted stats read.
    [Fact]
    public void ManagedMemoryStats_ordinary_agent_is_untouched_and_manual_profile_keeps_its_intersection()
    {
        var ordinary = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agent:Name"] = "agent1", ["Agent:Provider"] = "codex",
            ["Agent:AllowedTools:0"] = "mcp__fleet-memory__memory_get", ["Agent:AllowedTools:1"] = "mcp__fleet-memory__memory_stats",
        }).Build();
        var home = Path.Combine(Path.GetTempPath(), "ordinary-agent-" + Guid.NewGuid().ToString("N"));
        Assert.Null(StockOpenShellEnrollment.Validate(ordinary, home, () => 10001));
        Assert.False(Directory.Exists(home));

        foreach (var (grants, table) in new[]
        {
            (new[] { "mcp__memory__memory_get", "mcp__memory__memory_store" }, "[\"memory_get\"]"),
            (new[] { "mcp__memory__memory_get", "mcp__memory__memory_stats", "mcp__memory__memory_delete" }, "[\"memory_get\",\"memory_stats\"]"),
        })
        {
            using var f = new ManagedStockAgentTests.Fixture();
            f.Settings.Remove("Agent:NativeAuthMode");
            f.Settings.Remove("Agent:ProviderApiKeyEnvRef");
            Grants(f, grants);
            Directory.CreateDirectory(Path.Combine(f.Home, ".codex-host"));
            File.WriteAllText(Path.Combine(f.Home, ".codex-host", "auth.json"), "{\"synthetic_manual\":true}\n");
            var c = f.Config();
            var b = StockOpenShellEnrollment.Validate(c, f.Home, () => 10001, f.Root, _ => null)!;
            StockOpenShellEnrollment.PrepareNative(c, f.Home, b, f.Root, _ => null);

            Assert.False(StockOpenShellEnrollment.IsManaged(c));
            Assert.EndsWith(MemoryTable("memory", table), File.ReadAllText(Path.Combine(f.Home, ".codex", "config.toml")));
        }
    }
}
