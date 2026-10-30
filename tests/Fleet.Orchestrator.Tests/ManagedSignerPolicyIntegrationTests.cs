using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Endpoints;
using Fleet.Orchestrator.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fleet.Orchestrator.Tests;

/// <summary>
/// #503 C2/C3: the actual authenticated policy HTTP API → SQLite → <see cref="EfManagedBindingStore"/> → managed apply,
/// with only the stock CLI, host view and broker faked. The CLI's <c>--policy</c> input is captured as bytes when it runs,
/// never a separately invented fixture. SQLite is the local API store here, not migration evidence.
/// </summary>
public sealed class ManagedSignerPolicyIntegrationTests : IAsyncLifetime
{
    private const string ProfileHash = "b44acdcd83b1965ebff85528e0b56645a4714be483b6bc860fbbe562acade702"; // the fake export
    private readonly ManagedStockRuntimeAdapterTests _t = new();
    private readonly CapturingCli _cli;
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private Microsoft.Data.Sqlite.SqliteConnection _sqlite = null!;

    public ManagedSignerPolicyIntegrationTests() => _cli = new CapturingCli(_t._cli);

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["Orchestrator:AuthToken"] = "synthetic-test-token";
        builder.Logging.ClearProviders();
        _sqlite = new("Data Source=:memory:");
        await _sqlite.OpenAsync();
        builder.Services.AddDbContext<OrchestratorDbContext>(o => o.UseSqlite(_sqlite));
        builder.Services.AddSingleton<IManagedBindingStore, EfManagedBindingStore>();
        _app = builder.Build();
        _app.MapSandboxPolicyEndpoints();
        using (var scope = _app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Database.EnsureCreatedAsync();
        await _app.StartAsync();
        _client = _app.GetTestClient();
        _client.DefaultRequestHeaders.Authorization = new("Bearer", "synthetic-test-token");
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        await _sqlite.DisposeAsync();
        _t.Dispose();
    }

    private StockOpenShellRuntime Runtime() => _t.Runtime(cli: _cli, store: _app.Services.GetRequiredService<IManagedBindingStore>());

    private async Task<T> WithDb<T>(Func<OrchestratorDbContext, Task<T>> action)
    {
        using var scope = _app.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>());
    }

    private async Task<int> Create(string yaml)
    {
        var response = await _client.PostAsJsonAsync("/api/sandbox-policies",
            new { name = "Signed policy", yaml, providerProfileHashes = new Dictionary<string, string> { ["openai"] = ProfileHash } });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private async Task<JsonElement> Append(int policy, int version, string yaml)
    {
        var response = await _client.PostAsJsonAsync($"/api/sandbox-policies/{policy}/versions",
            new { version, yaml, providerProfileHashes = new Dictionary<string, string> { ["openai"] = ProfileHash } });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<int> AddAgent(int versionId, Action<Agent>? change = null)
    {
        var agent = _t.ManagedAgent(desired: "openshell", applied: "docker");
        agent.SandboxPolicyVersionId = versionId;
        change?.Invoke(agent);
        return await WithDb(async db => { db.Agents.Add(agent); await db.SaveChangesAsync(); return agent.Id; });
    }

    private Task<Agent> Stored() => WithDb(db => db.Agents.AsNoTracking().SingleAsync());

    [Fact]
    public async Task Saved_bytes_reach_the_cli_and_every_hash_is_the_recomputed_saved_artifact()
    {
        // Create an old unbound version over HTTP, then append the reviewed bound one; the agent first selects the old one.
        var policy = await Create(ManagedStockRuntimeTests.PolicyYaml);
        var bound = SignedPolicy.Yaml(SignedPolicy.Memory, SignedPolicy.Temporal);
        var appended = await Append(policy, 2, bound);
        var oldId = await WithDb(db => db.SandboxPolicyVersions.Where(v => v.PolicyId == policy && v.Version == 1).Select(v => v.Id).SingleAsync());
        await AddAgent(oldId, a => a.McpEndpoints.Add(new AgentMcpEndpoint { McpName = "fleet-temporal", Url = "https://temporal.example.test/mcp", TransportType = "http" }));
        _t._host.DockerStates = ["exited"];
        var runtime = Runtime();

        var refused = await runtime.ApplyAsync("agent1", default);

        Assert.Equal((409, StockManagedSignerPolicy.Missing), (refused.Status, refused.Code));
        Assert.Empty(_cli.Inner.Calls);
        Assert.Empty(_t._broker.Writes);
        var unchanged = await Stored();
        Assert.Equal(("docker", (int?)null), (unchanged.AppliedRuntimeKind, unchanged.AppliedSandboxPolicyVersionId));

        // Select the bound version (desired-only save) and apply through the real EF store.
        var boundId = appended.GetProperty("id").GetInt32();
        await WithDb(async db => { (await db.Agents.SingleAsync()).SandboxPolicyVersionId = boundId; return await db.SaveChangesAsync(); });
        var applied = await runtime.ApplyAsync("agent1", default);

        Assert.True(applied.Ok, applied.Message);
        var served = await _client.GetFromJsonAsync<JsonElement>($"/api/sandbox-policies/{policy}/versions/2");
        var savedYaml = served.GetProperty("yaml").GetString()!;
        var savedHash = served.GetProperty("sha256").GetString()!;
        var recomputed = SandboxPolicyValidation.Hash(savedYaml);
        Assert.Equal(recomputed, savedHash);
        // The exact saved UTF-8 bytes were the --policy input the stock CLI actually read at create time.
        var create = Assert.Single(_cli.PolicyInputs, p => p.Verb == "create");
        Assert.Equal(Encoding.UTF8.GetBytes(savedYaml), create.Bytes);
        var stored = await Stored();
        Assert.Equal(("openshell", boundId), (stored.AppliedRuntimeKind, stored.AppliedSandboxPolicyVersionId));
        var record = ManagedRecord.Parse(stored.AppliedRuntimeBinding);
        Assert.Equal((boundId, recomputed), (record.Applied!.PolicyVersionId, record.Applied.PolicySha256));
        var outside = JsonNode.Parse(File.ReadAllText(_t.AuthorityPath()))!;
        Assert.Equal(recomputed, outside["PolicySha256"]!.GetValue<string>());
        Assert.Equal("temporal.example.test", outside["Services"]!["temporal"]!["Host"]!.GetValue<string>());
        var guest = JsonNode.Parse(_t._cli.Uploads.Last(u => u.Remote == "/home/agent/trust/enrollment.json").Content!)!;
        Assert.Equal(recomputed, guest["PolicySha256"]!.GetValue<string>());
        // Stored bytes are immutable: the row read back still hashes to its recorded value.
        Assert.Equal(savedHash, await WithDb(db => db.SandboxPolicyVersions.Where(v => v.Id == boundId).Select(v => v.Sha256).SingleAsync()));
    }

    [Theory]
    [InlineData("api_key", "absent", StockManagedSignerPolicy.Missing)]
    [InlineData("api_key", "wrong", StockManagedSignerPolicy.Mismatch)]
    [InlineData("api_key", "extra", StockManagedSignerPolicy.Scope)]
    [InlineData("api_key", "unselected_route", StockManagedSignerPolicy.Missing)]
    [InlineData("subscription", "absent", StockManagedSignerPolicy.Missing)]
    [InlineData("subscription", "wrong", StockManagedSignerPolicy.Mismatch)]
    [InlineData("subscription", "extra", StockManagedSignerPolicy.Scope)]
    public async Task Absent_wrong_or_extra_binding_refuses_with_zero_effects(string mode, string fault, string code)
    {
        var yaml = fault switch
        {
            "absent" => ManagedStockRuntimeTests.PolicyYaml,
            "wrong" => SignedPolicy.Yaml([SignedPolicy.Memory], [("memory", "other.example.test")]),
            "extra" => SignedPolicy.Yaml(SignedPolicy.Memory, SignedPolicy.Temporal),
            // The agent selects Temporal, but the saved artifact signs memory alone.
            _ => SignedPolicy.Yaml([SignedPolicy.Memory, SignedPolicy.Temporal], [SignedPolicy.Memory]),
        };
        var policy = await Create(yaml);
        var versionId = await WithDb(db => db.SandboxPolicyVersions.Where(v => v.PolicyId == policy).Select(v => v.Id).SingleAsync());
        await AddAgent(versionId, a =>
        {
            if (mode == "subscription") { a.NativeAuthMode = "subscription"; a.ProviderApiKeyEnvRef = null; }
            if (fault == "unselected_route")
                a.McpEndpoints.Add(new AgentMcpEndpoint { McpName = "fleet-temporal", Url = "https://temporal.example.test/mcp", TransportType = "http" });
        });
        var before = (await Stored()).AppliedRuntimeBinding;
        var dockerStops = 0;
        _t._host.DockerStates = ["running"];

        var result = await _t.Runtime(_ => { dockerStops++; return Task.FromResult(true); }, cli: _cli,
            store: _app.Services.GetRequiredService<IManagedBindingStore>()).ApplyAsync("agent1", default);

        Assert.Equal((409, code), (result.Status, result.Code));
        // No old-runtime Stop, provider/broker mutation, create, policy-set or activation; the record is unchanged.
        Assert.Equal(0, dockerStops);
        Assert.Empty(_cli.Inner.Calls);
        Assert.Empty(_t._broker.Writes);
        var stored = await Stored();
        Assert.Equal(("docker", (int?)null), (stored.AppliedRuntimeKind, stored.AppliedSandboxPolicyVersionId));
        Assert.Equal(ManagedRecord.Parse(before).State, ManagedRecord.Parse(stored.AppliedRuntimeBinding).State);
        Assert.Null(ManagedRecord.Parse(stored.AppliedRuntimeBinding).Candidate);
        Assert.False(File.Exists(_t.AuthorityPath()));
    }

    /// <summary>Delegates to the adapter fake and records the bytes of every <c>--policy</c> file as the CLI is invoked.</summary>
    private sealed class CapturingCli(ManagedStockRuntimeAdapterTests.FakeCli inner) : IStockCli
    {
        public readonly ManagedStockRuntimeAdapterTests.FakeCli Inner = inner;
        public readonly List<(string Verb, byte[] Bytes)> PolicyInputs = [];

        public Task<StockCliResult> RunAsync(IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? secretEnv, TimeSpan timeout, CancellationToken ct)
        {
            var at = args.ToList().IndexOf("--policy");
            if (at >= 0) PolicyInputs.Add((args.Contains("create") ? "create" : "set", File.ReadAllBytes(args[at + 1])));
            return Inner.RunAsync(args, secretEnv, timeout, ct);
        }
    }
}
